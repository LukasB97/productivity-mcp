using ProductivityMcp.Core;
using ProductivityMcp.Providers.Imap;

namespace ProductivityMcp.App.Services;

public interface IGoogleSetupService
{
    SetupSnapshot Inspect();

    OperationResult<SetupSnapshot> ImportCredentials(string sourcePath);

    Task<OperationResult<ConnectionSummary>> ConnectAsync(CancellationToken cancellationToken = default);

    Task<OperationResult<ConnectionSummary>> AddAccountAsync(CancellationToken cancellationToken = default);

    Task<OperationResult<ConnectionSummary>> ReconnectAsync(string accountKey, CancellationToken cancellationToken = default);

    /// <summary>Finds IMAP/SMTP settings; succeeds with <c>null</c> when they must be entered manually.</summary>
    Task<OperationResult<MailServerSettings?>> DiscoverMailServerAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Verifies IMAP and SMTP sign-in, then stores the mailbox and its password.</summary>
    Task<OperationResult<ConnectionSummary>> ConnectEmailAsync(MailConnectionRequest request, CancellationToken cancellationToken = default);

    Task<OperationResult<ConnectionSummary>> DisconnectEmailAsync(string email, CancellationToken cancellationToken = default);

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

public sealed record MailConnectionRequest(string Email, string Password, MailServerSettings? Settings);

public sealed record ConnectionSummary(IReadOnlyList<AccountConnectionSummary> Accounts);
