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
        if (value.Body.Format == EmailComposeFormat.Markdown)
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
            var builder = new BodyBuilder();
            if (patch.HasBody)
                SetBody(builder, patch.Body);
            else
            {
                builder.TextBody = message.TextBody;
                builder.HtmlBody = message.HtmlBody;
            }

            AddExistingParts(builder, message, includeAttachments: !patch.HasAttachments);
            AddFileAttachments(builder, patch.HasAttachments ? patch.Attachments ?? [] : []);
            message.Body = builder.ToMessageBody();
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
        const int maximumBytes = 10 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new HttpRequestException("Remote image exceeds 10 MiB.");
        var bytes = await ReadBoundedAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
        return (bytes, contentType);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPrivate(address.MapToIPv4());
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var ipv6Bytes = address.GetAddressBytes();
            var isGlobalUnicast = (ipv6Bytes[0] & 0xe0) == 0x20;
            return !isGlobalUnicast || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                   address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) ||
                   (ipv6Bytes[0] & 0xfe) == 0xfc ||
                   (ipv6Bytes[0] == 0x20 && ipv6Bytes[1] == 0x01 && ipv6Bytes[2] == 0x0d && ipv6Bytes[3] == 0xb8);
        }
        var ipv4Bytes = address.GetAddressBytes();
        return ipv4Bytes[0] == 10 || ipv4Bytes[0] == 127 || ipv4Bytes[0] == 0 || ipv4Bytes[0] >= 224 ||
               (ipv4Bytes[0] == 100 && ipv4Bytes[1] is >= 64 and <= 127) ||
               (ipv4Bytes[0] == 169 && ipv4Bytes[1] == 254) ||
               (ipv4Bytes[0] == 172 && ipv4Bytes[1] is >= 16 and <= 31) ||
               (ipv4Bytes[0] == 192 && ipv4Bytes[1] == 168) ||
               (ipv4Bytes[0] == 192 && ipv4Bytes[1] == 0 && (ipv4Bytes[2] == 0 || ipv4Bytes[2] == 2)) ||
               (ipv4Bytes[0] == 198 && (ipv4Bytes[1] is 18 or 19 || (ipv4Bytes[1] == 51 && ipv4Bytes[2] == 100))) ||
               (ipv4Bytes[0] == 203 && ipv4Bytes[1] == 0 && ipv4Bytes[2] == 113);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return destination.ToArray();
            total += read;
            if (total > maximumBytes) throw new HttpRequestException("Remote image exceeds 10 MiB.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Add(InternetAddressList target, IEnumerable<string> values)
    { foreach (var value in values) target.Add(MailboxAddress.Parse(value)); }
    private static void Replace(InternetAddressList target, IEnumerable<string> values)
    { target.Clear(); Add(target, values); }
    public static void AddExistingParts(BodyBuilder builder, MimeMessage message, bool includeAttachments)
    {
        if (includeAttachments)
        {
            foreach (var attachment in message.Attachments) builder.Attachments.Add(attachment);
        }

        foreach (var resource in message.BodyParts.OfType<MimePart>().Where(part =>
                     !part.IsAttachment && !string.IsNullOrWhiteSpace(part.ContentId)))
            builder.LinkedResources.Add(resource);
    }

    private static void ApplyBody(MimeMessage message, EmailComposeBody? body, IEnumerable<EmailAttachmentInput> attachments)
    {
        var builder = new BodyBuilder();
        SetBody(builder, body);
        AddFileAttachments(builder, attachments);
        message.Body = builder.ToMessageBody();
    }

    private static void SetBody(BodyBuilder builder, EmailComposeBody? body)
    {
        if (body?.Format == EmailComposeFormat.Markdown)
        {
            builder.HtmlBody = Markdown.ToHtml(body.Content);
            builder.TextBody = MarkdownToPlain(body.Content);
        }
        else if (body is not null)
        {
            builder.HtmlBody = body.Content;
            builder.TextBody = HtmlToPlain(body.Content);
        }
    }

    private static void AddFileAttachments(BodyBuilder builder, IEnumerable<EmailAttachmentInput> attachments)
    {
        foreach (var attachment in attachments)
        {
            if (!File.Exists(attachment.Path)) throw new ConfigurationException("Email attachment was not found.", "attachments.path");
            builder.Attachments.Add(attachment.Name ?? Path.GetFileName(attachment.Path), File.ReadAllBytes(attachment.Path));
        }
    }
    private static string HtmlToPlain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")).Trim();
    private static string MarkdownToPlain(string markdown) => Regex.Replace(markdown, @"[`#*_>\[\]()]", "").Trim();
    private static string Base64UrlDecode(string value) => value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
    private static string Base64UrlEncode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
