using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using ProductivityMcp.Core;

namespace ProductivityMcp.Providers.Google;

internal interface IMasterKeyStore
{
    System.Threading.Tasks.Task<byte[]> GetOrCreateAsync(CancellationToken cancellationToken);
}

internal sealed class SecureDataStore : IDataStore
{
    private static readonly byte[] Magic = "PMCP1"u8.ToArray();
    private readonly string _directory;
    private readonly IMasterKeyStore _masterKeyStore;

    public SecureDataStore(string directory, IMasterKeyStore? masterKeyStore = null)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        RestrictDirectory(_directory);
        _masterKeyStore = masterKeyStore ?? new PlatformMasterKeyStore();
    }

    public async System.Threading.Tasks.Task StoreAsync<T>(string key, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
        var encrypted = await EncryptAsync(plaintext, CancellationToken.None).ConfigureAwait(false);
        await WriteAtomicallyAsync(FilePath<T>(key), encrypted, CancellationToken.None).ConfigureAwait(false);
    }

    public async System.Threading.Tasks.Task DeleteAsync<T>(string key)
    {
        var path = FilePath<T>(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
    }

    public async System.Threading.Tasks.Task<T?> GetAsync<T>(string key)
    {
        var path = FilePath<T>(key);
        if (!File.Exists(path))
        {
            return default;
        }

        var stored = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var encrypted = stored.AsSpan().StartsWith(Magic);
        var plaintext = encrypted
            ? await DecryptAsync(stored, CancellationToken.None).ConfigureAwait(false)
            : stored;
        var value = JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(plaintext));

        if (!encrypted && value is not null)
        {
            await StoreAsync(key, value).ConfigureAwait(false);
        }

        return value;
    }

    public System.Threading.Tasks.Task ClearAsync()
    {
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            if (!string.Equals(Path.GetFileName(path), PlatformMasterKeyStore.WindowsKeyFileName, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }

        return System.Threading.Tasks.Task.CompletedTask;
    }

    private string FilePath<T>(string key)
    {
        var fileName = $"{typeof(T).FullName}-{key}";
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The data-store key contains path characters.", nameof(key));
        }

        return Path.Combine(_directory, fileName);
    }

    private async System.Threading.Tasks.Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken)
    {
        var key = await _masterKeyStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);

        var result = new byte[Magic.Length + nonce.Length + tag.Length + ciphertext.Length];
        Magic.CopyTo(result, 0);
        nonce.CopyTo(result, Magic.Length);
        tag.CopyTo(result, Magic.Length + nonce.Length);
        ciphertext.CopyTo(result, Magic.Length + nonce.Length + tag.Length);
        return result;
    }

    private async System.Threading.Tasks.Task<byte[]> DecryptAsync(byte[] stored, CancellationToken cancellationToken)
    {
        const int nonceLength = 12;
        const int tagLength = 16;
        if (stored.Length < Magic.Length + nonceLength + tagLength)
        {
            throw new CryptographicException("The protected Google token is incomplete.");
        }

        var key = await _masterKeyStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var offset = Magic.Length;
        var nonce = stored.AsSpan(offset, nonceLength);
        offset += nonceLength;
        var tag = stored.AsSpan(offset, tagLength);
        offset += tagLength;
        var ciphertext = stored.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
        return plaintext;
    }

    private static async System.Threading.Tasks.Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            RestrictFile(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            RestrictFile(path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "SemaphoreSlim does not create an OS wait handle unless AvailableWaitHandle is accessed.")]
internal sealed class PlatformMasterKeyStore : IMasterKeyStore
{
    internal const string WindowsKeyFileName = ".protected-key";
    private const string ServiceName = "Productivity MCP OAuth";
    private readonly SemaphoreSlim _lock = new(1, 1);
    private byte[]? _cached;

    public async System.Threading.Tasks.Task<byte[]> GetOrCreateAsync(CancellationToken cancellationToken)
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
    private static async System.Threading.Tasks.Task<byte[]> GetWindowsKeyAsync(CancellationToken cancellationToken)
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

    private static async System.Threading.Tasks.Task<byte[]> GetMacKeyAsync(CancellationToken cancellationToken)
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

    private static async System.Threading.Tasks.Task<byte[]> GetLinuxKeyAsync(CancellationToken cancellationToken)
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

    private static async System.Threading.Tasks.Task<ProcessResult> RunAsync(
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
