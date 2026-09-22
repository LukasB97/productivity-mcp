using System.Diagnostics.CodeAnalysis;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Tasks.v1;
using Google.Apis.Util.Store;
using ProductivityMcp.Core;

namespace ProductivityMcp.Providers.Google;

[Flags]
public enum GoogleServiceAccess { CalendarTasks = 1, Email = 2, Combined = CalendarTasks | Email }

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "SemaphoreSlim does not create an OS wait handle unless AvailableWaitHandle is accessed.")]
public sealed class GoogleServiceFactory
{
    public const string TokenFileName = "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user";

    private readonly GoogleOptions _options;
    private readonly GoogleServiceAccess _access;
    private readonly bool _interactive;
    private readonly Func<CalendarService>? _createCalendar;
    private readonly SemaphoreSlim _credentialLock = new(1, 1);
    private UserCredential? _credential;

    public GoogleServiceFactory(GoogleOptions options, GoogleServiceAccess access = GoogleServiceAccess.CalendarTasks,
        bool interactive = false)
    {
        _options = options;
        _access = access;
        _interactive = interactive;
    }

    internal GoogleServiceFactory(GoogleOptions options, Func<CalendarService> createCalendar) : this(options)
    {
        _createCalendar = createCalendar;
    }

    public async System.Threading.Tasks.Task<GmailService> CreateGmailAsync(
        CancellationToken cancellationToken = default)
    {
        var credential = await GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        return new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName,
        });
    }

    public async System.Threading.Tasks.Task<CalendarService> CreateCalendarAsync(
        CancellationToken cancellationToken = default)
    {
        if (_createCalendar is not null) return _createCalendar();
        var credential = await GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName,
        });
    }

    public async System.Threading.Tasks.Task<TasksService> CreateTasksAsync(
        CancellationToken cancellationToken = default)
    {
        var credential = await GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        return new TasksService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName,
        });
    }

    private async System.Threading.Tasks.Task<UserCredential> GetCredentialAsync(
        CancellationToken cancellationToken)
    {
        if (_credential is not null)
        {
            return _credential;
        }

        await _credentialLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _credential ??= await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
            return _credential;
        }
        finally
        {
            _credentialLock.Release();
        }
    }

    private async System.Threading.Tasks.Task<UserCredential> AuthorizeAsync(
        CancellationToken cancellationToken)
    {
        var credentialsPath = _options.EffectiveCredentialsPath;
        if (!File.Exists(credentialsPath))
        {
            throw new ConfigurationException(
                "Google OAuth credentials not found. Set PRODUCTIVITY_MCP_GOOGLE_CREDENTIALS.",
                "credentialsPath");
        }

        Directory.CreateDirectory(_options.TokenStorePath);
        var secrets = (await GoogleClientSecrets.FromFileAsync(
            credentialsPath,
            cancellationToken).ConfigureAwait(false)).Secrets;

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            GetScopes(_access),
            "user",
            cancellationToken,
            new SecureDataStore(_options.TokenStorePath),
            _interactive ? new StatefulCodeReceiver() : new NonInteractiveCodeReceiver()).ConfigureAwait(false);
        ValidateGrantedScopes(credential.Token.Scope, _access, _interactive);
        return credential;
    }

    internal static void ValidateGrantedScopes(string? grantedScopes, GoogleServiceAccess access, bool requireScopes)
    {
        // Legacy tokens without scope metadata remain readable; newly authorized tokens
        // must explicitly cover every selected service before the app installs them.
        if (!requireScopes && string.IsNullOrWhiteSpace(grantedScopes)) return;
        var granted = (grantedScopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (GetScopes(access).Any(scope => !granted.Contains(scope) && !CoveredByBroaderScope(scope, granted)))
            throw new AuthenticationRequiredException("Not all selected Google permissions were granted. Open the app and sign in again, allowing every selected service.");
    }

    private static bool CoveredByBroaderScope(string scope, HashSet<string> granted)
    {
        if (scope == CalendarService.Scope.CalendarEvents) return granted.Contains(CalendarService.Scope.Calendar);
        if (scope == CalendarService.Scope.CalendarCalendarlistReadonly)
            return granted.Contains(CalendarService.Scope.Calendar) || granted.Contains(CalendarService.Scope.CalendarReadonly)
                || granted.Contains(CalendarService.Scope.CalendarCalendarlist);
        return scope == GmailService.Scope.GmailModify && granted.Contains("https://mail.google.com/");
    }

    private sealed class NonInteractiveCodeReceiver : ICodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1/";

        public Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url,
            CancellationToken taskCancellationToken) => throw new AuthenticationRequiredException(
                "Google sign-in is required. Open the Productivity MCP app and reconnect this account.");
    }

    internal static string[] GetScopes(GoogleServiceAccess access)
    {
        var scopes = new List<string>();
        if (access.HasFlag(GoogleServiceAccess.CalendarTasks))
        {
            scopes.Add(CalendarService.Scope.CalendarEvents);
            scopes.Add(CalendarService.Scope.CalendarCalendarlistReadonly);
            scopes.Add(TasksService.Scope.Tasks);
        }
        if (access.HasFlag(GoogleServiceAccess.Email)) scopes.Add(GmailService.Scope.GmailModify);
        return scopes.ToArray();
    }
}
