using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using System.Security.Cryptography;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using ProductivityMcp.Core;

namespace ProductivityMcp.Providers.Imap;

public static class MailOperation
{
    public static async Task<OperationResult<T>> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return OperationResult.Ok(await operation().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (Translate(exception) is { } error)
        {
            return OperationResult.Fail<T>(error);
        }
    }

    public static OperationError? Translate(Exception exception) => exception switch
    {
        OperationCanceledException => null,
        AuthenticationRequiredException authentication => new(OperationErrorCode.Authentication, authentication.Message),
        AuthenticationException => new(OperationErrorCode.Authentication,
            "The mail server rejected the stored password. Open the Productivity MCP app and connect the mailbox again."),
        SslHandshakeException => new(OperationErrorCode.Configuration,
            "A secure connection to the mail server could not be established. Check the server settings in the app."),
        ValidationException validation => new(OperationErrorCode.Validation, validation.Message),
        UnsupportedFeatureException unsupported => new(OperationErrorCode.Unsupported, unsupported.Message, unsupported.Field),
        ConfigurationException configuration => new(OperationErrorCode.Configuration, configuration.Message, configuration.Field),
        KeyNotFoundException notFound => new(OperationErrorCode.NotFound, notFound.Message),
        FolderNotFoundException => new(OperationErrorCode.NotFound, "The mail folder was not found."),
        CryptographicException => new(OperationErrorCode.Authentication,
            "The stored mail password cannot be read. Open the Productivity MCP app and connect the mailbox again."),
        SmtpCommandException smtp => TranslateSmtp(smtp),
        ImapCommandException imap => new(OperationErrorCode.Provider,
            string.IsNullOrWhiteSpace(imap.ResponseText)
                ? "The mail server rejected the request."
                : $"The mail server rejected the request: {imap.ResponseText}"),
        NotSupportedException => new(OperationErrorCode.Unsupported, "The mail server does not support this operation."),
        ImapProtocolException or SmtpProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException
            or IOException or SocketException or TimeoutException => new(OperationErrorCode.ProviderUnavailable,
                "The mail server is temporarily unavailable.", Retryable: true),
        _ => null,
    };

    private static OperationError TranslateSmtp(SmtpCommandException exception) => exception.ErrorCode switch
    {
        SmtpErrorCode.RecipientNotAccepted => new(OperationErrorCode.Validation,
            $"The mail server rejected the recipient {exception.Mailbox?.Address}.".Replace(" .", ".", StringComparison.Ordinal)),
        SmtpErrorCode.SenderNotAccepted => new(OperationErrorCode.PermissionDenied,
            "The mail server does not allow sending from this address."),
        _ when (int)exception.StatusCode is >= 400 and < 500 => new(OperationErrorCode.ProviderUnavailable,
            "The mail server temporarily refused the message; retry later.", Retryable: true),
        _ => new(OperationErrorCode.Provider, $"The mail server refused the message: {exception.Message}"),
    };
}
