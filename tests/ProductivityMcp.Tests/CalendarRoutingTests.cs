using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Calendar.v3;
using Google.Apis.Http;
using ProductivityMcp.Core;
using ProductivityMcp.Providers.Google;
using AsyncTask = System.Threading.Tasks.Task;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class CalendarRoutingTests
{
    private static readonly GoogleOptions Options = new()
    {
        CredentialsPath = "unused",
        TokenStorePath = "unused",
        AccountsPath = "unused",
    };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async AsyncTask AmbiguousEvent_AcrossAccountsNeverWrites(bool delete)
    {
        var first = new FakeCalendar("calendar-a");
        var second = new FakeCalendar("calendar-b");
        var provider = new MultiGoogleProvider(Options, () => [first.Provider(), second.Provider()]);

        var error = delete
            ? Assert.IsInstanceOfType<OperationResult<Unit>.Failure>(await provider.DeleteEventAsync("same-id")).Error
            : Assert.IsInstanceOfType<OperationResult<CalendarEvent>.Failure>(await provider.UpdateEventAsync("same-id", new() { Title = "Changed" })).Error;

        Assert.AreEqual(OperationErrorCode.Conflict, error.Code);
        Assert.AreEqual("calendarId", error.Field);
        Assert.AreEqual(0, first.Writes + second.Writes);
    }

    [TestMethod]
    public async AsyncTask ExplicitCalendar_UpdatesOnlyRequestedTarget()
    {
        var first = new FakeCalendar("calendar-a");
        var second = new FakeCalendar("calendar-b");
        var provider = new MultiGoogleProvider(Options, () => [first.Provider(), second.Provider()]);

        var result = await provider.UpdateEventAsync("same-id", new() { Title = "Changed" }, "calendar-b");

        Assert.IsInstanceOfType<OperationResult<CalendarEvent>.Success>(result);
        Assert.AreEqual(0, first.Writes);
        Assert.AreEqual(1, second.Writes);
        Assert.IsTrue(second.ReadCalendarListEntry);
        Assert.IsFalse(second.ReadCalendarResource);
    }

    [TestMethod]
    public async AsyncTask SharedCalendar_PrefersWriterOverFirstReader()
    {
        var reader = new FakeCalendar("shared", "reader");
        var writer = new FakeCalendar("shared");
        var provider = new MultiGoogleProvider(Options, () => [reader.Provider(), writer.Provider()]);

        Assert.IsInstanceOfType<OperationResult<Unit>.Success>(await provider.DeleteEventAsync("same-id"));
        Assert.AreEqual(0, reader.Writes);
        Assert.AreEqual(1, writer.Writes);
    }

    [TestMethod]
    public async AsyncTask FailedLookup_DoesNotMutatePreviouslyFoundEvent()
    {
        var first = new FakeCalendar("calendar-a");
        var second = new FakeCalendar("calendar-b", status: HttpStatusCode.Forbidden);
        var provider = new MultiGoogleProvider(Options, () => [first.Provider(), second.Provider()]);

        var failure = Assert.IsInstanceOfType<OperationResult<Unit>.Failure>(await provider.DeleteEventAsync("same-id"));

        Assert.AreEqual(OperationErrorCode.PermissionDenied, failure.Error.Code);
        Assert.AreEqual(0, first.Writes + second.Writes);
    }

    [TestMethod]
    public async AsyncTask ExplicitCalendar_RemainsUsableWhenAnotherAccountFails()
    {
        var target = new FakeCalendar("calendar-a");
        var unrelated = new FakeCalendar("calendar-b", status: HttpStatusCode.Forbidden);
        var provider = new MultiGoogleProvider(Options, () => [unrelated.Provider(), target.Provider()]);
        Assert.IsInstanceOfType<OperationResult<Unit>.Success>(await provider.DeleteEventAsync("same-id", "calendar-a"));
        Assert.AreEqual(1, target.Writes);
        Assert.AreEqual(0, unrelated.Writes);
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("writer")]
    [DataRow("writerWithoutPrivateAccess")]
    public async AsyncTask WritableCalendarRoles_CanDelete(string role)
    {
        var target = new FakeCalendar("calendar-a", role);
        var provider = new MultiGoogleProvider(Options, () => [target.Provider()]);
        Assert.IsInstanceOfType<OperationResult<Unit>.Success>(await provider.DeleteEventAsync("same-id", "calendar-a"));
        Assert.AreEqual(1, target.Writes);
    }

    [TestMethod]
    public async AsyncTask CalendarCreate_UsesCalendarListPermissionForPreflight()
    {
        var fake = new FakeCalendar("calendar-a");
        var result = await fake.Provider().CreateEventAsync("calendar-a", new()
        {
            Title = "Meeting",
            Start = "2037-04-02T12:00:00Z",
            End = "2037-04-02T12:30:00Z",
        });
        Assert.IsInstanceOfType<OperationResult<CalendarEvent>.Success>(result);
        Assert.IsTrue(fake.ReadCalendarListEntry);
        Assert.IsFalse(fake.ReadCalendarResource);
        Assert.AreEqual(1, fake.Writes);
    }

    private sealed class FakeCalendar(string calendarId, string accessRole = "owner", HttpStatusCode status = HttpStatusCode.OK)
    {
        public int Writes { get; private set; }
        public bool ReadCalendarListEntry { get; private set; }
        public bool ReadCalendarResource { get; private set; }

        public GoogleCalendarProvider Provider() => new(new GoogleServiceFactory(Options,
            () => new CalendarService(new() { HttpClientFactory = new FakeHttpFactory(Respond) })));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var entry = new { id = calendarId, summary = "Test", timeZone = "UTC", accessRole };
            object body;
            if (request.Method != HttpMethod.Get)
            {
                Writes++;
                body = Event();
            }
            else if (path.EndsWith("/users/me/calendarList", StringComparison.Ordinal))
                body = new { items = new[] { entry } };
            else if (path.Contains("/users/me/calendarList/", StringComparison.Ordinal))
            {
                ReadCalendarListEntry = true;
                body = entry;
            }
            else if (path.Contains("/events/", StringComparison.Ordinal)) body = Event();
            else
            {
                ReadCalendarResource = true;
                throw new InvalidOperationException($"Unexpected calendar endpoint: {path}");
            }
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(status == HttpStatusCode.OK ? body
                    : new { error = new { code = (int)status, message = "Denied" } }), Encoding.UTF8, "application/json"),
            };
        }

        private static object Event() => new
        {
            id = "same-id",
            summary = "Meeting",
            etag = "\"test\"",
            start = new { dateTime = "2037-04-02T12:00:00Z" },
            end = new { dateTime = "2037-04-02T12:30:00Z" },
        };
    }

    private sealed class FakeHttpFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) =>
            new(new ConfigurableMessageHandler(new FakeHandler(respond)));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(respond(request));
    }
}
