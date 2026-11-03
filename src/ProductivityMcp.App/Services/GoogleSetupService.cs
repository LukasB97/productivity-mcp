using System.Text.Json;
using MailKit.Security;
using ProductivityMcp.Core;
using ProductivityMcp.Providers.Google;
using ProductivityMcp.Providers.Imap;
using Task = System.Threading.Tasks.Task;

namespace ProductivityMcp.App.Services;

/// <summary>
/// Connects Google accounts (calendar and tasks, via OAuth) and mailboxes (via IMAP/SMTP with an
/// app password). Both are shown per email address; a mailbox without a Google account gets its own entry.
/// </summary>
public sealed class GoogleSetupService : IGoogleSetupService
{
    public const string MailKeyPrefix = "mail:";

    private readonly GoogleOptions _options;
    private readonly GoogleAccountCatalog _accounts;
    private readonly MailAccountCatalog _mail;
    private readonly MailServerDiscovery _discovery;
    private readonly Func<MailAccount, string, CancellationToken, Task<bool>> _verifySignIn;
    private readonly Func<MailAccount, CancellationToken, Task> _checkMailbox;

    public GoogleSetupService(
        GoogleOptions options,
        MailAccountCatalog mail,
        MailServerDiscovery? discovery = null,
        Func<MailAccount, string, CancellationToken, Task<bool>>? verifySignIn = null,
        Func<MailAccount, CancellationToken, Task>? checkMailbox = null)
    {
        _options = options;
        _accounts = new GoogleAccountCatalog(options);
        _mail = mail;
        _discovery = discovery ?? new MailServerDiscovery();
        _verifySignIn = verifySignIn ?? MailAccountVerifier.VerifySignInAsync;
        _checkMailbox = checkMailbox ?? ((account, token) => MailAccountVerifier.CheckStoredAsync(account, _mail, token));
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
            _accounts.HasAnyToken() || _mail.List().Count > 0,
            _accounts.List().Count > 0 || _mail.List().Count > 0);
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
                    true, false, CalendarTasksError: failure.Error));
                continue;
            }

            connected.Add(((OperationResult<AccountConnectionSummary>.Success)result).Value);
        }

        foreach (var mailbox in _mail.List())
        {
            var error = await CheckMailboxAsync(mailbox, cancellationToken).ConfigureAwait(false);
            var index = connected.FindIndex(item => string.Equals(item.Email, mailbox.Email, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                connected.Add(new AccountConnectionSummary(
                    MailKeyPrefix + mailbox.Key, mailbox.Email, [], [], false, false, true, error is null, EmailError: error));
            }
            else
            {
                connected[index] = connected[index] with { EmailEnabled = true, EmailConnected = error is null, EmailError = error };
            }
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
            var added = await AuthenticateAsync(accountKey, null, cancellationToken).ConfigureAwait(false);
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
        var result = await AuthenticateAsync(accountKey,
            registration.Email == "Google-Konto" ? null : registration.Email, cancellationToken).ConfigureAwait(false);
        if (result is OperationResult<string>.Success success)
            _accounts.Upsert(registration with { Email = success.Value });
        return result is OperationResult<string>.Failure failure
            ? OperationResult.Fail<ConnectionSummary>(failure.Error)
            : await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<MailServerSettings?>> DiscoverMailServerAsync(
        string email, CancellationToken cancellationToken = default)
    {
        if (!IsEmailAddress(email))
            return OperationResult.Fail<MailServerSettings?>(new(OperationErrorCode.Validation, "Gib eine gültige E-Mail-Adresse ein.", "email"));
        try
        {
            return OperationResult.Ok(await _discovery.DiscoverAsync(email.Trim(), cancellationToken).ConfigureAwait(false));
        }
        catch (UnsupportedFeatureException)
        {
            return OperationResult.Fail<MailServerSettings?>(new(OperationErrorCode.Unsupported,
                "Outlook-, Hotmail- und Live-Postfächer erlauben nur die Microsoft-Anmeldung. Das wird noch nicht unterstützt.", "email"));
        }
    }

    public async Task<OperationResult<ConnectionSummary>> ConnectEmailAsync(
        MailConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim();
        if (!IsEmailAddress(email))
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.Validation, "Gib eine gültige E-Mail-Adresse ein.", "email"));
        if (string.IsNullOrWhiteSpace(request.Password))
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.Validation, "Gib das Passwort oder App-Passwort ein.", "password"));

        // Reconnecting a known mailbox keeps its server settings unless new ones are entered.
        var existing = _mail.Find(email);
        var settings = request.Settings
            ?? (existing is null ? null : new MailServerSettings(existing.Imap, existing.Smtp, existing.Username));
        if (settings is null)
        {
            var discovered = await DiscoverMailServerAsync(email, cancellationToken).ConfigureAwait(false);
            if (discovered is OperationResult<MailServerSettings?>.Failure discoveryFailure)
                return OperationResult.Fail<ConnectionSummary>(discoveryFailure.Error);
            settings = ((OperationResult<MailServerSettings?>.Success)discovered).Value;
        }

        if (settings is null)
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.Validation,
                "Die Servereinstellungen konnten nicht automatisch ermittelt werden. Trage sie unter „Servereinstellungen“ ein.", "settings"));
        if (Invalid(settings.Imap) || Invalid(settings.Smtp))
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.Validation,
                "Prüfe die Servereinstellungen: Servername und Port (1–65535) sind erforderlich.", "settings"));

        var gmail = string.Equals(settings.Imap.Host.Trim(), "imap.gmail.com", StringComparison.OrdinalIgnoreCase);
        // Google shows app passwords in groups of four; the spaces are not part of the password.
        var password = gmail ? string.Concat(request.Password.Where(character => !char.IsWhiteSpace(character))) : request.Password;
        var account = new MailAccount(
            existing?.Key ?? MailAccountCatalog.CreateAccountKey(),
            existing?.Email ?? email,
            string.IsNullOrWhiteSpace(settings.Username) ? email : settings.Username.Trim(),
            settings.Imap with { Host = settings.Imap.Host.Trim() },
            settings.Smtp with { Host = settings.Smtp.Host.Trim() });

        try
        {
            await _verifySignIn(account, password, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AuthenticationException)
        {
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.Authentication, gmail
                ? "Google hat die Anmeldung abgelehnt. Verwende ein App-Passwort, nicht dein normales Google-Passwort. Dafür muss die Bestätigung in zwei Schritten aktiv sein."
                : "Der Mailserver hat die Anmeldung abgelehnt. Prüfe Adresse und Passwort. Manche Anbieter (z. B. GMX, web.de, T-Online) verlangen, dass IMAP in den Postfach-Einstellungen freigeschaltet ist oder ein eigenes Passwort dafür genutzt wird.",
                "password"));
        }
        catch (Exception exception) when (MailOperation.Translate(exception) is { } error)
        {
            return OperationResult.Fail<ConnectionSummary>(error with
            {
                Message = error.Code switch
                {
                    OperationErrorCode.ProviderUnavailable => "Der Mailserver ist nicht erreichbar. Prüfe Servername, Port und deine Internetverbindung.",
                    OperationErrorCode.Configuration => "Es konnte keine sichere Verbindung zum Mailserver aufgebaut werden. Prüfe Port und Verschlüsselung.",
                    _ => $"Die Verbindung zum Mailserver ist fehlgeschlagen: {error.Message}",
                },
            });
        }

        await _mail.SaveAsync(account, password, cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<ConnectionSummary>> DisconnectEmailAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var mailbox = _mail.Find(email);
        if (mailbox is null)
            return OperationResult.Fail<ConnectionSummary>(new(OperationErrorCode.NotFound, "Das E-Mail-Konto wurde nicht gefunden."));
        _mail.Remove(mailbox.Key);
        return await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public OperationResult<SetupSnapshot> Disconnect(string accountKey)
    {
        try
        {
            if (accountKey.StartsWith(MailKeyPrefix, StringComparison.Ordinal))
            {
                _mail.Remove(accountKey[MailKeyPrefix.Length..]);
                return OperationResult.Ok(Inspect());
            }

            var email = _accounts.List().FirstOrDefault(item => item.Key == accountKey)?.Email;
            if (email is not null && _mail.Find(email) is { } mailbox) _mail.Remove(mailbox.Key);

            var tokenFile = Path.Combine(
                _accounts.TokenDirectory(accountKey),
                GoogleServiceFactory.TokenFileName);
            if (File.Exists(tokenFile))
            {
                File.Delete(tokenFile);
            }

            // Builds that used the Gmail API kept a separate token here.
            var legacyEmailDirectory = Path.Combine(_accounts.TokenDirectory(accountKey), "email");
            if (Directory.Exists(legacyEmailDirectory)) Directory.Delete(legacyEmailDirectory, recursive: true);

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
        OperationError? calendarTasksError = null;
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
        }

        _accounts.Upsert(registration with { Email = email });
        return OperationResult.Ok(new AccountConnectionSummary(
            registration.Key, email, calendars, taskLists,
            true, calendarTasksError is null, CalendarTasksError: calendarTasksError));
    }

    private async Task<OperationResult<string>> AuthenticateAsync(
        string accountKey, string? expectedEmail, CancellationToken cancellationToken)
    {
        var pendingDirectory = Path.Combine(_accounts.TokenDirectory(accountKey), $"pending-{Guid.NewGuid():N}");
        try
        {
            var factory = new GoogleServiceFactory(_options with { TokenStorePath = pendingDirectory }, interactive: true);
            using var calendar = await factory.CreateCalendarAsync(cancellationToken).ConfigureAwait(false);
            var primary = await calendar.CalendarList.Get("primary").ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var email = primary.Id;
            using var tasks = await factory.CreateTasksAsync(cancellationToken).ConfigureAwait(false);
            await tasks.Tasklists.List().ExecuteAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<OperationError?> CheckMailboxAsync(MailAccount mailbox, CancellationToken cancellationToken)
    {
        try
        {
            await _checkMailbox(mailbox, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (MailOperation.Translate(exception) is { } error)
        {
            return error.Code == OperationErrorCode.Authentication
                ? error with { Message = "Das gespeicherte Passwort wird nicht mehr akzeptiert. Verbinde das Postfach erneut." }
                : error;
        }
    }

    private static bool IsEmailAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(value.Trim());

    private static bool Invalid(MailServer server) =>
        string.IsNullOrWhiteSpace(server.Host) || server.Port is < 1 or > 65535;

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
