using ProductivityMcp.App.ViewModels;
using ProductivityMcp.Core;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class AuthorizationBoundaryTests
{
    [TestMethod]
    public void NewAuthorization_RequiresAllSelectedPermissions()
    {
        var calendarScopes = string.Join(' ', GoogleServiceFactory.GetScopes(GoogleServiceAccess.CalendarTasks));
        GoogleServiceFactory.ValidateGrantedScopes(calendarScopes, GoogleServiceAccess.CalendarTasks, requireScopes: true);
        Assert.ThrowsExactly<AuthenticationRequiredException>(() =>
            GoogleServiceFactory.ValidateGrantedScopes(calendarScopes, GoogleServiceAccess.Combined, requireScopes: true));
        Assert.ThrowsExactly<AuthenticationRequiredException>(() =>
            GoogleServiceFactory.ValidateGrantedScopes(null, GoogleServiceAccess.Email, requireScopes: true));
    }

    [TestMethod]
    public void ExistingBroaderScopes_RemainCompatible()
    {
        GoogleServiceFactory.ValidateGrantedScopes(
            "https://www.googleapis.com/auth/calendar https://www.googleapis.com/auth/tasks https://mail.google.com/",
            GoogleServiceAccess.Combined, requireScopes: true);
        GoogleServiceFactory.ValidateGrantedScopes(null, GoogleServiceAccess.CalendarTasks, requireScopes: false);
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
