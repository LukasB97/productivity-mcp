using System.Text.Json;
using Google.Apis.Gmail.v1;
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
        var effectiveCredentialsPath = Path.GetFullPath(_options.EffectiveCredentialsPath);
        var tokenStorePath = Path.GetFullPath(_options.TokenStorePath);
        return new SetupSnapshot(
            Path.GetDirectoryName(credentialsPath) ?? tokenStorePath,
            credentialsPath,
            File.Exists(effectiveCredentialsPath),
            tokenStorePath,
            _accounts.HasAnyToken(),
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

        var connected = new List<AccountConnectionSummary>();
        foreach (var registration in registrations)
        {
            var result = await ConnectAccountAsync(registration, cancellationToken).ConfigureAwait(false);
            if (result is OperationResult<AccountConnectionSummary>.Failure failure)
            {
                connected.Add(new AccountConnectionSummary(
                    registration.Key, registration.Email, [], [],
                    registration.CalendarTasksEnabled, false,
                    registration.EmailEnabled, false, failure.Error, failure.Error));
                continue;
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
            var added = await AuthenticateAsync(accountKey, null, GoogleServiceAccess.CalendarTasks, cancellationToken).ConfigureAwait(false);
            if (added is OperationResult<string>.Failure failure)
            {
                return OperationResult.Fail<ConnectionSummary>(failure.Error);
            }

            var addedEmail = ((OperationResult<string>.Success)added).Value;
            if (existingAccounts.Any(item =>
                    string.Equals(item.Email, addedEmail, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult.Fail<ConnectionSummary>(new OperationError(
                    OperationErrorCode.Conflict,
                    $"{addedEmail} ist bereits verbunden."));
            }

            _accounts.Upsert(new GoogleAccountRegistration(accountKey, addedEmail));
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

    public async Task<OperationResult<ConnectionSummary>> ReconnectAsync(
        string accountKey, CancellationToken cancellationToken = default)
    {
        var registration = _accounts.List().FirstOrDefault(account => account.Key == accountKey);
        if (registration is null)
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.NotFound, "Das Google-Konto wurde nicht gefunden."));
        var access = (registration.CalendarTasksEnabled ? GoogleServiceAccess.CalendarTasks : 0)
            | (registration.EmailEnabled ? GoogleServiceAccess.Email : 0);
        if (access == 0)
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.Validation, "Aktiviere zuerst einen Dienst für dieses Konto."));
        var result = await AuthenticateAsync(accountKey,
            registration.Email == "Google-Konto" ? null : registration.Email, access, cancellationToken).ConfigureAwait(false);
        if (result is OperationResult<string>.Success success)
            _accounts.Upsert(registration with { Email = success.Value });
        return result is OperationResult<string>.Failure failure
            ? OperationResult.Fail<ConnectionSummary>(failure.Error)
            : await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ConnectionSummary>> AddEmailAccountAsync(CancellationToken cancellationToken = default)
    {
        var temporaryKey = GoogleAccountCatalog.CreateAccountKey();
        var authenticated = await AuthenticateEmailAsync(temporaryKey, null, false, cancellationToken).ConfigureAwait(false);
        if (authenticated is OperationResult<string>.Failure failure)
        {
            CleanupPendingAccount(temporaryKey);
            return OperationResult.Fail<ConnectionSummary>(failure.Error);
        }

        var email = ((OperationResult<string>.Success)authenticated).Value;
        var existing = _accounts.List().FirstOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            try
            {
                if (existing.CalendarTasksEnabled)
                {
                    var combined = await AuthenticateEmailAsync(
                        existing.Key, email, includeCalendarTasks: true, cancellationToken).ConfigureAwait(false);
                    if (combined is OperationResult<string>.Failure combinedFailure)
                        return OperationResult.Fail<ConnectionSummary>(combinedFailure.Error);
                }
                else
                {
                    PromoteEmailToken(temporaryKey, existing.Key);
                }

                _accounts.Upsert(existing with { EmailEnabled = true });
            }
            finally
            {
                CleanupPendingAccount(temporaryKey);
            }
        }
        else
        {
            _accounts.Upsert(new GoogleAccountRegistration(temporaryKey, email, false, true));
        }

        return await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ConnectionSummary>> EnableEmailAsync(
        string accountKey, CancellationToken cancellationToken = default)
    {
        var registration = _accounts.List().FirstOrDefault(x => x.Key == accountKey);
        if (registration is null)
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.NotFound, "Das Google-Konto wurde nicht gefunden."));

        var authenticated = await AuthenticateEmailAsync(
            accountKey, registration.Email, registration.CalendarTasksEnabled, cancellationToken).ConfigureAwait(false);
        if (authenticated is OperationResult<string>.Failure failure)
            return OperationResult.Fail<ConnectionSummary>(failure.Error);

        _accounts.Upsert(registration with { Email = ((OperationResult<string>.Success)authenticated).Value, EmailEnabled = true });
        return await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ConnectionSummary>> DisableEmailAsync(
        string accountKey, CancellationToken cancellationToken = default)
    {
        var registration = _accounts.List().FirstOrDefault(x => x.Key == accountKey);
        if (registration is null)
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.NotFound, "Das Google-Konto wurde nicht gefunden."));
        _accounts.Upsert(registration with { EmailEnabled = false });
        return await ConnectAsync(cancellationToken).ConfigureAwait(false);
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

            var emailDirectory = _accounts.EmailTokenDirectory(accountKey);
            if (Directory.Exists(emailDirectory)) Directory.Delete(emailDirectory, recursive: true);

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
        GoogleAccountRegistration registration,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CalendarInfo> calendars = [];
        IReadOnlyList<TaskListInfo> taskLists = [];
        var email = registration.Email;
        var calendarTasksConnected = false;
        OperationError? calendarTasksError = null;
        OperationError? emailError = null;
        if (registration.CalendarTasksEnabled)
        {
            var serviceFactory = new GoogleServiceFactory(_accounts.OptionsFor(registration.Key));
            var calendarResult = await new GoogleCalendarProvider(serviceFactory)
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (calendarResult is OperationResult<IReadOnlyList<CalendarInfo>>.Success calendarSuccess)
            {
                calendars = calendarSuccess.Value;
                var primaryEmail = calendars.FirstOrDefault(calendar => calendar.IsDefault is true)?.Id;
                if (registration.Email == "Google-Konto" && primaryEmail is not null) email = primaryEmail;
                else if (primaryEmail is not null && !string.Equals(primaryEmail, email, StringComparison.OrdinalIgnoreCase))
                    calendarTasksError = new(OperationErrorCode.Authentication, "Die gespeicherte Anmeldung gehört zu einem anderen Konto.");
            }
            else calendarTasksError = ((OperationResult<IReadOnlyList<CalendarInfo>>.Failure)calendarResult).Error;

            var taskListResult = await new GoogleTasksProvider(serviceFactory)
                .ListTaskListsAsync(cancellationToken)
                .ConfigureAwait(false);

            if (taskListResult is OperationResult<IReadOnlyList<TaskListInfo>>.Success taskSuccess)
                taskLists = taskSuccess.Value;
            else calendarTasksError ??= ((OperationResult<IReadOnlyList<TaskListInfo>>.Failure)taskListResult).Error;
            calendarTasksConnected = calendarTasksError is null;
        }

        var emailConnected = false;
        if (registration.EmailEnabled)
        {
            var emailResult = await GoogleOperation.ExecuteAsync(async () =>
            {
                using var gmail = await new GoogleServiceFactory(
                    _accounts.EmailOptionsFor(registration.Key), GoogleServiceAccess.Email)
                    .CreateGmailAsync(cancellationToken).ConfigureAwait(false);
                return await gmail.Users.GetProfile("me").ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (emailResult is OperationResult<Google.Apis.Gmail.v1.Data.Profile>.Success profile)
            {
                emailConnected = string.Equals(profile.Value.EmailAddress, email, StringComparison.OrdinalIgnoreCase);
                if (!emailConnected) emailError = new(OperationErrorCode.Authentication, "Die gespeicherte Anmeldung gehört zu einem anderen Konto.");
            }
            else emailError = ((OperationResult<Google.Apis.Gmail.v1.Data.Profile>.Failure)emailResult).Error;
        }
        _accounts.Upsert(registration with { Email = email });
        return OperationResult.Ok(new AccountConnectionSummary(
            registration.Key, email, calendars, taskLists,
            registration.CalendarTasksEnabled, calendarTasksConnected,
            registration.EmailEnabled, emailConnected, calendarTasksError, emailError));
    }

    private Task<OperationResult<string>> AuthenticateEmailAsync(
        string accountKey, string? expectedEmail, bool includeCalendarTasks, CancellationToken cancellationToken) =>
        AuthenticateAsync(accountKey, expectedEmail,
            includeCalendarTasks ? GoogleServiceAccess.Combined : GoogleServiceAccess.Email, cancellationToken);

    private async Task<OperationResult<string>> AuthenticateAsync(
        string accountKey, string? expectedEmail, GoogleServiceAccess access, CancellationToken cancellationToken)
    {
        var pendingDirectory = Path.Combine(_accounts.TokenDirectory(accountKey), $"email-pending-{Guid.NewGuid():N}");
        try
        {
            var factory = new GoogleServiceFactory(_options with { TokenStorePath = pendingDirectory }, access, interactive: true);
            string? email = null;
            if (access.HasFlag(GoogleServiceAccess.CalendarTasks))
            {
                using var calendar = await factory.CreateCalendarAsync(cancellationToken).ConfigureAwait(false);
                var primary = await calendar.CalendarList.Get("primary").ExecuteAsync(cancellationToken).ConfigureAwait(false);
                email = primary.Id;
                using var tasks = await factory.CreateTasksAsync(cancellationToken).ConfigureAwait(false);
                await tasks.Tasklists.List().ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            if (access.HasFlag(GoogleServiceAccess.Email))
            {
                using var gmail = await factory.CreateGmailAsync(cancellationToken).ConfigureAwait(false);
                var profile = await gmail.Users.GetProfile("me").ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (email is not null && !string.Equals(email, profile.EmailAddress, StringComparison.OrdinalIgnoreCase))
                    return OperationResult.Fail<string>(new(OperationErrorCode.Authentication, "Die Google-Dienste haben unterschiedliche Konten zurückgegeben."));
                email = profile.EmailAddress;
            }
            if (string.IsNullOrWhiteSpace(email))
                return OperationResult.Fail<string>(new(OperationErrorCode.Authentication, "Google hat keine E-Mail-Adresse zurückgegeben."));
            if (!string.IsNullOrWhiteSpace(expectedEmail) &&
                !string.Equals(expectedEmail, email, StringComparison.OrdinalIgnoreCase))
                return OperationResult.Fail<string>(new(OperationErrorCode.Conflict, $"Angemeldet wurde {email}, erwartet wurde {expectedEmail}."));

            var pendingToken = Path.Combine(pendingDirectory, GoogleServiceFactory.TokenFileName);
            var destination = _accounts.TokenDirectory(accountKey);
            Directory.CreateDirectory(destination);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pendingToken, Path.Combine(destination, GoogleServiceFactory.TokenFileName), overwrite: true);
            return OperationResult.Ok(email);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (GoogleOperation.Translate(exception) is { } error)
        {
            return OperationResult.Fail<string>(error);
        }
        finally
        {
            try { if (Directory.Exists(pendingDirectory)) Directory.Delete(pendingDirectory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void PromoteEmailToken(string sourceKey, string destinationKey)
    {
        var source = Path.Combine(_accounts.TokenDirectory(sourceKey), GoogleServiceFactory.TokenFileName);
        var destination = _accounts.TokenDirectory(destinationKey);
        Directory.CreateDirectory(destination);
        File.Move(source, Path.Combine(destination, GoogleServiceFactory.TokenFileName), overwrite: true);
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
