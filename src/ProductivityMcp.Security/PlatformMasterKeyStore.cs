using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ProductivityMcp.Core;

namespace ProductivityMcp.Security;

public interface IMasterKeyStore
{
    Task<byte[]> GetOrCreateAsync(CancellationToken cancellationToken);
}

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "SemaphoreSlim does not create an OS wait handle unless AvailableWaitHandle is accessed.")]
public sealed class PlatformMasterKeyStore : IMasterKeyStore
{
    public const string WindowsKeyFileName = ".protected-key";
    // Kept for compatibility with keys created before IMAP secrets shared this store.
    private const string ServiceName = "Productivity MCP OAuth";
    public static PlatformMasterKeyStore Shared { get; } = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private byte[]? _cached;

    public async Task<byte[]> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached ??= OperatingSystem.IsWindows()
                ? await GetWindowsKeyAsync(cancellationToken).ConfigureAwait(false)
                : OperatingSystem.IsMacOS()
                    ? await GetMacKeyAsync(cancellationToken).ConfigureAwait(false)
                    : await GetLinuxKeyAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<byte[]> GetWindowsKeyAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProductivityMcp",
            WindowsKeyFileName);
        if (File.Exists(path))
        {
            var protectedKey = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedBytes = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken).ConfigureAwait(false);
        return key;
    }

    private static async Task<byte[]> GetMacKeyAsync(CancellationToken cancellationToken)
    {
        var account = KeyAccount();
        var found = await RunAsync("security", ["find-generic-password", "-a", account, "-s", ServiceName, "-w"], null, cancellationToken, allowFailure: true).ConfigureAwait(false);
        if (found.ExitCode == 0 && TryDecodeKey(found.Output, out var existing))
        {
            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var encoded = Convert.ToBase64String(key);
        var stored = await RunAsync("security", ["add-generic-password", "-U", "-a", account, "-s", ServiceName, "-w", encoded], null, cancellationToken).ConfigureAwait(false);
        if (stored.ExitCode != 0)
        {
            throw SecureStoreUnavailable(stored.Error);
        }

        return key;
    }

    private static async Task<byte[]> GetLinuxKeyAsync(CancellationToken cancellationToken)
    {
        var account = KeyAccount();
        var found = await RunAsync("secret-tool", ["lookup", "application", "productivity-mcp", "account", account], null, cancellationToken, allowFailure: true).ConfigureAwait(false);
        if (found.ExitCode == 0 && TryDecodeKey(found.Output, out var existing))
        {
            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var encoded = Convert.ToBase64String(key);
        var stored = await RunAsync(
            "secret-tool",
            ["store", "--label", ServiceName, "application", "productivity-mcp", "account", account],
            encoded,
            cancellationToken).ConfigureAwait(false);
        if (stored.ExitCode != 0)
        {
            throw SecureStoreUnavailable(stored.Error);
        }

        return key;
    }

    private static string KeyAccount()
    {
        var user = Environment.UserName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)));
    }

    private static bool TryDecodeKey(string value, out byte[] key)
    {
        try
        {
            key = Convert.FromBase64String(value.Trim());
            return key.Length == 32;
        }
        catch (FormatException)
        {
            key = [];
            return false;
        }
    }

    private static ConfigurationException SecureStoreUnavailable(string detail) => new(
        $"The operating-system credential store is unavailable. {detail.Trim()}",
        "tokenStore");

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken,
        bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo) ?? throw SecureStoreUnavailable($"Could not start {fileName}.");
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var result = new ProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
            if (!allowFailure && result.ExitCode != 0)
            {
                throw SecureStoreUnavailable(result.Error);
            }

            return result;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw SecureStoreUnavailable($"{fileName} is not installed: {exception.Message}");
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
