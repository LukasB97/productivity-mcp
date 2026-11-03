using System.Globalization;
using System.Text;
using MailKit;

namespace ProductivityMcp.Providers.Imap;

/// <summary>
/// Message and thread identifiers exposed through MCP.
/// Gmail ids are the hexadecimal X-GM-MSGID/X-GM-THRID values (identical to Gmail API ids) and are
/// stable across labels. Other servers have no global id, so the id encodes folder, UIDVALIDITY and UID;
/// it changes when a message is moved (archive, trash).
/// </summary>
internal static class MailIds
{
    private const string GenericMessagePrefix = "m";
    private const string GenericThreadPrefix = "t";
    public const string MessageIdHeaderPrefix = "mid:";

    public static string Gmail(ulong value) => value.ToString("x", CultureInfo.InvariantCulture);

    public static ulong ParseGmail(string id) =>
        ulong.TryParse(id, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw NotFound(id);

    public static string Generic(string folder, uint uidValidity, UniqueId uid) =>
        GenericMessagePrefix + Encode($"{uidValidity.ToString(CultureInfo.InvariantCulture)}:{uid.Id.ToString(CultureInfo.InvariantCulture)}:{folder}");

    public static (string Folder, uint UidValidity, UniqueId Uid) ParseGeneric(string id)
    {
        if (!id.StartsWith(GenericMessagePrefix, StringComparison.Ordinal)) throw NotFound(id);
        var parts = (Decode(id[GenericMessagePrefix.Length..]) ?? throw NotFound(id)).Split(':', 3);
        if (parts.Length != 3
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var validity)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
            || uid == 0)
            throw NotFound(id);
        return (parts[2], validity, new UniqueId(validity, uid));
    }

    public static string GenericThread(string root) => GenericThreadPrefix + Encode(root);

    public static string ParseGenericThread(string id) =>
        id.StartsWith(GenericThreadPrefix, StringComparison.Ordinal) && Decode(id[GenericThreadPrefix.Length..]) is { Length: > 0 } root
            ? root
            : throw new KeyNotFoundException($"The email thread '{id}' was not found.");

    public static KeyNotFoundException NotFound(string id) => new($"The email message '{id}' was not found.");

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Decode(string value)
    {
        try
        {
            var normal = value.Replace('-', '+').Replace('_', '/');
            normal = normal.PadRight(normal.Length + (4 - normal.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(normal));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
