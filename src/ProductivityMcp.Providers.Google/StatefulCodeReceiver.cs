using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;

namespace ProductivityMcp.Providers.Google;

internal sealed class OAuthStateMismatchException : Exception
{
    public OAuthStateMismatchException()
        : base("Google returned an OAuth response with an invalid state value.")
    {
    }
}

internal sealed class StatefulCodeReceiver : ICodeReceiver
{
    private readonly ICodeReceiver _inner;

    public StatefulCodeReceiver()
        : this(new LocalServerCodeReceiver())
    {
    }

    internal StatefulCodeReceiver(ICodeReceiver inner) => _inner = inner;

    public string RedirectUri => _inner.RedirectUri;

    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(
        AuthorizationCodeRequestUrl url,
        CancellationToken taskCancellationToken)
    {
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        url.State = state;
        var response = await _inner.ReceiveCodeAsync(url, taskCancellationToken).ConfigureAwait(false);
        if (!FixedTimeEquals(state, response.State))
        {
            throw new OAuthStateMismatchException();
        }

        return response;
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null || expected.Length != actual.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual));
    }
}
