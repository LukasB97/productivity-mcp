using ProductivityMcp.Core;
using ProductivityMcp.Server;
using DomainEvent = ProductivityMcp.Core.Event;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class CalendarToolTests
{
    [TestMethod]
    public async System.Threading.Tasks.Task Create_UnsupportedVideoMeetingDoesNotCallProvider()
    {
        var provider = new UnsupportedCalendarProvider();
        var tools = new CalendarTools(provider);
        var value = new DomainEvent
        {
            Title = "Online meeting",
            Start = "2037-04-02T12:00:00",
            End = "2037-04-02T12:30:00",
            VideoMeeting = true,
        };

        var result = await tools.Create("calendar-id", value, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, provider.CreateCalls);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Update_UnsupportedVideoMeetingDoesNotCallProvider()
    {
        var provider = new UnsupportedCalendarProvider();
        var tools = new CalendarTools(provider);

        var result = await tools.Update(
            "event-id",
            new EventPatch { VideoMeeting = false },
            CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, provider.UpdateCalls);
    }

    private sealed class UnsupportedCalendarProvider : ICalendarProvider
    {
        public CalendarProviderCapabilities Capabilities { get; } = new(NativeVideoMeetings: false);

        public int CreateCalls { get; private set; }

        public int UpdateCalls { get; private set; }

        public System.Threading.Tasks.Task<OperationResult<IReadOnlyList<CalendarInfo>>> ListAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public System.Threading.Tasks.Task<OperationResult<IReadOnlyList<CalendarEvent>>> QueryEventsAsync(
            EventQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public System.Threading.Tasks.Task<OperationResult<CalendarEvent>> CreateEventAsync(
            string calendarId,
            DomainEvent @event,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            throw new InvalidOperationException("Unsupported providers must not be called.");
        }

        public System.Threading.Tasks.Task<OperationResult<CalendarEvent>> UpdateEventAsync(
            string eventId,
            EventPatch patch,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            throw new InvalidOperationException("Unsupported providers must not be called.");
        }

        public System.Threading.Tasks.Task<OperationResult<Unit>> DeleteEventAsync(
            string eventId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
