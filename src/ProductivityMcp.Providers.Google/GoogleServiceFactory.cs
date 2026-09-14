using System.Diagnostics.CodeAnalysis;
using Google.Apis.Auth.OAuth2;
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
    private readonly SemaphoreSlim _credentialLock = new(1, 1);
    private UserCredential? _credential;

    public GoogleServiceFactory(GoogleOptions options, GoogleServiceAccess access = GoogleServiceAccess.CalendarTasks)
    {
        _options = options;
        _access = access;
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
            throw new ConfigurationException(
                "Google OAuth credentials not found. Set PRODUCTIVITY_MCP_GOOGLE_CREDENTIALS.",
                "credentialsPath");
        }

        Directory.CreateDirectory(_options.TokenStorePath);
        var secrets = (await GoogleClientSecrets.FromFileAsync(
            _options.CredentialsPath,
            cancellationToken).ConfigureAwait(false)).Secrets;

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            Scopes(),
            "user",
            cancellationToken,
            new FileDataStore(_options.TokenStorePath, true)).ConfigureAwait(false);
    }

    private string[] Scopes()
    {
        var scopes = new List<string>();
        if (_access.HasFlag(GoogleServiceAccess.CalendarTasks))
        {
            scopes.Add(CalendarService.Scope.Calendar);
            scopes.Add(TasksService.Scope.Tasks);
        }
        if (_access.HasFlag(GoogleServiceAccess.Email)) scopes.Add(GmailService.Scope.GmailModify);
        return scopes.ToArray();
    }
}
