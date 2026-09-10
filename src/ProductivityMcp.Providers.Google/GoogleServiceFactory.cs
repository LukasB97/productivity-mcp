using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using Google.Apis.Tasks.v1;
using Google.Apis.Util.Store;

namespace ProductivityMcp.Providers.Google;

public sealed class GoogleServiceFactory
{
    public const string TokenFileName = "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user";

    private static readonly string[] Scopes =
    [
        CalendarService.Scope.Calendar,
        TasksService.Scope.Tasks,
    ];

    private readonly GoogleOptions _options;
    private readonly SemaphoreSlim _credentialLock = new(1, 1);
    private UserCredential? _credential;

    public GoogleServiceFactory(GoogleOptions options)
    {
        _options = options;
    }

    public async System.Threading.Tasks.Task<CalendarService> CreateCalendarAsync(
        CancellationToken cancellationToken = default)
    {
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
        if (!File.Exists(_options.CredentialsPath))
        {
            throw new FileNotFoundException(
                "Google OAuth credentials not found. Set PRODUCTIVITY_MCP_GOOGLE_CREDENTIALS.",
                _options.CredentialsPath);
        }

        Directory.CreateDirectory(_options.TokenStorePath);
        var secrets = (await GoogleClientSecrets.FromFileAsync(
            _options.CredentialsPath,
            cancellationToken).ConfigureAwait(false)).Secrets;

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            Scopes,
            "user",
            cancellationToken,
            new FileDataStore(_options.TokenStorePath, true)).ConfigureAwait(false);
    }
}
