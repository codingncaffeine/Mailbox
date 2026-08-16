namespace Mailbox.Core.Search;

/// <summary>What a message shows the matcher — the row's own facts, no store behind them.</summary>
public sealed record SearchFacts
{
    public string FromName { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public string Subject { get; init; } = string.Empty;

    /// <summary>The body, or as much of it as the row carries — the preview, in the list.</summary>
    public string Body { get; init; } = string.Empty;

    public IReadOnlyList<string> Categories { get; init; } = [];
    public bool HasAttachment { get; init; }
    public bool IsRead { get; init; }
    public bool IsFlagged { get; init; }
    public int Importance { get; init; } = 1;
    public long SizeBytes { get; init; }
    public DateTimeOffset Received { get; init; }
    public DateTimeOffset? Sent { get; init; }
    public DateTimeOffset? Due { get; init; }
}

/// <summary>
/// Applies a <see cref="SearchQuery"/> to one message in memory: the same grammar the store
/// searches by, for the places that already hold the rows — a view's filter, a conditional
/// formatting rule.
/// </summary>
/// <remarks>
/// The store's search and this one agree on meaning where the data lets them: a word matches
/// anywhere the row can be read (sender, subject, recipients, body), a keyword's words match
/// their field, the yes/no keywords compare, the spans compare. The one honest difference is
/// the body: the list carries a preview, so a <c>body:</c> word is looked for there.
/// </remarks>
public static class SearchMatcher
{
    public static bool Matches(SearchQuery query, SearchFacts facts)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(facts);

        if (query.IsEmpty) return true;

        var from = facts.FromName + " " + facts.FromAddress;
        var recipients = string.Join(" ", facts.To.Concat(facts.Cc));
        var everywhere = string.Join(" ", from, facts.Subject, recipients, facts.Body);

        if (!query.Words.All(w => Has(everywhere, w))) return false;
        if (!query.From.All(w => Has(from, w))) return false;
        if (!query.To.All(w => facts.To.Any(t => Has(t, w)))) return false;
        if (!query.Cc.All(w => facts.Cc.Any(c => Has(c, w)))) return false;
        if (!query.Subject.All(w => Has(facts.Subject, w))) return false;
        if (!query.Body.All(w => Has(facts.Body, w))) return false;
        if (!query.Categories.All(c => facts.Categories.Any(have => string.Equals(have, c, StringComparison.OrdinalIgnoreCase)))) return false;

        if (query.HasAttachment is { } attachment && facts.HasAttachment != attachment) return false;
        if (query.IsRead is { } read && facts.IsRead != read) return false;
        if (query.IsFlagged is { } flagged && facts.IsFlagged != flagged) return false;
        if (query.Importance is { } importance && facts.Importance != importance) return false;

        if (query.Size is { } size)
        {
            var ok = size.Bound switch
            {
                Bound.After => facts.SizeBytes > size.Bytes,
                Bound.Before => facts.SizeBytes < size.Bytes,
                _ => facts.SizeBytes >= size.Bytes * 9 / 10 && facts.SizeBytes <= size.Bytes * 11 / 10,
            };
            if (!ok) return false;
        }

        if (query.Received is { } received && !InSpan(facts.Received, received)) return false;
        if (query.Sent is { } sent && (facts.Sent is not { } sentAt || !InSpan(sentAt, sent))) return false;
        if (query.Due is { } due && (facts.Due is not { } dueAt || !InSpan(dueAt, due))) return false;

        return true;
    }

    private static bool Has(string text, string word)
        => word.Length == 0 || text.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static bool InSpan(DateTimeOffset when, (DateTimeOffset? After, DateTimeOffset? Before) span)
        => (span.After is not { } after || when >= after) && (span.Before is not { } before || when < before);
}
