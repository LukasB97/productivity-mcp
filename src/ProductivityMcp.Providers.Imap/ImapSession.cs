using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ProductivityMcp.Providers.Imap;

/// <summary>One authenticated IMAP connection with cached folder lookups.</summary>
internal sealed class ImapSession : IAsyncDisposable
{
    private const int TimeoutMilliseconds = 60_000;
    private static readonly Dictionary<SpecialFolder, string[]> FallbackNames = new()
    {
        [SpecialFolder.Sent] = ["Sent", "Sent Items", "Sent Messages", "Sent Mail", "Gesendet", "Gesendete Objekte", "Gesendete Elemente"],
        [SpecialFolder.Drafts] = ["Drafts", "Draft", "Entwürfe", "Entwurf"],
        [SpecialFolder.Trash] = ["Trash", "Deleted", "Deleted Items", "Deleted Messages", "Papierkorb", "Gelöscht", "Gelöschte Objekte", "Gelöschte Elemente"],
        [SpecialFolder.Junk] = ["Junk", "Spam", "Junk E-Mail", "Junk-E-Mail", "Bulk Mail", "Spamverdacht"],
        [SpecialFolder.Archive] = ["Archive", "Archiv"],
    };
    private static readonly Dictionary<SpecialFolder, FolderAttributes> SpecialAttributes = new()
    {
        [SpecialFolder.All] = FolderAttributes.All,
        [SpecialFolder.Archive] = FolderAttributes.Archive,
        [SpecialFolder.Drafts] = FolderAttributes.Drafts,
        [SpecialFolder.Flagged] = FolderAttributes.Flagged,
        [SpecialFolder.Important] = FolderAttributes.Important,
        [SpecialFolder.Junk] = FolderAttributes.Junk,
        [SpecialFolder.Sent] = FolderAttributes.Sent,
        [SpecialFolder.Trash] = FolderAttributes.Trash,
    };

    private readonly Dictionary<SpecialFolder, IMailFolder?> _special = [];
    private IReadOnlyList<IMailFolder>? _folders;

    private ImapSession(MailAccount account, ImapClient client)
    {
        Account = account;
        Client = client;
    }

    /// <summary>
    /// Test-only hook for local servers with self-signed certificates. Production code never sets it,
    /// so certificate validation always uses the operating system's trust store.
    /// </summary>
    internal static System.Net.Security.RemoteCertificateValidationCallback? CertificateValidationOverride { get; set; }

    public MailAccount Account { get; }
    public ImapClient Client { get; }
    public bool IsGmail => Client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);
    public bool HasUidPlus => Client.Capabilities.HasFlag(ImapCapabilities.UidPlus);

    public static async Task<ImapSession> OpenAsync(MailAccount account, string password, CancellationToken cancellationToken)
    {
        var client = new ImapClient { Timeout = TimeoutMilliseconds };
        if (CertificateValidationOverride is { } validation) client.ServerCertificateValidationCallback = validation;
        try
        {
            await client.ConnectAsync(account.Imap.Host, account.Imap.Port, Socket(account.Imap.Security), cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(account.Username, password, cancellationToken).ConfigureAwait(false);
            return new ImapSession(account, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task SendAsync(MailAccount account, string password, MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = CreateSmtp();
        await client.ConnectAsync(account.Smtp.Host, account.Smtp.Port, Socket(account.Smtp.Security), cancellationToken).ConfigureAwait(false);
        await client.AuthenticateAsync(account.Username, password, cancellationToken).ConfigureAwait(false);
        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verifies IMAP and SMTP sign-in without changing the mailbox.</summary>
    public static async Task<bool> VerifyAsync(MailAccount account, string password, CancellationToken cancellationToken)
    {
        await using (var session = await OpenAsync(account, password, cancellationToken).ConfigureAwait(false))
        {
            using var smtp = CreateSmtp();
            await smtp.ConnectAsync(account.Smtp.Host, account.Smtp.Port, Socket(account.Smtp.Security), cancellationToken).ConfigureAwait(false);
            await smtp.AuthenticateAsync(account.Username, password, cancellationToken).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return session.IsGmail;
        }
    }

    public async Task<IReadOnlyList<IMailFolder>> FoldersAsync(CancellationToken cancellationToken)
    {
        if (_folders is not null) return _folders;
        var namespaceRoot = Client.PersonalNamespaces.Count > 0 ? Client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var folders = await Client.GetFoldersAsync(namespaceRoot, StatusItems.None, false, cancellationToken).ConfigureAwait(false);
        _folders = folders
            .Where(folder => !folder.Attributes.HasFlag(FolderAttributes.NoSelect) && !folder.Attributes.HasFlag(FolderAttributes.NonExistent))
            .Prepend(Client.Inbox)
            .DistinctBy(folder => folder.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return _folders;
    }

    public async Task<IMailFolder?> SpecialAsync(SpecialFolder kind, CancellationToken cancellationToken)
    {
        if (_special.TryGetValue(kind, out var cached)) return cached;
        IMailFolder? folder = null;
        if (Client.Capabilities.HasFlag(ImapCapabilities.SpecialUse) || Client.Capabilities.HasFlag(ImapCapabilities.XList))
        {
            try { folder = Client.GetFolder(kind); }
            catch (NotSupportedException) { folder = null; }
        }

        if (folder is null)
        {
            var folders = await FoldersAsync(cancellationToken).ConfigureAwait(false);
            folder = folders.FirstOrDefault(item => SpecialAttributes.TryGetValue(kind, out var flag) && item.Attributes.HasFlag(flag))
                ?? (FallbackNames.TryGetValue(kind, out var names)
                    ? names.Select(name => folders.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(item => item is not null)
                    : null);
        }

        _special[kind] = folder;
        return folder;
    }

    public static async Task<IMailFolder> OpenAsync(IMailFolder folder, FolderAccess access, CancellationToken cancellationToken)
    {
        if (!folder.IsOpen || folder.Access < access)
            await folder.OpenAsync(access, cancellationToken).ConfigureAwait(false);
        return folder;
    }

    public async Task<IMailFolder> GetFolderAsync(string fullName, CancellationToken cancellationToken)
    {
        if (string.Equals(fullName, "INBOX", StringComparison.OrdinalIgnoreCase)) return Client.Inbox;
        return await Client.GetFolderAsync(fullName, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Client.IsConnected) await Client.DisconnectAsync(true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ImapProtocolException or ServiceNotConnectedException)
        {
            // The mailbox operation already completed; a failed LOGOUT does not change its result.
        }
        finally
        {
            Client.Dispose();
        }
    }

    private static SmtpClient CreateSmtp()
    {
        var client = new SmtpClient { Timeout = TimeoutMilliseconds };
        if (CertificateValidationOverride is { } validation) client.ServerCertificateValidationCallback = validation;
        return client;
    }

    private static SecureSocketOptions Socket(MailSecurity security) => security switch
    {
        MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => throw new ArgumentOutOfRangeException(nameof(security)),
    };
}
