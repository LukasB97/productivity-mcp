using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProductivityMcp.Core;
using DomainEvent = ProductivityMcp.Core.Event;

namespace ProductivityMcp.Server;

[McpServerToolType]
public sealed class CalendarTools(ICalendarProvider provider)
{
    [McpServerTool(
        Name = "calendar.list",
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ListOutput<CalendarInfo>))]
    [Description("Lists calendars with their provider ids and human-readable names.")]
    public async System.Threading.Tasks.Task<CallToolResult> List(CancellationToken cancellationToken) =>
        McpToolResults.FromList(await provider.ListAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(
        Name = "calendar.events.query",
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ListOutput<CalendarEvent>))]
    [Description("Queries calendar events, optionally across all calendars.")]
    public async System.Threading.Tasks.Task<CallToolResult> Query(
        [Description("Calendar, text and time-range filters.")] EventQuery query,
        CancellationToken cancellationToken)
    {
        if (!McpToolResults.TryValue(
                ModelValidation.Validate(query, nameof(query)),
                out var validatedQuery,
                out var error))
        {
            return error;
        }

        return McpToolResults.FromList(
            await provider.QueryEventsAsync(validatedQuery, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(
        Name = "calendar.events.create",
        Destructive = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEvent))]
    [Description("Creates an event in the specified calendar.")]
    public async System.Threading.Tasks.Task<CallToolResult> Create(
        [Description("Direct provider calendar id from calendar.list.")] string calendarId,
        [Description("Event to create.")] DomainEvent @event,
        CancellationToken cancellationToken)
    {
        if (!McpToolResults.TryValue(
                ModelValidation.RequiredId(calendarId, nameof(calendarId)),
                out var validatedCalendarId,
                out var idError))
        {
            return idError;
        }

        if (!McpToolResults.TryValue(
                ModelValidation.Validate(@event, nameof(@event)),
                out var validatedEvent,
                out var eventError))
        {
            return eventError;
        }

        return McpToolResults.FromValue(
            await provider.CreateEventAsync(validatedCalendarId, validatedEvent, cancellationToken)
                .ConfigureAwait(false));
    }

    [McpServerTool(
        Name = "calendar.events.update",
        Destructive = true,
        Idempotent = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEvent))]
    [Description("Patches an event identified by its direct provider event id.")]
    public async System.Threading.Tasks.Task<CallToolResult> Update(
        [Description("Direct provider event id.")] string eventId,
        [Description("Fields to change. Null clears nullable fields.")] EventPatch patch,
        CancellationToken cancellationToken)
    {
        if (!McpToolResults.TryValue(
                ModelValidation.RequiredId(eventId, nameof(eventId)),
                out var validatedEventId,
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
            await provider.UpdateEventAsync(validatedEventId, validatedPatch, cancellationToken)
                .ConfigureAwait(false));
    }

    [McpServerTool(Name = "calendar.events.delete", Destructive = true, Idempotent = true)]
    [Description("Deletes an event identified by its direct provider event id.")]
    public async System.Threading.Tasks.Task<CallToolResult> Delete(
        [Description("Direct provider event id.")] string eventId,
        CancellationToken cancellationToken)
    {
        if (!McpToolResults.TryValue(
                ModelValidation.RequiredId(eventId, nameof(eventId)),
                out var validatedEventId,
                out var error))
        {
            return error;
        }

        return McpToolResults.FromUnit(
            await provider.DeleteEventAsync(validatedEventId, cancellationToken).ConfigureAwait(false));
    }
}
