using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProductivityMcp.App.Services;
using ProductivityMcp.Core;
using ProductivityMcp.Email;
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
    bool EmailConnected)
{
    public string EmailActionText => EmailEnabled ? "E-Mail deaktivieren" : "E-Mail verbinden";
    public string EmailStatusText => EmailConnected ? "Verbunden" : EmailEnabled ? "Erneut anmelden" : "Deaktiviert";
    public string CalendarTasksStatusText => CalendarTasksConnected ? "Verbunden" : CalendarTasksEnabled ? "Erneut anmelden" : "Deaktiviert";
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
    private IReadOnlyList<ConnectedAccountViewModel> _accounts = [];

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
        if (snapshot.CredentialsPresent && snapshot.TokenPresent)
        {
            await VerifyConnectionAsync(showBrowserMessage: false);
        }
    }

    public async AsyncTask ImportCredentialsAsync(string sourcePath)
    {
        if (!CanInteract)
        {
            return;
        }

        var result = _setupService.ImportCredentials(sourcePath);
        switch (result)
        {
            case OperationResult<SetupSnapshot>.Success success:
                ApplyLocalState(success.Value);
                await VerifyConnectionAsync(showBrowserMessage: true);
                break;
            case OperationResult<SetupSnapshot>.Failure failure:
                ShowError(failure.Error.Message);
                break;
        }
    }

    [RelayCommand]
    private AsyncTask ConnectAsync() => VerifyConnectionAsync(showBrowserMessage: true);

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
    private async AsyncTask AddEmailAccountAsync()
    {
        if (!CanInteract) return;
        await RunConnectionAsync(
            "E-Mail-Konto hinzufügen …",
            "Wähle im Browser das Google-Konto aus, dessen E-Mail du verbinden möchtest.",
            cancellationToken => _setupService.AddEmailAccountAsync(cancellationToken));
    }

    public async AsyncTask EnableEmailAsync(string accountKey)
    {
        if (!CanInteract) return;
        await RunConnectionAsync(
            "Gmail wird verbunden …",
            "Bestätige im Browser den E-Mail-Zugriff für dieses Konto.",
            cancellationToken => _setupService.EnableEmailAsync(accountKey, cancellationToken));
    }

    public async AsyncTask DisableEmailAsync(string accountKey)
    {
        if (!CanInteract) return;
        await RunConnectionAsync(
            "Gmail wird deaktiviert …",
            "Der lokale E-Mail-Zugriff wird ausgeschaltet.",
            cancellationToken => _setupService.DisableEmailAsync(accountKey, cancellationToken));
    }

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
        StatusTitle = "Google-Anmeldung wird abgebrochen …";
        StatusDetail = "Der aktuelle Anmeldeversuch wird beendet.";
        _connectionCancellation.Cancel();
    }

    public void RequestDisconnect(string accountKey)
    {
        var account = Accounts.FirstOrDefault(item => item.Key == accountKey);
        if (account is null)
        {
            return;
        }

        _disconnectAccountKey = account.Key;
        DisconnectAccountEmail = account.Email;
        IsDisconnectConfirmationVisible = true;
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
        if (_disconnectAccountKey is null)
        {
            return;
        }

        var accountKey = _disconnectAccountKey;
        _disconnectAccountKey = null;
        IsDisconnectConfirmationVisible = false;
        var result = _setupService.Disconnect(accountKey);
        switch (result)
        {
            case OperationResult<SetupSnapshot>.Success success when success.Value.TokenPresent:
                await VerifyConnectionAsync(showBrowserMessage: false);
                break;
            case OperationResult<SetupSnapshot>.Success success:
                ApplyLocalState(success.Value);
                HasFeedback = true;
                IsFeedbackError = false;
                Feedback = "Google-Konto wurde getrennt.";
                break;
            case OperationResult<SetupSnapshot>.Failure failure:
                ShowError(failure.Error.Message);
                break;
        }
    }

    private AsyncTask VerifyConnectionAsync(bool showBrowserMessage) => RunConnectionAsync(
        "Verbindung wird geprüft …",
        showBrowserMessage
            ? "Falls nötig, öffnet sich gleich die Google-Anmeldung im Browser."
            : "Konten, Kalender und Aufgabenlisten werden kurz geprüft.",
        cancellationToken => _setupService.ConnectAsync(cancellationToken));

    private async AsyncTask RunConnectionAsync(
        string busyTitle,
        string busyDetail,
        Func<CancellationToken, System.Threading.Tasks.Task<OperationResult<ConnectionSummary>>> action)
    {
        if (!CanInteract)
        {
            return;
        }

        var previousState = CaptureUiState();
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
            Feedback = "Google-Anmeldung wurde abgebrochen.";
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
            StatusTitle = "Google-Anmeldung fehlt";
            StatusDetail = "Melde dich an, damit Calendar und Tasks genutzt werden können.";
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
            account.EmailConnected)).ToArray();

        HasAccounts = Accounts.Count > 0;
        IsConnected = Accounts.Any(account => account.CalendarTasksConnected || account.EmailConnected);
        NeedsCredentials = false;
        ShowConnectionAction = false;
        StatusTitle = Accounts.Count == 1 ? "1 Google-Konto konfiguriert" : $"{Accounts.Count} Google-Konten konfiguriert";
        StatusDetail = IsConnected ? "Aktivierte Google-Dienste sind einsatzbereit." : "Für diese Konten ist derzeit kein Dienst verbunden.";
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
        StatusDetail = "Melde dich erneut an, um Calendar und Tasks zu verbinden.";
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
