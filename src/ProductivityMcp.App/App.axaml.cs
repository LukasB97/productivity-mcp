using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ProductivityMcp.App.Services;
using ProductivityMcp.App.ViewModels;
using ProductivityMcp.App.Views;
using ProductivityMcp.Email;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Avalonia owns the Application lifetime; ExitApplication disposes the tray icon.")]
public sealed partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindowViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayStatusItem;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var options = GoogleOptions.FromEnvironment();
            var setupService = new GoogleSetupService(options);
            var autostartService = new AutostartService();
            var emailContent = new EmailContentService();
            _viewModel = new MainWindowViewModel(setupService, autostartService, emailContent);
            _viewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName is nameof(MainWindowViewModel.IsBusy)
                    or nameof(MainWindowViewModel.IsConnected)
                    or nameof(MainWindowViewModel.IsFeedbackError))
                {
                    RefreshTrayStatus();
                }
            };

            CreateTrayIcon();
            if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            {
                _ = _viewModel.InitializeAsync();
            }
            else
            {
                _mainWindow = CreateMainWindow();
                desktop.MainWindow = _mainWindow;
            }

            Program.InstanceCoordinator?.SetActivationHandler(
                () => Dispatcher.UIThread.Post(ShowMainWindow));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateMainWindow() => new()
    {
        DataContext = _viewModel,
        Icon = TrayIconFactory.Create(GetTrayState()),
    };

    private void CreateTrayIcon()
    {
        _trayStatusItem = new NativeMenuItem { IsEnabled = false };
        var openItem = new NativeMenuItem("Productivity MCP öffnen");
        openItem.Click += (_, _) => ShowMainWindow();
        var quitItem = new NativeMenuItem("Beenden");
        quitItem.Click += (_, _) => ExitApplication();

        var menu = new NativeMenu();
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        _trayIcon = new TrayIcon
        {
            Icon = TrayIconFactory.Create(GetTrayState()),
            IsVisible = true,
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
        RefreshTrayStatus();
    }

    private void ShowMainWindow()
    {
        if (_desktop is null || _viewModel is null)
        {
            return;
        }

        if (_mainWindow is null)
        {
            _mainWindow = CreateMainWindow();
            _desktop.MainWindow = _mainWindow;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _ = _viewModel.InitializeAsync();
    }

    private void ExitApplication()
    {
        _mainWindow?.AllowClose();
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
        }

        _desktop?.Shutdown();
    }

    private void RefreshTrayStatus()
    {
        if (_trayIcon is null || _trayStatusItem is null)
        {
            return;
        }

        var state = GetTrayState();
        var status = state switch
        {
            TrayConnectionState.Ready => "Verbunden",
            TrayConnectionState.Connecting => "Verbindung wird geprüft …",
            TrayConnectionState.Error => "Verbindungsfehler",
            _ => "Einrichtung erforderlich",
        };

        _trayIcon.Icon = TrayIconFactory.Create(state);
        _trayIcon.ToolTipText = $"Productivity MCP – {status}";
        _trayStatusItem.Header = $"Status: {status}";
        if (_mainWindow is not null)
        {
            _mainWindow.Icon = TrayIconFactory.Create(state);
        }
    }

    private TrayConnectionState GetTrayState()
    {
        if (_viewModel?.IsFeedbackError is true)
        {
            return TrayConnectionState.Error;
        }

        if (_viewModel?.IsBusy is true)
        {
            return TrayConnectionState.Connecting;
        }

        return _viewModel?.IsConnected is true
            ? TrayConnectionState.Ready
            : TrayConnectionState.SetupRequired;
    }
}
