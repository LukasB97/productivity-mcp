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
    private bool _initialized;
    private string? _disconnectAccountKey;

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
    private bool _needsCredentials;

    [ObservableProperty]
    private bool _showConnectionAction;

    [ObservableProperty]
    private bool _isDisconnectConfirmationVisible;

    [ObservableProperty]
    private string _disconnectAccountEmail = "";

    public MainWindowViewModel(IGoogleSetupService setupService)
    {
        _setupService = setupService;
        ApplyLocalState(_setupService.Inspect());
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
            () => _setupService.AddAccountAsync());
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
        () => _setupService.ConnectAsync());

    private async AsyncTask RunConnectionAsync(
        string busyTitle,
        string busyDetail,
        Func<System.Threading.Tasks.Task<OperationResult<ConnectionSummary>>> action)
    {
        if (!CanInteract)
        {
            return;
        }

        CanInteract = false;
        IsBusy = true;
        ShowConnectionAction = false;
        NeedsCredentials = false;
        HasFeedback = false;
        StatusTitle = busyTitle;
        StatusDetail = busyDetail;

        try
        {
            var result = await action();
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
        finally
        {
            IsBusy = false;
            CanInteract = true;
        }
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
}
