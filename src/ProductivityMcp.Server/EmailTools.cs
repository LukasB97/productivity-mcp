using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProductivityMcp.Core;

namespace ProductivityMcp.Server;

[McpServerToolType]
public sealed class EmailTools(IEmailProvider provider)
{
    [McpServerTool(Name = "email.accounts.list", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ListOutput<EmailAccountInfo>))]
    [Description("Lists email-enabled accounts by their primary email address.")]
    public async Task<CallToolResult> Accounts(CancellationToken token) => McpToolResults.FromList(await provider.ListAccountsAsync(token).ConfigureAwait(false));

    [McpServerTool(Name = "email.messages.query", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(EmailQueryResult))]
    [Description("Queries email messages with provider-neutral structured filters, optionally across all email accounts.")]
    public async Task<CallToolResult> Query(EmailQuery query, CancellationToken token)
    {
        if (!McpToolResults.TryValue(ModelValidation.Validate(query, nameof(query)), out var value, out var error)) return error;
        return McpToolResults.FromValue(await provider.QueryAsync(value, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.messages.get", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ListOutput<EmailMessageReadResult>))]
    [Description("Reads one or more messages without changing their unread state.")]
    public async Task<CallToolResult> GetMessages(string account, IReadOnlyList<string> messageIds, EmailContentFormat format, bool loadRemoteImages = false, CancellationToken token = default)
    {
        if (!Required(account, messageIds, out var error)) return error;
        return McpToolResults.FromEmailMessages(await provider.GetMessagesAsync(account, messageIds, format, loadRemoteImages, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.threads.get", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ListOutput<EmailThreadReadResult>))]
    [Description("Reads one or more email threads in chronological message order.")]
    public async Task<CallToolResult> GetThreads(string account, IReadOnlyList<string> threadIds, EmailContentFormat format, bool loadRemoteImages = false, CancellationToken token = default)
    {
        if (!Required(account, threadIds, out var error)) return error;
        return McpToolResults.FromEmailThreads(await provider.GetThreadsAsync(account, threadIds, format, loadRemoteImages, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.messages.send", Destructive = false, UseStructuredContent = true, OutputSchemaType = typeof(EmailMessage))]
    [Description("Sends a new email or reply from the selected account.")]
    public async Task<CallToolResult> Send(string account, OutgoingEmail message, CancellationToken token)
    {
        if (!McpToolResults.TryValue(ModelValidation.RequiredId(account, nameof(account)), out var selected, out var error)) return error;
        if (!McpToolResults.TryValue(ModelValidation.Validate(message, nameof(message)), out var value, out error)) return error;
        return McpToolResults.FromValue(await provider.SendAsync(selected, value, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.messages.forward", Destructive = false, UseStructuredContent = true, OutputSchemaType = typeof(EmailMessage))]
    [Description("Forwards a message inline and preserves its attachments.")]
    public async Task<CallToolResult> Forward(string account, string messageId, IReadOnlyList<string> to, IReadOnlyList<string>? cc = null, IReadOnlyList<string>? bcc = null, EmailBody? body = null, CancellationToken token = default)
    {
        if (!Required(account, [messageId], out var error)) return error;
        var candidate = new OutgoingEmail { To = to, Cc = cc ?? [], Bcc = bcc ?? [], Body = body ?? new(EmailContentFormat.Html, "") };
        if (!McpToolResults.TryValue(ModelValidation.Validate(candidate, "message"), out _, out error)) return error;
        return McpToolResults.FromValue(await provider.ForwardAsync(account, messageId, to, cc ?? [], bcc ?? [], body, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.messages.update", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ListOutput<EmailMutationResult>))]
    public async Task<CallToolResult> Update(string account, IReadOnlyList<string> messageIds, EmailMessagePatch patch, CancellationToken token)
    {
        if (!Required(account, messageIds, out var error)) return error;
        if (!McpToolResults.TryValue(ModelValidation.Validate(patch, nameof(patch)), out var value, out error)) return error;
        return McpToolResults.FromList(await provider.UpdateAsync(account, messageIds, value, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.messages.archive", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ListOutput<EmailMutationResult>))]
    public async Task<CallToolResult> Archive(string account, IReadOnlyList<string> messageIds, CancellationToken token)
    {
        if (!Required(account, messageIds, out var error)) return error;
        return McpToolResults.FromList(await provider.ArchiveAsync(account, messageIds, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.messages.trash", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ListOutput<EmailMutationResult>))]
    public async Task<CallToolResult> Trash(string account, IReadOnlyList<string> messageIds, CancellationToken token)
    {
        if (!Required(account, messageIds, out var error)) return error;
        return McpToolResults.FromList(await provider.TrashAsync(account, messageIds, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.attachments.get", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(EmailAttachmentFile))]
    public async Task<CallToolResult> Attachment(string account, string messageId, string attachmentId, CancellationToken token)
    {
        if (!Required(account, [messageId, attachmentId], out var error)) return error;
        return McpToolResults.FromValue(await provider.GetAttachmentAsync(account, messageId, attachmentId, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.drafts.list", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(EmailDraftPage))]
    public async Task<CallToolResult> Drafts(string account, int pageSize = 50, string? cursor = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(account) || pageSize is < 1 or > 100)
            return McpToolResults.Error(new(OperationErrorCode.Validation, "account is required and pageSize must be between 1 and 100."));
        return McpToolResults.FromValue(await provider.ListDraftsAsync(account, pageSize, cursor, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.drafts.create", Destructive = false, UseStructuredContent = true, OutputSchemaType = typeof(EmailDraft))]
    public async Task<CallToolResult> CreateDraft(string account, EmailDraftInput draft, CancellationToken token)
    {
        if (!McpToolResults.TryValue(ModelValidation.RequiredId(account, nameof(account)), out var selected, out var error)) return error;
        if (!McpToolResults.TryValue(ModelValidation.Validate(draft, nameof(draft)), out var value, out error)) return error;
        return McpToolResults.FromValue(await provider.CreateDraftAsync(selected, value, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.drafts.update", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(EmailDraft))]
    public async Task<CallToolResult> UpdateDraft(string account, string draftId, EmailDraftPatch patch, CancellationToken token)
    {
        if (!Required(account, [draftId], out var error)) return error;
        if (!McpToolResults.TryValue(ModelValidation.Validate(patch, nameof(patch)), out var value, out error)) return error;
        return McpToolResults.FromValue(await provider.UpdateDraftAsync(account, draftId, value, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.drafts.send", Destructive = false, UseStructuredContent = true, OutputSchemaType = typeof(EmailMessage))]
    public async Task<CallToolResult> SendDraft(string account, string draftId, CancellationToken token)
    {
        if (!Required(account, [draftId], out var error)) return error;
        return McpToolResults.FromValue(await provider.SendDraftAsync(account, draftId, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.labels.list", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ListOutput<EmailLabel>))]
    public async Task<CallToolResult> Labels(string account, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(account)) return McpToolResults.Error(new(OperationErrorCode.Validation, "account is required.", "account"));
        return McpToolResults.FromList(await provider.ListLabelsAsync(account, token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "email.labels.create", Destructive = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(EmailLabel))]
    public async Task<CallToolResult> CreateLabel(string account, string name, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(name)) return McpToolResults.Error(new(OperationErrorCode.Validation, "account and name are required."));
        return McpToolResults.FromValue(await provider.CreateLabelAsync(account, name, token).ConfigureAwait(false));
    }

    private static bool Required(string account, IReadOnlyList<string> ids, out CallToolResult error)
    {
        if (string.IsNullOrWhiteSpace(account)) { error = McpToolResults.Error(new(OperationErrorCode.Validation, "account is required.", "account")); return false; }
        if (ids is not { Count: > 0 } || ids.Any(string.IsNullOrWhiteSpace)) { error = McpToolResults.Error(new(OperationErrorCode.Validation, "ids must contain at least one non-empty value.", "ids")); return false; }
        error = null!; return true;
    }
}
