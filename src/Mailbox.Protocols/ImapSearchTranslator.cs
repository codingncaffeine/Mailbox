using MailKit.Search;
using CoreQuery = Mailbox.Core.Search.SearchQuery;

namespace Mailbox.Protocols;

/// <summary>
/// The search grammar, said in IMAP.
/// </summary>
/// <remarks>
/// The server's job here is recall, not precision: whatever it sends back is stored and the
/// local index then applies the whole grammar to it, so a predicate IMAP cannot express —
/// categories, importance, attachment-ness, a due date — is simply left out rather than
/// approximated. Leaving one out can only widen what comes home, never lose a match. A query
/// made ONLY of predicates the server cannot answer translates to nothing, and the caller
/// should not go to the server at all.
/// </remarks>
public static class ImapSearchTranslator
{
    /// <summary>The query's server-answerable half, or null when the server can answer none of it.</summary>
    public static SearchQuery? Translate(CoreQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parts = new List<SearchQuery>();

        // TEXT covers headers and body, which is a superset of everywhere the word list looks
        // locally — wider is the right direction.
        parts.AddRange(query.Words.Select(SearchQuery.MessageContains));
        parts.AddRange(query.From.Select(SearchQuery.FromContains));
        parts.AddRange(query.To.Select(SearchQuery.ToContains));
        parts.AddRange(query.Cc.Select(SearchQuery.CcContains));
        parts.AddRange(query.Subject.Select(SearchQuery.SubjectContains));
        parts.AddRange(query.Body.Select(SearchQuery.BodyContains));

        if (query.IsRead is { } read) parts.Add(read ? SearchQuery.Seen : SearchQuery.NotSeen);
        if (query.IsFlagged is { } flagged) parts.Add(flagged ? SearchQuery.Flagged : SearchQuery.NotFlagged);

        // An exact size has no IMAP key; leaving it out only widens what comes home.
        if (query.Size is { } size && size.Bound != Mailbox.Core.Search.Bound.Exact)
        {
            parts.Add(size.Bound == Mailbox.Core.Search.Bound.After
                ? SearchQuery.LargerThan((int)Math.Min(size.Bytes, int.MaxValue))
                : SearchQuery.SmallerThan((int)Math.Min(size.Bytes, int.MaxValue)));
        }

        if (query.Received is { } received)
        {
            if (received.After is { } after) parts.Add(SearchQuery.DeliveredAfter(after.UtcDateTime));
            if (received.Before is { } before) parts.Add(SearchQuery.DeliveredBefore(before.UtcDateTime));
        }

        if (query.Sent is { } sent)
        {
            if (sent.After is { } after) parts.Add(SearchQuery.SentSince(after.UtcDateTime));
            if (sent.Before is { } before) parts.Add(SearchQuery.SentBefore(before.UtcDateTime));
        }

        return parts.Count == 0 ? null : parts.Aggregate(SearchQuery.And);
    }
}
