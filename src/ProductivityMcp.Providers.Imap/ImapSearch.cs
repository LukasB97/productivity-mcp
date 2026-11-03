using MailKit;
using MailKit.Search;
using ProductivityMcp.Core;

namespace ProductivityMcp.Providers.Imap;

/// <summary>
/// Conditions that standard IMAP SEARCH cannot express exactly. They are only accepted at the top
/// level of a filter, where they select folders or are checked against fetched message data.
/// </summary>
internal sealed record ImapSearchScope(
    EmailMailbox? Mailbox,
    IReadOnlyList<string>? Folders,
    DateTimeOffset? After,
    DateTimeOffset? Before,
    bool? HasAttachments)
{
    public bool Matches(IMessageSummary summary)
    {
        var date = summary.InternalDate ?? summary.Envelope?.Date;
        if (After is { } after && (date is null || date <= after)) return false;
        if (Before is { } before && (date is null || date >= before)) return false;
        if (HasAttachments is { } attachments && summary.Attachments.Any() != attachments) return false;
        return true;
    }
}

/// <summary>Translates provider-neutral filters into standard IMAP SEARCH (RFC 3501).</summary>
internal static class ImapSearch
{
    public static (SearchQuery Query, ImapSearchScope Scope) Build(EmailFilter? filter)
    {
        if (filter is null) return (SearchQuery.All, new(null, null, null, null, null));
        if (filter.Mailbox is not null && filter.Labels is { Count: > 0 })
            throw Unsupported("mailbox and labels cannot be combined for this account; labels select folders.", "filter.labels");
        if (filter.Important is not null)
            throw Unsupported("important is only available for Gmail accounts.", "filter.important");

        var terms = new List<SearchQuery>();
        // IMAP dates have day precision in the server's time zone. Widen by a day and compare exactly afterwards.
        if (filter.After is { } after) terms.Add(SearchQuery.DeliveredAfter(after.UtcDateTime.Date.AddDays(-1)));
        if (filter.Before is { } before) terms.Add(SearchQuery.DeliveredBefore(before.UtcDateTime.Date.AddDays(2)));
        AddConditions(terms, filter);
        var scope = new ImapSearchScope(
            filter.Mailbox is EmailMailbox.All ? null : filter.Mailbox,
            filter.Labels, filter.After, filter.Before, filter.HasAttachments);
        return (Combine(terms), scope);
    }

    private static SearchQuery BuildNested(EmailFilter filter)
    {
        if (filter.Mailbox is not null || filter.Labels is { Count: > 0 })
            throw Unsupported("mailbox and labels can only be used at the top level of a filter for this account.", "filter");
        if (filter.After is not null || filter.Before is not null)
            throw Unsupported("after and before can only be used at the top level of a filter for this account.", "filter");
        if (filter.HasAttachments is not null)
            throw Unsupported("hasAttachments can only be used at the top level of a filter for this account.", "filter.hasAttachments");
        if (filter.Important is not null)
            throw Unsupported("important is only available for Gmail accounts.", "filter.important");
        var terms = new List<SearchQuery>();
        AddConditions(terms, filter);
        return Combine(terms);
    }

    private static void AddConditions(List<SearchQuery> terms, EmailFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Filename))
            throw Unsupported("filename search is only available for Gmail accounts.", "filter.filename");
        if (!string.IsNullOrWhiteSpace(filter.Text)) terms.Add(SearchQuery.MessageContains(filter.Text));
        AddAny(terms, filter.From, SearchQuery.FromContains);
        AddAny(terms, filter.To, SearchQuery.ToContains);
        AddAny(terms, filter.Cc, SearchQuery.CcContains);
        AddAny(terms, filter.Bcc, SearchQuery.BccContains);
        if (!string.IsNullOrWhiteSpace(filter.Subject)) terms.Add(SearchQuery.SubjectContains(filter.Subject));
        if (filter.Unread is { } unread) terms.Add(unread ? SearchQuery.NotSeen : SearchQuery.Seen);
        if (filter.Starred is { } starred) terms.Add(starred ? SearchQuery.Flagged : SearchQuery.NotFlagged);
        if (filter.LargerThanBytes is { } larger) terms.Add(SearchQuery.LargerThan(Clamp(larger)));
        if (filter.SmallerThanBytes is { } smaller) terms.Add(SearchQuery.SmallerThan(Clamp(smaller)));
        foreach (var child in filter.And ?? []) terms.Add(BuildNested(child));
        if (filter.Or is { Count: > 0 } any) terms.Add(any.Select(BuildNested).Aggregate(SearchQuery.Or));
        if (filter.Not is { } not) terms.Add(SearchQuery.Not(BuildNested(not)));
    }

    private static void AddAny(List<SearchQuery> terms, IReadOnlyList<string>? values, Func<string, SearchQuery> create)
    {
        var mapped = (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(create).ToArray();
        if (mapped.Length > 0) terms.Add(mapped.Aggregate(SearchQuery.Or));
    }

    private static SearchQuery Combine(List<SearchQuery> terms) =>
        terms.Count == 0 ? SearchQuery.All : terms.Aggregate(SearchQuery.And);

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private static UnsupportedFeatureException Unsupported(string message, string field) => new(message, field);
}
