using ProductivityMcp.Core;
using ProductivityMcp.Email;
using ProductivityMcp.Providers.Imap;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class EmailLiveTests
{
    [TestMethod]
    [TestCategory("Live")]
    public async System.Threading.Tasks.Task Mailbox_EndToEndAgainstOwnAccount()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_LIVE_TESTS"), "1", StringComparison.Ordinal))
            Assert.Inconclusive("Set PRODUCTIVITY_MCP_LIVE_TESTS=1 to run the opt-in mailbox test.");

        var account = Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_LIVE_ACCOUNT");
        if (string.IsNullOrWhiteSpace(account))
            Assert.Inconclusive("Set PRODUCTIVITY_MCP_LIVE_ACCOUNT to an explicitly authorized test mailbox. Tests only send to that same address.");
        var options = MailOptions.FromEnvironment();
        var catalog = new MailAccountCatalog(options);
        var registration = catalog.Find(account);
        if (registration is null)
            Assert.Inconclusive("Connect this mailbox in the configuration app before running live tests.");

        var provider = new MultiImapEmailProvider(catalog, new EmailContentService());
        var gmail = MultiImapEmailProvider.ProviderName(registration) == "gmail";
        await MailboxScenario.RunAsync(provider, account, gmail);
    }
}

/// <summary>Exercises every email tool against a real mailbox, sending only to that mailbox.</summary>
internal static class MailboxScenario
{
    public static async System.Threading.Tasks.Task RunAsync(IEmailProvider provider, string account, bool gmail)
    {
        var marker = $"[productivity-mcp-live-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}]";
        var attachmentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        string? downloadedPath = null;
        await File.WriteAllTextAsync(attachmentPath, marker);
        var created = new List<string>();
        try
        {
            var sent = Success(await provider.SendAsync(account, new OutgoingEmail
            {
                To = [account],
                Subject = marker,
                Body = new(EmailComposeFormat.Markdown, $"# Live test\n\n{marker}"),
                Attachments = [new EmailAttachmentInput { Path = attachmentPath, Name = "live-test.txt" }],
            }));
            created.Add(sent.Id);
            Assert.IsFalse(sent.Id.StartsWith("mid:", StringComparison.Ordinal), "The sent message should be readable from the Sent folder.");

            // Search indexes can lag a few seconds behind delivery.
            EmailQueryResult query = null!;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                query = Success(await provider.QueryAsync(new EmailQuery
                {
                    Account = account,
                    Filter = new EmailFilter { Subject = marker, Mailbox = EmailMailbox.Sent },
                    PageSize = 10,
                }));
                if (query.Messages.Any(x => x.Id == sent.Id)) break;
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2));
            }

            Assert.IsTrue(query.Complete);
            Assert.IsTrue(query.Messages.Any(x => x.Id == sent.Id), "The sent message was not found by subject.");
            Assert.IsTrue(query.Messages.Single(x => x.Id == sent.Id).HasAttachments);

            foreach (var format in new[] { EmailContentFormat.Plain, EmailContentFormat.Html, EmailContentFormat.Markdown })
            {
                var read = Success(await provider.GetMessagesAsync(account, [sent.Id], format, false));
                Assert.IsNull(read.Single().Error);
                StringAssert.Contains(read.Single().Message!.Body.Content, marker.Trim('[', ']'));
            }

            var full = Success(await provider.GetMessagesAsync(account, [sent.Id], EmailContentFormat.Plain, false)).Single().Message!;
            var attachment = full.Attachments.Single();
            var downloaded = Success(await provider.GetAttachmentAsync(account, sent.Id, attachment.AttachmentId));
            downloadedPath = downloaded.Path;
            Assert.AreEqual(marker, await File.ReadAllTextAsync(downloaded.Path));

            var thread = Success(await provider.GetThreadsAsync(account, [sent.ThreadId], EmailContentFormat.Markdown, false));
            Assert.IsNull(thread.Single().Error);

            var reply = Success(await provider.SendAsync(account, new OutgoingEmail
            {
                To = [account],
                Subject = $"Re: {marker}",
                Body = new(EmailComposeFormat.Html, $"<p>Reply {marker}</p>"),
                ReplyToMessageId = sent.Id,
            }));
            created.Add(reply.Id);
            Assert.AreEqual(sent.ThreadId, reply.ThreadId, "A reply must stay in the original thread.");

            var forwarded = Success(await provider.ForwardAsync(
                account, sent.Id, [account], [], [], new(EmailComposeFormat.Markdown, "Forward prefix")));
            created.Add(forwarded.Id);
            var forwardedRead = Success(await provider.GetMessagesAsync(account, [forwarded.Id], EmailContentFormat.Plain, false));
            Assert.HasCount(1, forwardedRead.Single().Message!.Attachments);

            var draft = Success(await provider.CreateDraftAsync(account, new EmailDraftInput
            {
                Subject = marker,
                ReplyToMessageId = sent.Id,
            }));
            var updatedDraft = Success(await provider.UpdateDraftAsync(account, draft.DraftId, new EmailDraftPatch
            {
                To = [account],
                Body = new(EmailComposeFormat.Markdown, $"Draft {marker}"),
            }));
            Assert.AreEqual(draft.DraftId, updatedDraft.DraftId);
            var drafts = Success(await provider.ListDraftsAsync(account, 50, null)).Drafts;
            Assert.AreEqual(1, drafts.Count(x => x.DraftId == draft.DraftId), "Updating a draft must not leave the old version behind.");
            var draftSent = Success(await provider.SendDraftAsync(account, draft.DraftId));
            created.Add(draftSent.Id);
            Assert.IsFalse(Success(await provider.ListDraftsAsync(account, 50, null)).Drafts.Any(x => x.DraftId == draft.DraftId));

            var labels = Success(await provider.ListLabelsAsync(account));
            Assert.IsTrue(labels.Any(x => x.Id == "INBOX"));
            var patch = new EmailMessagePatch { Unread = true, Starred = true };
            if (gmail)
            {
                var label = Success(await provider.CreateLabelAsync(account, "ProductivityMcp-Test"));
                Assert.IsTrue(Success(await provider.ListLabelsAsync(account)).Any(x => x.Id == label.Id));
                patch = patch with { Important = true, AddLabels = [label.Name] };
            }

            Assert.IsTrue(Success(await provider.UpdateAsync(account, [sent.Id], patch)).Single().Success);
            var updated = Success(await provider.GetMessagesAsync(account, [sent.Id], EmailContentFormat.Plain, false)).Single().Message!;
            Assert.IsTrue(updated.Unread);
            Assert.IsTrue(updated.Starred);
            if (gmail) Assert.IsTrue(updated.Important);
            Assert.IsTrue(Success(await provider.ArchiveAsync(account, [sent.Id])).Single().Success);
        }
        finally
        {
            if (created.Count > 0) await provider.TrashAsync(account, created);
            File.Delete(attachmentPath);
            if (downloadedPath is not null) File.Delete(downloadedPath);
        }
    }

    public static T Success<T>(OperationResult<T> result) => result switch
    {
        OperationResult<T>.Success success => success.Value,
        OperationResult<T>.Failure failure => throw new AssertFailedException(
            $"{failure.Error.Code}: {failure.Error.Message}"),
        _ => throw new AssertFailedException("Unexpected operation result."),
    };
}
