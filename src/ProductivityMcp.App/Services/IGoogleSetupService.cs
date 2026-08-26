using ProductivityMcp.Core;

namespace ProductivityMcp.App.Services;

public interface IGoogleSetupService
{
    SetupSnapshot Inspect();

    OperationResult<SetupSnapshot> ImportCredentials(string sourcePath);

    Task<OperationResult<ConnectionSummary>> ConnectAsync(CancellationToken cancellationToken = default);

    Task<OperationResult<ConnectionSummary>> AddAccountAsync(CancellationToken cancellationToken = default);

    OperationResult<SetupSnapshot> Disconnect(string accountKey);
}

public sealed record SetupSnapshot(
    string DataDirectory,
    string CredentialsPath,
    bool CredentialsPresent,
    string TokenStorePath,
    bool TokenPresent);

public sealed record AccountConnectionSummary(
    string Key,
    string Email,
    IReadOnlyList<CalendarInfo> Calendars,
    IReadOnlyList<TaskListInfo> TaskLists);

public sealed record ConnectionSummary(IReadOnlyList<AccountConnectionSummary> Accounts);
