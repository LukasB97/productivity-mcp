using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Search;
using MimeKit;
using ProductivityMcp.Core;
using ProductivityMcp.Email;
using Task = System.Threading.Tasks.Task;

namespace ProductivityMcp.Providers.Imap;

/// <summary>Email operations for one IMAP/SMTP account. Each call uses its own connection.</summary>
internal sealed class ImapEmailAccount(MailAccount account, MailAccountCatalog catalog, EmailContentService content)
{
    private const MessageSummaryItems BaseItems = MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate
        | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.BodyStructure
        | MessageSummaryItems.PreviewText | MessageSummaryItems.References | MessageSummaryItems.Size;
    private const MessageSummaryItems GmailItems = MessageSummaryItems.GMailMessageId
        | MessageSummaryItems.GMailThreadId | MessageSummaryItems.GMailLabels;
    private const int MaxThreadMessages = 100;

    private static readonly Dictionary<string, string> GmailSystemLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INBOX"] = "\\Inbox",
        ["IMPORTANT"] = "\\Important",
        ["SENT"] = "\\Sent",
        ["DRAFT"] = "\\Draft",
        ["STARRED"] = "\\Starred",
        ["TRASH"] = "\\Trash",
        ["SPAM"] = "\\Spam",
    };
    private static readonly (SpecialFolder Kind, string Id)[] SystemFolders =
    [
        (SpecialFolder.Sent, "SENT"), (SpecialFolder.Drafts, "DRAFT"), (SpecialFolder.Trash, "TRASH"),
        (SpecialFolder.Junk, "SPAM"), (SpecialFolder.Flagged, "STARRED"), (SpecialFolder.Important, "IMPORTANT"),
        (SpecialFolder.Archive, "ARCHIVE"), (SpecialFolder.All, "ALL"),
    ];

    public string Email => account.Email;

    // ---------------------------------------------------------------- query

    public async Task<(IReadOnlyList<EmailSummary> Messages, string? Cursor)> QueryAsync(
        EmailQuery query, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (search, scope) = session.IsGmail ? GmailPlan(query) : ImapSearch.Build(query.Filter);
        var folders = session.IsGmail
            ? await GmailQueryFoldersAsync(session, query, cancellationToken).ConfigureAwait(false)
            : await GenericQueryFoldersAsync(session, query, scope, cancellationToken).ConfigureAwait(false);
        var state = QueryCursor.Decode(cursor);
        var request = FetchRequest(session);
        var pools = new List<FolderPool>();

        foreach (var folder in folders)
        {
            state.Folders.TryGetValue(folder.FullName, out var saved);
            if (saved is { Done: true }) continue;
            await ImapSession.OpenAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (saved is not null && saved.Validity != folder.UidValidity)
                throw new ValidationException("cursor expired because the mailbox changed; run the query again without a cursor.");
            var uids = (await folder.SearchAsync(search, cancellationToken).ConfigureAwait(false))
                .Where(uid => saved is null || uid.Id < saved.Below)
                .OrderByDescending(uid => uid.Id)
                .ToArray();
            var matched = new List<IMessageSummary>();
            var scanned = 0;
            while (matched.Count < pageSize && scanned < uids.Length)
            {
                var chunk = uids.Skip(scanned).Take(pageSize).ToArray();
                scanned += chunk.Length;
                var summaries = await folder.FetchAsync(chunk, request, cancellationToken).ConfigureAwait(false);
                matched.AddRange(summaries.Where(scope.Matches).OrderByDescending(summary => summary.UniqueId.Id));
            }

            pools.Add(new FolderPool(folder, matched, scanned >= uids.Length, saved?.Below));
        }

        // Merge per-folder lists by date while consuming each folder strictly in UID order, so the
        // cursor (an exclusive UID bound per folder) never skips a message.
        var page = new List<(IMailFolder Folder, IMessageSummary Summary)>();
        while (page.Count < pageSize)
        {
            var next = pools.Where(pool => pool.Taken < pool.Matched.Count)
                .MaxBy(pool => Date(pool.Matched[pool.Taken]));
            if (next is null) break;
            page.Add((next.Folder, next.Matched[next.Taken]));
            next.Taken++;
        }

        var nextState = new QueryCursor(new Dictionary<string, FolderCursor>(state.Folders, StringComparer.Ordinal));
        foreach (var pool in pools)
        {
            var exhausted = pool.ScannedAll && pool.Taken == pool.Matched.Count;
            var below = pool.Taken > 0 ? pool.Matched[pool.Taken - 1].UniqueId.Id : pool.PreviousBelow ?? uint.MaxValue;
            nextState.Folders[pool.Folder.FullName] = new FolderCursor(pool.Folder.UidValidity, below, exhausted);
        }

        var messages = page.Select(item => ToSummary(session, item.Folder, item.Summary)).ToArray();
        return (messages, nextState.Folders.Values.All(folder => folder.Done) ? null : nextState.Encode());
    }

    private static (SearchQuery, ImapSearchScope) GmailPlan(EmailQuery query)
    {
        var raw = GmailSearchQuery.Build(query.Filter);
        return (raw.Length == 0 ? SearchQuery.All : SearchQuery.GMailRawSearch(raw), new(null, null, null, null, null));
    }

    private static async Task<IReadOnlyList<IMailFolder>> GmailQueryFoldersAsync(
        ImapSession session, EmailQuery query, CancellationToken cancellationToken)
    {
        // Gmail's "All Mail" excludes spam and trash, matching the Gmail API default.
        var folders = new List<IMailFolder> { await RequiredSpecialAsync(session, SpecialFolder.All, cancellationToken).ConfigureAwait(false) };
        if (query.IncludeSpamTrash || ReferencesSpamOrTrash(query.Filter))
        {
            foreach (var kind in new[] { SpecialFolder.Trash, SpecialFolder.Junk })
                if (await session.SpecialAsync(kind, cancellationToken).ConfigureAwait(false) is { } folder) folders.Add(folder);
        }

        return folders;
    }

    private static async Task<IReadOnlyList<IMailFolder>> GenericQueryFoldersAsync(
        ImapSession session, EmailQuery query, ImapSearchScope scope, CancellationToken cancellationToken)
    {
        if (scope.Mailbox is { } mailbox)
        {
            var folder = mailbox switch
            {
                EmailMailbox.Inbox => session.Client.Inbox,
                EmailMailbox.Sent => await session.SpecialAsync(SpecialFolder.Sent, cancellationToken).ConfigureAwait(false),
                EmailMailbox.Drafts => await session.SpecialAsync(SpecialFolder.Drafts, cancellationToken).ConfigureAwait(false),
                EmailMailbox.Spam => await session.SpecialAsync(SpecialFolder.Junk, cancellationToken).ConfigureAwait(false),
                EmailMailbox.Trash => await session.SpecialAsync(SpecialFolder.Trash, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
            return folder is null ? [] : [folder];
        }

        if (scope.Folders is { Count: > 0 } names)
        {
            var resolved = new List<IMailFolder>();
            foreach (var name in names) resolved.Add(await ResolveFolderAsync(session, name, cancellationToken).ConfigureAwait(false));
            return resolved.DistinctBy(folder => folder.FullName).ToArray();
        }

        var excluded = new List<string>();
        if (!query.IncludeSpamTrash)
        {
            foreach (var kind in new[] { SpecialFolder.Trash, SpecialFolder.Junk })
                if (await session.SpecialAsync(kind, cancellationToken).ConfigureAwait(false) is { } folder) excluded.Add(folder.FullName);
        }

        return (await session.FoldersAsync(cancellationToken).ConfigureAwait(false))
            .Where(folder => !excluded.Contains(folder.FullName, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static bool ReferencesSpamOrTrash(EmailFilter? filter) =>
        filter is not null && (filter.Mailbox is EmailMailbox.Spam or EmailMailbox.Trash
            || (filter.And ?? []).Concat(filter.Or ?? []).Append(filter.Not).Any(ReferencesSpamOrTrash));

    // ---------------------------------------------------------------- read

    public async Task<IReadOnlyList<EmailMessageReadResult>> GetMessagesAsync(
        IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<EmailMessageReadResult>();
        foreach (var id in ids)
        {
            try
            {
                var location = await LocateAsync(session, id, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
                results.Add(new(id, await ReadAsync(session, location, format, loadRemoteImages, cancellationToken).ConfigureAwait(false), null));
            }
            catch (Exception exception) when (MailOperation.Translate(exception) is { } error)
            {
                results.Add(new(id, null, error));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<EmailThreadReadResult>> GetThreadsAsync(
        IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<EmailThreadReadResult>();
        foreach (var id in ids)
        {
            try
            {
                var locations = await LocateThreadAsync(session, id, cancellationToken).ConfigureAwait(false);
                var messages = new List<EmailMessage>();
                foreach (var location in locations)
                    messages.Add(await ReadAsync(session, location, format, loadRemoteImages, cancellationToken).ConfigureAwait(false));
                results.Add(new(id, new EmailThread(id, account.Email, messages.OrderBy(message => message.Date).ToArray()), null));
            }
            catch (Exception exception) when (MailOperation.Translate(exception) is { } error)
            {
                results.Add(new(id, null, error));
            }
        }

        return results;
    }

    public async Task<EmailAttachmentFile> GetAttachmentAsync(
        string messageId, string attachmentId, string downloadsDirectory, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (folder, uid) = await LocateAsync(session, messageId, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        var summary = (await folder.FetchAsync([uid], new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure), cancellationToken).ConfigureAwait(false)).Single();
        var part = summary.BodyParts.FirstOrDefault(item => item.PartSpecifier == attachmentId)
            ?? throw new KeyNotFoundException("Email attachment was not found.");
        // Partial BODY[n] fetches are not decoded consistently across servers; locating the part in
        // the parsed message by the same IMAP part path is reliable.
        var message = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        using (var iterator = new MimeIterator(message))
        {
            var found = false;
            while (!found && iterator.MoveNext())
            {
                if (iterator.PathSpecifier != attachmentId) continue;
                found = true;
                switch (iterator.Current)
                {
                    case MimePart mime when mime.Content is not null:
                        await mime.Content.DecodeToAsync(buffer, cancellationToken).ConfigureAwait(false);
                        break;
                    case MessagePart attached when attached.Message is not null:
                        await attached.Message.WriteToAsync(buffer, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new KeyNotFoundException("Email attachment was not found.");
                }
            }

            if (!found) throw new KeyNotFoundException("Email attachment was not found.");
        }

        Directory.CreateDirectory(downloadsDirectory);
        var safeName = string.IsNullOrWhiteSpace(part.FileName) ? "attachment" : Path.GetFileName(part.FileName);
        var path = UniquePath(downloadsDirectory, safeName);
        await File.WriteAllBytesAsync(path, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        return new EmailAttachmentFile(attachmentId, safeName, part.ContentType.MimeType, buffer.Length, path);
    }

    // ---------------------------------------------------------------- send

    public async Task<EmailMessage> SendAsync(OutgoingEmail value, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var mime = EmailContentService.Compose(account.Email, value);
        if (!string.IsNullOrWhiteSpace(value.ReplyToMessageId))
            await ApplyReplyAsync(session, mime, value.ReplyToMessageId, cancellationToken).ConfigureAwait(false);
        return await DeliverAsync(session, mime, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EmailMessage> ForwardAsync(
        string messageId, IReadOnlyList<string> to, IReadOnlyList<string> cc, IReadOnlyList<string> bcc,
        EmailComposeBody? prefix, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var (folder, uid) = await LocateAsync(session, messageId, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        var source = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
        var header = $"---------- Forwarded message ----------\nFrom: {source.From}\nDate: {source.Date}\nSubject: {source.Subject}\nTo: {source.To}\n\n";
        var sourceSubject = source.Subject ?? "";
        var prefixHtml = prefix is null
            ? ""
            : EmailContentService.Compose(account.Email, new OutgoingEmail { To = to, Body = prefix }).HtmlBody ?? "";
        var originalHtml = source.HtmlBody ?? $"<pre>{System.Net.WebUtility.HtmlEncode(source.TextBody ?? "")}</pre>";
        var outgoing = new OutgoingEmail
        {
            To = to,
            Cc = cc,
            Bcc = bcc,
            Subject = sourceSubject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? sourceSubject : $"Fwd: {sourceSubject}",
            Body = new EmailComposeBody(EmailComposeFormat.Html,
                prefixHtml + "<br><pre>" + System.Net.WebUtility.HtmlEncode(header) + "</pre>" + originalHtml),
        };
        var mime = EmailContentService.Compose(account.Email, outgoing);
        var builder = new BodyBuilder { HtmlBody = mime.HtmlBody, TextBody = mime.TextBody };
        EmailContentService.AddExistingParts(builder, source, includeAttachments: true);
        mime.Body = builder.ToMessageBody();
        return await DeliverAsync(session, mime, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmailMessage> DeliverAsync(ImapSession session, MimeMessage mime, CancellationToken cancellationToken)
    {
        mime.Date = DateTimeOffset.Now;
        var password = await catalog.ReadPasswordAsync(account, cancellationToken).ConfigureAwait(false);
        await ImapSession.SendAsync(account, password, mime, cancellationToken).ConfigureAwait(false);

        (IMailFolder Folder, UniqueId Uid)? stored = null;
        var sent = await session.SpecialAsync(SpecialFolder.Sent, cancellationToken).ConfigureAwait(false);
        if (session.IsGmail)
        {
            // Gmail files SMTP submissions into "Sent" itself; appending would duplicate them.
            stored = await FindByMessageIdAsync(session, sent, mime.MessageId, retries: 5, cancellationToken).ConfigureAwait(false);
        }
        else if (sent is not null)
        {
            await ImapSession.OpenAsync(sent, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            var uid = await sent.AppendAsync(new AppendRequest(mime, MessageFlags.Seen), cancellationToken).ConfigureAwait(false);
            stored = uid is { } appended
                ? (sent, appended)
                : await FindByMessageIdAsync(session, sent, mime.MessageId, retries: 0, cancellationToken).ConfigureAwait(false);
        }

        if (stored is { } location)
        {
            try
            {
                return await ReadAsync(session, location, EmailContentFormat.Plain, false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && MailOperation.Translate(exception) is not null)
            {
                // The message was sent. A failed read-back must not report the send as failed.
            }
        }

        return Receipt(mime);
    }

    private EmailMessage Receipt(MimeMessage mime) => new(
        MailIds.MessageIdHeaderPrefix + mime.MessageId, account.Email, "", mime.Date,
        Address(mime.From.Mailboxes.FirstOrDefault()), mime.To.Mailboxes.Select(Address).ToArray(),
        mime.Cc.Mailboxes.Select(Address).ToArray(), mime.Bcc.Mailboxes.Select(Address).ToArray(),
        mime.Subject ?? "", new EmailBody(EmailContentFormat.Plain, mime.TextBody ?? ""), [], false, false, false, [], []);

    private static async Task ApplyReplyAsync(ImapSession session, MimeMessage mime, string messageId, CancellationToken cancellationToken)
    {
        var (folder, uid) = await LocateAsync(session, messageId, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        var parent = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
        mime.InReplyTo = null;
        mime.References.Clear();
        foreach (var reference in parent.References) mime.References.Add(reference);
        if (!string.IsNullOrWhiteSpace(parent.MessageId))
        {
            mime.InReplyTo = parent.MessageId;
            if (!mime.References.Contains(parent.MessageId)) mime.References.Add(parent.MessageId);
        }
    }

    // ---------------------------------------------------------------- mutate

    public async Task<IReadOnlyList<EmailMutationResult>> UpdateAsync(
        IReadOnlyList<string> ids, EmailMessagePatch patch, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!session.IsGmail && (patch.Important is not null || patch.AddLabels.Count > 0 || patch.RemoveLabels.Count > 0))
            throw new UnsupportedFeatureException("Labels and importance are only available for Gmail accounts; this account uses folders.", "patch");

        var addFlags = MessageFlags.None;
        var removeFlags = MessageFlags.None;
        var addLabels = new List<string>();
        var removeLabels = new List<string>();
        SetFlag(ref addFlags, ref removeFlags, MessageFlags.Seen, patch.Unread is { } unread ? !unread : null);
        SetFlag(ref addFlags, ref removeFlags, MessageFlags.Flagged, patch.Starred);
        if (patch.Important is true) addLabels.Add("\\Important");
        if (patch.Important is false) removeLabels.Add("\\Important");
        if (session.IsGmail)
        {
            foreach (var name in patch.AddLabels)
                ApplyGmailLabel(await ResolveGmailLabelAsync(session, name, cancellationToken).ConfigureAwait(false), addLabels, ref addFlags, ref removeFlags, adding: true);
            foreach (var name in patch.RemoveLabels)
                ApplyGmailLabel(await ResolveGmailLabelAsync(session, name, cancellationToken).ConfigureAwait(false), removeLabels, ref addFlags, ref removeFlags, adding: false);
        }

        return await MutateAsync(session, ids, async (folder, uid) =>
        {
            if (addFlags != MessageFlags.None)
                await folder.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, addFlags) { Silent = true }, cancellationToken).ConfigureAwait(false);
            if (removeFlags != MessageFlags.None)
                await folder.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Remove, removeFlags) { Silent = true }, cancellationToken).ConfigureAwait(false);
            if (addLabels.Count > 0)
                await folder.StoreAsync(uid, new StoreLabelsRequest(StoreAction.Add, addLabels) { Silent = true }, cancellationToken).ConfigureAwait(false);
            if (removeLabels.Count > 0)
                await folder.StoreAsync(uid, new StoreLabelsRequest(StoreAction.Remove, removeLabels) { Silent = true }, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EmailMutationResult>> ArchiveAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (session.IsGmail)
        {
            return await MutateAsync(session, ids, (folder, uid) => folder.StoreAsync(
                uid, new StoreLabelsRequest(StoreAction.Remove, ["\\Inbox"]) { Silent = true }, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        IMailFolder? archive = null;
        return await MutateAsync(session, ids, async (folder, uid) =>
        {
            if (!string.Equals(folder.FullName, session.Client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)) return;
            archive ??= await session.SpecialAsync(SpecialFolder.Archive, cancellationToken).ConfigureAwait(false)
                ?? await CreateFolderAsync(session, "Archive", cancellationToken).ConfigureAwait(false);
            await folder.MoveToAsync(uid, archive, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EmailMutationResult>> TrashAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var trash = await session.SpecialAsync(SpecialFolder.Trash, cancellationToken).ConfigureAwait(false)
            ?? throw new UnsupportedFeatureException("This mailbox has no trash folder.");
        return await MutateAsync(session, ids, async (folder, uid) =>
        {
            if (string.Equals(folder.FullName, trash.FullName, StringComparison.OrdinalIgnoreCase)) return;
            await folder.MoveToAsync(uid, trash, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<EmailMutationResult>> MutateAsync(
        ImapSession session, IReadOnlyList<string> ids, Func<IMailFolder, UniqueId, Task> action, CancellationToken cancellationToken)
    {
        var results = new List<EmailMutationResult>();
        foreach (var id in ids)
        {
            try
            {
                var (folder, uid) = await LocateAsync(session, id, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
                await action(folder, uid).ConfigureAwait(false);
                results.Add(new(id, true));
            }
            catch (Exception exception) when (MailOperation.Translate(exception) is { } error)
            {
                results.Add(new(id, false, error));
            }
        }

        return results;
    }

    private static void SetFlag(ref MessageFlags add, ref MessageFlags remove, MessageFlags flag, bool? value)
    {
        if (value is true) add |= flag;
        if (value is false) remove |= flag;
    }

    private static void ApplyGmailLabel(string label, List<string> labels, ref MessageFlags add, ref MessageFlags remove, bool adding)
    {
        // Gmail exposes stars and read state as IMAP flags rather than labels.
        if (label == "\\Starred") SetFlag(ref add, ref remove, MessageFlags.Flagged, adding);
        else if (label == "UNREAD") SetFlag(ref add, ref remove, MessageFlags.Seen, !adding);
        else labels.Add(label);
    }

    private static async Task<string> ResolveGmailLabelAsync(ImapSession session, string name, CancellationToken cancellationToken)
    {
        if (string.Equals(name, "UNREAD", StringComparison.OrdinalIgnoreCase)) return "UNREAD";
        if (GmailSystemLabels.TryGetValue(name, out var system)) return system;
        var folder = (await session.FoldersAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.FullName, name, StringComparison.OrdinalIgnoreCase));
        return folder?.FullName ?? throw new KeyNotFoundException($"Email label '{name}' was not found.");
    }

    // ---------------------------------------------------------------- drafts

    public async Task<EmailDraftPage> ListDraftsAsync(int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var drafts = await DraftsFolderAsync(session, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        uint? below = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var parts = cursor.Split('.');
            if (parts.Length != 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var validity)
                || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bound))
                throw new ValidationException("cursor is invalid.");
            if (validity != drafts.UidValidity)
                throw new ValidationException("cursor expired because the drafts folder changed; list drafts again without a cursor.");
            below = bound;
        }

        var uids = (await drafts.SearchAsync(SearchQuery.NotDeleted, cancellationToken).ConfigureAwait(false))
            .Where(uid => below is null || uid.Id < below)
            .OrderByDescending(uid => uid.Id)
            .ToArray();
        var pageUids = uids.Take(pageSize).ToArray();
        var summaries = pageUids.Length == 0
            ? []
            : (await drafts.FetchAsync(pageUids, FetchRequest(session), cancellationToken).ConfigureAwait(false))
                .OrderByDescending(summary => summary.UniqueId.Id).ToArray();
        var next = uids.Length > pageUids.Length
            ? string.Create(CultureInfo.InvariantCulture, $"{drafts.UidValidity}.{pageUids[^1].Id}")
            : null;
        return new EmailDraftPage(summaries.Select(summary => ToDraft(session, drafts, summary)).ToArray(), next);
    }

    public async Task<EmailDraft> CreateDraftAsync(EmailDraftInput value, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var mime = EmailContentService.ComposeDraft(account.Email, value);
        if (!string.IsNullOrWhiteSpace(value.ReplyToMessageId))
            await ApplyReplyAsync(session, mime, value.ReplyToMessageId, cancellationToken).ConfigureAwait(false);
        var drafts = await DraftsFolderAsync(session, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
        return await StoreDraftAsync(session, drafts, mime, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EmailDraft> UpdateDraftAsync(string draftId, EmailDraftPatch patch, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var drafts = await DraftsFolderAsync(session, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
        var uid = await FindDraftAsync(drafts, draftId, cancellationToken).ConfigureAwait(false);
        var mime = await drafts.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
        EmailContentService.ApplyDraftPatch(mime, patch);
        if (patch.HasReplyToMessageId)
        {
            if (string.IsNullOrWhiteSpace(patch.ReplyToMessageId))
            {
                mime.InReplyTo = null;
                mime.References.Clear();
            }
            else
            {
                await ApplyReplyAsync(session, mime, patch.ReplyToMessageId, cancellationToken).ConfigureAwait(false);
                await ImapSession.OpenAsync(drafts, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            }
        }

        // IMAP messages are immutable: store the new version (same Message-ID, so the draft id is
        // stable) before removing the old one, so a failure never loses the draft.
        var stored = await StoreDraftAsync(session, drafts, mime, cancellationToken).ConfigureAwait(false);
        await DeleteDraftAsync(session, drafts, uid, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task<EmailMessage> SendDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var drafts = await DraftsFolderAsync(session, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
        var uid = await FindDraftAsync(drafts, draftId, cancellationToken).ConfigureAwait(false);
        var mime = await drafts.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
        if (!mime.To.Mailboxes.Any() && !mime.Cc.Mailboxes.Any() && !mime.Bcc.Mailboxes.Any())
            throw new ValidationException("The draft must contain at least one recipient before it can be sent.");
        if (string.IsNullOrWhiteSpace(mime.TextBody) && string.IsNullOrWhiteSpace(mime.HtmlBody))
            throw new ValidationException("The draft must contain a body before it can be sent.");

        mime.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(DomainOf(account.Email));
        var sent = await DeliverAsync(session, mime, cancellationToken).ConfigureAwait(false);
        await ImapSession.OpenAsync(drafts, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
        await DeleteDraftAsync(session, drafts, uid, cancellationToken).ConfigureAwait(false);
        return sent;
    }

    private async Task<EmailDraft> StoreDraftAsync(ImapSession session, IMailFolder drafts, MimeMessage mime, CancellationToken cancellationToken)
    {
        await ImapSession.OpenAsync(drafts, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
        var uid = await drafts.AppendAsync(new AppendRequest(mime, MessageFlags.Draft | MessageFlags.Seen), cancellationToken).ConfigureAwait(false)
            ?? await FindDraftAsync(drafts, mime.MessageId ?? "", cancellationToken).ConfigureAwait(false);
        var summary = (await drafts.FetchAsync([uid], FetchRequest(session), cancellationToken).ConfigureAwait(false)).Single();
        return ToDraft(session, drafts, summary);
    }

    private static async Task DeleteDraftAsync(ImapSession session, IMailFolder drafts, UniqueId uid, CancellationToken cancellationToken)
    {
        if (session.IsGmail)
        {
            // Expunging from Gmail's Drafts label alone can leave the message in "All Mail".
            // Moving it to Trash and expunging there deletes it permanently.
            var trash = await RequiredSpecialAsync(session, SpecialFolder.Trash, cancellationToken).ConfigureAwait(false);
            var moved = await drafts.MoveToAsync(uid, trash, cancellationToken).ConfigureAwait(false);
            if (moved is not { } trashed) return;
            await ImapSession.OpenAsync(trash, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            await trash.StoreAsync(trashed, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, cancellationToken).ConfigureAwait(false);
            await trash.ExpungeAsync([trashed], cancellationToken).ConfigureAwait(false);
            return;
        }

        await drafts.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, cancellationToken).ConfigureAwait(false);
        if (session.HasUidPlus) await drafts.ExpungeAsync([uid], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IMailFolder> DraftsFolderAsync(ImapSession session, FolderAccess access, CancellationToken cancellationToken)
    {
        var drafts = await session.SpecialAsync(SpecialFolder.Drafts, cancellationToken).ConfigureAwait(false)
            ?? throw new UnsupportedFeatureException("This mailbox has no drafts folder.");
        return await ImapSession.OpenAsync(drafts, access, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<UniqueId> FindDraftAsync(IMailFolder drafts, string draftId, CancellationToken cancellationToken)
    {
        var uids = await drafts.SearchAsync(
            SearchQuery.HeaderContains("Message-ID", draftId).And(SearchQuery.NotDeleted), cancellationToken).ConfigureAwait(false);
        return uids.Count == 0
            ? throw new KeyNotFoundException($"The email draft '{draftId}' was not found.")
            : uids.Max();
    }

    // ---------------------------------------------------------------- labels

    public async Task<IReadOnlyList<EmailLabel>> ListLabelsAsync(CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var system = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [session.Client.Inbox.FullName] = "INBOX" };
        foreach (var (kind, id) in SystemFolders)
            if (await session.SpecialAsync(kind, cancellationToken).ConfigureAwait(false) is { } folder) system.TryAdd(folder.FullName, id);

        var labels = new List<EmailLabel>();
        foreach (var folder in await session.FoldersAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = system.GetValueOrDefault(folder.FullName, folder.FullName);
            if (session.IsGmail && id == "ALL") continue;
            long? total = null, unread = null;
            try
            {
                await folder.StatusAsync(StatusItems.Count | StatusItems.Unread, cancellationToken).ConfigureAwait(false);
                total = folder.Count;
                unread = folder.Unread;
            }
            catch (Exception exception) when (exception is MailKit.Net.Imap.ImapCommandException)
            {
                // Some servers refuse STATUS for special folders; the label is still usable.
            }

            labels.Add(new EmailLabel(id, id, total, unread, null, null));
        }

        return labels;
    }

    public async Task<EmailLabel> CreateLabelAsync(string name, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existing = (await session.FoldersAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(folder => string.Equals(folder.FullName, name, StringComparison.OrdinalIgnoreCase));
        var folder = existing ?? await CreateFolderAsync(session, name, cancellationToken).ConfigureAwait(false);
        return new EmailLabel(folder.FullName, folder.FullName, existing is null ? 0 : null, existing is null ? 0 : null, null, null);
    }

    private static async Task<IMailFolder> CreateFolderAsync(ImapSession session, string name, CancellationToken cancellationToken)
    {
        var root = (session.Client.PersonalNamespaces.Count > 0 ? session.Client.GetFolder(session.Client.PersonalNamespaces[0]) : null)
            ?? throw new UnsupportedFeatureException("The mail server does not allow creating folders.");
        var parent = root;
        foreach (var segment in name.Split(root.DirectorySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var subfolders = await parent.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false);
            parent = subfolders.FirstOrDefault(folder => string.Equals(folder.Name, segment, StringComparison.OrdinalIgnoreCase))
                ?? await parent.CreateAsync(segment, true, cancellationToken).ConfigureAwait(false)
                ?? throw new UnsupportedFeatureException($"The mail server did not create the folder '{segment}'.");
        }

        return ReferenceEquals(parent, root) ? throw new ValidationException("label name must not be empty.") : parent;
    }

    // ---------------------------------------------------------------- lookup

    private static async Task<(IMailFolder Folder, UniqueId Uid)> LocateAsync(
        ImapSession session, string id, FolderAccess access, CancellationToken cancellationToken)
    {
        if (id.StartsWith(MailIds.MessageIdHeaderPrefix, StringComparison.Ordinal))
        {
            var header = id[MailIds.MessageIdHeaderPrefix.Length..];
            foreach (var folder in await SearchableFoldersAsync(session, includeTrash: true, cancellationToken).ConfigureAwait(false))
            {
                if (await FindByMessageIdAsync(session, folder, header, retries: 0, cancellationToken).ConfigureAwait(false) is { } found)
                {
                    await ImapSession.OpenAsync(found.Folder, access, cancellationToken).ConfigureAwait(false);
                    return found;
                }
            }

            throw MailIds.NotFound(id);
        }

        if (session.IsGmail)
        {
            var value = MailIds.ParseGmail(id);
            foreach (var kind in new[] { SpecialFolder.All, SpecialFolder.Trash, SpecialFolder.Junk })
            {
                if (await session.SpecialAsync(kind, cancellationToken).ConfigureAwait(false) is not { } folder) continue;
                await ImapSession.OpenAsync(folder, access, cancellationToken).ConfigureAwait(false);
                var uids = await folder.SearchAsync(SearchQuery.GMailMessageId(value), cancellationToken).ConfigureAwait(false);
                if (uids.Count > 0) return (folder, uids[0]);
            }

            throw MailIds.NotFound(id);
        }

        var (name, validity, uid) = MailIds.ParseGeneric(id);
        IMailFolder target;
        try { target = await session.GetFolderAsync(name, cancellationToken).ConfigureAwait(false); }
        catch (FolderNotFoundException) { throw MailIds.NotFound(id); }
        await ImapSession.OpenAsync(target, access, cancellationToken).ConfigureAwait(false);
        if (target.UidValidity != validity) throw MailIds.NotFound(id);
        var exists = await target.SearchAsync(SearchQuery.Uids([uid]).And(SearchQuery.NotDeleted), cancellationToken).ConfigureAwait(false);
        return exists.Count == 0 ? throw MailIds.NotFound(id) : (target, uid);
    }

    private static async Task<IReadOnlyList<(IMailFolder Folder, UniqueId Uid)>> LocateThreadAsync(
        ImapSession session, string threadId, CancellationToken cancellationToken)
    {
        var found = new List<(IMailFolder, UniqueId)>();
        if (session.IsGmail)
        {
            var value = MailIds.ParseGmail(threadId);
            foreach (var kind in new[] { SpecialFolder.All, SpecialFolder.Trash })
            {
                if (await session.SpecialAsync(kind, cancellationToken).ConfigureAwait(false) is not { } folder) continue;
                await ImapSession.OpenAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
                found.AddRange((await folder.SearchAsync(SearchQuery.GMailThreadId(value), cancellationToken).ConfigureAwait(false))
                    .Select(uid => (folder, uid)));
            }
        }
        else
        {
            var root = MailIds.ParseGenericThread(threadId);
            if (root.StartsWith(MailIds.MessageIdHeaderPrefix, StringComparison.Ordinal))
                return [await LocateAsync(session, root[MailIds.MessageIdHeaderPrefix.Length..], FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false)];
            var query = SearchQuery.HeaderContains("Message-ID", root).Or(SearchQuery.HeaderContains("References", root));
            foreach (var folder in await SearchableFoldersAsync(session, includeTrash: false, cancellationToken).ConfigureAwait(false))
            {
                await ImapSession.OpenAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
                found.AddRange((await folder.SearchAsync(query.And(SearchQuery.NotDeleted), cancellationToken).ConfigureAwait(false))
                    .Select(uid => (folder, uid)));
                if (found.Count > MaxThreadMessages) break;
            }
        }

        return found.Count == 0
            ? throw new KeyNotFoundException($"The email thread '{threadId}' was not found.")
            : found.Take(MaxThreadMessages).ToArray();
    }

    private static async Task<IReadOnlyList<IMailFolder>> SearchableFoldersAsync(ImapSession session, bool includeTrash, CancellationToken cancellationToken)
    {
        if (session.IsGmail)
        {
            var folders = new List<IMailFolder> { await RequiredSpecialAsync(session, SpecialFolder.All, cancellationToken).ConfigureAwait(false) };
            if (includeTrash && await session.SpecialAsync(SpecialFolder.Trash, cancellationToken).ConfigureAwait(false) is { } trash) folders.Add(trash);
            return folders;
        }

        var junk = await session.SpecialAsync(SpecialFolder.Junk, cancellationToken).ConfigureAwait(false);
        var trashFolder = includeTrash ? null : await session.SpecialAsync(SpecialFolder.Trash, cancellationToken).ConfigureAwait(false);
        return (await session.FoldersAsync(cancellationToken).ConfigureAwait(false))
            .Where(folder => folder.FullName != junk?.FullName && folder.FullName != trashFolder?.FullName)
            .ToArray();
    }

    private static async Task<(IMailFolder Folder, UniqueId Uid)?> FindByMessageIdAsync(
        ImapSession session, IMailFolder? folder, string? messageId, int retries, CancellationToken cancellationToken)
    {
        if (folder is null || string.IsNullOrWhiteSpace(messageId)) return null;
        for (var attempt = 0; ; attempt++)
        {
            await ImapSession.OpenAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            var uids = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId), cancellationToken).ConfigureAwait(false);
            if (uids.Count > 0) return (folder, uids.Max());
            if (attempt >= retries) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);
            // A fresh SELECT is required before the server reports newly delivered messages.
            await folder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IMailFolder> ResolveFolderAsync(ImapSession session, string name, CancellationToken cancellationToken)
    {
        if (string.Equals(name, "INBOX", StringComparison.OrdinalIgnoreCase)) return session.Client.Inbox;
        foreach (var (kind, id) in SystemFolders)
        {
            if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase)
                && await session.SpecialAsync(kind, cancellationToken).ConfigureAwait(false) is { } special)
                return special;
        }

        return (await session.FoldersAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(folder => string.Equals(folder.FullName, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Email label '{name}' was not found.");
    }

    private static async Task<IMailFolder> RequiredSpecialAsync(ImapSession session, SpecialFolder kind, CancellationToken cancellationToken) =>
        await session.SpecialAsync(kind, cancellationToken).ConfigureAwait(false)
        ?? throw new UnsupportedFeatureException($"The mailbox has no {kind} folder. Enable it in the Gmail IMAP settings (Show in IMAP).");

    // ---------------------------------------------------------------- mapping

    private async Task<EmailMessage> ReadAsync(
        ImapSession session, (IMailFolder Folder, UniqueId Uid) location, EmailContentFormat format, bool loadRemoteImages,
        CancellationToken cancellationToken)
    {
        var (folder, uid) = location;
        await ImapSession.OpenAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        var summary = (await folder.FetchAsync([uid], FetchRequest(session), cancellationToken).ConfigureAwait(false)).SingleOrDefault()
            ?? throw MailIds.NotFound(MailIds.Generic(folder.FullName, folder.UidValidity, uid));
        var mime = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
        var converted = await content.ConvertAsync(mime, format, loadRemoteImages, cancellationToken).ConfigureAwait(false);
        var labels = Labels(session, folder, summary);
        var attachments = summary.Attachments.Select(part => new EmailAttachment(
            part.PartSpecifier, part.FileName ?? "attachment", part.ContentType.MimeType, DecodedSize(part))).ToArray();
        return new EmailMessage(
            MessageId(session, folder, summary), account.Email, ThreadId(session, folder, summary), Date(summary),
            Address(mime.From.Mailboxes.FirstOrDefault()), mime.To.Mailboxes.Select(Address).ToArray(),
            mime.Cc.Mailboxes.Select(Address).ToArray(), mime.Bcc.Mailboxes.Select(Address).ToArray(),
            mime.Subject ?? "", converted.Body, attachments, IsUnread(summary), IsStarred(summary),
            labels.Contains("IMPORTANT", StringComparer.Ordinal), labels, converted.Images);
    }

    private EmailSummary ToSummary(ImapSession session, IMailFolder folder, IMessageSummary summary) => new(
        MessageId(session, folder, summary), account.Email, ThreadId(session, folder, summary), Date(summary),
        Address(summary.Envelope?.From.Mailboxes.FirstOrDefault()), summary.Envelope?.Subject ?? "",
        summary.PreviewText ?? "", IsUnread(summary), summary.Attachments.Any());

    private EmailDraft ToDraft(ImapSession session, IMailFolder folder, IMessageSummary summary)
    {
        var envelope = summary.Envelope;
        var id = MessageId(session, folder, summary);
        return new EmailDraft(
            string.IsNullOrWhiteSpace(envelope?.MessageId) ? id : envelope.MessageId, id, account.Email,
            envelope?.To.Mailboxes.Select(item => item.Address).ToArray() ?? [],
            envelope?.Cc.Mailboxes.Select(item => item.Address).ToArray() ?? [],
            envelope?.Bcc.Mailboxes.Select(item => item.Address).ToArray() ?? [],
            envelope?.Subject ?? "", Truncate(summary.PreviewText ?? "", 200), summary.Attachments.Any());
    }

    private static string MessageId(ImapSession session, IMailFolder folder, IMessageSummary summary) =>
        session.IsGmail && summary.GMailMessageId is { } gmail
            ? MailIds.Gmail(gmail)
            : MailIds.Generic(folder.FullName, folder.UidValidity, summary.UniqueId);

    private static string ThreadId(ImapSession session, IMailFolder folder, IMessageSummary summary)
    {
        if (session.IsGmail && summary.GMailThreadId is { } gmail) return MailIds.Gmail(gmail);
        var root = summary.References?.FirstOrDefault() ?? summary.Envelope?.InReplyTo ?? summary.Envelope?.MessageId;
        return MailIds.GenericThread(string.IsNullOrWhiteSpace(root)
            ? MailIds.MessageIdHeaderPrefix + MailIds.Generic(folder.FullName, folder.UidValidity, summary.UniqueId)
            : root);
    }

    private static string[] Labels(ImapSession session, IMailFolder folder, IMessageSummary summary)
    {
        if (!session.IsGmail) return [folder.FullName];
        var labels = (summary.GMailLabels ?? []).Select(label =>
            GmailSystemLabels.FirstOrDefault(pair => string.Equals(pair.Value, label, StringComparison.OrdinalIgnoreCase)).Key ?? label).ToList();
        if (folder.Attributes.HasFlag(FolderAttributes.Trash)) labels.Add("TRASH");
        if (folder.Attributes.HasFlag(FolderAttributes.Junk)) labels.Add("SPAM");
        if (IsStarred(summary)) labels.Add("STARRED");
        if (IsUnread(summary)) labels.Add("UNREAD");
        return labels.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static FetchRequest FetchRequest(ImapSession session) =>
        new(session.IsGmail ? BaseItems | GmailItems : BaseItems);

    private static DateTimeOffset Date(IMessageSummary summary) =>
        summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.MinValue;

    private static bool IsUnread(IMessageSummary summary) => summary.Flags?.HasFlag(MessageFlags.Seen) != true;
    private static bool IsStarred(IMessageSummary summary) => summary.Flags?.HasFlag(MessageFlags.Flagged) == true;

    private static long DecodedSize(BodyPartBasic part) =>
        string.Equals(part.ContentTransferEncoding, "base64", StringComparison.OrdinalIgnoreCase)
            ? (long)(part.Octets * 3 / 4)
            : (long)part.Octets;

    private static EmailAddress Address(MailboxAddress? value) =>
        value is null ? new("") : new(value.Address, string.IsNullOrWhiteSpace(value.Name) ? null : value.Name);

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static string DomainOf(string email) => email[(email.LastIndexOf('@') + 1)..];

    private static string UniquePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        return !File.Exists(path)
            ? path
            : Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}-{Guid.NewGuid():N}{Path.GetExtension(name)}");
    }

    private async Task<ImapSession> OpenAsync(CancellationToken cancellationToken) =>
        await ImapSession.OpenAsync(account, await catalog.ReadPasswordAsync(account, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    // ---------------------------------------------------------------- cursor

    private sealed class FolderPool(IMailFolder folder, List<IMessageSummary> matched, bool scannedAll, uint? previousBelow)
    {
        public IMailFolder Folder { get; } = folder;
        public List<IMessageSummary> Matched { get; } = matched;
        public bool ScannedAll { get; } = scannedAll;
        public uint? PreviousBelow { get; } = previousBelow;
        public int Taken { get; set; }
    }

    internal sealed record FolderCursor(uint Validity, uint Below, bool Done);

    internal sealed record QueryCursor(Dictionary<string, FolderCursor> Folders)
    {
        public string Encode() =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Folders))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static QueryCursor Decode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new(new Dictionary<string, FolderCursor>(StringComparer.Ordinal));
            try
            {
                var normal = value.Replace('-', '+').Replace('_', '/');
                normal = normal.PadRight(normal.Length + (4 - normal.Length % 4) % 4, '=');
                var folders = JsonSerializer.Deserialize<Dictionary<string, FolderCursor>>(Encoding.UTF8.GetString(Convert.FromBase64String(normal)))
                    ?? throw new ValidationException("cursor is invalid.");
                return new(new Dictionary<string, FolderCursor>(folders, StringComparer.Ordinal));
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                throw new ValidationException("cursor is invalid.");
            }
        }
    }
}
