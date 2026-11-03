using System.Text;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using ProductivityMcp.Security;

namespace ProductivityMcp.Providers.Google;

/// <summary>Google token store adapter over <see cref="ProtectedFileStore"/>.</summary>
internal sealed class SecureDataStore : IDataStore
{
    private readonly string _directory;
    private readonly ProtectedFileStore _files;

    public SecureDataStore(string directory, IMasterKeyStore? masterKeyStore = null)
    {
        _directory = Path.GetFullPath(directory);
        ProtectedFileStore.EnsurePrivateDirectory(_directory);
        _files = new ProtectedFileStore(masterKeyStore);
    }

    public async System.Threading.Tasks.Task StoreAsync<T>(string key, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
        await _files.WriteAsync(FilePath<T>(key), plaintext).ConfigureAwait(false);
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
        var encrypted = ProtectedFileStore.IsProtected(stored);
        var plaintext = encrypted
            ? await _files.DecryptAsync(stored).ConfigureAwait(false)
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
}
