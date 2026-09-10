using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace ProductivityMcp.App.Services;

public sealed class AutostartService : IAutostartService
{
    private const string AppName = "ProductivityMcp";
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MacLaunchAgentName = "com.productivitymcp.app.plist";
    private const string LinuxDesktopFileName = "productivity-mcp.desktop";

    private readonly string _executablePath;

    public AutostartService()
    {
        _executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Der Pfad der laufenden App konnte nicht ermittelt werden.");
    }

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey);
            return key?.GetValue(AppName) is string;
        }

        if (OperatingSystem.IsMacOS())
        {
            return File.Exists(GetMacLaunchAgentPath());
        }

        if (OperatingSystem.IsLinux())
        {
            return File.Exists(GetLinuxDesktopFilePath());
        }

        return false;
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Autostart wird auf diesem System nicht unterstützt.");
        }

        if (OperatingSystem.IsWindows())
        {
            SetWindowsEnabled(enabled);
        }
        else if (OperatingSystem.IsMacOS())
        {
            SetMacEnabled(enabled);
        }
        else
        {
            SetLinuxEnabled(enabled);
        }
    }

    [SupportedOSPlatform("windows")]
    private void SetWindowsEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey, writable: true)
            ?? throw new InvalidOperationException("Der Windows-Autostart konnte nicht geöffnet werden.");

        if (enabled)
        {
            key.SetValue(AppName, $"{QuoteArgument(_executablePath)} --minimized", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    private void SetMacEnabled(bool enabled)
    {
        var path = GetMacLaunchAgentPath();
        if (!enabled)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var executable = SecurityElement.Escape(_executablePath);
        File.WriteAllText(
            path,
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
              <dict>
                <key>Label</key>
                <string>com.productivitymcp.app</string>
                <key>ProgramArguments</key>
                <array>
                  <string>{executable}</string>
                  <string>--minimized</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
              </dict>
            </plist>
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void SetLinuxEnabled(bool enabled)
    {
        var path = GetLinuxDesktopFilePath();
        if (!enabled)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"""
            [Desktop Entry]
            Type=Application
            Name=Productivity MCP
            Exec={QuoteArgument(_executablePath)} --minimized
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string GetMacLaunchAgentPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        MacLaunchAgentName);

    private static string GetLinuxDesktopFilePath()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configRoot, "autostart", LinuxDesktopFileName);
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
