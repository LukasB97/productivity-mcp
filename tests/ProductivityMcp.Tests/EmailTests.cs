using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MimeKit;
using ModelContextProtocol.Server;
using ProductivityMcp.Core;
using ProductivityMcp.Email;
using ProductivityMcp.Providers.Google;
using ProductivityMcp.Server;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class EmailTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "email.accounts.list", "email.messages.query", "email.messages.get", "email.messages.send",
        "email.messages.forward", "email.messages.update", "email.messages.archive", "email.messages.trash",
        "email.threads.get", "email.attachments.get", "email.drafts.list", "email.drafts.create",
        "email.drafts.update", "email.drafts.send", "email.labels.list", "email.labels.create",
    ];
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [TestMethod]
    public void EmailTools_ExposeStableDotNames()
    {
        var names = typeof(EmailTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .ToArray();

        CollectionAssert.AreEquivalent(ExpectedToolNames, names);
    }

    [TestMethod]
    public void GoogleQuery_TranslatesNestedFiltersAndExactTimes()
    {
        var filter = new EmailFilter
        {
            From = ["sender@example.com"],
            After = DateTimeOffset.Parse("2026-09-14T10:00:00+02:00", CultureInfo.InvariantCulture),
            HasAttachments = true,
            Or = [new EmailFilter { Subject = "quarterly \"review\"" }, new EmailFilter { Unread = true }],
            Not = new EmailFilter { Mailbox = EmailMailbox.Trash },
        };

        var query = GoogleEmailQuery.Build(filter);

        StringAssert.Contains(query, "from:\"sender@example.com\"");
        StringAssert.Contains(query, $"after:{filter.After!.Value.ToUnixTimeSeconds()}");
        StringAssert.Contains(query, "has:attachment");
        StringAssert.Contains(query, "subject:\"quarterly \\\"review\\\"\"");
        StringAssert.Contains(query, "-(in:trash)");
    }

    [TestMethod]
    public void EmailQuery_RejectsExcessiveNestingAndNegativeSizes()
    {
        var filter = new EmailFilter { LargerThanBytes = -1 };
        for (var index = 0; index < 10; index++) filter = new EmailFilter { Not = filter };

        var result = ModelValidation.Validate(new EmailQuery { Filter = filter }, "query");

        Assert.IsInstanceOfType<OperationResult<EmailQuery>.Failure>(result);
    }

    [TestMethod]
    public void MimeContent_ComposesAndParsesMarkdownWithAttachment()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "attachment-data");
            var mime = EmailContentService.Compose("sender@example.com", new OutgoingEmail
            {
                To = ["recipient@example.com"],
                Subject = "Test",
                Body = new(EmailContentFormat.Markdown, "# Heading\n\n[Link](https://example.com)"),
                Attachments = [new EmailAttachmentInput { Path = path, Name = "sample.txt" }],
            });

            var parsed = EmailContentService.Parse(EmailContentService.ToRaw(mime));

            Assert.AreEqual("Test", parsed.Subject);
            StringAssert.Contains(parsed.HtmlBody, "<h1>Heading</h1>");
            StringAssert.Contains(parsed.TextBody, "Heading");
            Assert.AreEqual("sample.txt", parsed.Attachments.OfType<MimePart>().Single().FileName);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async System.Threading.Tasks.Task MimeContent_ProducesPlainHtmlAndMarkdownWithoutRenderer()
    {
        var service = new EmailContentService();
        var mime = new MimeMessage { Body = new TextPart("html") { Text = "<h1>Hello</h1><p><a href=\"https://example.com\">Example</a></p>" } };

        var plain = await service.ConvertAsync(mime, EmailContentFormat.Plain, false, CancellationToken.None);
        var html = await service.ConvertAsync(mime, EmailContentFormat.Html, false, CancellationToken.None);
        var markdown = await service.ConvertAsync(mime, EmailContentFormat.Markdown, false, CancellationToken.None);

        StringAssert.Contains(plain.Body.Content, "Hello");
        StringAssert.Contains(html.Body.Content, "<h1>Hello</h1>");
        StringAssert.Contains(markdown.Body.Content, "# Hello");
        StringAssert.Contains(markdown.Body.Content, "https://example.com");
    }

    [TestMethod]
    public async System.Threading.Tasks.Task MimeContent_ImageSplitsLongMailIntoOrderedPages()
    {
        Assert.IsTrue(EmailContentService.IsRendererInstalled(), "CI must install Playwright Chromium before running tests.");
        var service = new EmailContentService();
        var mime = new MimeMessage
        {
            Body = new TextPart("html") { Text = "<div style=\"height:2600px\">Long message</div>" },
        };

        var rendered = await service.ConvertAsync(mime, EmailContentFormat.Image, false, CancellationToken.None);

        Assert.IsGreaterThanOrEqualTo(3, rendered.Images.Count);
        CollectionAssert.AreEqual(Enumerable.Range(1, rendered.Images.Count).ToArray(), rendered.Images.Select(x => x.Page).ToArray());
        Assert.IsTrue(rendered.Images.All(x => x.MediaType == "image/png" && x.Data.Length > 0));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task MimeContent_BlocksLocalNetworkImagesEvenWhenRemoteLoadingIsEnabled()
    {
        Assert.IsTrue(EmailContentService.IsRendererInstalled(), "CI must install Playwright Chromium before running tests.");
        var service = new EmailContentService();
        var mime = new MimeMessage
        {
            Body = new TextPart("html") { Text = "<p>Safe</p><img src=\"http://127.0.0.1:1/private.png\">" },
        };

        var rendered = await service.ConvertAsync(mime, EmailContentFormat.Image, true, CancellationToken.None);

        Assert.HasCount(1, rendered.Images);
        Assert.IsGreaterThan(0, rendered.Images[0].Data.Length);
    }

    [TestMethod]
    public void DraftPatch_DistinguishesOmittedNullAndEmptyCollections()
    {
        var omitted = JsonSerializer.Deserialize<EmailDraftPatch>(
            "{\"subject\":null,\"to\":[]}",
            CamelCaseJson);

        Assert.IsNotNull(omitted);
        Assert.IsTrue(omitted.HasSubject);
        Assert.IsNull(omitted.Subject);
        Assert.IsTrue(omitted.HasTo);
        Assert.HasCount(0, omitted.To!);
        Assert.IsFalse(omitted.HasBody);
    }

    [TestMethod]
    public void DraftPatch_PreservesOmittedBodyAndAttachments()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "attachment-data");
            var mime = EmailContentService.ComposeDraft("sender@example.com", new EmailDraftInput
            {
                To = ["old@example.com"],
                Body = new(EmailContentFormat.Html, "<b>Kept</b>"),
                Attachments = [new EmailAttachmentInput { Path = path, Name = "kept.txt" }],
            });

            EmailContentService.ApplyDraftPatch(mime, new EmailDraftPatch { To = ["new@example.com"] });

            Assert.AreEqual("new@example.com", mime.To.Mailboxes.Single().Address);
            StringAssert.Contains(mime.HtmlBody, "Kept");
            Assert.AreEqual("kept.txt", mime.Attachments.OfType<MimePart>().Single().FileName);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void DraftPatch_PreservesLiteralPlainBodyWhenOnlyAttachmentsChange()
    {
        const string original = "invoice_item_1 and *literal* [brackets]";
        var mime = new MimeMessage { Body = new TextPart("plain") { Text = original } };

        EmailContentService.ApplyDraftPatch(mime, new EmailDraftPatch { Attachments = [] });

        Assert.AreEqual(original, mime.TextBody);
        Assert.IsNull(mime.HtmlBody);
    }

    [TestMethod]
    public void DraftPatch_PreservesInlineResourcesAndAttachedMessages()
    {
        var builder = new BodyBuilder
        {
            TextBody = "Original",
            HtmlBody = "<p>Original</p><img src=\"cid:logo\">",
        };
        builder.LinkedResources.Add(new MimePart("image", "png")
        {
            Content = new MimeContent(new MemoryStream([1, 2, 3])),
            ContentId = "logo",
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
        });
        builder.Attachments.Add(new MessagePart
        {
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "attached.eml" },
            Message = new MimeMessage { Subject = "Attached message", Body = new TextPart("plain") { Text = "Body" } },
        });
        var mime = new MimeMessage { Body = builder.ToMessageBody() };

        EmailContentService.ApplyDraftPatch(mime, new EmailDraftPatch
        {
            Body = new EmailBody(EmailContentFormat.Html, "<p>Replacement</p>"),
        });

        Assert.AreEqual("attached.eml", mime.Attachments.OfType<MessagePart>().Single().ContentDisposition?.FileName);
        Assert.IsTrue(mime.BodyParts.OfType<MimePart>().Any(part => part.ContentId == "logo"));
    }

    [TestMethod]
    public void RemoteImages_RejectPrivateIpv6AndMappedIpv4Addresses()
    {
        var method = typeof(EmailContentService).GetMethod("IsPrivate", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        foreach (var value in new[] { "fd12:3456:789a::1", "::2", "::ffff:10.0.0.1", "::ffff:169.254.169.254", "2001:db8::1" })
            Assert.IsTrue((bool)method.Invoke(null, [IPAddress.Parse(value)])!, value);

        Assert.IsFalse((bool)method.Invoke(null, [IPAddress.Parse("2606:4700:4700::1111")])!);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task RemoteImages_StopReadingAtConfiguredLimit()
    {
        var method = typeof(EmailContentService).GetMethod("ReadBoundedAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        using var content = new ByteArrayContent(new byte[10 * 1024 * 1024 + 1]);
        var task = (System.Threading.Tasks.Task<byte[]>)method.Invoke(null, [content, 10 * 1024 * 1024, CancellationToken.None])!;

        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await task);
    }

    [TestMethod]
    public void ExistingAccountJson_DefaultsEmailToDisabled()
    {
        var value = JsonSerializer.Deserialize<GoogleAccountRegistration>("{\"Key\":\"primary\",\"Email\":\"person@example.com\"}");

        Assert.IsNotNull(value);
        Assert.IsTrue(value.CalendarTasksEnabled);
        Assert.IsFalse(value.EmailEnabled);
    }
}
