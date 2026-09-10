using System.ComponentModel.DataAnnotations;
using Google.Apis.Calendar.v3.Data;
using ProductivityMcp.Core;
using ProductivityMcp.Providers.Google;
using DomainEvent = ProductivityMcp.Core.Event;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class GoogleAdapterTests
{
    [TestMethod]
    public void GetSortKey_UsesTypedOffset_WhenSdkRawValueUsesWholeHourOffset()
    {
        var expected = new DateTimeOffset(2037, 4, 2, 12, 0, 0, TimeSpan.FromHours(2));
        var googleValue = new EventDateTime { DateTimeDateTimeOffset = expected };

        var actual = GoogleTemporal.GetSortKey(googleValue, "Europe/Berlin");

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void FormatProviderValue_PreservesLocalTimeAndNormalizesCompactOffset()
    {
        var local = new EventDateTime
        {
            DateTimeRaw = "2037-04-02T12:00:00",
            TimeZone = "Europe/Berlin",
        };
        var offset = new EventDateTime
        {
            DateTimeRaw = "2037-04-02T12:00:00+02",
        };

        Assert.AreEqual("2037-04-02T12:00:00", GoogleTemporal.FormatProviderValue(local));
        Assert.AreEqual("2037-04-02T12:00:00+02:00", GoogleTemporal.FormatProviderValue(offset));
    }

    [TestMethod]
    public void MergeAttendees_PreservesProviderMetadataForRetainedEmails()
    {
        var retained = new EventAttendee
        {
            Email = "existing@example.com",
            ResponseStatus = "accepted",
            Comment = "provider metadata",
            Optional = true,
            DisplayName = "Existing Person",
        };
        var removed = new EventAttendee { Email = "removed@example.com", ResponseStatus = "tentative" };

        var result = GoogleCalendarProvider.MergeAttendees(
            [retained, removed],
            ["EXISTING@example.com", "new@example.com"]);

        Assert.HasCount(2, result);
        Assert.AreSame(retained, result[0]);
        Assert.AreEqual("accepted", result[0].ResponseStatus);
        Assert.AreEqual("provider metadata", result[0].Comment);
        Assert.IsTrue(result[0].Optional);
        Assert.AreEqual("new@example.com", result[1].Email);
        Assert.IsNull(result[1].ResponseStatus);
    }

    [TestMethod]
    public void MergeAttendees_NullOrEmptyRequestedList_ClearsAllAttendees()
    {
        var existing = new[] { new EventAttendee { Email = "existing@example.com" } };

        Assert.IsEmpty(GoogleCalendarProvider.MergeAttendees(existing, null));
        Assert.IsEmpty(GoogleCalendarProvider.MergeAttendees(existing, []));
    }

    [TestMethod]
    public void EventValidation_RejectsNullAttendeeElement()
    {
        var value = new DomainEvent
        {
            Title = "Test",
            Start = "2037-04-02T12:00:00",
            End = "2037-04-02T12:30:00",
            Attendees = new string[] { null! },
        };

        var result = ModelValidation.Validate(value, "event");

        Assert.IsInstanceOfType<OperationResult<DomainEvent>.Failure>(result);
        StringAssert.Contains(((OperationResult<DomainEvent>.Failure)result).Error.Message, "must not contain null");
    }

    [TestMethod]
    public void EventPatchValidation_RejectsNullAttendeeElement()
    {
        var value = new EventPatch { Attendees = new string[] { null! } };

        var result = ModelValidation.Validate(value, "patch");

        Assert.IsInstanceOfType<OperationResult<EventPatch>.Failure>(result);
        StringAssert.Contains(((OperationResult<EventPatch>.Failure)result).Error.Message, "must not contain null");
    }

    [TestMethod]
    public void ToGoogleEvent_VideoMeetingCreatesUniqueMeetRequest()
    {
        var source = new DomainEvent
        {
            Title = "Online meeting",
            Start = "2037-04-02T12:00:00",
            End = "2037-04-02T12:30:00",
            VideoMeeting = true,
        };

        var first = GoogleCalendarProvider.ToGoogleEvent(source, "Europe/Berlin");
        var second = GoogleCalendarProvider.ToGoogleEvent(source, "Europe/Berlin");

        Assert.AreEqual("hangoutsMeet", first.ConferenceData?.CreateRequest?.ConferenceSolutionKey?.Type);
        Assert.IsFalse(string.IsNullOrWhiteSpace(first.ConferenceData?.CreateRequest?.RequestId));
        Assert.AreNotEqual(
            first.ConferenceData?.CreateRequest?.RequestId,
            second.ConferenceData?.CreateRequest?.RequestId);
    }

    [TestMethod]
    public void ApplyPatch_VideoMeetingSupportsOmittedCreateAndRemove()
    {
        var existingConference = new ConferenceData
        {
            EntryPoints = [new EntryPoint { EntryPointType = "video", Uri = "https://meet.google.com/old" }],
        };
        var target = new Google.Apis.Calendar.v3.Data.Event
        {
            Summary = "Existing",
            Start = new EventDateTime { DateTimeRaw = "2037-04-02T12:00:00" },
            End = new EventDateTime { DateTimeRaw = "2037-04-02T12:30:00" },
            ConferenceData = existingConference,
        };

        GoogleCalendarProvider.ApplyPatch(target, new EventPatch { Title = "Renamed" }, "Europe/Berlin");
        Assert.AreSame(existingConference, target.ConferenceData);

        GoogleCalendarProvider.ApplyPatch(target, new EventPatch { VideoMeeting = true }, "Europe/Berlin");
        Assert.AreEqual("hangoutsMeet", target.ConferenceData?.CreateRequest?.ConferenceSolutionKey?.Type);

        GoogleCalendarProvider.ApplyPatch(target, new EventPatch { VideoMeeting = false }, "Europe/Berlin");
        Assert.IsNull(target.ConferenceData);
    }

    [TestMethod]
    public void MapEvent_ReturnsProviderAndVideoJoinUrl()
    {
        var googleEvent = new Google.Apis.Calendar.v3.Data.Event
        {
            Id = "event-id",
            Summary = "Meeting",
            Start = new EventDateTime { DateTimeRaw = "2037-04-02T12:00:00+02:00" },
            End = new EventDateTime { DateTimeRaw = "2037-04-02T12:30:00+02:00" },
            ConferenceData = new ConferenceData
            {
                ConferenceSolution = new ConferenceSolution
                {
                    Key = new ConferenceSolutionKey { Type = "hangoutsMeet" },
                },
                EntryPoints =
                [
                    new EntryPoint { EntryPointType = "phone", Uri = "tel:+49123" },
                    new EntryPoint { EntryPointType = "video", Uri = "https://meet.google.com/abc-defg-hij" },
                ],
            },
        };

        var result = GoogleCalendarProvider.MapEvent("calendar-id", googleEvent);

        Assert.AreEqual("googleMeet", result.VideoMeeting?.Provider);
        Assert.AreEqual("https://meet.google.com/abc-defg-hij", result.VideoMeeting?.JoinUrl);
    }

    [TestMethod]
    public void EnsureVideoMeetingSupported_RejectsCalendarWithoutMeet()
    {
        var calendar = new Google.Apis.Calendar.v3.Data.Calendar
        {
            ConferenceProperties = new ConferenceProperties
            {
                AllowedConferenceSolutionTypes = ["eventHangout"],
            },
        };

        var exception = Assert.Throws<UnsupportedFeatureException>(() =>
            GoogleCalendarProvider.EnsureVideoMeetingSupported(calendar, "event.videoMeeting"));

        Assert.AreEqual("event.videoMeeting", exception.Field);
        var translated = GoogleOperation.Translate(exception);
        Assert.AreEqual(OperationErrorCode.Unsupported, translated?.Code);
        Assert.AreEqual("event.videoMeeting", translated?.Field);
    }

    [TestMethod]
    public void EventPatchValidation_AcceptsFalseVideoMeetingAsOnlyChange()
    {
        var patch = new EventPatch { VideoMeeting = false };

        var result = ModelValidation.Validate(patch, "patch");

        Assert.IsInstanceOfType<OperationResult<EventPatch>.Success>(result);
        Assert.IsTrue(patch.HasVideoMeeting);
    }
}
