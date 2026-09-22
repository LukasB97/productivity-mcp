using System.Text.Json.Serialization;

namespace ProductivityMcp.Core;

[JsonConverter(typeof(JsonStringEnumConverter<OperationErrorCode>))]
public enum OperationErrorCode
{
    [JsonStringEnumMemberName("validation_error")]
    Validation,

    [JsonStringEnumMemberName("not_found")]
    NotFound,

    [JsonStringEnumMemberName("conflict")]
    Conflict,

    [JsonStringEnumMemberName("configuration_error")]
    Configuration,

    [JsonStringEnumMemberName("authentication_error")]
    Authentication,

    [JsonStringEnumMemberName("permission_denied")]
    PermissionDenied,

    [JsonStringEnumMemberName("rate_limited")]
    RateLimited,

    [JsonStringEnumMemberName("provider_unavailable")]
    ProviderUnavailable,

    [JsonStringEnumMemberName("provider_error")]
    Provider,

    [JsonStringEnumMemberName("unsupported")]
    Unsupported,
}

public sealed class UnsupportedFeatureException(
    string message,
    string? field = null) : Exception(message)
{
    public string? Field { get; } = field;
}

public sealed class AmbiguousResourceException(string message) : Exception(message);

public sealed class AuthenticationRequiredException(string message) : Exception(message);

public sealed class ConfigurationException(
    string message,
    string? field = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string? Field { get; } = field;
}

public sealed record OperationError(
    OperationErrorCode Code,
    string Message,
    string? Field = null,
    bool Retryable = false,
    int? RetryAfterSeconds = null);

public abstract record OperationResult<T>
{
    private OperationResult()
    {
    }

    public sealed record Success(T Value) : OperationResult<T>;

    public sealed record Failure(OperationError Error) : OperationResult<T>;
}

public static class OperationResult
{
    public static OperationResult<T> Ok<T>(T value) => new OperationResult<T>.Success(value);

    public static OperationResult<T> Fail<T>(OperationError error) => new OperationResult<T>.Failure(error);
}

public readonly record struct Unit;
