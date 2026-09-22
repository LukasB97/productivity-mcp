using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using ProductivityMcp.App.Services;
using ProductivityMcp.App.ViewModels;
using ProductivityMcp.App.Views;
using ProductivityMcp.Core;

namespace ProductivityMcp.UiPreview;

// A synthetic-data preview for manual layout checks and screenshots. It neither
// reads the user's configuration nor starts OAuth or the production tray service.
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<PreviewApplication>()
        .UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
}

internal sealed class PreviewApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                Title = "Productivity MCP — UI preview (synthetic data)",
                DataContext = new MainWindowViewModel(new PreviewSetupService()),
            };
            if (desktop.Args?.Contains("--compact", StringComparer.Ordinal) == true)
            {
                window.Width = 520;
                window.Height = 580;
            }
            window.AllowClose();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class PreviewSetupService : IGoogleSetupService
{
    public SetupSnapshot Inspect() => new("preview", "preview", true, "preview", true);
    public OperationResult<SetupSnapshot> ImportCredentials(string sourcePath) => OperationResult.Ok(Inspect());
    public OperationResult<SetupSnapshot> Disconnect(string accountKey) => OperationResult.Ok(Inspect());
    public Task<OperationResult<ConnectionSummary>> ConnectAsync(CancellationToken cancellationToken = default) =>
        System.Threading.Tasks.Task.FromResult<OperationResult<ConnectionSummary>>(OperationResult.Ok(new ConnectionSummary([
            new("personal", "alex@example.com",
                [new("primary", "Persönlich", "google", true, "Europe/Berlin"), new("team", "Teamkalender mit einem sehr langen Namen für den Umbruchtest", "google", false, "Europe/Berlin")],
                [new("tasks", "Meine Aufgaben", "google", null)], true, true, true, true),
            new("work", "alex.long-account-name@engineering.example.org", [], [], false, false, true, false,
                EmailError: new(OperationErrorCode.Authentication, "Google authorization is invalid or expired. Sign in again before retrying.")),
        ])));
    public Task<OperationResult<ConnectionSummary>> AddAccountAsync(CancellationToken cancellationToken = default) => ConnectAsync(cancellationToken);
    public Task<OperationResult<ConnectionSummary>> ReconnectAsync(string accountKey, CancellationToken cancellationToken = default) => ConnectAsync(cancellationToken);
    public Task<OperationResult<ConnectionSummary>> AddEmailAccountAsync(CancellationToken cancellationToken = default) => ConnectAsync(cancellationToken);
    public Task<OperationResult<ConnectionSummary>> EnableEmailAsync(string accountKey, CancellationToken cancellationToken = default) => ConnectAsync(cancellationToken);
    public Task<OperationResult<ConnectionSummary>> DisableEmailAsync(string accountKey, CancellationToken cancellationToken = default) => ConnectAsync(cancellationToken);
}
