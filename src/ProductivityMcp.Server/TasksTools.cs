using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProductivityMcp.Core;
using DomainTask = ProductivityMcp.Core.Task;

namespace ProductivityMcp.Server;

[McpServerToolType]
public sealed class TasksTools(ITasksProvider provider)
{
    [McpServerTool(
        Name = "tasks.lists.list",
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ListOutput<TaskListInfo>))]
    [Description("Lists task lists with their provider ids and human-readable names.")]
    public async System.Threading.Tasks.Task<CallToolResult> Lists(CancellationToken cancellationToken) =>
        McpToolResults.FromList(await provider.ListTaskListsAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(
        Name = "tasks.list",
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ListOutput<TodoTask>))]
    [Description("Lists the tasks in a task list.")]
    public async System.Threading.Tasks.Task<CallToolResult> List(
        [Description("Direct provider task-list id from tasks.lists.list.")] string taskListId,
        CancellationToken cancellationToken)
    {
        if (!McpToolResults.TryValue(
                ModelValidation.RequiredId(taskListId, nameof(taskListId)),
                out var validatedTaskListId,
                out var error))
        {
            return error;
        }

        return McpToolResults.FromList(
            await provider.ListTasksAsync(validatedTaskListId, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(
        Name = "tasks.create",
        Destructive = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(TodoTask))]
    [Description("Creates a task in the specified task list.")]
    public async System.Threading.Tasks.Task<CallToolResult> Create(
        [Description("Direct provider task-list id from tasks.lists.list.")] string taskListId,
        [Description("Task to create.")] DomainTask task,
        CancellationToken cancellationToken)
    {
        if (!McpToolResults.TryValue(
                ModelValidation.RequiredId(taskListId, nameof(taskListId)),
                out var validatedTaskListId,
                out var idError))
        {
            return idError;
        }

        if (!McpToolResults.TryValue(
                ModelValidation.Validate(task, nameof(task)),
                out var validatedTask,
                out var taskError))
        {
            return taskError;
        }

        return McpToolResults.FromValue(
            await provider.CreateTaskAsync(validatedTaskListId, validatedTask, cancellationToken)
                .ConfigureAwait(false));
    }

    [McpServerTool(
        Name = "tasks.update",
        Destructive = true,
        Idempotent = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(TodoTask))]
    [Description("Patches a task identified by its direct provider task id.")]
    public async System.Threading.Tasks.Task<CallToolResult> Update(
        [Description("Direct provider task id.")] string taskId,
        [Description("Fields to change. Null clears nullable fields.")] TaskPatch patch,
        CancellationToken cancellationToken)
    {
        if (!McpToolResults.TryValue(
                ModelValidation.RequiredId(taskId, nameof(taskId)),
                out var validatedTaskId,
                out var idError))
        {
            return idError;
        }

        if (!McpToolResults.TryValue(
                ModelValidation.Validate(patch, nameof(patch)),
                out var validatedPatch,
                out var patchError))
        {
            return patchError;
        }

        return McpToolResults.FromValue(
            await provider.UpdateTaskAsync(validatedTaskId, validatedPatch, cancellationToken)
                .ConfigureAwait(false));
    }

    [McpServerTool(
        Name = "tasks.complete",
        Destructive = true,
        Idempotent = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(TodoTask))]
    [Description("Marks a task as completed.")]
    public async System.Threading.Tasks.Task<CallToolResult> Complete(
        [Description("Direct provider task id.")] string taskId,
        CancellationToken cancellationToken)
    {
        if (!McpToolResults.TryValue(
                ModelValidation.RequiredId(taskId, nameof(taskId)),
                out var validatedTaskId,
                out var error))
        {
            return error;
        }

        return McpToolResults.FromValue(
            await provider.CompleteTaskAsync(validatedTaskId, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "tasks.delete", Destructive = true, Idempotent = true)]
    [Description("Deletes a task identified by its direct provider task id.")]
    public async System.Threading.Tasks.Task<CallToolResult> Delete(
        [Description("Direct provider task id.")] string taskId,
        CancellationToken cancellationToken)
    {
        if (!McpToolResults.TryValue(
                ModelValidation.RequiredId(taskId, nameof(taskId)),
                out var validatedTaskId,
                out var error))
        {
            return error;
        }

        return McpToolResults.FromUnit(
            await provider.DeleteTaskAsync(validatedTaskId, cancellationToken).ConfigureAwait(false));
    }
}
