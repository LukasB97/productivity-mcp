using System.Globalization;
using System.Xml.Linq;
using ProductivityMcp.Core;

namespace ProductivityMcp.Providers.Imap;

/// <summary>
/// Finds IMAP/SMTP settings for an address: a built-in table for common providers first,
/// then Mozilla's public autoconfig database (only the domain is sent).
/// </summary>
public sealed class MailServerDiscovery(HttpClient? httpClient = null)
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _http = httpClient ?? SharedClient;

    private static readonly string[] MicrosoftConsumerDomains =
    [
        "outlook.com", "outlook.de", "hotmail.com", "hotmail.de", "live.com", "live.de", "msn.com",
    ];

    private static readonly Dictionary<string, MailServerSettings> Known = Build(
        (["gmail.com", "googlemail.com"], Ssl("imap.gmail.com", 993), Ssl("smtp.gmail.com", 465)),
        (["gmx.de", "gmx.net", "gmx.at", "gmx.ch"], Ssl("imap.gmx.net", 993), Ssl("mail.gmx.net", 465)),
        (["web.de"], Ssl("imap.web.de", 993), StartTls("smtp.web.de", 587)),
        (["icloud.com", "me.com", "mac.com"], Ssl("imap.mail.me.com", 993), StartTls("smtp.mail.me.com", 587)),
        (["yahoo.com", "yahoo.de"], Ssl("imap.mail.yahoo.com", 993), Ssl("smtp.mail.yahoo.com", 465)),
        (["aol.com", "aol.de"], Ssl("imap.aol.com", 993), Ssl("smtp.aol.com", 465)),
        (["posteo.de", "posteo.net", "posteo.org", "posteo.eu"], Ssl("posteo.de", 993), Ssl("posteo.de", 465)),
        (["mailbox.org"], Ssl("imap.mailbox.org", 993), Ssl("smtp.mailbox.org", 465)),
        (["t-online.de", "magenta.de"], Ssl("secureimap.t-online.de", 993), Ssl("securesmtp.t-online.de", 465)),
        (["freenet.de"], Ssl("mx.freenet.de", 993), StartTls("mx.freenet.de", 587)));

    public static bool IsGmailAddress(string email) =>
        Domain(email) is "gmail.com" or "googlemail.com";

    public static bool IsMicrosoftConsumerAddress(string email) =>
        MicrosoftConsumerDomains.Contains(Domain(email), StringComparer.OrdinalIgnoreCase);

    public async Task<MailServerSettings?> DiscoverAsync(string email, CancellationToken cancellationToken = default)
    {
        var domain = Domain(email);
        if (domain.Length == 0) return null;
        if (IsMicrosoftConsumerAddress(email))
            throw new UnsupportedFeatureException(
                "Microsoft mailboxes only allow Microsoft sign-in, which is not supported yet.", "email");
        if (Known.TryGetValue(domain, out var known)) return known;

        try
        {
            using var response = await _http.GetAsync(
                new Uri($"https://autoconfig.thunderbird.net/v1.1/{Uri.EscapeDataString(domain)}"),
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseAutoconfig(xml, email);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (System.Xml.XmlException) { return null; }
    }

    internal static MailServerSettings? ParseAutoconfig(string xml, string email)
    {
        var document = XDocument.Parse(xml);
        var imap = document.Descendants("incomingServer")
            .Where(server => (string?)server.Attribute("type") == "imap")
            .Select(server => ParseServer(server, email))
            .FirstOrDefault(server => server is not null);
        var smtp = document.Descendants("outgoingServer")
            .Where(server => (string?)server.Attribute("type") == "smtp")
            .Select(server => ParseServer(server, email))
            .FirstOrDefault(server => server is not null);
        return imap is null || smtp is null
            ? null
            : new MailServerSettings(imap.Value.Server, smtp.Value.Server, imap.Value.Username);
    }

    private static (MailServer Server, string? Username)? ParseServer(XElement element, string email)
    {
        var host = element.Element("hostname")?.Value.Trim();
        var socket = element.Element("socketType")?.Value.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(host)
            || !int.TryParse(element.Element("port")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            return null;
        // Plain-text connections are never offered: passwords must not leave the machine unencrypted.
        MailSecurity? security = socket switch { "SSL" => MailSecurity.SslOnConnect, "STARTTLS" => MailSecurity.StartTls, _ => null };
        if (security is null) return null;
        var localPart = email.Split('@')[0];
        var username = element.Element("username")?.Value.Trim()
            .Replace("%EMAILADDRESS%", email, StringComparison.Ordinal)
            .Replace("%EMAILLOCALPART%", localPart, StringComparison.Ordinal);
        if (username?.Contains('%', StringComparison.Ordinal) == true) username = null;
        return (new MailServer(host.Replace("%EMAILDOMAIN%", Domain(email), StringComparison.Ordinal), port, security.Value), username);
    }

    private static string Domain(string email)
    {
        var index = email.LastIndexOf('@');
        return index < 0 ? "" : email[(index + 1)..].Trim().ToLowerInvariant();
    }

    private static MailServer Ssl(string host, int port) => new(host, port, MailSecurity.SslOnConnect);
    private static MailServer StartTls(string host, int port) => new(host, port, MailSecurity.StartTls);

    private static Dictionary<string, MailServerSettings> Build(
        params (string[] Domains, MailServer Imap, MailServer Smtp)[] entries) =>
        entries.SelectMany(entry => entry.Domains.Select(domain => (domain, settings: new MailServerSettings(entry.Imap, entry.Smtp))))
            .ToDictionary(item => item.domain, item => item.settings, StringComparer.OrdinalIgnoreCase);
}
