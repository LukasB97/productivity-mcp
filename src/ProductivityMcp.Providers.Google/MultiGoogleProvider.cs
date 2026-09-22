using ProductivityMcp.Core;
using DomainEvent = ProductivityMcp.Core.Event;
using DomainTask = ProductivityMcp.Core.Task;

namespace ProductivityMcp.Providers.Google;

public sealed class MultiGoogleProvider : ICalendarProvider, ITasksProvider
{
    private readonly GoogleAccountCatalog _accounts;
    private readonly Func<GoogleCalendarProvider[]> _calendarProviders;

    public MultiGoogleProvider(GoogleOptions options)
    {
        _accounts = new(options);
        _calendarProviders = () => _accounts.List()
            .Where(account => account.CalendarTasksEnabled)
            .Select(account => new GoogleCalendarProvider(new GoogleServiceFactory(_accounts.OptionsFor(account.Key))))
            .ToArray();
    }

    internal MultiGoogleProvider(GoogleOptions options, Func<GoogleCalendarProvider[]> calendarProviders) : this(options)
    {
        _calendarProviders = calendarProviders;
    }

    public CalendarProviderCapabilities Capabilities { get; } = new(NativeVideoMeetings: true);

    public async System.Threading.Tasks.Task<OperationResult<IReadOnlyList<CalendarInfo>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var providers = CalendarProviders();
        if (providers.Length == 0) return NoAccounts<IReadOnlyList<CalendarInfo>>();

        var calendars = new List<CalendarInfo>();
        foreach (var provider in providers)
        {
            var result = await provider.ListAsync(cancellationToken).ConfigureAwait(false);
            if (result is OperationResult<IReadOnlyList<CalendarInfo>>.Failure failure)
            {
                return OperationResult.Fail<IReadOnlyList<CalendarInfo>>(failure.Error);
            }

            calendars.AddRange(((OperationResult<IReadOnlyList<CalendarInfo>>.Success)result).Value);
        }

        return OperationResult.Ok<IReadOnlyList<CalendarInfo>>(
            calendars.DistinctBy(calendar => calendar.Id).ToArray());
    }

    public async System.Threading.Tasks.Task<OperationResult<IReadOnlyList<CalendarEvent>>> QueryEventsAsync(
        EventQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.CalendarId is not null)
        {
            var resolved = await ResolveCalendarProviderAsync(query.CalendarId, cancellationToken)
                .ConfigureAwait(false);
            return resolved is OperationResult<GoogleCalendarProvider>.Failure failure
                ? OperationResult.Fail<IReadOnlyList<CalendarEvent>>(failure.Error)
                : await ((OperationResult<GoogleCalendarProvider>.Success)resolved).Value.QueryEventsAsync(query, cancellationToken).ConfigureAwait(false);
        }

        var providers = CalendarProviders();
        if (providers.Length == 0) return NoAccounts<IReadOnlyList<CalendarEvent>>();

        var events = new List<CalendarEvent>();
        foreach (var provider in providers)
        {
            var result = await provider.QueryEventsAsync(query, cancellationToken).ConfigureAwait(false);
            if (result is OperationResult<IReadOnlyList<CalendarEvent>>.Failure failure)
            {
                return OperationResult.Fail<IReadOnlyList<CalendarEvent>>(failure.Error);
            }

            events.AddRange(((OperationResult<IReadOnlyList<CalendarEvent>>.Success)result).Value);
            if (events.DistinctBy(item => (item.CalendarId, item.Id)).Take(5001).Count() > 5000)
                return OperationResult.Fail<IReadOnlyList<CalendarEvent>>(new(OperationErrorCode.Validation,
                    "The query exceeds 5000 events. Narrow the calendar or time range; no partial result was returned."));
        }

        return OperationResult.Ok<IReadOnlyList<CalendarEvent>>(
            events.DistinctBy(item => (item.CalendarId, item.Id)).ToArray());
    }

    public async System.Threading.Tasks.Task<OperationResult<CalendarEvent>> CreateEventAsync(
        string calendarId,
        DomainEvent @event,
        CancellationToken cancellationToken = default)
    {
        var provider = await ResolveCalendarProviderAsync(calendarId, cancellationToken, requireWrite: true).ConfigureAwait(false);
        return provider is OperationResult<GoogleCalendarProvider>.Failure failure
            ? OperationResult.Fail<CalendarEvent>(failure.Error)
            : await ((OperationResult<GoogleCalendarProvider>.Success)provider).Value.CreateEventAsync(calendarId, @event, cancellationToken).ConfigureAwait(false);
    }

    public System.Threading.Tasks.Task<OperationResult<CalendarEvent>> UpdateEventAsync(
        string eventId,
        EventPatch patch,
        string? calendarId = null,
        CancellationToken cancellationToken = default) =>
        MutateEventAsync(eventId, calendarId,
            (provider, resolvedCalendarId) => provider.UpdateEventAsync(eventId, patch, resolvedCalendarId, cancellationToken),
            cancellationToken);

    public System.Threading.Tasks.Task<OperationResult<Unit>> DeleteEventAsync(
        string eventId,
        string? calendarId = null,
        CancellationToken cancellationToken = default) =>
        MutateEventAsync(eventId, calendarId,
            (provider, resolvedCalendarId) => provider.DeleteEventAsync(eventId, resolvedCalendarId, cancellationToken),
            cancellationToken);

    public async System.Threading.Tasks.Task<OperationResult<IReadOnlyList<TaskListInfo>>> ListTaskListsAsync(
        CancellationToken cancellationToken = default)
    {
        var providers = TaskProviders();
        if (providers.Length == 0) return NoAccounts<IReadOnlyList<TaskListInfo>>();

        var taskLists = new List<TaskListInfo>();
        foreach (var provider in providers)
        {
            var result = await provider.ListTaskListsAsync(cancellationToken).ConfigureAwait(false);
            if (result is OperationResult<IReadOnlyList<TaskListInfo>>.Failure failure)
            {
                return OperationResult.Fail<IReadOnlyList<TaskListInfo>>(failure.Error);
            }

            taskLists.AddRange(((OperationResult<IReadOnlyList<TaskListInfo>>.Success)result).Value);
        }

        return OperationResult.Ok<IReadOnlyList<TaskListInfo>>(
            taskLists.DistinctBy(taskList => taskList.Id).ToArray());
    }

    public async System.Threading.Tasks.Task<OperationResult<IReadOnlyList<TodoTask>>> ListTasksAsync(
        string taskListId,
        CancellationToken cancellationToken = default)
    {
        var provider = await ResolveTaskProviderAsync(taskListId, cancellationToken).ConfigureAwait(false);
        return provider is null
            ? NotFound<IReadOnlyList<TodoTask>>("task list", taskListId)
            : await provider.ListTasksAsync(taskListId, cancellationToken).ConfigureAwait(false);
    }

    public async System.Threading.Tasks.Task<OperationResult<TodoTask>> CreateTaskAsync(
        string taskListId,
        DomainTask task,
        CancellationToken cancellationToken = default)
    {
        var provider = await ResolveTaskProviderAsync(taskListId, cancellationToken).ConfigureAwait(false);
        return provider is null
            ? NotFound<TodoTask>("task list", taskListId)
            : await provider.CreateTaskAsync(taskListId, task, cancellationToken).ConfigureAwait(false);
    }

    public System.Threading.Tasks.Task<OperationResult<TodoTask>> UpdateTaskAsync(
        string taskId,
        TaskPatch patch,
        CancellationToken cancellationToken = default) =>
        TryTaskAccountsAsync(
            provider => provider.UpdateTaskAsync(taskId, patch, cancellationToken),
            "task",
            taskId);

    public System.Threading.Tasks.Task<OperationResult<TodoTask>> CompleteTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default) =>
        TryTaskAccountsAsync(
            provider => provider.CompleteTaskAsync(taskId, cancellationToken),
            "task",
            taskId);

    public System.Threading.Tasks.Task<OperationResult<Unit>> DeleteTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default) =>
        TryTaskAccountsAsync(
            provider => provider.DeleteTaskAsync(taskId, cancellationToken),
            "task",
            taskId);

    private GoogleCalendarProvider[] CalendarProviders() => _calendarProviders();

    private GoogleTasksProvider[] TaskProviders() => _accounts.List()
        .Where(account => account.CalendarTasksEnabled)
        .Select(account => new GoogleTasksProvider(new GoogleServiceFactory(_accounts.OptionsFor(account.Key))))
        .ToArray();

    private async System.Threading.Tasks.Task<OperationResult<GoogleCalendarProvider>> ResolveCalendarProviderAsync(
        string calendarId,
        CancellationToken cancellationToken,
        bool requireWrite = false)
    {
        var providers = CalendarProviders();
        if (providers.Length == 0) return NoAccounts<GoogleCalendarProvider>();
        OperationError? error = null;
        var foundReadOnly = false;
        foreach (var provider in providers)
        {
            var result = await provider.ListAsync(cancellationToken).ConfigureAwait(false);
            if (result is OperationResult<IReadOnlyList<CalendarInfo>>.Failure failure)
            {
                error ??= failure.Error;
                continue;
            }
            var calendar = ((OperationResult<IReadOnlyList<CalendarInfo>>.Success)result).Value.FirstOrDefault(calendar => calendar.Id == calendarId);
            if (calendar is not null)
            {
                if (!requireWrite || CanWrite(calendar)) return OperationResult.Ok(provider);
                foundReadOnly = true;
            }
        }
        if (error is not null) return OperationResult.Fail<GoogleCalendarProvider>(error);
        return foundReadOnly
            ? OperationResult.Fail<GoogleCalendarProvider>(new(OperationErrorCode.PermissionDenied, "None of the connected accounts has write access to this calendar."))
            : NotFound<GoogleCalendarProvider>("calendar", calendarId);
    }

    private async System.Threading.Tasks.Task<GoogleTasksProvider?> ResolveTaskProviderAsync(
        string taskListId,
        CancellationToken cancellationToken)
    {
        foreach (var provider in TaskProviders())
        {
            var result = await provider.ListTaskListsAsync(cancellationToken).ConfigureAwait(false);
            if (result is OperationResult<IReadOnlyList<TaskListInfo>>.Success success &&
                success.Value.Any(taskList => taskList.Id == taskListId))
            {
                return provider;
            }
        }

        return null;
    }

    private async System.Threading.Tasks.Task<OperationResult<T>> MutateEventAsync<T>(
        string eventId,
        string? calendarId,
        Func<GoogleCalendarProvider, string, System.Threading.Tasks.Task<OperationResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        var providers = CalendarProviders();
        if (providers.Length == 0) return NoAccounts<T>();
        if (calendarId is not null && string.IsNullOrWhiteSpace(calendarId))
            return OperationResult.Fail<T>(new OperationError(OperationErrorCode.Validation, "calendarId must not be empty.", "calendarId"));

        if (calendarId is not null)
        {
            var resolved = await ResolveCalendarProviderAsync(calendarId, cancellationToken, requireWrite: true).ConfigureAwait(false);
            return resolved is OperationResult<GoogleCalendarProvider>.Failure failure
                ? OperationResult.Fail<T>(failure.Error)
                : await operation(((OperationResult<GoogleCalendarProvider>.Success)resolved).Value, calendarId).ConfigureAwait(false);
        }

        // Resolve every account before issuing any write. Provider event ids are only
        // unique inside a calendar, and shared calendars can appear in several accounts.
        var matches = new List<(GoogleCalendarProvider Provider, CalendarInfo Calendar)>();
        foreach (var provider in providers)
        {
            var listed = await provider.ListAsync(cancellationToken).ConfigureAwait(false);
            if (listed is OperationResult<IReadOnlyList<CalendarInfo>>.Failure listFailure)
                return OperationResult.Fail<T>(listFailure.Error);
            foreach (var calendar in ((OperationResult<IReadOnlyList<CalendarInfo>>.Success)listed).Value)
            {
                var result = await provider.GetEventAsync(calendar.Id, eventId, cancellationToken).ConfigureAwait(false);
                if (result is OperationResult<CalendarEvent>.Success) matches.Add((provider, calendar));
                else if (result is OperationResult<CalendarEvent>.Failure failure && failure.Error.Code != OperationErrorCode.NotFound)
                    return OperationResult.Fail<T>(failure.Error);
            }
        }
        if (matches.Count == 0) return NotFound<T>("event", eventId);
        if (matches.Select(match => match.Calendar.Id).Distinct(StringComparer.Ordinal).Skip(1).Any())
            return OperationResult.Fail<T>(new OperationError(OperationErrorCode.Conflict,
                "The event id exists in multiple calendars. Supply calendarId; nothing was changed.", "calendarId"));
        var writable = matches.FirstOrDefault(match => CanWrite(match.Calendar));
        if (writable.Provider is null)
            return OperationResult.Fail<T>(new OperationError(OperationErrorCode.PermissionDenied,
                "None of the connected accounts has write access to this calendar."));
        return await operation(writable.Provider, writable.Calendar.Id).ConfigureAwait(false);
    }

    private static bool CanWrite(CalendarInfo calendar) => calendar.AccessRole is "owner" or "writer" or "writerWithoutPrivateAccess";

    private async System.Threading.Tasks.Task<OperationResult<T>> TryTaskAccountsAsync<T>(
        Func<GoogleTasksProvider, System.Threading.Tasks.Task<OperationResult<T>>> operation,
        string resource,
        string id)
    {
        var providers = TaskProviders();
        if (providers.Length == 0) return NoAccounts<T>();

        foreach (var provider in providers)
        {
            var result = await operation(provider).ConfigureAwait(false);
            if (result is OperationResult<T>.Success) return result;
            if (((OperationResult<T>.Failure)result).Error.Code != OperationErrorCode.NotFound) return result;
        }

        return NotFound<T>(resource, id);
    }

    private static OperationResult<T> NoAccounts<T>() => OperationResult.Fail<T>(new OperationError(
        OperationErrorCode.Configuration,
        "No Google account is connected. Open the Productivity MCP app and add an account."));

    private static OperationResult<T> NotFound<T>(string resource, string id) =>
        OperationResult.Fail<T>(new OperationError(
            OperationErrorCode.NotFound,
            $"The {resource} '{id}' was not found in any connected Google account."));
}
