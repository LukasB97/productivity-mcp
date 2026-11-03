using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProductivityMcp.App.Services;
using ProductivityMcp.Core;
using ProductivityMcp.Email;
using ProductivityMcp.Providers.Imap;
using AsyncTask = System.Threading.Tasks.Task;

namespace ProductivityMcp.App.ViewModels;

public sealed record ConnectedResourceViewModel(string Name, bool IsDefault);

public sealed record ConnectedAccountViewModel(
    string Key,
    string Email,
    IReadOnlyList<ConnectedResourceViewModel> Calendars,
    IReadOnlyList<ConnectedResourceViewModel> TaskLists,
    bool CalendarTasksEnabled,
    bool CalendarTasksConnected,
    bool EmailEnabled,
    bool EmailConnected,
    OperationError? CalendarTasksError = null,
    OperationError? EmailError = null)
{
    public string EmailActionText => EmailEnabled ? "E-Mail trennen" : "E-Mail verbinden";
    public string EmailStatusText => Status(EmailEnabled, EmailConnected, EmailError);
    public string CalendarTasksStatusText => Status(CalendarTasksEnabled, CalendarTasksConnected, CalendarTasksError);
    public bool NeedsGoogleReconnect => CalendarTasksEnabled
        && (NeedsSignIn(CalendarTasksError) || (!CalendarTasksConnected && CalendarTasksError is null));
    public bool NeedsEmailReconnect => EmailEnabled
        && (NeedsSignIn(EmailError) || (!EmailConnected && EmailError is null));
    public bool NeedsReconnect => NeedsGoogleReconnect || NeedsEmailReconnect;
    public bool HasConnectionError => CalendarTasksError is not null || EmailError is not null;
    public string ConnectionError => string.Join("\n", new[] { CalendarTasksError?.Message, EmailError?.Message }
        .Where(message => !string.IsNullOrWhiteSpace(message)).Distinct());
    public string ResourceSummary => $"{Calendars.Count} Kalender · {TaskLists.Count} Aufgabenlisten";

    private static bool NeedsSignIn(OperationError? error) => error?.Code is OperationErrorCode.Authentication or OperationErrorCode.PermissionDenied;
    private static string Status(bool enabled, bool connected, OperationError? error) => !enabled ? "Nicht verbunden"
        : connected ? "Verbunden" : error is null || NeedsSignIn(error) ? "Erneut anmelden" : "Prüfung fehlgeschlagen";
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IGoogleSetupService _setupService;
    private readonly IAutostartService _autostartService;
    private readonly EmailContentService _emailContent;
    private bool _initialized;
    private bool _updatingAutostart;
    private string? _disconnectAccountKey;
    private CancellationTokenSource? _connectionCancellation;
    private CancellationTokenSource? _rendererCancellation;

    [ObservableProperty]
    private string _statusTitle = "Google einrichten";

    [ObservableProperty]
    private string _statusDetail = "Verbinde dein Google-Konto einmalig.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisconnectedServices))]
    private IReadOnlyList<ConnectedAccountViewModel> _accounts = [];

    public bool HasDisconnectedServices => Accounts.Any(account =>
        (account.CalendarTasksEnabled && !account.CalendarTasksConnected) || (account.EmailEnabled && !account.EmailConnected));

    [ObservableProperty]
    private string _connectionActionText = "Mit Google anmelden";

    [ObservableProperty]
    private string _feedback = "";

    [ObservableProperty]
    private bool _hasFeedback;

    [ObservableProperty]
    private bool _isFeedbackError;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _hasAccounts;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _canInteract = true;

    [ObservableProperty]
    private bool _canCancelConnection;

    [ObservableProperty]
    private bool _needsCredentials;

    [ObservableProperty]
    private bool _showConnectionAction;

    [ObservableProperty]
    private bool _isDisconnectConfirmationVisible;

    [ObservableProperty]
    private string _disconnectAccountEmail = "";

    [ObservableProperty]
    private bool _isAutostartSupported;

    [ObservableProperty]
    private bool _startWithSystem;

    [ObservableProperty]
    private bool _isRendererInstalled;

    [ObservableProperty]
    private bool _isRendererBusy;

    [ObservableProperty]
    private bool _isEmailFormVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGmailHelp))]
    private string _emailAddress = "";

    [ObservableProperty]
    private bool _isEmailAddressLocked;

    [ObservableProperty]
    private string _emailPassword = "";

    [ObservableProperty]
    private bool _showServerSettings;

    [ObservableProperty]
    private string _mailUsername = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGmailHelp))]
    private string _imapHost = "";

    [ObservableProperty]
    private string _imapPort = "993";

    [ObservableProperty]
    private int _imapSecurityIndex;

    [ObservableProperty]
    private string _smtpHost = "";

    [ObservableProperty]
    private string _smtpPort = "465";

    [ObservableProperty]
    private int _smtpSecurityIndex;

    [ObservableProperty]
    private string _emailFormError = "";

    [ObservableProperty]
    private bool _hasEmailFormError;

    public static IReadOnlyList<string> SecurityChoices { get; } = ["SSL/TLS", "STARTTLS"];

    public bool ShowGmailHelp => MailServerDiscovery.IsGmailAddress(EmailAddress.Trim())
        || string.Equals(ImapHost.Trim(), "imap.gmail.com", StringComparison.OrdinalIgnoreCase);

    public MainWindowViewModel(
        IGoogleSetupService setupService,
        IAutostartService? autostartService = null,
        EmailContentService? emailContent = null)
    {
        _setupService = setupService;
        _autostartService = autostartService ?? UnsupportedAutostartService.Instance;
        _emailContent = emailContent ?? new EmailContentService();
        _isAutostartSupported = _autostartService.IsSupported;
        _startWithSystem = _isAutostartSupported && _autostartService.IsEnabled();
        _isRendererInstalled = EmailContentService.IsRendererInstalled();
        ApplyLocalState(_setupService.Inspect());
    }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (_updatingAutostart || !IsAutostartSupported)
        {
            return;
        }

        try
        {
            _autostartService.SetEnabled(value);
        }
        catch (Exception exception)
        {
            _updatingAutostart = true;
            StartWithSystem = !value;
            _updatingAutostart = false;
            ShowError($"Autostart konnte nicht geändert werden: {exception.Message}");
        }
    }

    public async AsyncTask InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var snapshot = _setupService.Inspect();
        ApplyLocalState(snapshot);
        if (snapshot.TokenPresent || snapshot.AccountsPresent)
        {
            await VerifyConnectionAsync(showBrowserMessage: false);
        }
    }

    public AsyncTask ImportCredentialsAsync(string sourcePath)
    {
        if (!CanInteract)
        {
            return AsyncTask.CompletedTask;
        }

        var result = _setupService.ImportCredentials(sourcePath);
        switch (result)
        {
            case OperationResult<SetupSnapshot>.Success success:
                ApplyLocalState(success.Value);
                break;
            case OperationResult<SetupSnapshot>.Failure failure:
                ShowError(failure.Error.Message);
                break;
        }
        return AsyncTask.CompletedTask;
    }

    [RelayCommand]
    private AsyncTask ConnectAsync() => AddAccountAsync();

    [RelayCommand]
    private AsyncTask RefreshAsync() => VerifyConnectionAsync(showBrowserMessage: false);

    public AsyncTask ReconnectAsync(string accountKey) => RunConnectionAsync(
        "Google-Konto erneut verbinden …",
        "Bestätige im Browser die Berechtigungen. Bis dahin bleibt die bisherige Anmeldung erhalten.",
        cancellationToken => _setupService.ReconnectAsync(accountKey, cancellationToken));

    [RelayCommand]
    private async AsyncTask AddAccountAsync()
    {
        if (!CanInteract)
        {
            return;
        }

        await RunConnectionAsync(
            "Google-Konto hinzufügen …",
            "Wähle im Browser das zusätzliche Google-Konto aus.",
            cancellationToken => _setupService.AddAccountAsync(cancellationToken));
    }

    [RelayCommand]
    private void OpenEmailForm() => ShowEmailForm(null);

    /// <summary>Opens the mailbox form for an address that is already shown, e.g. a Google account.</summary>
    public void OpenEmailFormFor(string email) => ShowEmailForm(email);

    private void ShowEmailForm(string? email)
    {
        if (!CanInteract) return;
        CancelDisconnect();
        EmailAddress = email ?? "";
        IsEmailAddressLocked = email is not null;
        EmailPassword = "";
        ShowServerSettings = false;
        MailUsername = "";
        ImapHost = "";
        ImapPort = "993";
        ImapSecurityIndex = 0;
        SmtpHost = "";
        SmtpPort = "465";
        SmtpSecurityIndex = 0;
        ClearEmailFormError();
        HasFeedback = false;
        IsEmailFormVisible = true;
    }

    [RelayCommand]
    private void CancelEmailForm()
    {
        IsEmailFormVisible = false;
        EmailPassword = "";
        ClearEmailFormError();
    }

    [RelayCommand]
    private async AsyncTask DetectServerSettingsAsync()
    {
        if (!CanInteract) return;
        ClearEmailFormError();
        var result = await _setupService.DiscoverMailServerAsync(EmailAddress);
        switch (result)
        {
            case OperationResult<MailServerSettings?>.Success { Value: { } settings }:
                FillServerSettings(settings);
                break;
            case OperationResult<MailServerSettings?>.Success:
                FillServerGuess();
                ShowEmailFormError("Für diese Adresse sind keine Einstellungen bekannt. Die Felder enthalten übliche Werte; prüfe sie mit den Angaben deines Anbieters.");
                break;
            case OperationResult<MailServerSettings?>.Failure failure:
                ShowEmailFormError(failure.Error.Message);
                break;
        }
    }

    [RelayCommand]
    private async AsyncTask ConnectEmailAsync()
    {
        if (!CanInteract) return;
        ClearEmailFormError();
        MailServerSettings? settings = null;
        if (ShowServerSettings && !TryReadServerSettings(out settings)) return;

        var request = new MailConnectionRequest(EmailAddress, EmailPassword, settings);
        var connected = false;
        await RunConnectionAsync(
            "E-Mail wird verbunden …",
            "Die Anmeldung beim Mailserver wird geprüft. Es wird nichts gesendet oder verändert.",
            async cancellationToken =>
            {
                var result = await _setupService.ConnectEmailAsync(request, cancellationToken);
                connected = result is OperationResult<ConnectionSummary>.Success;
                return result;
            },
            error =>
            {
                IsEmailFormVisible = true;
                if (error.Field == "settings")
                {
                    ShowServerSettings = true;
                    if (string.IsNullOrWhiteSpace(ImapHost)) FillServerGuess();
                }

                ShowEmailFormError(error.Message);
            });
        if (connected)
        {
            IsEmailFormVisible = false;
            EmailPassword = "";
        }
    }

    public async AsyncTask DisconnectEmailAsync(string email)
    {
        if (!CanInteract) return;
        await RunConnectionAsync(
            "E-Mail wird getrennt …",
            "Die Postfach-Einstellungen und das gespeicherte Passwort werden entfernt.",
            cancellationToken => _setupService.DisconnectEmailAsync(email, cancellationToken));
    }

    private bool TryReadServerSettings(out MailServerSettings? settings)
    {
        settings = null;
        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(SmtpHost)
            || !int.TryParse(ImapPort, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var imapPort)
            || !int.TryParse(SmtpPort, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var smtpPort)
            || imapPort is < 1 or > 65535 || smtpPort is < 1 or > 65535)
        {
            ShowEmailFormError("Prüfe die Servereinstellungen: Servername und Port (1–65535) sind erforderlich.");
            return false;
        }

        settings = new MailServerSettings(
            new MailServer(ImapHost.Trim(), imapPort, Security(ImapSecurityIndex)),
            new MailServer(SmtpHost.Trim(), smtpPort, Security(SmtpSecurityIndex)),
            string.IsNullOrWhiteSpace(MailUsername) ? null : MailUsername.Trim());
        return true;
    }

    private void FillServerSettings(MailServerSettings settings)
    {
        ShowServerSettings = true;
        MailUsername = settings.Username ?? "";
        ImapHost = settings.Imap.Host;
        ImapPort = settings.Imap.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ImapSecurityIndex = SecurityIndex(settings.Imap.Security);
        SmtpHost = settings.Smtp.Host;
        SmtpPort = settings.Smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SmtpSecurityIndex = SecurityIndex(settings.Smtp.Security);
    }

    private void FillServerGuess()
    {
        var domain = EmailAddress.Contains('@', StringComparison.Ordinal) ? EmailAddress[(EmailAddress.LastIndexOf('@') + 1)..].Trim() : "";
        if (domain.Length == 0) return;
        FillServerSettings(new MailServerSettings(
            new MailServer($"imap.{domain}", 993, MailSecurity.SslOnConnect),
            new MailServer($"smtp.{domain}", 465, MailSecurity.SslOnConnect)));
    }

    private void ShowEmailFormError(string message)
    {
        EmailFormError = message;
        HasEmailFormError = true;
    }

    private void ClearEmailFormError()
    {
        EmailFormError = "";
        HasEmailFormError = false;
    }

    private static MailSecurity Security(int index) => index == 1 ? MailSecurity.StartTls : MailSecurity.SslOnConnect;
    private static int SecurityIndex(MailSecurity security) => security == MailSecurity.StartTls ? 1 : 0;

    [RelayCommand]
    private async AsyncTask InstallRendererAsync()
    {
        if (IsRendererBusy) return;
        IsRendererBusy = true;
        HasFeedback = false;
        using var cancellation = new CancellationTokenSource();
        _rendererCancellation = cancellation;
        try
        {
            var exitCode = await EmailContentService.InstallRendererAsync(cancellation.Token);
            IsRendererInstalled = exitCode == 0 && EmailContentService.IsRendererInstalled();
            if (!IsRendererInstalled) ShowError("Chromium konnte nicht installiert werden.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            HasFeedback = true;
            IsFeedbackError = false;
            Feedback = "Renderer-Installation wurde abgebrochen.";
        }
        catch (Exception exception) { ShowError($"Chromium konnte nicht installiert werden: {exception.Message}"); }
        finally { _rendererCancellation = null; IsRendererBusy = false; }
    }

    [RelayCommand]
    private void CancelRenderer() => _rendererCancellation?.Cancel();

    [RelayCommand]
    private void CancelConnection()
    {
        if (!CanCancelConnection || _connectionCancellation is null)
        {
            return;
        }

        CanCancelConnection = false;
        StatusTitle = "Wird abgebrochen …";
        StatusDetail = "Der aktuelle Anmeldeversuch wird beendet.";
        _connectionCancellation.Cancel();
    }

    public void RequestDisconnect(string accountKey)
    {
        if (!CanInteract) return;
        var account = Accounts.FirstOrDefault(item => item.Key == accountKey);
        if (account is null)
        {
            return;
        }

        _disconnectAccountKey = account.Key;
        DisconnectAccountEmail = account.Email;
        IsDisconnectConfirmationVisible = true;
        IsEmailFormVisible = false;
        HasFeedback = false;
    }

    [RelayCommand]
    private void CancelDisconnect()
    {
        _disconnectAccountKey = null;
        IsDisconnectConfirmationVisible = false;
    }

    [RelayCommand]
    private async AsyncTask ConfirmDisconnectAsync()
    {
        if (!CanInteract || _disconnectAccountKey is null)
        {
            return;
        }

        var accountKey = _disconnectAccountKey;
        _disconnectAccountKey = null;
        IsDisconnectConfirmationVisible = false;
        var result = _setupService.Disconnect(accountKey);
        switch (result)
        {
            case OperationResult<SetupSnapshot>.Success success when success.Value.TokenPresent || success.Value.AccountsPresent:
                await VerifyConnectionAsync(showBrowserMessage: false);
                break;
            case OperationResult<SetupSnapshot>.Success success:
                ApplyLocalState(success.Value);
                HasFeedback = true;
                IsFeedbackError = false;
                Feedback = "Konto wurde getrennt.";
                break;
            case OperationResult<SetupSnapshot>.Failure failure:
                ShowError(failure.Error.Message);
                break;
        }
    }

    private AsyncTask VerifyConnectionAsync(bool showBrowserMessage) => RunConnectionAsync(
        "Verbindung wird geprüft …",
        "Konten und aktivierte Dienste werden geprüft. Es wird keine Anmeldung gestartet.",
        cancellationToken => _setupService.ConnectAsync(cancellationToken));

    private async AsyncTask RunConnectionAsync(
        string busyTitle,
        string busyDetail,
        Func<CancellationToken, System.Threading.Tasks.Task<OperationResult<ConnectionSummary>>> action,
        Action<OperationError>? onFailure = null)
    {
        if (!CanInteract)
        {
            return;
        }

        var previousState = CaptureUiState();
        CancelDisconnect();
        using var cancellation = new CancellationTokenSource();
        _connectionCancellation = cancellation;

        CanInteract = false;
        CanCancelConnection = true;
        IsBusy = true;
        ShowConnectionAction = false;
        NeedsCredentials = false;
        HasFeedback = false;
        StatusTitle = busyTitle;
        StatusDetail = busyDetail;

        try
        {
            var result = await action(cancellation.Token);
            CanCancelConnection = false;
            switch (result)
            {
                case OperationResult<ConnectionSummary>.Success success:
                    ApplyConnectedState(success.Value);
                    break;
                case OperationResult<ConnectionSummary>.Failure failure when onFailure is not null:
                    RestoreUiState(previousState);
                    onFailure(failure.Error);
                    break;
                case OperationResult<ConnectionSummary>.Failure failure:
                    ApplyConnectionError(failure.Error.Message);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RestoreUiState(previousState);
            HasFeedback = true;
            IsFeedbackError = false;
            Feedback = "Anmeldung wurde abgebrochen.";
        }
        finally
        {
            if (ReferenceEquals(_connectionCancellation, cancellation))
            {
                _connectionCancellation = null;
            }

            CanCancelConnection = false;
            IsBusy = false;
            CanInteract = true;
        }
    }

    private UiState CaptureUiState() => new(
        Accounts,
        StatusTitle,
        StatusDetail,
        ConnectionActionText,
        IsConnected,
        HasAccounts,
        NeedsCredentials,
        ShowConnectionAction,
        HasFeedback,
        IsFeedbackError,
        Feedback);

    private void RestoreUiState(UiState state)
    {
        Accounts = state.Accounts;
        StatusTitle = state.StatusTitle;
        StatusDetail = state.StatusDetail;
        ConnectionActionText = state.ConnectionActionText;
        IsConnected = state.IsConnected;
        HasAccounts = state.HasAccounts;
        NeedsCredentials = state.NeedsCredentials;
        ShowConnectionAction = state.ShowConnectionAction;
        HasFeedback = state.HasFeedback;
        IsFeedbackError = state.IsFeedbackError;
        Feedback = state.Feedback;
    }

    private void ApplyLocalState(SetupSnapshot snapshot)
    {
        IsBusy = false;
        IsConnected = false;
        HasAccounts = false;
        NeedsCredentials = !snapshot.CredentialsPresent;
        ShowConnectionAction = snapshot.CredentialsPresent;
        Accounts = [];

        if (!snapshot.CredentialsPresent)
        {
            StatusTitle = "Google einrichten";
            StatusDetail = "Wähle einmalig die OAuth-Datei aus der Google Cloud Console aus.";
            ConnectionActionText = "OAuth-Datei auswählen";
            ShowConnectionAction = false;
        }
        else if (snapshot.TokenPresent)
        {
            StatusTitle = "Gespeicherte Verbindung gefunden";
            StatusDetail = "Die Verbindung wird beim Start automatisch geprüft.";
            ConnectionActionText = "Erneut mit Google anmelden";
        }
        else
        {
            StatusTitle = "Noch kein Konto verbunden";
            StatusDetail = "Verbinde Google für Kalender und Aufgaben oder ein E-Mail-Postfach.";
            ConnectionActionText = "Mit Google anmelden";
        }
    }

    private void ApplyConnectedState(ConnectionSummary summary)
    {
        Accounts = summary.Accounts.Select(account => new ConnectedAccountViewModel(
            account.Key,
            account.Email,
            account.Calendars.Select(calendar => new ConnectedResourceViewModel(
                calendar.Name,
                calendar.IsDefault is true)).ToArray(),
            account.TaskLists.Select(taskList => new ConnectedResourceViewModel(
                taskList.Name,
                taskList.IsDefault is true)).ToArray(),
            account.CalendarTasksEnabled,
            account.CalendarTasksConnected,
            account.EmailEnabled,
            account.EmailConnected,
            account.CalendarTasksError,
            account.EmailError)).ToArray();

        HasAccounts = Accounts.Count > 0;
        IsConnected = Accounts.Any(account => account.CalendarTasksConnected || account.EmailConnected);
        // Mailboxes work without a Google OAuth client, so keep offering the import for calendar and tasks.
        NeedsCredentials = !_setupService.Inspect().CredentialsPresent;
        ShowConnectionAction = !HasAccounts && !NeedsCredentials;
        if (!HasAccounts)
        {
            StatusTitle = "Noch kein Konto verbunden";
            StatusDetail = "Verbinde Google für Kalender und Aufgaben oder ein E-Mail-Postfach.";
            ConnectionActionText = "Mit Google anmelden";
            HasFeedback = false;
            IsFeedbackError = false;
            Feedback = "";
            return;
        }

        StatusTitle = Accounts.Count == 1 ? "1 Konto konfiguriert" : $"{Accounts.Count} Konten konfiguriert";
        StatusDetail = HasDisconnectedServices ? "Nicht alle aktivierten Dienste sind verbunden. Details stehen beim jeweiligen Konto."
            : IsConnected ? "Aktivierte Dienste sind einsatzbereit." : "Für diese Konten ist derzeit kein Dienst verbunden.";
        HasFeedback = false;
        IsFeedbackError = false;
        Feedback = "";
    }

    private void ApplyConnectionError(string message)
    {
        HasAccounts = Accounts.Count > 0;
        IsConnected = Accounts.Any(account => account.CalendarTasksConnected || account.EmailConnected);
        ShowConnectionAction = !HasAccounts;
        ConnectionActionText = "Erneut mit Google anmelden";
        StatusTitle = "Verbindung nicht verfügbar";
        StatusDetail = "Die Änderung konnte nicht abgeschlossen werden. Bestehende Konten bleiben erhalten.";
        ShowError(message);
    }

    private void ShowError(string message)
    {
        HasFeedback = true;
        IsFeedbackError = true;
        Feedback = message;
    }

    private sealed record UiState(
        IReadOnlyList<ConnectedAccountViewModel> Accounts,
        string StatusTitle,
        string StatusDetail,
        string ConnectionActionText,
        bool IsConnected,
        bool HasAccounts,
        bool NeedsCredentials,
        bool ShowConnectionAction,
        bool HasFeedback,
        bool IsFeedbackError,
        string Feedback);
}
