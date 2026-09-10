using ProductivityMcp.Core;
using DomainEvent = ProductivityMcp.Core.Event;
using DomainTask = ProductivityMcp.Core.Task;

namespace ProductivityMcp.Providers.Google;

public sealed class MultiGoogleProvider(GoogleOptions options) : ICalendarProvider, ITasksProvider
{
    private readonly GoogleAccountCatalog _accounts = new(options);

    public CalendarProviderCapabilities Capabilities { get; } = new(NativeVideoMeetings: true);

    public async System.Threading.Tasks.Task<OperationResult<IReadOnlyList<CalendarInfo>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var providers = CalendarProviders();
        if (providers.Count == 0) return NoAccounts<IReadOnlyList<CalendarInfo>>();

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
            return resolved is null
                ? NotFound<IReadOnlyList<CalendarEvent>>("calendar", query.CalendarId)
                : await resolved.QueryEventsAsync(query, cancellationToken).ConfigureAwait(false);
        }

        var providers = CalendarProviders();
        if (providers.Count == 0) return NoAccounts<IReadOnlyList<CalendarEvent>>();

        var events = new List<CalendarEvent>();
        foreach (var provider in providers)
        {
            var result = await provider.QueryEventsAsync(query, cancellationToken).ConfigureAwait(false);
            if (result is OperationResult<IReadOnlyList<CalendarEvent>>.Failure failure)
            {
                return OperationResult.Fail<IReadOnlyList<CalendarEvent>>(failure.Error);
            }

            events.AddRange(((OperationResult<IReadOnlyList<CalendarEvent>>.Success)result).Value);
        }

        return OperationResult.Ok<IReadOnlyList<CalendarEvent>>(
            events.DistinctBy(item => (item.CalendarId, item.Id)).ToArray());
    }

    public async System.Threading.Tasks.Task<OperationResult<CalendarEvent>> CreateEventAsync(
        string calendarId,
        DomainEvent @event,
        CancellationToken cancellationToken = default)
    {
        var provider = await ResolveCalendarProviderAsync(calendarId, cancellationToken).ConfigureAwait(false);
        return provider is null
            ? NotFound<CalendarEvent>("calendar", calendarId)
            : await provider.CreateEventAsync(calendarId, @event, cancellationToken).ConfigureAwait(false);
    }

    public System.Threading.Tasks.Task<OperationResult<CalendarEvent>> UpdateEventAsync(
        string eventId,
        EventPatch patch,
        CancellationToken cancellationToken = default) =>
        TryCalendarAccountsAsync(
            provider => provider.UpdateEventAsync(eventId, patch, cancellationToken),
            "event",
            eventId);

    public System.Threading.Tasks.Task<OperationResult<Unit>> DeleteEventAsync(
        string eventId,
        CancellationToken cancellationToken = default) =>
        TryCalendarAccountsAsync(
            provider => provider.DeleteEventAsync(eventId, cancellationToken),
            "event",
            eventId);

    public async System.Threading.Tasks.Task<OperationResult<IReadOnlyList<TaskListInfo>>> ListTaskListsAsync(
        CancellationToken cancellationToken = default)
    {
        var providers = TaskProviders();
        if (providers.Count == 0) return NoAccounts<IReadOnlyList<TaskListInfo>>();

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

    private IReadOnlyList<GoogleCalendarProvider> CalendarProviders() => _accounts.List()
        .Select(account => new GoogleCalendarProvider(new GoogleServiceFactory(_accounts.OptionsFor(account.Key))))
        .ToArray();

    private IReadOnlyList<GoogleTasksProvider> TaskProviders() => _accounts.List()
        .Select(account => new GoogleTasksProvider(new GoogleServiceFactory(_accounts.OptionsFor(account.Key))))
        .ToArray();

    private async System.Threading.Tasks.Task<GoogleCalendarProvider?> ResolveCalendarProviderAsync(
        string calendarId,
        CancellationToken cancellationToken)
    {
        foreach (var provider in CalendarProviders())
        {
            var result = await provider.ListAsync(cancellationToken).ConfigureAwait(false);
            if (result is OperationResult<IReadOnlyList<CalendarInfo>>.Success success &&
                success.Value.Any(calendar => calendar.Id == calendarId))
            {
                return provider;
            }
        }

        return null;
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

    private async System.Threading.Tasks.Task<OperationResult<T>> TryCalendarAccountsAsync<T>(
        Func<GoogleCalendarProvider, System.Threading.Tasks.Task<OperationResult<T>>> operation,
        string resource,
        string id)
    {
        var providers = CalendarProviders();
        if (providers.Count == 0) return NoAccounts<T>();

        foreach (var provider in providers)
        {
            var result = await operation(provider).ConfigureAwait(false);
            if (result is OperationResult<T>.Success) return result;
            if (((OperationResult<T>.Failure)result).Error.Code != OperationErrorCode.NotFound) return result;
        }

        return NotFound<T>(resource, id);
    }

    private async System.Threading.Tasks.Task<OperationResult<T>> TryTaskAccountsAsync<T>(
        Func<GoogleTasksProvider, System.Threading.Tasks.Task<OperationResult<T>>> operation,
        string resource,
        string id)
    {
        var providers = TaskProviders();
        if (providers.Count == 0) return NoAccounts<T>();

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
