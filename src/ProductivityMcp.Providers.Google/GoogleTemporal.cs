using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Google.Apis.Calendar.v3.Data;

namespace ProductivityMcp.Providers.Google;

internal enum CalendarTimeKind
{
    Date,
    LocalDateTime,
    OffsetDateTime,
}

internal readonly record struct CalendarTimeValue(
    CalendarTimeKind Kind,
    DateOnly Date,
    DateTime LocalDateTime,
    DateTimeOffset OffsetDateTime);

internal static class GoogleTemporal
{
    private static readonly string[] OffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    private static readonly string[] UtcFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
    ];

    public static CalendarTimeValue Parse(string value, string field)
    {
        if (DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return new CalendarTimeValue(CalendarTimeKind.Date, date, default, default);
        }

        if (DateTimeOffset.TryParseExact(
                value,
                OffsetFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var offsetDateTime))
        {
            return new CalendarTimeValue(CalendarTimeKind.OffsetDateTime, default, default, offsetDateTime);
        }

        if (DateTimeOffset.TryParseExact(
                value,
                UtcFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var utcDateTime))
        {
            return new CalendarTimeValue(CalendarTimeKind.OffsetDateTime, default, default, utcDateTime);
        }

        if (DateTime.TryParseExact(
                value,
                LocalFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDateTime))
        {
            return new CalendarTimeValue(
                CalendarTimeKind.LocalDateTime,
                default,
                DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified),
                default);
        }

        throw new ValidationException(
            $"{field} must be yyyy-MM-dd, a local ISO 8601 date-time, or an RFC 3339 timestamp with offset.");
    }

    public static EventDateTime ToGoogleEventDateTime(
        CalendarTimeValue value,
        string calendarTimeZone) => value.Kind switch
        {
            CalendarTimeKind.Date => new EventDateTime
            {
                Date = value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            CalendarTimeKind.LocalDateTime => new EventDateTime
            {
                DateTimeRaw = value.LocalDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                    CultureInfo.InvariantCulture),
                TimeZone = calendarTimeZone,
            },
            CalendarTimeKind.OffsetDateTime => new EventDateTime
            {
                DateTimeDateTimeOffset = value.OffsetDateTime,
            },
            _ => throw new InvalidOperationException("Unsupported calendar time kind."),
        };

    public static DateTimeOffset ResolveInstant(CalendarTimeValue value, string timeZone) =>
        value.Kind switch
        {
            CalendarTimeKind.Date => ResolveLocal(
                value.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                timeZone),
            CalendarTimeKind.LocalDateTime => ResolveLocal(value.LocalDateTime, timeZone),
            CalendarTimeKind.OffsetDateTime => value.OffsetDateTime,
            _ => throw new InvalidOperationException("Unsupported calendar time kind."),
        };

    public static void ValidateRange(
        CalendarTimeValue start,
        CalendarTimeValue end,
        string timeZone)
    {
        var startIsDate = start.Kind is CalendarTimeKind.Date;
        var endIsDate = end.Kind is CalendarTimeKind.Date;
        if (startIsDate != endIsDate)
        {
            throw new ValidationException("start and end must both be dates or both be date-times.");
        }

        var valid = startIsDate
            ? end.Date > start.Date
            : ResolveInstant(end, timeZone) > ResolveInstant(start, timeZone);

        if (!valid)
        {
            throw new ValidationException("end must be after start.");
        }
    }

    public static DateTimeOffset GetSortKey(EventDateTime? value, string calendarTimeZone)
    {
        if (value?.Date is { } dateText &&
            DateOnly.TryParseExact(
                dateText,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return ResolveInstant(
                new CalendarTimeValue(CalendarTimeKind.Date, date, default, default),
                calendarTimeZone);
        }

        if (value?.DateTimeRaw is { } raw)
        {
            return ResolveInstant(ParseGoogleDateTime(raw), value.TimeZone ?? calendarTimeZone);
        }

        if (value?.DateTimeDateTimeOffset is { } offsetDateTime)
        {
            return offsetDateTime;
        }

        return DateTimeOffset.MaxValue;
    }

    public static string FormatProviderValue(EventDateTime? value)
    {
        if (value?.Date is { } date)
        {
            return date;
        }

        if (value?.DateTimeRaw is { } raw)
        {
            return NormalizeProviderDateTime(raw);
        }

        return value?.DateTimeDateTimeOffset?.ToString("O", CultureInfo.InvariantCulture) ?? "";
    }

    private static CalendarTimeValue ParseGoogleDateTime(string value) =>
        Parse(NormalizeProviderDateTime(value), "event start");

    private static string NormalizeProviderDateTime(string value) =>
        HasCompactWholeHourOffset(value) ? value + ":00" : value;

    private static bool HasCompactWholeHourOffset(string value) =>
        value.Length >= 22 &&
        value.Contains('T', StringComparison.Ordinal) &&
        value[^3] is '+' or '-' &&
        char.IsAsciiDigit(value[^2]) &&
        char.IsAsciiDigit(value[^1]);

    private static DateTimeOffset ResolveLocal(DateTime localDateTime, string timeZoneId)
    {
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ValidationException($"Unknown or invalid calendar time zone '{timeZoneId}'.", exception);
        }

        if (timeZone.IsInvalidTime(localDateTime))
        {
            throw new ValidationException(
                $"Local time '{localDateTime:yyyy-MM-ddTHH:mm:ss}' does not exist in '{timeZoneId}' because of a daylight-saving transition.");
        }

        if (timeZone.IsAmbiguousTime(localDateTime))
        {
            throw new ValidationException(
                $"Local time '{localDateTime:yyyy-MM-ddTHH:mm:ss}' is ambiguous in '{timeZoneId}'; provide an explicit offset.");
        }

        return new DateTimeOffset(localDateTime, timeZone.GetUtcOffset(localDateTime));
    }
}
