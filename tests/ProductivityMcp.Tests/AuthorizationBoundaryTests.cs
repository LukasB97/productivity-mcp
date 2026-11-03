using ProductivityMcp.App.ViewModels;
using ProductivityMcp.Core;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class AuthorizationBoundaryTests
{
    [TestMethod]
    public void NewAuthorization_RequiresAllCalendarAndTaskPermissions()
    {
        GoogleServiceFactory.ValidateGrantedScopes(string.Join(' ', GoogleServiceFactory.Scopes), requireScopes: true);
        Assert.ThrowsExactly<AuthenticationRequiredException>(() =>
            GoogleServiceFactory.ValidateGrantedScopes(GoogleServiceFactory.Scopes[0], requireScopes: true));
        Assert.ThrowsExactly<AuthenticationRequiredException>(() =>
            GoogleServiceFactory.ValidateGrantedScopes(null, requireScopes: true));
    }

    [TestMethod]
    public void ExistingBroaderScopes_RemainCompatible()
    {
        GoogleServiceFactory.ValidateGrantedScopes(
            "https://www.googleapis.com/auth/calendar https://www.googleapis.com/auth/tasks https://mail.google.com/",
            requireScopes: true);
        GoogleServiceFactory.ValidateGrantedScopes(null, requireScopes: false);
    }

    [TestMethod]
    public void AccountStatus_DistinguishesNetworkFailureFromSignIn()
    {
        var unavailable = new ConnectedAccountViewModel("test", "test@example.com", [], [], true, false, true, true,
            new(OperationErrorCode.ProviderUnavailable, "Network unavailable", Retryable: true));
        Assert.AreEqual("Prüfung fehlgeschlagen", unavailable.CalendarTasksStatusText);
        Assert.AreEqual("Verbunden", unavailable.EmailStatusText);
        Assert.IsFalse(unavailable.NeedsReconnect);
        var expired = unavailable with { CalendarTasksError = new(OperationErrorCode.Authentication, "Expired") };
        Assert.IsTrue(expired.NeedsReconnect);
        Assert.AreEqual("Erneut anmelden", expired.CalendarTasksStatusText);
    }
}
