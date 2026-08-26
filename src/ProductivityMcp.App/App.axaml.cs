using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ProductivityMcp.App.Services;
using ProductivityMcp.App.ViewModels;
using ProductivityMcp.App.Views;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = GoogleOptions.FromEnvironment();
            var setupService = new GoogleSetupService(options);
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(setupService),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
