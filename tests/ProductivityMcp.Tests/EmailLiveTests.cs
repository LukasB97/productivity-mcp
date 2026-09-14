using ProductivityMcp.App.Services;
using ProductivityMcp.Core;
using ProductivityMcp.Email;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class EmailLiveTests
{
    [TestMethod]
    [TestCategory("Live")]
    public async System.Threading.Tasks.Task Gmail_EndToEndAgainstOwnAccount()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_LIVE_TESTS"), "1", StringComparison.Ordinal))
            Assert.Inconclusive("Set PRODUCTIVITY_MCP_LIVE_TESTS=1 to run the opt-in Gmail test.");

        var options = GoogleOptions.FromEnvironment();
        var setup = new GoogleSetupService(options);
        var catalog = new GoogleAccountCatalog(options);
        var registration = catalog.List().Single(x =>
            string.Equals(x.Email, "lukas.brueckner97@gmail.com", StringComparison.OrdinalIgnoreCase));
        if (!registration.EmailEnabled || !catalog.HasEmailToken(registration.Key))
        {
            var authorization = await setup.EnableEmailAsync(registration.Key);
            _ = Success(authorization);
        }

        var provider = new MultiGoogleEmailProvider(options, new EmailContentService());
        const string account = "lukas.brueckner97@gmail.com";
        var marker = $"[productivity-mcp-live-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}]";
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
                Body = new(EmailContentFormat.Markdown, $"# Live test\n\n{marker}"),
                Attachments = [new EmailAttachmentInput { Path = attachmentPath, Name = "live-test.txt" }],
            }));
            created.Add(sent.Id);

            var query = Success(await provider.QueryAsync(new EmailQuery
            {
                Account = account,
                Filter = new EmailFilter { Subject = marker },
                PageSize = 10,
            }));
            Assert.IsTrue(query.Complete);
            Assert.IsTrue(query.Messages.Any(x => x.Id == sent.Id));

            foreach (var format in new[] { EmailContentFormat.Plain, EmailContentFormat.Html, EmailContentFormat.Markdown, EmailContentFormat.Image })
            {
                var read = Success(await provider.GetMessagesAsync(account, [sent.Id], format, false));
                Assert.IsNull(read.Single().Error);
                Assert.IsNotNull(read.Single().Message);
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
                Body = new(EmailContentFormat.Html, $"<p>Reply {marker}</p>"),
                ReplyToMessageId = sent.Id,
            }));
            created.Add(reply.Id);

            var forwarded = Success(await provider.ForwardAsync(
                account, sent.Id, [account], [], [], new(EmailContentFormat.Markdown, "Forward prefix")));
            created.Add(forwarded.Id);

            var draft = Success(await provider.CreateDraftAsync(account, new EmailDraftInput { Subject = marker }));
            var updatedDraft = Success(await provider.UpdateDraftAsync(account, draft.DraftId, new EmailDraftPatch
            {
                To = [account],
                Body = new(EmailContentFormat.Markdown, $"Draft {marker}"),
            }));
            Assert.AreEqual(draft.DraftId, updatedDraft.DraftId);
            Assert.IsTrue(Success(await provider.ListDraftsAsync(account, 50, null)).Drafts.Any(x => x.DraftId == draft.DraftId));
            var draftSent = Success(await provider.SendDraftAsync(account, draft.DraftId));
            created.Add(draftSent.Id);

            var label = Success(await provider.CreateLabelAsync(account, "ProductivityMcp-Test"));
            Assert.IsTrue(Success(await provider.ListLabelsAsync(account)).Any(x => x.Id == label.Id));
            var changed = Success(await provider.UpdateAsync(account, [sent.Id], new EmailMessagePatch
            {
                Unread = true,
                Starred = true,
                Important = true,
                AddLabels = [label.Name],
            }));
            Assert.IsTrue(changed.Single().Success);
            Assert.IsTrue(Success(await provider.ArchiveAsync(account, [sent.Id])).Single().Success);
        }
        finally
        {
            if (created.Count > 0) await provider.TrashAsync(account, created);
            File.Delete(attachmentPath);
            if (downloadedPath is not null) File.Delete(downloadedPath);
        }
    }

    private static T Success<T>(OperationResult<T> result) => result switch
    {
        OperationResult<T>.Success success => success.Value,
        OperationResult<T>.Failure failure => throw new AssertFailedException(
            $"{failure.Error.Code}: {failure.Error.Message}"),
        _ => throw new AssertFailedException("Unexpected operation result."),
    };
}
