using System.Globalization;
using System.Text;
using ProductivityMcp.Core;

namespace ProductivityMcp.Providers.Google;

internal static class GoogleEmailQuery
{
    public static string Build(EmailFilter? filter)
    {
        if (filter is null) return "";
        var terms = new List<string>();
        Add(terms, filter.Text);
        AddMany(terms, "from", filter.From);
        AddMany(terms, "to", filter.To);
        AddMany(terms, "cc", filter.Cc);
        AddMany(terms, "bcc", filter.Bcc);
        AddNamed(terms, "subject", filter.Subject);
        if (filter.After is { } after) terms.Add($"after:{after.ToUnixTimeSeconds()}");
        if (filter.Before is { } before) terms.Add($"before:{before.ToUnixTimeSeconds()}");
        AddMany(terms, "label", filter.Labels);
        if (filter.Mailbox is { } mailbox and not EmailMailbox.All) terms.Add($"in:{mailbox.ToString().ToLowerInvariant()}");
        AddBool(terms, "is:unread", filter.Unread);
        AddBool(terms, "is:starred", filter.Starred);
        AddBool(terms, "is:important", filter.Important);
        AddBool(terms, "has:attachment", filter.HasAttachments);
        AddNamed(terms, "filename", filter.Filename);
        if (filter.LargerThanBytes is { } larger) terms.Add($"larger:{larger.ToString(CultureInfo.InvariantCulture)}");
        if (filter.SmallerThanBytes is { } smaller) terms.Add($"smaller:{smaller.ToString(CultureInfo.InvariantCulture)}");
        if (filter.And is { Count: > 0 }) terms.AddRange(filter.And.Select(Build).Where(x => x.Length > 0).Select(x => $"({x})"));
        if (filter.Or is { Count: > 0 })
        {
            var values = filter.Or.Select(Build).Where(x => x.Length > 0).ToArray();
            if (values.Length > 0) terms.Add($"{{{string.Join(' ', values.Select(x => $"({x})"))}}}");
        }
        if (filter.Not is { } not)
        {
            var value = Build(not);
            if (value.Length > 0) terms.Add($"-({value})");
        }
        return string.Join(' ', terms);
    }

    private static void Add(List<string> terms, string? value)
    { if (!string.IsNullOrWhiteSpace(value)) terms.Add(Quote(value)); }
    private static void AddNamed(List<string> terms, string name, string? value)
    { if (!string.IsNullOrWhiteSpace(value)) terms.Add($"{name}:{Quote(value)}"); }
    private static void AddMany(List<string> terms, string name, IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 }) return;
        var mapped = values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => $"{name}:{Quote(x)}").ToArray();
        if (mapped.Length == 1) terms.Add(mapped[0]);
        else if (mapped.Length > 1) terms.Add($"{{{string.Join(' ', mapped)}}}");
    }
    private static void AddBool(List<string> terms, string term, bool? value)
    { if (value is true) terms.Add(term); else if (value is false) terms.Add($"-{term}"); }
    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
