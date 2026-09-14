using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProductivityMcp.Core;
using ProductivityMcp.Email;

namespace ProductivityMcp.Providers.Google;

public sealed class MultiGoogleEmailProvider(GoogleOptions options, EmailContentService content) : IEmailProvider
{
    private readonly GoogleAccountCatalog _accounts = new(options);
    private sealed record CursorState(string Fingerprint, Dictionary<string, string?> Tokens);

    public Task<OperationResult<IReadOnlyList<EmailAccountInfo>>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        System.Threading.Tasks.Task.FromResult(OperationResult.Ok<IReadOnlyList<EmailAccountInfo>>(Registrations().Select(x => new EmailAccountInfo(x.Email, "google")).ToArray()));

    public async Task<OperationResult<EmailQueryResult>> QueryAsync(EmailQuery query, CancellationToken cancellationToken = default)
    {
        var registrations = string.IsNullOrWhiteSpace(query.Account)
            ? Registrations()
            : Registrations().Where(x => string.Equals(x.Email, query.Account, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (registrations.Length == 0) return NotFound<EmailQueryResult>("email account", query.Account ?? "");
        CursorState? state;
        try { state = DecodeCursor(query.Cursor); }
        catch (FormatException) { return OperationResult.Fail<EmailQueryResult>(new(OperationErrorCode.Validation, "cursor is invalid.", "query.cursor")); }
        var fingerprint = Fingerprint(query);
        if (state is not null && state.Fingerprint != fingerprint) return OperationResult.Fail<EmailQueryResult>(new(OperationErrorCode.Validation, "cursor does not belong to this query.", "query.cursor"));

        var messages = new List<EmailSummary>(); var errors = new List<EmailAccountError>(); var next = new Dictionary<string, string?>();
        var remaining = query.PageSize;
        for (var index = 0; index < registrations.Length; index++)
        {
            var registration = registrations[index]; var provider = Provider(registration);
            var providerToken = state is null ? "" : state.Tokens.GetValueOrDefault(registration.Email);
            if (providerToken is null)
            {
                next[registration.Email] = null;
                continue;
            }
            if (remaining == 0)
            {
                next[registration.Email] = providerToken;
                continue;
            }
            try
            {
                var accountMessages = await provider.QueryAsync(query, remaining, providerToken.Length == 0 ? null : providerToken, cancellationToken).ConfigureAwait(false);
                messages.AddRange(accountMessages);
                remaining -= accountMessages.Count;
                next[registration.Email] = provider.NextCursor;
            }
            catch (Exception exception) when (GoogleOperation.Translate(exception) is { } error)
            {
                errors.Add(new(registration.Email, error));
                next[registration.Email] = null;
            }
        }
        var cursor = next.Values.Any(x => x is not null) ? EncodeCursor(new(fingerprint, next)) : null;
        return OperationResult.Ok(new EmailQueryResult(messages.OrderByDescending(x => x.Date).ToArray(), errors, errors.Count == 0, cursor));
    }

    public Task<OperationResult<IReadOnlyList<EmailMessageReadResult>>> GetMessagesAsync(string account, IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken token = default) => Run(account, p => p.GetMessagesAsync(ids, format, loadRemoteImages, token));
    public Task<OperationResult<IReadOnlyList<EmailThreadReadResult>>> GetThreadsAsync(string account, IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken token = default) => Run(account, p => p.GetThreadsAsync(ids, format, loadRemoteImages, token));
    public Task<OperationResult<EmailMessage>> SendAsync(string account, OutgoingEmail message, CancellationToken token = default) => Run(account, p => p.SendAsync(message, token));
    public Task<OperationResult<EmailMessage>> ForwardAsync(string account, string id, IReadOnlyList<string> to, IReadOnlyList<string> cc, IReadOnlyList<string> bcc, EmailBody? body, CancellationToken token = default) => Run(account, p => p.ForwardAsync(id, to, cc, bcc, body, token));
    public Task<OperationResult<IReadOnlyList<EmailMutationResult>>> UpdateAsync(string account, IReadOnlyList<string> ids, EmailMessagePatch patch, CancellationToken token = default) => Run(account, p => p.UpdateAsync(ids, patch, token));
    public Task<OperationResult<IReadOnlyList<EmailMutationResult>>> ArchiveAsync(string account, IReadOnlyList<string> ids, CancellationToken token = default) => Run(account, p => p.ArchiveAsync(ids, token));
    public Task<OperationResult<IReadOnlyList<EmailMutationResult>>> TrashAsync(string account, IReadOnlyList<string> ids, CancellationToken token = default) => Run(account, p => p.TrashAsync(ids, token));
    public Task<OperationResult<EmailAttachmentFile>> GetAttachmentAsync(string account, string messageId, string attachmentId, CancellationToken token = default) => Run(account, p => p.GetAttachmentAsync(messageId, attachmentId, token));
    public Task<OperationResult<EmailDraftPage>> ListDraftsAsync(string account, int pageSize, string? cursor, CancellationToken token = default) => Run(account, p => p.ListDraftsAsync(pageSize, cursor, token));
    public Task<OperationResult<EmailDraft>> CreateDraftAsync(string account, EmailDraftInput draft, CancellationToken token = default) => Run(account, p => p.CreateDraftAsync(draft, token));
    public Task<OperationResult<EmailDraft>> UpdateDraftAsync(string account, string id, EmailDraftPatch patch, CancellationToken token = default) => Run(account, p => p.UpdateDraftAsync(id, patch, token));
    public Task<OperationResult<EmailMessage>> SendDraftAsync(string account, string id, CancellationToken token = default) => Run(account, p => p.SendDraftAsync(id, token));
    public Task<OperationResult<IReadOnlyList<EmailLabel>>> ListLabelsAsync(string account, CancellationToken token = default) => Run(account, p => p.ListLabelsAsync(token));
    public Task<OperationResult<EmailLabel>> CreateLabelAsync(string account, string name, CancellationToken token = default) => Run(account, p => p.CreateLabelAsync(name, token));

    private async Task<OperationResult<T>> Run<T>(string account, Func<GoogleEmailProvider, Task<T>> action)
    {
        var registration = Registrations().FirstOrDefault(x => string.Equals(x.Email, account, StringComparison.OrdinalIgnoreCase));
        if (registration is null) return NotFound<T>("email account", account);
        return await GoogleOperation.ExecuteAsync(() => action(Provider(registration))).ConfigureAwait(false);
    }

    private GoogleEmailProvider Provider(GoogleAccountRegistration value) => new(value.Email, new GoogleServiceFactory(_accounts.EmailOptionsFor(value.Key), GoogleServiceAccess.Email), options, content);
    private GoogleAccountRegistration[] Registrations() => _accounts.List().Where(x => x.EmailEnabled && _accounts.HasEmailToken(x.Key)).ToArray();
    private static OperationResult<T> NotFound<T>(string resource, string id) => OperationResult.Fail<T>(new(OperationErrorCode.NotFound, $"The {resource} '{id}' was not found."));
    private static string Fingerprint(EmailQuery query) { var json = JsonSerializer.Serialize(query with { Cursor = null }); return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))); }
    private static string EncodeCursor(CursorState cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static CursorState? DecodeCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normal = value.Replace('-', '+').Replace('_', '/'); normal = normal.PadRight(normal.Length + (4 - normal.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<CursorState>(Encoding.UTF8.GetString(Convert.FromBase64String(normal))) ?? throw new FormatException();
    }
}
