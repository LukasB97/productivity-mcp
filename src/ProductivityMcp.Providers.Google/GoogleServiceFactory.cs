using System.Diagnostics.CodeAnalysis;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using Google.Apis.Tasks.v1;
using Google.Apis.Util.Store;
using ProductivityMcp.Core;

namespace ProductivityMcp.Providers.Google;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "SemaphoreSlim does not create an OS wait handle unless AvailableWaitHandle is accessed.")]
public sealed class GoogleServiceFactory
{
    public const string TokenFileName = "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user";

    private readonly GoogleOptions _options;
    private readonly bool _interactive;
    private readonly Func<CalendarService>? _createCalendar;
    private readonly SemaphoreSlim _credentialLock = new(1, 1);
    private UserCredential? _credential;

    public GoogleServiceFactory(GoogleOptions options, bool interactive = false)
    {
        _options = options;
        _interactive = interactive;
    }

    internal GoogleServiceFactory(GoogleOptions options, Func<CalendarService> createCalendar) : this(options)
    {
        _createCalendar = createCalendar;
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
            Scopes,
            "user",
            cancellationToken,
            new SecureDataStore(_options.TokenStorePath),
            _interactive ? new StatefulCodeReceiver() : new NonInteractiveCodeReceiver()).ConfigureAwait(false);
        ValidateGrantedScopes(credential.Token.Scope, _interactive);
        return credential;
    }

    internal static void ValidateGrantedScopes(string? grantedScopes, bool requireScopes)
    {
        // Legacy tokens without scope metadata remain readable; newly authorized tokens
        // must explicitly cover calendar and tasks before the app installs them.
        if (!requireScopes && string.IsNullOrWhiteSpace(grantedScopes)) return;
        var granted = (grantedScopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (Scopes.Any(scope => !granted.Contains(scope) && !CoveredByBroaderScope(scope, granted)))
            throw new AuthenticationRequiredException("Not all Google calendar and task permissions were granted. Open the app and sign in again, allowing every requested permission.");
    }

    private static bool CoveredByBroaderScope(string scope, HashSet<string> granted)
    {
        if (scope == CalendarService.Scope.CalendarEvents) return granted.Contains(CalendarService.Scope.Calendar);
        if (scope == CalendarService.Scope.CalendarCalendarlistReadonly)
            return granted.Contains(CalendarService.Scope.Calendar) || granted.Contains(CalendarService.Scope.CalendarReadonly)
                || granted.Contains(CalendarService.Scope.CalendarCalendarlist);
        return false;
    }

    private sealed class NonInteractiveCodeReceiver : ICodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1/";

        public Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url,
            CancellationToken taskCancellationToken) => throw new AuthenticationRequiredException(
                "Google sign-in is required. Open the Productivity MCP app and reconnect this account.");
    }

    // Gmail scopes are deliberately absent: they are "restricted" and would require an
    // annual third-party security assessment. Email uses IMAP/SMTP with app passwords.
    internal static readonly string[] Scopes =
    [
        CalendarService.Scope.CalendarEvents,
        CalendarService.Scope.CalendarCalendarlistReadonly,
        TasksService.Scope.Tasks,
    ];
}
