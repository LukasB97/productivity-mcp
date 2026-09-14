using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.Playwright;
using MimeKit;
using ProductivityMcp.Core;
using ReverseMarkdown;

namespace ProductivityMcp.Email;

public sealed class EmailContentService
{
    private readonly Converter _markdown = new();

    public static MimeMessage Parse(string raw)
    {
        var bytes = Convert.FromBase64String(Base64UrlDecode(raw));
        using var stream = new MemoryStream(bytes);
        return MimeMessage.Load(stream);
    }

    public static MimeMessage Compose(string account, OutgoingEmail value)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(account));
        Add(message.To, value.To);
        Add(message.Cc, value.Cc);
        Add(message.Bcc, value.Bcc);
        message.Subject = value.Subject;

        var builder = new BodyBuilder();
        if (value.Body.Format == EmailContentFormat.Markdown)
        {
            builder.HtmlBody = Markdown.ToHtml(value.Body.Content);
            builder.TextBody = MarkdownToPlain(value.Body.Content);
        }
        else
        {
            builder.HtmlBody = value.Body.Content;
            builder.TextBody = HtmlToPlain(value.Body.Content);
        }

        foreach (var attachment in value.Attachments)
        {
            if (!File.Exists(attachment.Path)) throw new ConfigurationException("Email attachment was not found.", "attachments.path");
            builder.Attachments.Add(attachment.Name ?? Path.GetFileName(attachment.Path), File.ReadAllBytes(attachment.Path));
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    public static MimeMessage ComposeDraft(string account, EmailDraftInput value)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(account));
        Add(message.To, value.To);
        Add(message.Cc, value.Cc);
        Add(message.Bcc, value.Bcc);
        message.Subject = value.Subject;
        ApplyBody(message, value.Body, value.Attachments);
        return message;
    }

    public static void ApplyDraftPatch(MimeMessage message, EmailDraftPatch patch)
    {
        if (patch.HasTo) Replace(message.To, patch.To ?? []);
        if (patch.HasCc) Replace(message.Cc, patch.Cc ?? []);
        if (patch.HasBcc) Replace(message.Bcc, patch.Bcc ?? []);
        if (patch.HasSubject) message.Subject = patch.Subject ?? "";

        if (patch.HasBody || patch.HasAttachments)
        {
            var body = patch.HasBody ? patch.Body : ExistingBody(message);
            var attachments = patch.HasAttachments
                ? patch.Attachments ?? []
                : [];
            var preserved = patch.HasAttachments ? [] : message.Attachments.OfType<MimePart>().ToArray();
            ApplyBody(message, body, attachments, preserved);
        }
    }

    public static string ToRaw(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return Base64UrlEncode(stream.ToArray());
    }

    public async Task<(EmailBody Body, IReadOnlyList<EmailImage> Images)> ConvertAsync(
        MimeMessage message, EmailContentFormat format, bool loadRemoteImages,
        CancellationToken cancellationToken)
    {
        var plain = message.TextBody ?? HtmlToPlain(message.HtmlBody ?? "");
        var html = message.HtmlBody ?? $"<pre>{WebUtility.HtmlEncode(plain)}</pre>";
        return format switch
        {
            EmailContentFormat.Plain => (new(format, plain), []),
            EmailContentFormat.Html => (new(format, html), []),
            EmailContentFormat.Markdown => (new(format, message.HtmlBody is null ? plain : _markdown.Convert(html)), []),
            EmailContentFormat.Image => (new(format, ""), await RenderAsync(InlineImages(message, html), loadRemoteImages, cancellationToken).ConfigureAwait(false)),
            _ => throw new UnsupportedFeatureException("Unsupported email content format.", "format"),
        };
    }

    public static async System.Threading.Tasks.Task<int> InstallRendererAsync(CancellationToken cancellationToken = default)
    {
        var platform = OperatingSystem.IsWindows() ? "win32_x64"
            : OperatingSystem.IsMacOS() ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "darwin-arm64" : "darwin-x64")
            : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        var nodeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var root = Path.Combine(AppContext.BaseDirectory, ".playwright");
        var node = Path.Combine(root, "node", platform, nodeName);
        var cli = Path.Combine(root, "package", "cli.js");
        if (!File.Exists(node) || !File.Exists(cli))
            throw new ConfigurationException("The bundled Playwright installer is missing.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = node,
            Arguments = $"\"{cli}\" install chromium",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new ConfigurationException("The Playwright installer could not be started.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await System.Threading.Tasks.Task.WhenAll(
                process.WaitForExitAsync(cancellationToken), stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    public static bool IsRendererInstalled()
    {
        try
        {
            using var playwright = Playwright.CreateAsync().GetAwaiter().GetResult();
            return File.Exists(playwright.Chromium.ExecutablePath);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<EmailImage>> RenderAsync(string html, bool loadRemoteImages, CancellationToken cancellationToken)
    {
        try
        {
            using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true }).ConfigureAwait(false);
            var context = await browser.NewContextAsync(new() { JavaScriptEnabled = false, ViewportSize = new() { Width = 900, Height = 1200 } }).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            await page.RouteAsync("**/*", async route =>
            {
                var uri = new Uri(route.Request.Url);
                if (!loadRemoteImages || route.Request.ResourceType != "image")
                {
                    await route.AbortAsync().ConfigureAwait(false);
                    return;
                }
                try
                {
                    var remote = await FetchPublicImageAsync(uri, cancellationToken).ConfigureAwait(false);
                    await route.FulfillAsync(new()
                    {
                        Status = 200,
                        ContentType = remote.ContentType,
                        BodyBytes = remote.Bytes,
                    }).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
                {
                    await route.AbortAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
            await page.SetContentAsync(html, new() { WaitUntil = WaitUntilState.NetworkIdle }).ConfigureAwait(false);
            var height = await page.EvaluateAsync<int>("document.documentElement.scrollHeight").ConfigureAwait(false);
            var images = new List<EmailImage>();
            for (var y = 0; y < Math.Max(1, height); y += 1200)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await page.EvaluateAsync("y => window.scrollTo(0, y)", y).ConfigureAwait(false);
                var bytes = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png }).ConfigureAwait(false);
                images.Add(new EmailImage("image/png", bytes, images.Count + 1));
            }
            return images;
        }
        catch (PlaywrightException exception) when (exception.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigurationException("The email image renderer is not installed. Install it from the Productivity MCP app.", "format", exception);
        }
    }

    private static string InlineImages(MimeMessage message, string html)
    {
        foreach (var part in message.BodyParts.OfType<MimePart>().Where(x =>
                     !string.IsNullOrWhiteSpace(x.ContentId) && x.Content is not null))
        {
            using var stream = new MemoryStream();
            part.Content!.DecodeTo(stream);
            html = html.Replace($"cid:{part.ContentId}", $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(stream.ToArray())}", StringComparison.OrdinalIgnoreCase);
        }
        return html;
    }

    private static async Task<(byte[] Bytes, string ContentType)> FetchPublicImageAsync(
        Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https")) throw new HttpRequestException("Unsupported remote image URL.");
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
                var address = addresses.FirstOrDefault(candidate =>
                    !IPAddress.IsLoopback(candidate) &&
                    candidate.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
                    !IsPrivate(candidate)) ?? throw new HttpRequestException("Remote image resolves to a local network address.");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Remote resource is not an image.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 10 * 1024 * 1024) throw new HttpRequestException("Remote image exceeds 10 MiB.");
        return (bytes, contentType);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Any);
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static void Add(InternetAddressList target, IEnumerable<string> values)
    { foreach (var value in values) target.Add(MailboxAddress.Parse(value)); }
    private static void Replace(InternetAddressList target, IEnumerable<string> values)
    { target.Clear(); Add(target, values); }
    private static EmailBody? ExistingBody(MimeMessage message) => message.HtmlBody is not null
        ? new(EmailContentFormat.Html, message.HtmlBody)
        : message.TextBody is not null ? new(EmailContentFormat.Markdown, message.TextBody) : null;
    private static void ApplyBody(MimeMessage message, EmailBody? body, IEnumerable<EmailAttachmentInput> attachments, IEnumerable<MimePart>? preserved = null)
    {
        var builder = new BodyBuilder();
        if (body?.Format == EmailContentFormat.Markdown)
        {
            builder.HtmlBody = Markdown.ToHtml(body.Content);
            builder.TextBody = MarkdownToPlain(body.Content);
        }
        else if (body is not null)
        {
            if (body.Format != EmailContentFormat.Html) throw new UnsupportedFeatureException("body.format must be html or markdown.", "body.format");
            builder.HtmlBody = body.Content;
            builder.TextBody = HtmlToPlain(body.Content);
        }
        foreach (var part in preserved ?? []) builder.Attachments.Add(part);
        foreach (var attachment in attachments)
        {
            if (!File.Exists(attachment.Path)) throw new ConfigurationException("Email attachment was not found.", "attachments.path");
            builder.Attachments.Add(attachment.Name ?? Path.GetFileName(attachment.Path), File.ReadAllBytes(attachment.Path));
        }
        message.Body = builder.ToMessageBody();
    }
    private static string HtmlToPlain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")).Trim();
    private static string MarkdownToPlain(string markdown) => Regex.Replace(markdown, @"[`#*_>\[\]()]", "").Trim();
    private static string Base64UrlDecode(string value) => value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
    private static string Base64UrlEncode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
