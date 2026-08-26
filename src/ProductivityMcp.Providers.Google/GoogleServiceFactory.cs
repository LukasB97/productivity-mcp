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
    private readonly Lazy<System.Threading.Tasks.Task<UserCredential>> _credential;

    public GoogleServiceFactory(GoogleOptions options)
    {
        _options = options;
        _credential = new Lazy<System.Threading.Tasks.Task<UserCredential>>(AuthorizeAsync);
    }

    public async System.Threading.Tasks.Task<CalendarService> CreateCalendarAsync()
    {
        var credential = await _credential.Value.ConfigureAwait(false);
        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName,
        });
    }

    public async System.Threading.Tasks.Task<TasksService> CreateTasksAsync()
    {
        var credential = await _credential.Value.ConfigureAwait(false);
        return new TasksService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName,
        });
    }

    private async System.Threading.Tasks.Task<UserCredential> AuthorizeAsync()
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
            CancellationToken.None).ConfigureAwait(false)).Secrets;

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            Scopes,
            "user",
            CancellationToken.None,
            new FileDataStore(_options.TokenStorePath, true)).ConfigureAwait(false);
    }
}
