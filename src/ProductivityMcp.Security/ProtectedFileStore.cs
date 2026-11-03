using System.Security.Cryptography;

namespace ProductivityMcp.Security;

/// <summary>
/// Stores small secrets as AES-256-GCM encrypted files. The master key is held by the
/// operating-system credential store, so copied files are unreadable on another account.
/// </summary>
public sealed class ProtectedFileStore
{
    private static readonly byte[] Magic = "PMCP1"u8.ToArray();
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly IMasterKeyStore _masterKeyStore;

    public ProtectedFileStore(IMasterKeyStore? masterKeyStore = null)
    {
        _masterKeyStore = masterKeyStore ?? PlatformMasterKeyStore.Shared;
    }

    public static bool IsProtected(ReadOnlySpan<byte> stored) => stored.StartsWith(Magic);

    public async Task WriteAsync(string path, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        EnsurePrivateDirectory(directory);
        var encrypted = await EncryptAsync(plaintext, cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(path, encrypted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the decrypted content, or <c>null</c> when the file does not exist.</summary>
    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        var stored = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!IsProtected(stored)) throw new CryptographicException("The protected file has an unknown format.");
        return await DecryptAsync(stored, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken = default)
    {
        var key = await _masterKeyStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var tag = new byte[TagLength];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);

        var result = new byte[Magic.Length + nonce.Length + tag.Length + ciphertext.Length];
        Magic.CopyTo(result, 0);
        nonce.CopyTo(result, Magic.Length);
        tag.CopyTo(result, Magic.Length + nonce.Length);
        ciphertext.CopyTo(result, Magic.Length + nonce.Length + tag.Length);
        return result;
    }

    public async Task<byte[]> DecryptAsync(byte[] stored, CancellationToken cancellationToken = default)
    {
        if (stored.Length < Magic.Length + NonceLength + TagLength)
            throw new CryptographicException("The protected file is incomplete.");

        var key = await _masterKeyStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var offset = Magic.Length;
        var nonce = stored.AsSpan(offset, NonceLength);
        offset += NonceLength;
        var tag = stored.AsSpan(offset, TagLength);
        offset += TagLength;
        var ciphertext = stored.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
        return plaintext;
    }

    public static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            RestrictFile(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            RestrictFile(path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
