namespace ProductivityMcp.Core;

public interface IEmailProvider
{
    Task<OperationResult<IReadOnlyList<EmailAccountInfo>>> ListAccountsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<EmailQueryResult>> QueryAsync(EmailQuery query, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<EmailMessageReadResult>>> GetMessagesAsync(string account, IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken token = default);
    Task<OperationResult<IReadOnlyList<EmailThreadReadResult>>> GetThreadsAsync(string account, IReadOnlyList<string> ids, EmailContentFormat format, bool loadRemoteImages, CancellationToken token = default);
    Task<OperationResult<EmailMessage>> SendAsync(string account, OutgoingEmail message, CancellationToken token = default);
    Task<OperationResult<EmailMessage>> ForwardAsync(string account, string id, IReadOnlyList<string> to, IReadOnlyList<string> cc, IReadOnlyList<string> bcc, EmailComposeBody? body, CancellationToken token = default);
    Task<OperationResult<IReadOnlyList<EmailMutationResult>>> UpdateAsync(string account, IReadOnlyList<string> ids, EmailMessagePatch patch, CancellationToken token = default);
    Task<OperationResult<IReadOnlyList<EmailMutationResult>>> ArchiveAsync(string account, IReadOnlyList<string> ids, CancellationToken token = default);
    Task<OperationResult<IReadOnlyList<EmailMutationResult>>> TrashAsync(string account, IReadOnlyList<string> ids, CancellationToken token = default);
    Task<OperationResult<EmailAttachmentFile>> GetAttachmentAsync(string account, string messageId, string attachmentId, CancellationToken token = default);
    Task<OperationResult<EmailDraftPage>> ListDraftsAsync(string account, int pageSize, string? cursor, CancellationToken token = default);
    Task<OperationResult<EmailDraft>> CreateDraftAsync(string account, EmailDraftInput draft, CancellationToken token = default);
    Task<OperationResult<EmailDraft>> UpdateDraftAsync(string account, string id, EmailDraftPatch patch, CancellationToken token = default);
    Task<OperationResult<EmailMessage>> SendDraftAsync(string account, string id, CancellationToken token = default);
    Task<OperationResult<IReadOnlyList<EmailLabel>>> ListLabelsAsync(string account, CancellationToken token = default);
    Task<OperationResult<EmailLabel>> CreateLabelAsync(string account, string name, CancellationToken token = default);
}
