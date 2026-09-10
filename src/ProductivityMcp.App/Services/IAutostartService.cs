namespace ProductivityMcp.App.Services;

public interface IAutostartService
{
    bool IsSupported { get; }

    bool IsEnabled();

    void SetEnabled(bool enabled);
}

public sealed class UnsupportedAutostartService : IAutostartService
{
    public static UnsupportedAutostartService Instance { get; } = new();

    private UnsupportedAutostartService()
    {
    }

    public bool IsSupported => false;

    public bool IsEnabled() => false;

    public void SetEnabled(bool enabled) =>
        throw new PlatformNotSupportedException("Autostart wird auf diesem System nicht unterstützt.");
}
