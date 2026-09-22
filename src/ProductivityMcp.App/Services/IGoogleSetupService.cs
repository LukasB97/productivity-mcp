using ProductivityMcp.Core;

namespace ProductivityMcp.App.Services;

public interface IGoogleSetupService
{
    SetupSnapshot Inspect();

    OperationResult<SetupSnapshot> ImportCredentials(string sourcePath);

    Task<OperationResult<ConnectionSummary>> ConnectAsync(CancellationToken cancellationToken = default);

    Task<OperationResult<ConnectionSummary>> AddAccountAsync(CancellationToken cancellationToken = default);

    Task<OperationResult<ConnectionSummary>> ReconnectAsync(string accountKey, CancellationToken cancellationToken = default);

    Task<OperationResult<ConnectionSummary>> AddEmailAccountAsync(CancellationToken cancellationToken = default);

    Task<OperationResult<ConnectionSummary>> EnableEmailAsync(string accountKey, CancellationToken cancellationToken = default);

    Task<OperationResult<ConnectionSummary>> DisableEmailAsync(string accountKey, CancellationToken cancellationToken = default);

    OperationResult<SetupSnapshot> Disconnect(string accountKey);
}

public sealed record SetupSnapshot(
    string DataDirectory,
    string CredentialsPath,
    bool CredentialsPresent,
    string TokenStorePath,
    bool TokenPresent,
    bool AccountsPresent = false);

public sealed record AccountConnectionSummary(
    string Key,
    string Email,
    IReadOnlyList<CalendarInfo> Calendars,
    IReadOnlyList<TaskListInfo> TaskLists,
    bool CalendarTasksEnabled = true,
    bool CalendarTasksConnected = true,
    bool EmailEnabled = false,
    bool EmailConnected = false,
    OperationError? CalendarTasksError = null,
    OperationError? EmailError = null);

public sealed record ConnectionSummary(IReadOnlyList<AccountConnectionSummary> Accounts);
