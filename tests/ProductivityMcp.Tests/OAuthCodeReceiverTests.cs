using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class OAuthCodeReceiverTests
{
    [TestMethod]
    public async System.Threading.Tasks.Task ReceiveCode_AddsAndValidatesState()
    {
        var inner = new FakeCodeReceiver(returnMatchingState: true);
        var receiver = new StatefulCodeReceiver(inner);
        var request = new AuthorizationCodeRequestUrl(new Uri("https://accounts.google.com/o/oauth2/v2/auth"));

        var response = await receiver.ReceiveCodeAsync(request, CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(request.State));
        Assert.AreEqual(request.State, response.State);
        Assert.AreEqual("code", response.Code);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task ReceiveCode_RejectsMismatchedState()
    {
        var receiver = new StatefulCodeReceiver(new FakeCodeReceiver(returnMatchingState: false));
        var request = new AuthorizationCodeRequestUrl(new Uri("https://accounts.google.com/o/oauth2/v2/auth"));

        await Assert.ThrowsExactlyAsync<OAuthStateMismatchException>(
            () => receiver.ReceiveCodeAsync(request, CancellationToken.None));
    }

    private sealed class FakeCodeReceiver(bool returnMatchingState) : ICodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1:12345/authorize/";

        public System.Threading.Tasks.Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(
            AuthorizationCodeRequestUrl url,
            CancellationToken taskCancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new AuthorizationCodeResponseUrl
            {
                Code = "code",
                State = returnMatchingState ? url.State : "mismatch",
            });
    }
}
