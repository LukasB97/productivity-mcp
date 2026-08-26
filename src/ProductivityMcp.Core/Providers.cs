namespace ProductivityMcp.Core;

public interface ICalendarProvider
{
    System.Threading.Tasks.Task<OperationResult<IReadOnlyList<CalendarInfo>>> ListAsync(CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task<OperationResult<IReadOnlyList<CalendarEvent>>> QueryEventsAsync(EventQuery query, CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task<OperationResult<CalendarEvent>> CreateEventAsync(string calendarId, Event @event, CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task<OperationResult<CalendarEvent>> UpdateEventAsync(string eventId, EventPatch patch, CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task<OperationResult<Unit>> DeleteEventAsync(string eventId, CancellationToken cancellationToken = default);
}

public interface ITasksProvider
{
    System.Threading.Tasks.Task<OperationResult<IReadOnlyList<TaskListInfo>>> ListTaskListsAsync(CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task<OperationResult<IReadOnlyList<TodoTask>>> ListTasksAsync(string taskListId, CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task<OperationResult<TodoTask>> CreateTaskAsync(string taskListId, Task task, CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task<OperationResult<TodoTask>> UpdateTaskAsync(string taskId, TaskPatch patch, CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task<OperationResult<TodoTask>> CompleteTaskAsync(string taskId, CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task<OperationResult<Unit>> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default);
}
