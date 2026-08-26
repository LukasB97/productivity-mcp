using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace ProductivityMcp.Core;

public sealed record CalendarInfo(
    string Id,
    string Name,
    string Provider,
    bool? IsDefault,
    string? TimeZone);

public sealed record Event : IValidatableObject
{
    [Required(AllowEmptyStrings = false)]
    public required string Title { get; init; }

    [Required]
    [Description("All-day date (yyyy-MM-dd), local calendar time (yyyy-MM-ddTHH:mm:ss), or RFC 3339 timestamp with offset.")]
    public required string Start { get; init; }

    [Required]
    [Description("Exclusive all-day end date, local calendar time, or RFC 3339 timestamp with offset.")]
    public required string End { get; init; }

    public string Description { get; init; } = null!;
    public string Location { get; init; } = null!;
    public IReadOnlyList<string> Attendees { get; init; } = null!;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            yield return new ValidationResult("title must not be empty.", [nameof(Title)]);
        }

        if (Attendees is not null)
        {
            var emailValidator = new EmailAddressAttribute();
            foreach (var attendee in Attendees.Where(email =>
                         string.IsNullOrWhiteSpace(email) || !emailValidator.IsValid(email)))
            {
                yield return new ValidationResult(
                    attendee is null
                        ? "attendees must not contain null values."
                        : $"'{attendee}' is not a valid attendee email address.",
                    [nameof(Attendees)]);
            }
        }
    }
}

public sealed record EventQuery
{
    public string CalendarId { get; init; } = null!;
    public string Text { get; init; } = null!;

    [Description("Inclusive lower bound: yyyy-MM-dd, local calendar time, or RFC 3339 timestamp with offset.")]
    public string From { get; init; } = null!;

    [Description("Exclusive upper bound: yyyy-MM-dd, local calendar time, or RFC 3339 timestamp with offset.")]
    public string To { get; init; } = null!;
}

public sealed record EventPatch : IValidatableObject
{
    private string? _description;
    private string? _location;
    private IReadOnlyList<string>? _attendees;

    public string Title { get; init; } = null!;

    [Description("All-day date, local calendar time, or RFC 3339 timestamp with offset.")]
    public string Start { get; init; } = null!;

    [Description("Exclusive all-day end date, local calendar time, or RFC 3339 timestamp with offset.")]
    public string End { get; init; } = null!;
    public string? Description
    {
        get => _description;
        init
        {
            _description = value;
            HasDescription = true;
        }
    }
    public string? Location
    {
        get => _location;
        init
        {
            _location = value;
            HasLocation = true;
        }
    }
    public IReadOnlyList<string>? Attendees
    {
        get => _attendees;
        init
        {
            _attendees = value;
            HasAttendees = true;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasDescription { get; private init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasLocation { get; private init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAttendees { get; private init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Title is null && Start is null && End is null && !HasDescription && !HasLocation && !HasAttendees)
        {
            yield return new ValidationResult("patch must contain at least one field.");
        }

        if (Title is not null && string.IsNullOrWhiteSpace(Title))
        {
            yield return new ValidationResult("title must not be empty.", [nameof(Title)]);
        }

        if (Attendees is not null)
        {
            var emailValidator = new EmailAddressAttribute();
            foreach (var attendee in Attendees.Where(email =>
                         string.IsNullOrWhiteSpace(email) || !emailValidator.IsValid(email)))
            {
                yield return new ValidationResult(
                    attendee is null
                        ? "attendees must not contain null values."
                        : $"'{attendee}' is not a valid attendee email address.",
                    [nameof(Attendees)]);
            }
        }
    }
}

public sealed record CalendarEvent(
    string Id,
    string CalendarId,
    string Title,
    string Start,
    string End,
    string? Description,
    string? Location,
    IReadOnlyList<string> Attendees);

public sealed record TaskListInfo(
    string Id,
    string Name,
    string Provider,
    bool? IsDefault);

public sealed record Task : IValidatableObject
{
    [Required(AllowEmptyStrings = false)]
    public required string Title { get; init; }
    public string Notes { get; init; } = null!;

    [Description("Due date in yyyy-MM-dd format. Google Tasks does not store a due time or time zone.")]
    public DateOnly? Due { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            yield return new ValidationResult("title must not be empty.", [nameof(Title)]);
        }
    }
}

public sealed record TaskPatch : IValidatableObject
{
    private string? _notes;
    private DateOnly? _due;

    public string Title { get; init; } = null!;
    public string? Notes
    {
        get => _notes;
        init
        {
            _notes = value;
            HasNotes = true;
        }
    }
    [Description("Due date in yyyy-MM-dd format; null clears it.")]
    public DateOnly? Due
    {
        get => _due;
        init
        {
            _due = value;
            HasDue = true;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasNotes { get; private init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasDue { get; private init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Title is null && !HasNotes && !HasDue)
        {
            yield return new ValidationResult("patch must contain at least one field.");
        }

        if (Title is not null && string.IsNullOrWhiteSpace(Title))
        {
            yield return new ValidationResult("title must not be empty.", [nameof(Title)]);
        }
    }
}

public sealed record TodoTask(
    string Id,
    string TaskListId,
    string Title,
    string? Notes,
    DateOnly? Due,
    bool Completed);
