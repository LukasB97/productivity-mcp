using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ProductivityMcp.Core;

[JsonConverter(typeof(JsonStringEnumConverter<EmailContentFormat>))]
public enum EmailContentFormat
{
    [JsonStringEnumMemberName("plain")] Plain,
    [JsonStringEnumMemberName("html")] Html,
    [JsonStringEnumMemberName("markdown")] Markdown,
    [JsonStringEnumMemberName("image")] Image,
}

[JsonConverter(typeof(JsonStringEnumConverter<EmailComposeFormat>))]
public enum EmailComposeFormat
{
    [JsonStringEnumMemberName("html")] Html,
    [JsonStringEnumMemberName("markdown")] Markdown,
}

[JsonConverter(typeof(JsonStringEnumConverter<EmailMailbox>))]
public enum EmailMailbox
{
    [JsonStringEnumMemberName("all")] All,
    [JsonStringEnumMemberName("inbox")] Inbox,
    [JsonStringEnumMemberName("sent")] Sent,
    [JsonStringEnumMemberName("drafts")] Drafts,
    [JsonStringEnumMemberName("spam")] Spam,
    [JsonStringEnumMemberName("trash")] Trash,
}

public sealed record EmailAccountInfo(string Account, string Provider);
public sealed record EmailAddress(string Address, string? Name = null);
public sealed record EmailAttachment(string AttachmentId, string Name, string MediaType, long Size);
public sealed record EmailBody(EmailContentFormat Format, string Content);
public sealed record EmailComposeBody(
    [property: EnumDataType(typeof(EmailComposeFormat))] EmailComposeFormat Format,
    [property: Required(AllowEmptyStrings = true)] string Content);
public sealed record EmailImage(string MediaType, byte[] Data, int Page);

public sealed record EmailSummary(
    string Id, string Account, string ThreadId, DateTimeOffset Date, EmailAddress From,
    string Subject, string Preview, bool Unread, bool HasAttachments);

public sealed record EmailMessage(
    string Id, string Account, string ThreadId, DateTimeOffset Date, EmailAddress From,
    IReadOnlyList<EmailAddress> To, IReadOnlyList<EmailAddress> Cc, IReadOnlyList<EmailAddress> Bcc,
    string Subject, EmailBody Body, IReadOnlyList<EmailAttachment> Attachments,
    bool Unread, bool Starred, bool Important, IReadOnlyList<string> Labels,
    [property: JsonIgnore] IReadOnlyList<EmailImage>? Images = null);

public sealed record EmailThread(string ThreadId, string Account, IReadOnlyList<EmailMessage> Messages);
public sealed record EmailMessageReadResult(string Id, EmailMessage? Message, OperationError? Error);
public sealed record EmailThreadReadResult(string ThreadId, EmailThread? Thread, OperationError? Error);
public sealed record EmailAccountError(string Account, OperationError Error);
public sealed record EmailQueryResult(
    IReadOnlyList<EmailSummary> Messages, IReadOnlyList<EmailAccountError> Errors,
    bool Complete, string? Cursor);

public sealed record EmailFilter
{
    public string? Text { get; init; }
    public IReadOnlyList<string>? From { get; init; }
    public IReadOnlyList<string>? To { get; init; }
    public IReadOnlyList<string>? Cc { get; init; }
    public IReadOnlyList<string>? Bcc { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? After { get; init; }
    public DateTimeOffset? Before { get; init; }
    public IReadOnlyList<string>? Labels { get; init; }
    public EmailMailbox? Mailbox { get; init; }
    public bool? Unread { get; init; }
    public bool? Starred { get; init; }
    public bool? Important { get; init; }
    public bool? HasAttachments { get; init; }
    public string? Filename { get; init; }
    public long? LargerThanBytes { get; init; }
    public long? SmallerThanBytes { get; init; }
    public IReadOnlyList<EmailFilter>? And { get; init; }
    public IReadOnlyList<EmailFilter>? Or { get; init; }
    public EmailFilter? Not { get; init; }
}

public sealed record EmailQuery : IValidatableObject
{
    public string? Account { get; init; }
    public EmailFilter? Filter { get; init; }
    [Range(1, 100)] public int PageSize { get; init; } = 50;
    public string? Cursor { get; init; }
    public bool IncludeSpamTrash { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PageSize is < 1 or > 100)
            yield return new ValidationResult("pageSize must be between 1 and 100.", [nameof(PageSize)]);
        foreach (var failure in ValidateFilter(Filter, 0)) yield return failure;
    }

    private static IEnumerable<ValidationResult> ValidateFilter(EmailFilter? filter, int depth)
    {
        if (filter is null) yield break;
        if (depth > 8) { yield return new ValidationResult("filter nesting must not exceed 8 levels.", [nameof(Filter)]); yield break; }
        if (filter.LargerThanBytes is < 0 || filter.SmallerThanBytes is < 0)
            yield return new ValidationResult("message sizes must not be negative.", [nameof(Filter)]);
        if (filter.After is not null && filter.Before is not null && filter.After >= filter.Before)
            yield return new ValidationResult("before must be after after.", [nameof(Filter)]);
        if (filter.Mailbox is not null && !Enum.IsDefined(filter.Mailbox.Value))
            yield return new ValidationResult("mailbox is not supported.", [nameof(Filter)]);
        if (new[] { filter.From, filter.To, filter.Cc, filter.Bcc, filter.Labels }
            .Any(values => values?.Any(string.IsNullOrWhiteSpace) == true))
            yield return new ValidationResult("filter lists must not contain null or empty values.", [nameof(Filter)]);
        foreach (var child in (filter.And ?? []).Concat(filter.Or ?? []))
        {
            if (child is null) yield return new ValidationResult("filter conditions must not be null.", [nameof(Filter)]);
            foreach (var failure in ValidateFilter(child, depth + 1)) yield return failure;
        }
        foreach (var failure in ValidateFilter(filter.Not, depth + 1)) yield return failure;
    }
}

public sealed record EmailAttachmentInput
{
    [Required(AllowEmptyStrings = false)] public required string Path { get; init; }
    public string? Name { get; init; }
}

public sealed record OutgoingEmail : IValidatableObject
{
    [NotNullValue] public IReadOnlyList<string> To { get; init; } = [];
    [NotNullValue] public IReadOnlyList<string> Cc { get; init; } = [];
    [NotNullValue] public IReadOnlyList<string> Bcc { get; init; } = [];
    [NotNullValue] public string Subject { get; init; } = "";
    [Required] public required EmailComposeBody Body { get; init; }
    [NotNullValue] public IReadOnlyList<EmailAttachmentInput> Attachments { get; init; } = [];
    public string? ReplyToMessageId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (To.Count + Cc.Count + Bcc.Count == 0)
            yield return new ValidationResult("at least one recipient is required.", [nameof(To)]);
        foreach (var failure in EmailDraftInput.ValidateDraftFields(To, Cc, Bcc, Body, Attachments)) yield return failure;
    }
}

public sealed record EmailDraftInput : IValidatableObject
{
    [NotNullValue] public IReadOnlyList<string> To { get; init; } = [];
    [NotNullValue] public IReadOnlyList<string> Cc { get; init; } = [];
    [NotNullValue] public IReadOnlyList<string> Bcc { get; init; } = [];
    [NotNullValue] public string Subject { get; init; } = "";
    public EmailComposeBody? Body { get; init; }
    [NotNullValue] public IReadOnlyList<EmailAttachmentInput> Attachments { get; init; } = [];
    public string? ReplyToMessageId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ValidateDraftFields(To, Cc, Bcc, Body, Attachments);

    internal static IEnumerable<ValidationResult> ValidateDraftFields(
        IEnumerable<string>? to, IEnumerable<string>? cc, IEnumerable<string>? bcc, EmailComposeBody? body,
        IReadOnlyList<EmailAttachmentInput>? attachments)
    {
        var validator = new EmailAddressAttribute();
        foreach (var address in (to ?? []).Concat(cc ?? []).Concat(bcc ?? []).Where(x => string.IsNullOrWhiteSpace(x) || !validator.IsValid(x)))
            yield return new ValidationResult($"'{address}' is not a valid email address.", [nameof(To)]);
        if (body is not null)
        {
            var failures = new List<ValidationResult>();
            Validator.TryValidateObject(body, new ValidationContext(body), failures, validateAllProperties: true);
            foreach (var failure in failures)
                yield return new ValidationResult(failure.ErrorMessage, [nameof(Body)]);
        }
        foreach (var attachment in attachments ?? [])
        {
            if (attachment is null)
            {
                yield return new ValidationResult("attachments must not contain null.", [nameof(Attachments)]);
                continue;
            }
            var failures = new List<ValidationResult>();
            Validator.TryValidateObject(attachment, new ValidationContext(attachment), failures, validateAllProperties: true);
            foreach (var failure in failures)
                yield return new ValidationResult(failure.ErrorMessage, [nameof(Attachments)]);
        }
    }
}

public sealed record EmailDraftPatch : IValidatableObject
{
    private IReadOnlyList<string>? _to;
    private IReadOnlyList<string>? _cc;
    private IReadOnlyList<string>? _bcc;
    private string? _subject;
    private EmailComposeBody? _body;
    private IReadOnlyList<EmailAttachmentInput>? _attachments;
    private string? _replyToMessageId;

    public IReadOnlyList<string>? To { get => _to; init { _to = value; HasTo = true; } }
    public IReadOnlyList<string>? Cc { get => _cc; init { _cc = value; HasCc = true; } }
    public IReadOnlyList<string>? Bcc { get => _bcc; init { _bcc = value; HasBcc = true; } }
    public string? Subject { get => _subject; init { _subject = value; HasSubject = true; } }
    public EmailComposeBody? Body { get => _body; init { _body = value; HasBody = true; } }
    public IReadOnlyList<EmailAttachmentInput>? Attachments { get => _attachments; init { _attachments = value; HasAttachments = true; } }
    public string? ReplyToMessageId { get => _replyToMessageId; init { _replyToMessageId = value; HasReplyToMessageId = true; } }

    [JsonIgnore] public bool HasTo { get; private init; }
    [JsonIgnore] public bool HasCc { get; private init; }
    [JsonIgnore] public bool HasBcc { get; private init; }
    [JsonIgnore] public bool HasSubject { get; private init; }
    [JsonIgnore] public bool HasBody { get; private init; }
    [JsonIgnore] public bool HasAttachments { get; private init; }
    [JsonIgnore] public bool HasReplyToMessageId { get; private init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!HasTo && !HasCc && !HasBcc && !HasSubject && !HasBody && !HasAttachments && !HasReplyToMessageId)
            yield return new ValidationResult("patch must contain at least one field.");
        foreach (var failure in EmailDraftInput.ValidateDraftFields(To, Cc, Bcc, Body, Attachments)) yield return failure;
    }
}

public sealed record EmailMessagePatch : IValidatableObject
{
    public bool? Unread { get; init; }
    public bool? Starred { get; init; }
    public bool? Important { get; init; }
    [NotNullValue] public IReadOnlyList<string> AddLabels { get; init; } = [];
    [NotNullValue] public IReadOnlyList<string> RemoveLabels { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Unread is null && Starred is null && Important is null && AddLabels.Count == 0 && RemoveLabels.Count == 0)
            yield return new ValidationResult("patch must contain at least one field.");
        if (AddLabels.Any(string.IsNullOrWhiteSpace) || RemoveLabels.Any(string.IsNullOrWhiteSpace))
            yield return new ValidationResult("labels must not contain empty values.", [nameof(AddLabels)]);
    }
}

public sealed record EmailDraft(
    string DraftId, string MessageId, string Account, IReadOnlyList<string> To,
    IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc, string Subject,
    string Preview, bool HasAttachments);
public sealed record EmailDraftPage(IReadOnlyList<EmailDraft> Drafts, string? Cursor);
public sealed record EmailLabel(
    string Id, string Name, long? MessagesTotal, long? MessagesUnread,
    long? ThreadsTotal, long? ThreadsUnread);
public sealed record EmailAttachmentFile(
    string AttachmentId, string Name, string MediaType, long Size, string Path);
public sealed record EmailMutationResult(string Id, bool Success, OperationError? Error = null);
