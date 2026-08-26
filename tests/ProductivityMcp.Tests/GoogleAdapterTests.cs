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
}
