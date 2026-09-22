using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ProductivityMcp.Core;

namespace ProductivityMcp.Server;

internal sealed record ListOutput<T>(IReadOnlyList<T> Result);

internal sealed record ErrorOutput(OperationError Error);

internal static class McpToolResults
{
    private static readonly JsonSerializerOptions StructuredContentOptions = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static bool TryValue<T>(
        OperationResult<T> result,
        out T value,
        out CallToolResult errorResult)
    {
        if (result is OperationResult<T>.Success success)
        {
            value = success.Value;
            errorResult = null!;
            return true;
        }

        value = default!;
        errorResult = Error(((OperationResult<T>.Failure)result).Error);
        return false;
    }

    public static CallToolResult FromValue<T>(OperationResult<T> result) => result switch
    {
        OperationResult<T>.Success success => Success(success.Value),
        OperationResult<T>.Failure failure => Error(failure.Error),
        _ => throw new InvalidOperationException("Unsupported operation result."),
    };

    public static CallToolResult FromList<T>(OperationResult<IReadOnlyList<T>> result) => result switch
    {
        OperationResult<IReadOnlyList<T>>.Success success => Success(new ListOutput<T>(success.Value)),
        OperationResult<IReadOnlyList<T>>.Failure failure => Error(failure.Error),
        _ => throw new InvalidOperationException("Unsupported operation result."),
    };

    public static CallToolResult FromUnit(OperationResult<Unit> result) => result switch
    {
        OperationResult<Unit>.Success => new CallToolResult { Content = [], IsError = false },
        OperationResult<Unit>.Failure failure => Error(failure.Error),
        _ => throw new InvalidOperationException("Unsupported operation result."),
    };

    public static CallToolResult FromEmailMessages(OperationResult<IReadOnlyList<EmailMessageReadResult>> result) =>
        WithImages(FromList(result), result is OperationResult<IReadOnlyList<EmailMessageReadResult>>.Success success
            ? success.Value.Where(x => x.Message is not null).SelectMany(x =>
                (x.Message!.Images ?? []).Select(image => ($"message:{x.Id}:page:{image.Page}", image))) : []);

    public static CallToolResult FromEmailThreads(OperationResult<IReadOnlyList<EmailThreadReadResult>> result) =>
        WithImages(FromList(result), result is OperationResult<IReadOnlyList<EmailThreadReadResult>>.Success success
            ? success.Value.Where(x => x.Thread is not null).SelectMany(x => x.Thread!.Messages).SelectMany(message =>
                (message.Images ?? []).Select(image => ($"message:{message.Id}:page:{image.Page}", image))) : []);

    public static CallToolResult Error(OperationError error) =>
        Create(new ErrorOutput(error), isError: true);

    public static OperationError InvalidJson(JsonException exception)
    {
        var field = exception.Path?.TrimStart('$', '.');
        var label = string.IsNullOrWhiteSpace(field) ? "input" : field;
        var message = exception.Message.Contains("DateOnly", StringComparison.Ordinal)
            ? $"{label} must be yyyy-MM-dd."
            : $"{label} has an invalid type or format.";

        return new OperationError(OperationErrorCode.Validation, message, field);
    }

    private static CallToolResult Success<T>(T value) => Create(value, isError: false);

    private static CallToolResult WithImages(CallToolResult result, IEnumerable<(string Label, EmailImage Image)> images)
    {
        foreach (var (label, image) in images)
        {
            result.Content.Add(new TextContentBlock { Text = label });
            result.Content.Add(ImageContentBlock.FromBytes(image.Data, image.MediaType));
        }
        return result;
    }

    private static CallToolResult Create<T>(T value, bool isError)
    {
        var structuredContent = JsonSerializer.SerializeToElement(value, StructuredContentOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = structuredContent.GetRawText() }],
            StructuredContent = structuredContent,
            IsError = isError,
        };
    }
}
