using System.Net;
using System.Globalization;
using Google;
using Google.Apis;
using Google.Apis.Tasks.v1;
using ProductivityMcp.Core;
using DomainTask = ProductivityMcp.Core.Task;
using GoogleTask = Google.Apis.Tasks.v1.Data.Task;

namespace ProductivityMcp.Providers.Google;

public sealed class GoogleTasksProvider(GoogleServiceFactory serviceFactory) : ITasksProvider
{
    public System.Threading.Tasks.Task<OperationResult<IReadOnlyList<TaskListInfo>>> ListTaskListsAsync(
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => ListTaskListsCoreAsync(cancellationToken));

    private async System.Threading.Tasks.Task<IReadOnlyList<TaskListInfo>> ListTaskListsCoreAsync(
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateTasksAsync(cancellationToken).ConfigureAwait(false);
        return await ListTaskListsWithServiceAsync(service, cancellationToken).ConfigureAwait(false);
    }

    private static async System.Threading.Tasks.Task<IReadOnlyList<TaskListInfo>> ListTaskListsWithServiceAsync(
        TasksService service,
        CancellationToken cancellationToken)
    {
        var result = new List<TaskListInfo>();
        string? pageToken = null;

        do
        {
            var request = service.Tasklists.List();
            request.PageToken = pageToken;
            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            result.AddRange((page.Items ?? []).Select(list => new TaskListInfo(
                list.Id,
                list.Title ?? list.Id,
                "google",
                null)));

            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return result;
    }

    public System.Threading.Tasks.Task<OperationResult<IReadOnlyList<TodoTask>>> ListTasksAsync(
        string taskListId,
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => ListTasksCoreAsync(taskListId, cancellationToken));

    private async System.Threading.Tasks.Task<IReadOnlyList<TodoTask>> ListTasksCoreAsync(
        string taskListId,
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateTasksAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<TodoTask>();
        string? pageToken = null;

        do
        {
            var request = service.Tasks.List(taskListId);
            request.PageToken = pageToken;
            request.ShowCompleted = true;
            request.ShowHidden = true;
            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            result.AddRange((page.Items ?? []).Select(item => MapTask(taskListId, item)));
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return result;
    }

    public System.Threading.Tasks.Task<OperationResult<TodoTask>> CreateTaskAsync(
        string taskListId,
        DomainTask task,
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => CreateTaskCoreAsync(taskListId, task, cancellationToken));

    private async System.Threading.Tasks.Task<TodoTask> CreateTaskCoreAsync(
        string taskListId,
        DomainTask task,
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateTasksAsync(cancellationToken).ConfigureAwait(false);
        var created = await service.Tasks.Insert(new GoogleTask
        {
            Title = task.Title,
            Notes = task.Notes,
            Due = ParseDue(task.Due),
        }, taskListId).ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return MapTask(taskListId, created);
    }

    public System.Threading.Tasks.Task<OperationResult<TodoTask>> UpdateTaskAsync(
        string taskId,
        TaskPatch patch,
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => UpdateTaskCoreAsync(taskId, patch, cancellationToken));

    private async System.Threading.Tasks.Task<TodoTask> UpdateTaskCoreAsync(
        string taskId,
        TaskPatch patch,
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateTasksAsync(cancellationToken).ConfigureAwait(false);
        var located = await FindTaskAsync(service, taskId, cancellationToken).ConfigureAwait(false);
        var body = located.Task;

        if (patch.Title is not null) body.Title = patch.Title;
        if (patch.HasNotes) body.Notes = patch.Notes;
        if (patch.HasDue) body.Due = ParseDue(patch.Due);

        var request = service.Tasks.Update(body, located.TaskListId, taskId);
        request.ETagAction = ETagAction.IfMatch;
        var updated = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return MapTask(located.TaskListId, updated);
    }

    public System.Threading.Tasks.Task<OperationResult<TodoTask>> CompleteTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => CompleteTaskCoreAsync(taskId, cancellationToken));

    private async System.Threading.Tasks.Task<TodoTask> CompleteTaskCoreAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateTasksAsync(cancellationToken).ConfigureAwait(false);
        var located = await FindTaskAsync(service, taskId, cancellationToken).ConfigureAwait(false);
        var updated = await service.Tasks.Patch(
                new GoogleTask { Status = "completed" },
                located.TaskListId,
                taskId)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return MapTask(located.TaskListId, updated);
    }

    public System.Threading.Tasks.Task<OperationResult<Unit>> DeleteTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default) =>
        GoogleOperation.ExecuteAsync(() => DeleteTaskCoreAsync(taskId, cancellationToken));

    private async System.Threading.Tasks.Task<Unit> DeleteTaskCoreAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        using var service = await serviceFactory.CreateTasksAsync(cancellationToken).ConfigureAwait(false);
        var located = await FindTaskAsync(service, taskId, cancellationToken).ConfigureAwait(false);
        await service.Tasks.Delete(located.TaskListId, taskId)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return default;
    }

    private async System.Threading.Tasks.Task<(string TaskListId, GoogleTask Task)> FindTaskAsync(
        TasksService service,
        string taskId,
        CancellationToken cancellationToken)
    {
        (string TaskListId, GoogleTask Task)? found = null;

        foreach (var list in await ListTaskListsWithServiceAsync(service, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var item = await service.Tasks.Get(list.Id, taskId)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (found is not null)
                {
                    throw new InvalidOperationException(
                        $"Task id '{taskId}' exists in more than one task list.");
                }

                found = (list.Id, item);
            }
            catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.NotFound)
            {
                // The public MCP signature deliberately uses only the provider task id.
            }
        }

        return found ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
    }

    private static string? ParseDue(DateOnly? value)
    {
        if (value is null) return null;
        return value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.000Z";
    }

    private static DateOnly? ParseGoogleDue(string? value)
    {
        if (value is null) return null;

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var timestamp))
        {
            return DateOnly.FromDateTime(timestamp.UtcDateTime);
        }

        throw new InvalidOperationException($"Google returned an invalid task due date: '{value}'.");
    }

    private static TodoTask MapTask(string taskListId, GoogleTask source) => new(
        source.Id,
        taskListId,
        source.Title ?? "",
        source.Notes,
        ParseGoogleDue(source.Due),
        string.Equals(source.Status, "completed", StringComparison.Ordinal));
}
