using System.Text;
using Newtonsoft.Json;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class SecureDataStoreTests
{
    [TestMethod]
    public async System.Threading.Tasks.Task RoundTrip_EncryptsStoredValue()
    {
        using var directory = TemporaryDirectory.Create();
        var store = new SecureDataStore(directory.Path, new FixedMasterKeyStore());

        await store.StoreAsync("example", new StoredValue("secret-value"));

        var path = System.IO.Path.Combine(directory.Path, $"{typeof(StoredValue).FullName}-example");
        var raw = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("secret-value", raw, StringComparison.Ordinal);
        Assert.AreEqual(new StoredValue("secret-value"), await store.GetAsync<StoredValue>("example"));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Get_MigratesLegacyPlaintextInPlace()
    {
        using var directory = TemporaryDirectory.Create();
        var path = System.IO.Path.Combine(directory.Path, $"{typeof(StoredValue).FullName}-example");
        await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(new StoredValue("legacy-secret")));
        var store = new SecureDataStore(directory.Path, new FixedMasterKeyStore());

        var value = await store.GetAsync<StoredValue>("example");

        Assert.AreEqual(new StoredValue("legacy-secret"), value);
        var raw = await File.ReadAllBytesAsync(path);
        CollectionAssert.AreEqual("PMCP1"u8.ToArray(), raw[..5]);
        Assert.IsFalse(Encoding.UTF8.GetString(raw).Contains("legacy-secret", StringComparison.Ordinal));
    }

    private sealed record StoredValue(string Value);

    private sealed class FixedMasterKeyStore : IMasterKeyStore
    {
        public System.Threading.Tasks.Task<byte[]> GetOrCreateAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProductivityMcp.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
