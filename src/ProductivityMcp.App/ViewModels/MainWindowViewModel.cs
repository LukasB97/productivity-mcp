using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProductivityMcp.App.Services;
using ProductivityMcp.Core;
using AsyncTask = System.Threading.Tasks.Task;

namespace ProductivityMcp.App.ViewModels;

public sealed record ConnectedResourceViewModel(string Name, bool IsDefault);

public sealed record ConnectedAccountViewModel(
    string Key,
    string Email,
    IReadOnlyList<ConnectedResourceViewModel> Calendars,
    IReadOnlyList<ConnectedResourceViewModel> TaskLists);

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IGoogleSetupService _setupService;
    private readonly IAutostartService _autostartService;
    private bool _initialized;
    private bool _updatingAutostart;
    private string? _disconnectAccountKey;
    private CancellationTokenSource? _connectionCancellation;

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

    public MainWindowViewModel(
        IGoogleSetupService setupService,
        IAutostartService? autostartService = null)
    {
        _setupService = setupService;
        _autostartService = autostartService ?? UnsupportedAutostartService.Instance;
        _isAutostartSupported = _autostartService.IsSupported;
        _startWithSystem = _isAutostartSupported && _autostartService.IsEnabled();
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
                taskList.IsDefault is true)).ToArray())).ToArray();

        IsConnected = Accounts.Count > 0;
        NeedsCredentials = false;
        ShowConnectionAction = false;
        StatusTitle = Accounts.Count == 1 ? "1 Google-Konto verbunden" : $"{Accounts.Count} Google-Konten verbunden";
        StatusDetail = "Calendar und Tasks sind einsatzbereit.";
        HasFeedback = false;
        IsFeedbackError = false;
        Feedback = "";
    }

    private void ApplyConnectionError(string message)
    {
        IsConnected = Accounts.Count > 0;
        ShowConnectionAction = !IsConnected;
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
        bool NeedsCredentials,
        bool ShowConnectionAction,
        bool HasFeedback,
        bool IsFeedbackError,
        string Feedback);
}
