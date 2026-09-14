using System.Globalization;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using MimeKit;
using ProductivityMcp.Core;
using ProductivityMcp.Email;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace ProductivityMcp.Providers.Google;

internal sealed class GoogleEmailProvider(
    string account,
    GoogleServiceFactory serviceFactory,
    GoogleOptions options,
    EmailContentService content)
{
    public async Task<IReadOnlyList<EmailSummary>> QueryAsync(EmailQuery query, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var request = service.Users.Messages.List("me");
        request.Q = GoogleEmailQuery.Build(query.Filter);
        request.MaxResults = pageSize;
        request.PageToken = cursor;
        request.IncludeSpamTrash = query.IncludeSpamTrash;
        var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<EmailSummary>();
        foreach (var item in page.Messages ?? [])
        {
            var get = service.Users.Messages.Get("me", item.Id);
            get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            var message = await get.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            result.Add(ToSummary(message));
        }
        NextCursor = page.NextPageToken;
        return result;
    }

    public string? NextCursor { get; private set; }

    public async Task<IReadOnlyList<EmailMessageReadResult>> GetMessagesAsync(IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<EmailMessageReadResult>();
        foreach (var id in ids)
        {
            try { result.Add(new(id, await GetMessageAsync(service, id, format, loadRemoteImages, cancellationToken).ConfigureAwait(false), null)); }
            catch (Exception exception) when (GoogleOperation.Translate(exception) is { } error) { result.Add(new(id, null, error)); }
        }
        return result;
    }

    public async Task<IReadOnlyList<EmailThreadReadResult>> GetThreadsAsync(IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<EmailThreadReadResult>();
        foreach (var id in ids)
        {
            try
            {
                var request = service.Users.Threads.Get("me", id);
                request.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Minimal;
                var thread = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                var messages = new List<EmailMessage>();
                foreach (var item in thread.Messages ?? []) messages.Add(await GetMessageAsync(service, item.Id, format, loadRemoteImages, cancellationToken).ConfigureAwait(false));
                result.Add(new(id, new EmailThread(id, account, messages.OrderBy(x => x.Date).ToArray()), null));
            }
            catch (Exception exception) when (GoogleOperation.Translate(exception) is { } error) { result.Add(new(id, null, error)); }
        }
        return result;
    }

    public async Task<EmailMessage> SendAsync(OutgoingEmail value, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var mime = EmailContentService.Compose(account, value);
        string? threadId = null;
        if (!string.IsNullOrWhiteSpace(value.ReplyToMessageId))
            threadId = await ApplyReplyAsync(service, mime, value.ReplyToMessageId, cancellationToken).ConfigureAwait(false);
        var sent = await service.Users.Messages.Send(new GmailMessage { Raw = EmailContentService.ToRaw(mime), ThreadId = threadId }, "me").ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return await GetSentMessageOrReceiptAsync(service, sent, mime, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EmailMessage> ForwardAsync(string messageId, IReadOnlyList<string> to, IReadOnlyList<string> cc, IReadOnlyList<string> bcc, EmailBody? prefix, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var raw = await GetRawAsync(service, messageId, cancellationToken).ConfigureAwait(false);
        var source = EmailContentService.Parse(raw.Raw);
        var header = $"---------- Forwarded message ----------\nFrom: {source.From}\nDate: {source.Date}\nSubject: {source.Subject}\nTo: {source.To}\n\n";
        var sourceSubject = source.Subject ?? "";
        var prefixHtml = "";
        if (prefix is not null)
        {
            var prefixMessage = EmailContentService.Compose(account, new OutgoingEmail
            {
                To = to,
                Body = prefix,
            });
            prefixHtml = prefixMessage.HtmlBody ?? "";
        }
        var originalHtml = source.HtmlBody ?? $"<pre>{System.Net.WebUtility.HtmlEncode(source.TextBody ?? "")}</pre>";
        var outgoing = new OutgoingEmail { To = to, Cc = cc, Bcc = bcc, Subject = sourceSubject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? sourceSubject : $"Fwd: {sourceSubject}", Body = new EmailBody(EmailContentFormat.Html, prefixHtml + "<br><pre>" + System.Net.WebUtility.HtmlEncode(header) + "</pre>" + originalHtml) };
        var mime = EmailContentService.Compose(account, outgoing);
        var builder = new BodyBuilder { HtmlBody = mime.HtmlBody, TextBody = mime.TextBody };
        EmailContentService.AddExistingParts(builder, source, includeAttachments: true);
        mime.Body = builder.ToMessageBody();
        var sent = await service.Users.Messages.Send(new GmailMessage { Raw = EmailContentService.ToRaw(mime) }, "me").ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return await GetSentMessageOrReceiptAsync(service, sent, mime, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<EmailMutationResult>> UpdateAsync(IReadOnlyList<string> ids, EmailMessagePatch patch, CancellationToken cancellationToken) => ModifyAsync(ids, patch, cancellationToken);
    public Task<IReadOnlyList<EmailMutationResult>> ArchiveAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken) => ModifyAsync(ids, new EmailMessagePatch { RemoveLabels = ["INBOX"] }, cancellationToken);

    public async Task<IReadOnlyList<EmailMutationResult>> TrashAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<EmailMutationResult>();
        foreach (var id in ids)
        {
            try { await service.Users.Messages.Trash("me", id).ExecuteAsync(cancellationToken).ConfigureAwait(false); results.Add(new(id, true)); }
            catch (Exception e) when (GoogleOperation.Translate(e) is { } error) { results.Add(new(id, false, error)); }
        }
        return results;
    }

    public async Task<EmailAttachmentFile> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var get = service.Users.Messages.Get("me", messageId);
        get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
        var full = await get.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var part = Flatten(full.Payload).FirstOrDefault(x => x.PartId == attachmentId && x.Body?.AttachmentId is not null)
            ?? throw new KeyNotFoundException("Email attachment was not found.");
        var value = await service.Users.Messages.Attachments.Get("me", messageId, part.Body.AttachmentId).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var bytes = Decode(value.Data);
        var directory = Path.Combine(Path.GetDirectoryName(options.AccountsPath)!, "downloads");
        Directory.CreateDirectory(directory);
        var safeName = string.IsNullOrWhiteSpace(part.Filename) ? "attachment" : Path.GetFileName(part.Filename);
        var path = UniquePath(directory, safeName);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return new EmailAttachmentFile(attachmentId, safeName, part.MimeType ?? "application/octet-stream", bytes.LongLength, path);
    }

    public async Task<EmailDraftPage> ListDraftsAsync(int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var request = service.Users.Drafts.List("me"); request.MaxResults = pageSize; request.PageToken = cursor;
        var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var drafts = new List<EmailDraft>();
        foreach (var item in page.Drafts ?? [])
        {
            var draft = await service.Users.Drafts.Get("me", item.Id).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var mime = EmailContentService.Parse((await GetRawAsync(service, draft.Message.Id, cancellationToken).ConfigureAwait(false)).Raw);
            drafts.Add(ToDraft(item.Id, draft.Message.Id, mime));
        }
        return new EmailDraftPage(drafts, page.NextPageToken);
    }

    public async Task<EmailDraft> CreateDraftAsync(EmailDraftInput value, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var mime = EmailContentService.ComposeDraft(account, value);
        var threadId = string.IsNullOrWhiteSpace(value.ReplyToMessageId)
            ? null
            : await ApplyReplyAsync(service, mime, value.ReplyToMessageId, cancellationToken).ConfigureAwait(false);
        var draft = await service.Users.Drafts.Create(new Draft { Message = new GmailMessage { Raw = EmailContentService.ToRaw(mime), ThreadId = threadId } }, "me").ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return ToDraft(draft.Id, draft.Message.Id, mime);
    }

    public async Task<EmailDraft> UpdateDraftAsync(string draftId, EmailDraftPatch patch, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var current = await service.Users.Drafts.Get("me", draftId).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var raw = await GetRawAsync(service, current.Message.Id, cancellationToken).ConfigureAwait(false);
        var mime = EmailContentService.Parse(raw.Raw);
        EmailContentService.ApplyDraftPatch(mime, patch);
        var threadId = current.Message.ThreadId;
        if (patch.HasReplyToMessageId)
            threadId = string.IsNullOrWhiteSpace(patch.ReplyToMessageId)
                ? ClearReply(mime)
                : await ApplyReplyAsync(service, mime, patch.ReplyToMessageId, cancellationToken).ConfigureAwait(false);
        var draft = await service.Users.Drafts.Update(new Draft { Id = draftId, Message = new GmailMessage { Raw = EmailContentService.ToRaw(mime), ThreadId = threadId } }, "me", draftId).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return ToDraft(draft.Id, draft.Message.Id, mime);
    }

    public async Task<EmailMessage> SendDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var draft = await service.Users.Drafts.Get("me", draftId).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var raw = await GetRawAsync(service, draft.Message.Id, cancellationToken).ConfigureAwait(false);
        var mime = EmailContentService.Parse(raw.Raw);
        if (!mime.To.Mailboxes.Any() && !mime.Cc.Mailboxes.Any() && !mime.Bcc.Mailboxes.Any())
            throw new System.ComponentModel.DataAnnotations.ValidationException("The draft must contain at least one recipient before it can be sent.");
        if (string.IsNullOrWhiteSpace(mime.TextBody) && string.IsNullOrWhiteSpace(mime.HtmlBody))
            throw new System.ComponentModel.DataAnnotations.ValidationException("The draft must contain a body before it can be sent.");
        var sent = await service.Users.Drafts.Send(new Draft { Id = draftId }, "me").ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return await GetSentMessageOrReceiptAsync(service, sent, mime, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EmailLabel>> ListLabelsAsync(CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var labels = await service.Users.Labels.List("me").ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<EmailLabel>();
        foreach (var item in labels.Labels ?? [])
        {
            var label = await service.Users.Labels.Get("me", item.Id).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            result.Add(ToLabel(label));
        }
        return result;
    }

    public async Task<EmailLabel> CreateLabelAsync(string name, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var existing = (await service.Users.Labels.List("me").ExecuteAsync(cancellationToken).ConfigureAwait(false)).Labels?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return ToLabel(await service.Users.Labels.Get("me", existing.Id).ExecuteAsync(cancellationToken).ConfigureAwait(false));
        return ToLabel(await service.Users.Labels.Create(new Label { Name = name, LabelListVisibility = "labelShow", MessageListVisibility = "show" }, "me").ExecuteAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<EmailMutationResult>> ModifyAsync(IReadOnlyList<string> ids, EmailMessagePatch patch, CancellationToken cancellationToken)
    {
        using var service = await serviceFactory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
        var labels = await ResolveLabelsAsync(service, patch.AddLabels.Concat(patch.RemoveLabels), cancellationToken).ConfigureAwait(false);
        var add = patch.AddLabels.Select(x => labels[x]).ToList(); var remove = patch.RemoveLabels.Select(x => labels[x]).ToList();
        SetSystem(add, remove, "UNREAD", patch.Unread); SetSystem(add, remove, "STARRED", patch.Starred); SetSystem(add, remove, "IMPORTANT", patch.Important);
        var results = new List<EmailMutationResult>();
        foreach (var id in ids)
        {
            try { await service.Users.Messages.Modify(new ModifyMessageRequest { AddLabelIds = add, RemoveLabelIds = remove }, "me", id).ExecuteAsync(cancellationToken).ConfigureAwait(false); results.Add(new(id, true)); }
            catch (Exception e) when (GoogleOperation.Translate(e) is { } error) { results.Add(new(id, false, error)); }
        }
        return results;
    }

    private async Task<EmailMessage> GetMessageAsync(GmailService service, string id, EmailContentFormat format, bool loadRemoteImages, CancellationToken cancellationToken)
    {
        var get = service.Users.Messages.Get("me", id);
        get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
        var full = await get.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var raw = await GetRawAsync(service, id, cancellationToken).ConfigureAwait(false);
        var mime = EmailContentService.Parse(raw.Raw);
        var converted = await content.ConvertAsync(mime, format, loadRemoteImages, cancellationToken).ConfigureAwait(false);
        var attachments = Flatten(full.Payload).Where(x => x.Body?.AttachmentId is not null).Select(x => new EmailAttachment(x.PartId, x.Filename ?? "attachment", x.MimeType ?? "application/octet-stream", x.Body.Size ?? 0)).ToArray();
        return new EmailMessage(id, account, full.ThreadId, Date(full), Address(mime.From.Mailboxes.FirstOrDefault()), mime.To.Mailboxes.Select(Address).ToArray(), mime.Cc.Mailboxes.Select(Address).ToArray(), mime.Bcc.Mailboxes.Select(Address).ToArray(), mime.Subject ?? "", converted.Body, attachments, Has(full, "UNREAD"), Has(full, "STARRED"), Has(full, "IMPORTANT"), full.LabelIds?.ToArray() ?? [], converted.Images);
    }

    private static async Task<GmailMessage> GetRawAsync(GmailService service, string id, CancellationToken token)
    { var request = service.Users.Messages.Get("me", id); request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw; return await request.ExecuteAsync(token).ConfigureAwait(false); }

    private async Task<EmailMessage> GetSentMessageOrReceiptAsync(
        GmailService service, GmailMessage sent, MimeMessage mime, CancellationToken cancellationToken)
    {
        try
        {
            return await GetMessageAsync(service, sent.Id, EmailContentFormat.Plain, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && GoogleOperation.Translate(exception) is not null)
        {
            return new EmailMessage(
                sent.Id, account, sent.ThreadId, mime.Date == default ? DateTimeOffset.UtcNow : mime.Date,
                Address(mime.From.Mailboxes.FirstOrDefault()), mime.To.Mailboxes.Select(Address).ToArray(),
                mime.Cc.Mailboxes.Select(Address).ToArray(), mime.Bcc.Mailboxes.Select(Address).ToArray(),
                mime.Subject ?? "", new EmailBody(EmailContentFormat.Plain, mime.TextBody ?? HtmlToText(mime.HtmlBody ?? "")),
                [], false, false, false, sent.LabelIds?.ToArray() ?? [], []);
        }
    }

    private static async Task<string?> ApplyReplyAsync(
        GmailService service, MimeMessage mime, string messageId, CancellationToken cancellationToken)
    {
        var source = await GetRawAsync(service, messageId, cancellationToken).ConfigureAwait(false);
        var parent = EmailContentService.Parse(source.Raw);
        mime.InReplyTo = null;
        mime.References.Clear();
        foreach (var reference in parent.References) mime.References.Add(reference);
        if (!string.IsNullOrWhiteSpace(parent.MessageId))
        {
            mime.InReplyTo = parent.MessageId;
            if (!mime.References.Contains(parent.MessageId)) mime.References.Add(parent.MessageId);
        }
        return source.ThreadId;
    }

    private static string? ClearReply(MimeMessage mime)
    {
        mime.InReplyTo = null;
        mime.References.Clear();
        return null;
    }
    private EmailSummary ToSummary(GmailMessage x) => new(x.Id, account, x.ThreadId, Date(x), ParseAddress(Header(x, "From")), Header(x, "Subject"), x.Snippet ?? "", Has(x, "UNREAD"), Flatten(x.Payload).Any(p => p.Body?.AttachmentId is not null));
    private EmailDraft ToDraft(string draftId, string messageId, MimeMessage mime) => new(draftId, messageId, account, mime.To.Mailboxes.Select(x => x.Address).ToArray(), mime.Cc.Mailboxes.Select(x => x.Address).ToArray(), mime.Bcc.Mailboxes.Select(x => x.Address).ToArray(), mime.Subject ?? "", (mime.TextBody ?? HtmlToText(mime.HtmlBody ?? "")).Take(200).Aggregate("", (a, c) => a + c), mime.Attachments.Any());
    private static EmailLabel ToLabel(Label x) => new(x.Id, x.Name, x.MessagesTotal, x.MessagesUnread, x.ThreadsTotal, x.ThreadsUnread);
    private static IEnumerable<global::Google.Apis.Gmail.v1.Data.MessagePart> Flatten(global::Google.Apis.Gmail.v1.Data.MessagePart? part) { if (part is null) yield break; yield return part; foreach (var child in part.Parts ?? []) foreach (var value in Flatten(child)) yield return value; }
    private static string Header(GmailMessage message, string name) => message.Payload?.Headers?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
    private static DateTimeOffset Date(GmailMessage message) => DateTimeOffset.FromUnixTimeMilliseconds(message.InternalDate ?? 0);
    private static bool Has(GmailMessage message, string label) => message.LabelIds?.Contains(label, StringComparer.Ordinal) is true;
    private static EmailAddress ParseAddress(string value) { try { return Address(MailboxAddress.Parse(value)); } catch { return new(value); } }
    private static EmailAddress Address(MailboxAddress? value) => value is null ? new("") : new(value.Address, string.IsNullOrWhiteSpace(value.Name) ? null : value.Name);
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
    private static string UniquePath(string directory, string name) { var path = Path.Combine(directory, name); if (!File.Exists(path)) return path; return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}-{Guid.NewGuid():N}{Path.GetExtension(name)}"); }
    private static string HtmlToText(string value) => System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " ").Trim();
    private static void SetSystem(List<string> add, List<string> remove, string label, bool? value) { if (value is true) add.Add(label); if (value is false) remove.Add(label); }
    private static async Task<Dictionary<string, string>> ResolveLabelsAsync(GmailService service, IEnumerable<string> names, CancellationToken token)
    {
        var requested = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var labels = (await service.Users.Labels.List("me").ExecuteAsync(token).ConfigureAwait(false)).Labels ?? [];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in requested)
        {
            var found = labels.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Id, name, StringComparison.Ordinal));
            if (found is null) throw new KeyNotFoundException($"Email label '{name}' was not found.");
            result[name] = found.Id;
        }
        return result;
    }
}
