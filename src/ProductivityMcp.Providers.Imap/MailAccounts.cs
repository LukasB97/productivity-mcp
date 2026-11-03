using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProductivityMcp.Core;
using ProductivityMcp.Security;
using Task = System.Threading.Tasks.Task;

namespace ProductivityMcp.Providers.Imap;

[JsonConverter(typeof(JsonStringEnumConverter<MailSecurity>))]
public enum MailSecurity
{
    /// <summary>Implicit TLS, usually IMAP 993 and SMTP 465.</summary>
    SslOnConnect,

    /// <summary>Plain connection upgraded with STARTTLS, usually SMTP 587. TLS is mandatory.</summary>
    StartTls,
}

public sealed record MailServer(string Host, int Port, MailSecurity Security);

public sealed record MailServerSettings(MailServer Imap, MailServer Smtp, string? Username = null);

public sealed record MailAccount(string Key, string Email, string Username, MailServer Imap, MailServer Smtp);

public sealed record MailOptions
{
    public required string DataDirectory { get; init; }
    public required string DownloadsDirectory { get; init; }

    public string AccountsPath => Path.Combine(DataDirectory, "accounts.json");
    public string SecretsDirectory => Path.Combine(DataDirectory, "secrets");

    public static MailOptions FromEnvironment()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProductivityMcp");
        return new MailOptions
        {
            DataDirectory = Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_MAIL_DATA") ?? Path.Combine(root, "mail"),
            DownloadsDirectory = Path.Combine(root, "downloads"),
        };
    }
}

/// <summary>
/// Stores IMAP/SMTP account settings as plain JSON and each password as a protected file.
/// </summary>
public sealed class MailAccountCatalog(MailOptions options, ProtectedFileStore? secrets = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly ProtectedFileStore _secrets = secrets ?? new ProtectedFileStore();

    public MailOptions Options => options;

    public IReadOnlyList<MailAccount> List()
    {
        if (!File.Exists(options.AccountsPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<MailAccount[]>(File.ReadAllText(options.AccountsPath), SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public MailAccount? Find(string email) =>
        List().FirstOrDefault(account => string.Equals(account.Email, email, StringComparison.OrdinalIgnoreCase));

    public static string CreateAccountKey() => Guid.NewGuid().ToString("N");

    public async Task SaveAsync(MailAccount account, string password, CancellationToken cancellationToken = default)
    {
        await _secrets.WriteAsync(SecretPath(account.Key), Encoding.UTF8.GetBytes(password), cancellationToken).ConfigureAwait(false);
        var accounts = List()
            .Where(item => !string.Equals(item.Key, account.Key, StringComparison.Ordinal))
            .Append(account)
            .ToArray();
        Write(accounts);
    }

    public void Remove(string accountKey)
    {
        Write(List().Where(item => !string.Equals(item.Key, accountKey, StringComparison.Ordinal)).ToArray());
        var secret = SecretPath(accountKey);
        if (File.Exists(secret)) File.Delete(secret);
    }

    public async Task<string> ReadPasswordAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        byte[]? value;
        try
        {
            value = await _secrets.ReadAsync(SecretPath(account.Key), cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            throw new AuthenticationRequiredException(
                $"The stored password for {account.Email} cannot be read. Open the Productivity MCP app and enter it again.");
        }

        return value is null
            ? throw new AuthenticationRequiredException(
                $"No password is stored for {account.Email}. Open the Productivity MCP app and connect the mailbox again.")
            : Encoding.UTF8.GetString(value);
    }

    private string SecretPath(string accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountKey) || accountKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || accountKey.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("The account key contains invalid characters.", nameof(accountKey));
        return Path.Combine(options.SecretsDirectory, accountKey);
    }

    private void Write(IReadOnlyList<MailAccount> accounts)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var temporary = Path.Combine(options.DataDirectory, $".{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(accounts, SerializerOptions));
        File.Move(temporary, options.AccountsPath, overwrite: true);
    }
}
