using System.Text.Json;
using ProductivityMcp.Core;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.App.Services;

public sealed class GoogleSetupService : IGoogleSetupService
{
    private readonly GoogleOptions _options;
    private readonly GoogleAccountCatalog _accounts;

    public GoogleSetupService(GoogleOptions options)
    {
        _options = options;
        _accounts = new GoogleAccountCatalog(options);
    }

    public SetupSnapshot Inspect()
    {
        var credentialsPath = Path.GetFullPath(_options.CredentialsPath);
        var tokenStorePath = Path.GetFullPath(_options.TokenStorePath);
        return new SetupSnapshot(
            Path.GetDirectoryName(credentialsPath) ?? tokenStorePath,
            credentialsPath,
            File.Exists(credentialsPath),
            tokenStorePath,
            _accounts.List().Count > 0);
    }

    public OperationResult<SetupSnapshot> ImportCredentials(string sourcePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return Failure("Select an existing Google OAuth credentials JSON file.", "credentialsPath");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(sourcePath));
            if (!document.RootElement.TryGetProperty("installed", out var installed) ||
                !HasText(installed, "client_id") ||
                !HasText(installed, "client_secret"))
            {
                return Failure(
                    "This is not a Google OAuth desktop-app credentials file.",
                    "credentialsPath");
            }

            var destination = Path.GetFullPath(_options.CredentialsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!string.Equals(Path.GetFullPath(sourcePath), destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, destination, overwrite: true);
            }

            return OperationResult.Ok(Inspect());
        }
        catch (JsonException)
        {
            return Failure("The selected credentials file is not valid JSON.", "credentialsPath");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("The credentials file could not be read or saved because access was denied.");
        }
        catch (IOException exception)
        {
            return Failure($"The credentials file could not be saved: {exception.Message}");
        }
    }

    public async Task<OperationResult<ConnectionSummary>> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        var registrations = _accounts.List();
        if (registrations.Count == 0)
        {
            registrations =
            [
                new GoogleAccountRegistration(GoogleAccountCatalog.PrimaryAccountKey, "Google-Konto"),
            ];
        }

        var connected = new List<AccountConnectionSummary>();
        foreach (var registration in registrations)
        {
            var result = await ConnectAccountAsync(registration.Key, cancellationToken).ConfigureAwait(false);
            if (result is OperationResult<AccountConnectionSummary>.Failure failure)
            {
                return OperationResult.Fail<ConnectionSummary>(failure.Error);
            }

            connected.Add(((OperationResult<AccountConnectionSummary>.Success)result).Value);
        }

        return OperationResult.Ok(new ConnectionSummary(connected));
    }

    public async Task<OperationResult<ConnectionSummary>> AddAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var existingAccounts = _accounts.List();
        var accountKey = GoogleAccountCatalog.CreateAccountKey();
        var keepAccount = false;
        try
        {
            var added = await ConnectAccountAsync(accountKey, cancellationToken).ConfigureAwait(false);
            if (added is OperationResult<AccountConnectionSummary>.Failure failure)
            {
                return OperationResult.Fail<ConnectionSummary>(failure.Error);
            }

            var addedAccount = ((OperationResult<AccountConnectionSummary>.Success)added).Value;
            if (existingAccounts.Any(item =>
                    string.Equals(item.Email, addedAccount.Email, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult.Fail<ConnectionSummary>(new OperationError(
                    OperationErrorCode.Conflict,
                    $"{addedAccount.Email} ist bereits verbunden."));
            }

            var result = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            keepAccount = result is OperationResult<ConnectionSummary>.Success;
            return result;
        }
        finally
        {
            if (!keepAccount)
            {
                CleanupPendingAccount(accountKey);
            }
        }
    }

    public OperationResult<SetupSnapshot> Disconnect(string accountKey)
    {
        try
        {
            var tokenFile = Path.Combine(
                _accounts.TokenDirectory(accountKey),
                GoogleServiceFactory.TokenFileName);
            if (File.Exists(tokenFile))
            {
                File.Delete(tokenFile);
            }

            _accounts.Remove(accountKey);
            return OperationResult.Ok(Inspect());
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("The Google token could not be removed because access was denied.");
        }
        catch (IOException exception)
        {
            return Failure($"The Google token could not be removed: {exception.Message}");
        }
    }

    private async Task<OperationResult<AccountConnectionSummary>> ConnectAccountAsync(
        string accountKey,
        CancellationToken cancellationToken)
    {
        var serviceFactory = new GoogleServiceFactory(_accounts.OptionsFor(accountKey));
        var calendarResult = await new GoogleCalendarProvider(serviceFactory)
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (calendarResult is OperationResult<IReadOnlyList<CalendarInfo>>.Failure calendarFailure)
        {
            return OperationResult.Fail<AccountConnectionSummary>(calendarFailure.Error);
        }

        var calendars = ((OperationResult<IReadOnlyList<CalendarInfo>>.Success)calendarResult).Value;
        var email = calendars.FirstOrDefault(calendar => calendar.IsDefault is true)?.Id
                    ?? "Google-Konto";
        _accounts.Upsert(new GoogleAccountRegistration(accountKey, email));

        var taskListResult = await new GoogleTasksProvider(serviceFactory)
            .ListTaskListsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (taskListResult is OperationResult<IReadOnlyList<TaskListInfo>>.Failure taskListFailure)
        {
            return OperationResult.Fail<AccountConnectionSummary>(taskListFailure.Error);
        }

        var taskLists = ((OperationResult<IReadOnlyList<TaskListInfo>>.Success)taskListResult).Value;
        return OperationResult.Ok(new AccountConnectionSummary(accountKey, email, calendars, taskLists));
    }

    private static bool HasText(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString());

    private static OperationResult<SetupSnapshot> Failure(string message, string? field = null) =>
        OperationResult.Fail<SetupSnapshot>(new OperationError(
            OperationErrorCode.Configuration,
            message,
            field));

    private void CleanupPendingAccount(string accountKey)
    {
        _accounts.Remove(accountKey);
        var tokenDirectory = _accounts.TokenDirectory(accountKey);
        try
        {
            if (Directory.Exists(tokenDirectory))
            {
                Directory.Delete(tokenDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
