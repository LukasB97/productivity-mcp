using System.Globalization;
using MailKit.Net.Imap;
using MailKit.Security;
using ProductivityMcp.Email;
using ProductivityMcp.Providers.Imap;

namespace ProductivityMcp.Tests;

/// <summary>
/// Runs the full mailbox scenario against a disposable local IMAP/SMTP server (for example
/// GreenMail: see docs/architecture.md). Opt-in, because it needs a running server.
/// </summary>
[TestClass]
public sealed class ImapServerTests
{
    [TestMethod]
    [TestCategory("Live")]
    public async System.Threading.Tasks.Task GenericImapServer_SupportsEveryEmailTool()
    {
        var host = Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_TEST_IMAP_HOST");
        if (string.IsNullOrWhiteSpace(host))
            Assert.Inconclusive("Set PRODUCTIVITY_MCP_TEST_IMAP_HOST (and optionally _IMAP_PORT, _SMTP_PORT, _USER, _PASSWORD) to a disposable test server.");

        var email = Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_TEST_IMAP_USER") ?? "alex@example.org";
        var password = Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_TEST_IMAP_PASSWORD") ?? "secret";
        var account = new MailAccount(
            MailAccountCatalog.CreateAccountKey(), email, email,
            new MailServer(host, Port("PRODUCTIVITY_MCP_TEST_IMAP_PORT", 3993), MailSecurity.SslOnConnect),
            new MailServer(host, Port("PRODUCTIVITY_MCP_TEST_SMTP_PORT", 3465), MailSecurity.SslOnConnect));

        // Disposable local test servers use self-signed certificates; only loopback hosts are exempt.
        ImapSession.CertificateValidationOverride = LoopbackOnly(host);
        using var files = TemporaryMail.Create();
        try
        {
            await CreateStandardFoldersAsync(account, password);
            Assert.IsTrue(await MailAccountVerifier.VerifySignInAsync(account, password) is false, "The test server must not be Gmail.");
            await files.Catalog.SaveAsync(account, password);
            var provider = new MultiImapEmailProvider(files.Catalog, new EmailContentService());

            await MailboxScenario.RunAsync(provider, email, gmail: false);
        }
        finally
        {
            ImapSession.CertificateValidationOverride = null;
        }
    }

    private static async System.Threading.Tasks.Task CreateStandardFoldersAsync(MailAccount account, string password)
    {
        using var client = new ImapClient { ServerCertificateValidationCallback = LoopbackOnly(account.Imap.Host) };
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync(account.Username, password);
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var existing = (await root.GetSubfoldersAsync(false)).Select(folder => folder.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // German names on purpose: servers without SPECIAL-USE are found by their localized names.
        foreach (var name in new[] { "Gesendet", "Entwürfe", "Papierkorb" })
            if (!existing.Contains(name)) await root.CreateAsync(name, true);
        await client.DisconnectAsync(true);
    }

    private static System.Net.Security.RemoteCertificateValidationCallback LoopbackOnly(string host)
    {
        var loopback = host is "localhost" || (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));
        return (_, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || loopback;
    }

    private static int Port(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : fallback;
}
