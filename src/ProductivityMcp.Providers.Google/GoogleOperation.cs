using System.ComponentModel.DataAnnotations;
using System.Net;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using ProductivityMcp.Core;

namespace ProductivityMcp.Providers.Google;

internal static class GoogleOperation
{
    public static async System.Threading.Tasks.Task<OperationResult<T>> ExecuteAsync<T>(
        Func<System.Threading.Tasks.Task<T>> operation)
    {
        try
        {
            return OperationResult.Ok(await operation().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = Translate(exception);
            if (error is null)
            {
                throw;
            }

            return OperationResult.Fail<T>(error);
        }
    }

    internal static OperationError? Translate(Exception exception) => exception switch
    {
        ValidationException validation => new OperationError(
            OperationErrorCode.Validation,
            validation.Message),
        KeyNotFoundException notFound => new OperationError(
            OperationErrorCode.NotFound,
            notFound.Message),
        FileNotFoundException => new OperationError(
            OperationErrorCode.Configuration,
            "Google OAuth credentials are missing. Configure the credentials file before retrying."),
        TokenResponseException => new OperationError(
            OperationErrorCode.Authentication,
            "Google authorization is invalid or expired. Sign in again before retrying."),
        GoogleApiException google => TranslateGoogleApiException(google),
        HttpRequestException or TimeoutException => new OperationError(
            OperationErrorCode.ProviderUnavailable,
            "The Google provider is temporarily unavailable.",
            Retryable: true),
        _ => null,
    };

    private static OperationError TranslateGoogleApiException(GoogleApiException exception)
    {
        var status = exception.HttpStatusCode;
        if ((int)status >= 500)
        {
            return new OperationError(
                OperationErrorCode.ProviderUnavailable,
                "The Google provider is temporarily unavailable.",
                Retryable: true);
        }

        return status switch
        {
            HttpStatusCode.BadRequest => new OperationError(
                OperationErrorCode.Validation,
                "The Google provider rejected the supplied values."),
            HttpStatusCode.Unauthorized => new OperationError(
                OperationErrorCode.Authentication,
                "Google authorization is invalid or expired. Sign in again before retrying."),
            HttpStatusCode.Forbidden => new OperationError(
                OperationErrorCode.PermissionDenied,
                "Google denied permission for this operation."),
            HttpStatusCode.NotFound => new OperationError(
                OperationErrorCode.NotFound,
                "The requested calendar or task resource was not found."),
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => new OperationError(
                OperationErrorCode.Conflict,
                "The resource was modified concurrently; retry with fresh data.",
                Retryable: true),
            HttpStatusCode.TooManyRequests => new OperationError(
                OperationErrorCode.RateLimited,
                "The Google provider rate limit was reached; retry later.",
                Retryable: true),
            _ => new OperationError(
                OperationErrorCode.Provider,
                "The Google provider could not complete the operation."),
        };
    }
}
