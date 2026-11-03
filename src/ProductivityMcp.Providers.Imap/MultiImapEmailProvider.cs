using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProductivityMcp.Core;
using ProductivityMcp.Email;
using Task = System.Threading.Tasks.Task;

namespace ProductivityMcp.Providers.Imap;

/// <summary>Routes email operations to the IMAP/SMTP account selected by its address.</summary>
public sealed class MultiImapEmailProvider(MailAccountCatalog accounts, EmailContentService content) : IEmailProvider
{
    private readonly MailAccountCatalog _accounts = accounts;
    private sealed record CursorState(string Fingerprint, Dictionary<string, string?> Tokens);

    public Task<OperationResult<IReadOnlyList<EmailAccountInfo>>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Ok<IReadOnlyList<EmailAccountInfo>>(_accounts.List()
            .Select(account => new EmailAccountInfo(account.Email, ProviderName(account)))
            .ToArray()));

    public async Task<OperationResult<EmailQueryResult>> QueryAsync(EmailQuery query, CancellationToken cancellationToken = default)
    {
        var accounts = string.IsNullOrWhiteSpace(query.Account)
            ? _accounts.List().ToArray()
            : _accounts.List().Where(account => string.Equals(account.Email, query.Account, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (accounts.Length == 0) return NotFound<EmailQueryResult>(query.Account ?? "");

        CursorState? state;
        try { state = DecodeCursor(query.Cursor); }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return OperationResult.Fail<EmailQueryResult>(new(OperationErrorCode.Validation, "cursor is invalid.", "query.cursor"));
        }

        var fingerprint = Fingerprint(query);
        if (state is not null && state.Fingerprint != fingerprint)
            return OperationResult.Fail<EmailQueryResult>(new(OperationErrorCode.Validation, "cursor does not belong to this query.", "query.cursor"));

        var messages = new List<EmailSummary>();
        var errors = new List<EmailAccountError>();
        var next = new Dictionary<string, string?>();
        var remaining = query.PageSize;
        foreach (var account in accounts)
        {
            // "" = not started, null = exhausted, otherwise the account's own continuation token.
            var token = state is null ? "" : state.Tokens.GetValueOrDefault(account.Email);
            if (token is null || remaining == 0)
            {
                next[account.Email] = token;
                continue;
            }

            try
            {
                var (page, cursor) = await Account(account)
                    .QueryAsync(query, remaining, token.Length == 0 ? null : token, cancellationToken).ConfigureAwait(false);
                messages.AddRange(page);
                remaining -= page.Count;
                next[account.Email] = cursor;
            }
            catch (Exception exception) when (MailOperation.Translate(exception) is { } error)
            {
                errors.Add(new(account.Email, error));
                next[account.Email] = null;
            }
        }

        var nextCursor = next.Values.Any(value => value is not null) ? EncodeCursor(new(fingerprint, next)) : null;
        return OperationResult.Ok(new EmailQueryResult(
            messages.OrderByDescending(message => message.Date).ToArray(), errors, errors.Count == 0, nextCursor));
    }

    public Task<OperationResult<IReadOnlyList<EmailMessageReadResult>>> GetMessagesAsync(string account, IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken token = default) =>
        Run(account, provider => provider.GetMessagesAsync(ids, format, loadRemoteImages, token));
    public Task<OperationResult<IReadOnlyList<EmailThreadReadResult>>> GetThreadsAsync(string account, IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken token = default) =>
        Run(account, provider => provider.GetThreadsAsync(ids, format, loadRemoteImages, token));
    public Task<OperationResult<EmailMessage>> SendAsync(string account, OutgoingEmail message, CancellationToken token = default) =>
        Run(account, provider => provider.SendAsync(message, token));
    public Task<OperationResult<EmailMessage>> ForwardAsync(string account, string id, IReadOnlyList<string> to, IReadOnlyList<string> cc, IReadOnlyList<string> bcc, EmailComposeBody? body, CancellationToken token = default) =>
        Run(account, provider => provider.ForwardAsync(id, to, cc, bcc, body, token));
    public Task<OperationResult<IReadOnlyList<EmailMutationResult>>> UpdateAsync(string account, IReadOnlyList<string> ids, EmailMessagePatch patch, CancellationToken token = default) =>
        Run(account, provider => provider.UpdateAsync(ids, patch, token));
    public Task<OperationResult<IReadOnlyList<EmailMutationResult>>> ArchiveAsync(string account, IReadOnlyList<string> ids, CancellationToken token = default) =>
        Run(account, provider => provider.ArchiveAsync(ids, token));
    public Task<OperationResult<IReadOnlyList<EmailMutationResult>>> TrashAsync(string account, IReadOnlyList<string> ids, CancellationToken token = default) =>
        Run(account, provider => provider.TrashAsync(ids, token));
    public Task<OperationResult<EmailAttachmentFile>> GetAttachmentAsync(string account, string messageId, string attachmentId, CancellationToken token = default) =>
        Run(account, provider => provider.GetAttachmentAsync(messageId, attachmentId, _accounts.Options.DownloadsDirectory, token));
    public Task<OperationResult<EmailDraftPage>> ListDraftsAsync(string account, int pageSize, string? cursor, CancellationToken token = default) =>
        Run(account, provider => provider.ListDraftsAsync(pageSize, cursor, token));
    public Task<OperationResult<EmailDraft>> CreateDraftAsync(string account, EmailDraftInput draft, CancellationToken token = default) =>
        Run(account, provider => provider.CreateDraftAsync(draft, token));
    public Task<OperationResult<EmailDraft>> UpdateDraftAsync(string account, string id, EmailDraftPatch patch, CancellationToken token = default) =>
        Run(account, provider => provider.UpdateDraftAsync(id, patch, token));
    public Task<OperationResult<EmailMessage>> SendDraftAsync(string account, string id, CancellationToken token = default) =>
        Run(account, provider => provider.SendDraftAsync(id, token));
    public Task<OperationResult<IReadOnlyList<EmailLabel>>> ListLabelsAsync(string account, CancellationToken token = default) =>
        Run(account, provider => provider.ListLabelsAsync(token));
    public Task<OperationResult<EmailLabel>> CreateLabelAsync(string account, string name, CancellationToken token = default) =>
        Run(account, provider => provider.CreateLabelAsync(name, token));

    internal static string ProviderName(MailAccount account) =>
        string.Equals(account.Imap.Host, "imap.gmail.com", StringComparison.OrdinalIgnoreCase) ? "gmail" : "imap";

    private async Task<OperationResult<T>> Run<T>(string account, Func<ImapEmailAccount, Task<T>> action)
    {
        var registration = _accounts.Find(account);
        return registration is null
            ? NotFound<T>(account)
            : await MailOperation.ExecuteAsync(() => action(Account(registration))).ConfigureAwait(false);
    }

    private ImapEmailAccount Account(MailAccount account) => new(account, _accounts, content);

    private static OperationResult<T> NotFound<T>(string account) =>
        OperationResult.Fail<T>(new(OperationErrorCode.NotFound, $"The email account '{account}' was not found."));

    private static string Fingerprint(EmailQuery query)
    {
        var json = JsonSerializer.Serialize(query with { Cursor = null });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string EncodeCursor(CursorState cursor) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CursorState? DecodeCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normal = value.Replace('-', '+').Replace('_', '/');
        normal = normal.PadRight(normal.Length + (4 - normal.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<CursorState>(Encoding.UTF8.GetString(Convert.FromBase64String(normal))) ?? throw new FormatException();
    }
}
