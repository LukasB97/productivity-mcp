using Avalonia;

namespace ProductivityMcp.App;

internal static class Program
{
    internal static SingleInstanceCoordinator? InstanceCoordinator { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        using var coordinator = SingleInstanceCoordinator.Create();
        if (!coordinator.IsPrimary)
        {
            if (ShouldActivateExistingInstance(args))
            {
                coordinator.ActivateAsync().GetAwaiter().GetResult();
            }

            return;
        }

        coordinator.StartListening();
        InstanceCoordinator = coordinator;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            InstanceCoordinator = null;
        }
    }

    internal static bool ShouldActivateExistingInstance(IEnumerable<string> args) =>
        !args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
