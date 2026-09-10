using System.Net;
using System.Globalization;
using System.ComponentModel.DataAnnotations;
using Google;
using Google.Apis;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using ProductivityMcp.Core;
using DomainEvent = ProductivityMcp.Core.Event;
using GoogleEvent = Google.Apis.Calendar.v3.Data.Event;
using GoogleCalendar = Google.Apis.Calendar.v3.Data.Calendar;

namespace ProductivityMcp.Providers.Google;

public sealed class GoogleCalendarProvider(GoogleServiceFactory serviceFactory) : ICalendarProvider
{
    private const string GoogleMeetConferenceType = "hangoutsMeet";

    public CalendarProviderCapabilities Capabilities { get; } = new(NativeVideoMeetings: true);

    public System.Threading.Tasks.Task<OperationResult<IReadOnlyList<CalendarInfo>>> ListAsync(
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => ListCoreAsync(cancellationToken));

    private async System.Threading.Tasks.Task<IReadOnlyList<CalendarInfo>> ListCoreAsync(
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateCalendarAsync(cancellationToken).ConfigureAwait(false);
        return await ListCalendarsAsync(service, cancellationToken).ConfigureAwait(false);
    }

    private static async System.Threading.Tasks.Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(
        CalendarService service,
        CancellationToken cancellationToken)
    {
        var result = new List<CalendarInfo>();
        string? pageToken = null;

        do
        {
            var request = service.CalendarList.List();
            request.PageToken = pageToken;
            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            result.AddRange((page.Items ?? []).Select(calendar => new CalendarInfo(
                calendar.Id,
                calendar.SummaryOverride ?? calendar.Summary ?? calendar.Id,
                "google",
                calendar.Primary ?? false,
                calendar.TimeZone)));

            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return result;
    }

    public System.Threading.Tasks.Task<OperationResult<IReadOnlyList<CalendarEvent>>> QueryEventsAsync(
        EventQuery query,
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => QueryEventsCoreAsync(query, cancellationToken));

    private async System.Threading.Tasks.Task<IReadOnlyList<CalendarEvent>> QueryEventsCoreAsync(
        EventQuery query,
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateCalendarAsync(cancellationToken).ConfigureAwait(false);
        var calendars = query.CalendarId is not null
            ? new[]
            {
                (
                    Id: query.CalendarId,
                    TimeZone: await GetCalendarTimeZoneAsync(service, query.CalendarId, cancellationToken)
                        .ConfigureAwait(false)
                ),
            }
            : (await ListCalendarsAsync(service, cancellationToken).ConfigureAwait(false))
                .Select(calendar => (
                    calendar.Id,
                    TimeZone: string.IsNullOrWhiteSpace(calendar.TimeZone) ? "UTC" : calendar.TimeZone))
                .ToArray();

        var result = new List<(CalendarEvent Event, DateTimeOffset SortKey)>();
        foreach (var calendar in calendars)
        {
            var timeMin = ParseOptionalBound(query.From, nameof(query.From), calendar.TimeZone);
            var timeMax = ParseOptionalBound(query.To, nameof(query.To), calendar.TimeZone);
            if (timeMin is not null && timeMax is not null && timeMax <= timeMin)
            {
                throw new ValidationException("to must be after from.");
            }

            string? pageToken = null;
            do
            {
                var request = service.Events.List(calendar.Id);
                request.PageToken = pageToken;
                request.SingleEvents = true;
                request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                request.Q = query.Text;
                request.TimeMinDateTimeOffset = timeMin;
                request.TimeMaxDateTimeOffset = timeMax;
                request.TimeZone = calendar.TimeZone;

                var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                result.AddRange((page.Items ?? []).Select(item => (
                    MapEvent(calendar.Id, item),
                    GoogleTemporal.GetSortKey(item.Start, calendar.TimeZone))));
                pageToken = page.NextPageToken;
            }
            while (pageToken is not null);
        }

        return result.OrderBy(item => item.SortKey).Select(item => item.Event).ToArray();
    }

    public System.Threading.Tasks.Task<OperationResult<CalendarEvent>> CreateEventAsync(
        string calendarId,
        DomainEvent @event,
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => CreateEventCoreAsync(calendarId, @event, cancellationToken));

    private async System.Threading.Tasks.Task<CalendarEvent> CreateEventCoreAsync(
        string calendarId,
        DomainEvent @event,
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateCalendarAsync(cancellationToken).ConfigureAwait(false);
        var calendar = await GetCalendarAsync(service, calendarId, cancellationToken).ConfigureAwait(false);
        var calendarTimeZone = GetCalendarTimeZone(calendar);
        if (@event.VideoMeeting)
        {
            EnsureVideoMeetingSupported(calendar, "event.videoMeeting");
        }

        var request = service.Events.Insert(ToGoogleEvent(@event, calendarTimeZone), calendarId);
        request.SendUpdates = EventsResource.InsertRequest.SendUpdatesEnum.All;
        if (@event.VideoMeeting)
        {
            request.ConferenceDataVersion = 1;
        }

        var created = await request.ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return MapEvent(calendarId, created);
    }

    public System.Threading.Tasks.Task<OperationResult<CalendarEvent>> UpdateEventAsync(
        string eventId,
        EventPatch patch,
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => UpdateEventCoreAsync(eventId, patch, cancellationToken));

    private async System.Threading.Tasks.Task<CalendarEvent> UpdateEventCoreAsync(
        string eventId,
        EventPatch patch,
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateCalendarAsync(cancellationToken).ConfigureAwait(false);
        var located = await FindEventAsync(service, eventId, cancellationToken).ConfigureAwait(false);
        var calendar = await GetCalendarAsync(service, located.CalendarId, cancellationToken).ConfigureAwait(false);
        var calendarTimeZone = GetCalendarTimeZone(calendar);
        if (patch.HasVideoMeeting && patch.VideoMeeting)
        {
            EnsureVideoMeetingSupported(calendar, "patch.videoMeeting");
        }

        ApplyPatch(located.Event, patch, calendarTimeZone);
        ValidateEventRange(located.Event.Start, located.Event.End, calendarTimeZone);
        var request = service.Events.Update(located.Event, located.CalendarId, eventId);
        request.ETagAction = ETagAction.IfMatch;
        request.SendUpdates = EventsResource.UpdateRequest.SendUpdatesEnum.All;
        if (patch.HasVideoMeeting)
        {
            request.ConferenceDataVersion = 1;
        }

        var updated = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return MapEvent(located.CalendarId, updated);
    }

    public System.Threading.Tasks.Task<OperationResult<Unit>> DeleteEventAsync(
        string eventId,
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => DeleteEventCoreAsync(eventId, cancellationToken));

    private async System.Threading.Tasks.Task<Unit> DeleteEventCoreAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateCalendarAsync(cancellationToken).ConfigureAwait(false);
        var located = await FindEventAsync(service, eventId, cancellationToken).ConfigureAwait(false);
        var request = service.Events.Delete(located.CalendarId, eventId);
        request.SendUpdates = EventsResource.DeleteRequest.SendUpdatesEnum.All;
        await request.ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return default;
    }

    private async System.Threading.Tasks.Task<(string CalendarId, GoogleEvent Event)> FindEventAsync(
        CalendarService service,
        string eventId,
        CancellationToken cancellationToken)
    {
        (string CalendarId, GoogleEvent Event)? found = null;

        foreach (var calendar in await ListCalendarsAsync(service, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var item = await service.Events.Get(calendar.Id, eventId)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (found is not null)
                {
                    throw new InvalidOperationException(
                        $"Event id '{eventId}' exists in more than one calendar.");
                }

                found = (calendar.Id, item);
            }
            catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.NotFound)
            {
                // The public MCP signature deliberately uses only the provider event id.
            }
        }

        return found ?? throw new KeyNotFoundException($"Event '{eventId}' was not found.");
    }

    internal static GoogleEvent ToGoogleEvent(DomainEvent source, string calendarTimeZone)
    {
        var start = GoogleTemporal.Parse(source.Start, nameof(source.Start));
        var end = GoogleTemporal.Parse(source.End, nameof(source.End));
        GoogleTemporal.ValidateRange(start, end, calendarTimeZone);

        return new GoogleEvent
        {
            Summary = source.Title,
            Start = GoogleTemporal.ToGoogleEventDateTime(start, calendarTimeZone),
            End = GoogleTemporal.ToGoogleEventDateTime(end, calendarTimeZone),
            Description = source.Description,
            Location = source.Location,
            Attendees = source.Attendees?.Select(email => new EventAttendee { Email = email }).ToList(),
            ConferenceData = source.VideoMeeting ? CreateGoogleMeetConferenceData() : null,
        };
    }

    internal static void ApplyPatch(GoogleEvent target, EventPatch patch, string calendarTimeZone)
    {
        if (patch.Title is not null) target.Summary = patch.Title;
        if (patch.Start is not null)
        {
            target.Start = GoogleTemporal.ToGoogleEventDateTime(
                GoogleTemporal.Parse(patch.Start, nameof(patch.Start)),
                calendarTimeZone);
        }
        if (patch.End is not null)
        {
            target.End = GoogleTemporal.ToGoogleEventDateTime(
                GoogleTemporal.Parse(patch.End, nameof(patch.End)),
                calendarTimeZone);
        }
        if (patch.HasDescription) target.Description = patch.Description;
        if (patch.HasLocation) target.Location = patch.Location;
        if (patch.HasAttendees)
        {
            target.Attendees = MergeAttendees(target.Attendees, patch.Attendees);
        }
        if (patch.HasVideoMeeting)
        {
            target.ConferenceData = patch.VideoMeeting ? CreateGoogleMeetConferenceData() : null;
        }
    }

    internal static ConferenceData CreateGoogleMeetConferenceData() => new()
    {
        CreateRequest = new CreateConferenceRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ConferenceSolutionKey = new ConferenceSolutionKey
            {
                Type = GoogleMeetConferenceType,
            },
        },
    };

    internal static void EnsureVideoMeetingSupported(GoogleCalendar calendar, string field)
    {
        if (calendar.ConferenceProperties?.AllowedConferenceSolutionTypes?.Contains(
                GoogleMeetConferenceType,
                StringComparer.Ordinal) is true)
        {
            return;
        }

        throw new UnsupportedFeatureException(
            "The selected calendar does not support provider-native video meetings.",
            field);
    }

    internal static IList<EventAttendee> MergeAttendees(
        IList<EventAttendee>? existingAttendees,
        IReadOnlyList<string>? requestedEmails)
    {
        if (requestedEmails is null)
        {
            return [];
        }

        var existingByEmail = (existingAttendees ?? [])
            .Where(attendee => !string.IsNullOrWhiteSpace(attendee.Email))
            .GroupBy(attendee => attendee.Email, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return requestedEmails
            .Select(email => existingByEmail.TryGetValue(email, out var existing)
                ? existing
                : new EventAttendee { Email = email })
            .ToList();
    }

    private static async System.Threading.Tasks.Task<string> GetCalendarTimeZoneAsync(
        CalendarService service,
        string calendarId,
        CancellationToken cancellationToken)
    {
        var calendar = await GetCalendarAsync(service, calendarId, cancellationToken).ConfigureAwait(false);
        return GetCalendarTimeZone(calendar);
    }

    private static async System.Threading.Tasks.Task<GoogleCalendar> GetCalendarAsync(
        CalendarService service,
        string calendarId,
        CancellationToken cancellationToken) =>
        await service.Calendars.Get(calendarId)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

    private static string GetCalendarTimeZone(GoogleCalendar calendar) =>
        string.IsNullOrWhiteSpace(calendar.TimeZone) ? "UTC" : calendar.TimeZone;

    private static DateTimeOffset? ParseOptionalBound(string? value, string field, string calendarTimeZone) =>
        value is null
            ? null
            : GoogleTemporal.ResolveInstant(GoogleTemporal.Parse(value, field), calendarTimeZone);

    private static void ValidateEventRange(
        EventDateTime? start,
        EventDateTime? end,
        string calendarTimeZone)
    {
        if (start is null || end is null)
        {
            throw new ValidationException("start and end are required.");
        }

        var startIsDate = start.Date is not null;
        var endIsDate = end.Date is not null;
        if (startIsDate != endIsDate)
        {
            throw new ValidationException("start and end must both be dates or both be date-times.");
        }

        if (GoogleTemporal.GetSortKey(end, calendarTimeZone) <=
            GoogleTemporal.GetSortKey(start, calendarTimeZone))
        {
            throw new ValidationException("end must be after start.");
        }
    }

    internal static CalendarEvent MapEvent(string calendarId, GoogleEvent source) => new(
        source.Id,
        calendarId,
        source.Summary ?? "",
        GoogleTemporal.FormatProviderValue(source.Start),
        GoogleTemporal.FormatProviderValue(source.End),
        source.Description,
        source.Location,
        source.Attendees?.Where(item => item.Email is not null).Select(item => item.Email).ToArray() ?? [],
        MapVideoMeeting(source));

    private static VideoMeetingInfo? MapVideoMeeting(GoogleEvent source)
    {
        var joinUrl = source.ConferenceData?.EntryPoints?
                          .FirstOrDefault(entry =>
                              string.Equals(entry.EntryPointType, "video", StringComparison.Ordinal) &&
                              !string.IsNullOrWhiteSpace(entry.Uri))
                          ?.Uri
                      ?? source.HangoutLink;
        if (string.IsNullOrWhiteSpace(joinUrl))
        {
            return null;
        }

        var provider = string.Equals(
            source.ConferenceData?.ConferenceSolution?.Key?.Type,
            GoogleMeetConferenceType,
            StringComparison.Ordinal)
            ? "googleMeet"
            : "google";
        return new VideoMeetingInfo(provider, joinUrl);
    }

}
