using System.Globalization;

namespace Mailbox.Core.Search;

/// <summary>A comparison a keyword makes against a number or a date.</summary>
public enum Bound
{
    Exact,
    Before,
    After,
}

/// <summary>
/// What the search box was asked, taken apart: the words to match, and the reference's
/// keywords — <c>from:</c>, <c>to:</c>, <c>subject:</c>, <c>hasattachment:yes</c>,
/// <c>received:thisweek</c>, <c>read:no</c> and the rest — each turned into a filter the store
/// can apply beside the full-text match.
/// </summary>
/// <remarks>
/// The reference's Instant Search keywords, as its own help lists them; a keyword it does not
/// know is searched for as a word rather than swallowed. Values are one token, or a quoted
/// phrase. Pure, so the grammar is tested without a store.
/// </remarks>
public sealed record SearchQuery
{
    /// <summary>Words to match anywhere — subject, sender, preview, body.</summary>
    public IReadOnlyList<string> Words { get; init; } = [];

    /// <summary>Words that must appear in the sender's name or address.</summary>
    public IReadOnlyList<string> From { get; init; } = [];

    /// <summary>Words that must appear among the To addresses.</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>Words that must appear among the Cc addresses.</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>Words that must appear in the subject.</summary>
    public IReadOnlyList<string> Subject { get; init; } = [];

    /// <summary>Words that must appear in the body.</summary>
    public IReadOnlyList<string> Body { get; init; } = [];

    /// <summary>Category names the message must carry.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary><c>hasattachment:yes</c> / <c>no</c>, or null.</summary>
    public bool? HasAttachment { get; init; }

    /// <summary><c>read:yes</c> / <c>no</c> (and <c>unread:</c> the other way round), or null.</summary>
    public bool? IsRead { get; init; }

    /// <summary><c>flagged:yes</c> / <c>no</c>, or null.</summary>
    public bool? IsFlagged { get; init; }

    /// <summary><c>importance:high</c> (2), <c>normal</c> (1), <c>low</c> (0), or null.</summary>
    public int? Importance { get; init; }

    /// <summary><c>size:&gt;1mb</c>, <c>size:&lt;10kb</c>: the bound and the bytes.</summary>
    public (Bound Bound, long Bytes)? Size { get; init; }

    /// <summary><c>received:today</c>, <c>received:&gt;2026-08-01</c>: a span the receipt must fall in.</summary>
    public (DateTimeOffset? After, DateTimeOffset? Before)? Received { get; init; }

    /// <summary>The same for the sent date.</summary>
    public (DateTimeOffset? After, DateTimeOffset? Before)? Sent { get; init; }

    /// <summary><c>due:today</c>, <c>due:&lt;today</c> (overdue): a span the follow-up's due date must fall in.</summary>
    public (DateTimeOffset? After, DateTimeOffset? Before)? Due { get; init; }

    /// <summary>True when the query has nothing to match on at all — an empty box.</summary>
    public bool IsEmpty =>
        Words.Count == 0 && From.Count == 0 && To.Count == 0 && Cc.Count == 0 && Subject.Count == 0
        && Body.Count == 0 && Categories.Count == 0 && HasAttachment is null && IsRead is null
        && IsFlagged is null && Importance is null && Size is null && Received is null && Sent is null && Due is null;

    /// <summary>True when the query needs the full-text index — it has words for it.</summary>
    public bool HasText => Words.Count > 0 || From.Count > 0 || Subject.Count > 0 || Body.Count > 0;

    /// <summary>Takes a search box's text apart. <paramref name="now"/> anchors the date words.</summary>
    /// <remarks>
    /// With no anchor the application's own clock answers, not the machine's: <c>received:today</c>
    /// has to mean the same day the list is calling Today, and a second copy of a clock is how two
    /// halves of one application come to disagree about what day it is. Live and identical to the
    /// machine's unless <c>MAILBOX_TODAY</c> pins it — see <see cref="PosedClock"/>.
    /// </remarks>
    public static SearchQuery Parse(string text, DateTimeOffset? now = null)
    {
        var today = (now ?? PosedClock.Now).ToLocalTime();
        var query = new SearchQuery();
        var words = new List<string>();
        var from = new List<string>();
        var to = new List<string>();
        var cc = new List<string>();
        var subject = new List<string>();
        var body = new List<string>();
        var categories = new List<string>();

        foreach (var token in Tokens(text ?? string.Empty))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1)
            {
                words.Add(Unquote(token));
                continue;
            }

            var key = token[..colon].ToLowerInvariant();
            var value = Unquote(token[(colon + 1)..]);
            if (value.Length == 0) continue;

            switch (key)
            {
                case "from": from.Add(value); break;
                case "to": to.Add(value); break;
                case "cc": cc.Add(value); break;
                case "subject": subject.Add(value); break;
                case "body": body.Add(value); break;
                case "category": case "categories": categories.Add(value); break;
                case "hasattachment": case "hasattachments": case "attachment": case "attachments":
                    query = query with { HasAttachment = YesNo(value) };
                    break;
                case "read":
                    query = query with { IsRead = YesNo(value) };
                    break;
                case "unread":
                    query = query with { IsRead = YesNo(value) is { } u ? !u : null };
                    break;
                case "flagged": case "followupflag":
                    query = query with { IsFlagged = YesNo(value) };
                    break;
                case "importance":
                    query = query with
                    {
                        Importance = value.ToLowerInvariant() switch
                        {
                            "high" or "urgent" => 2,
                            "low" => 0,
                            "normal" => 1,
                            _ => query.Importance,
                        },
                    };
                    break;
                case "size": case "messagesize":
                    query = query with { Size = ParseSize(value) ?? query.Size };
                    break;
                case "received":
                    query = query with { Received = ParseSpan(value, today) ?? query.Received };
                    break;
                case "sent":
                    query = query with { Sent = ParseSpan(value, today) ?? query.Sent };
                    break;
                case "due":
                    query = query with { Due = ParseSpan(value, today) ?? query.Due };
                    break;
                default:
                    // Not a keyword the reference knows: a word with a colon in it, searched for.
                    words.Add(Unquote(token));
                    break;
            }
        }

        return query with
        {
            Words = words, From = from, To = to, Cc = cc, Subject = subject, Body = body, Categories = categories,
        };
    }

    /// <summary>Splits on spaces, keeping a quoted phrase — and a keyword with a quoted value — whole.</summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; current.Append(c); continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string Unquote(string token) => token.Trim('"');

    private static bool? YesNo(string value) => value.ToLowerInvariant() switch
    {
        "yes" or "true" or "1" => true,
        "no" or "false" or "0" => false,
        _ => null,
    };

    /// <summary><c>&gt;1mb</c>, <c>&lt;10kb</c>, <c>500kb</c>, and the reference's size words.</summary>
    public static (Bound, long)? ParseSize(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        switch (text)
        {
            case "tiny": return (Bound.Before, 10 * 1024);
            case "small": return (Bound.Before, 25 * 1024);
            case "medium": return (Bound.Before, 100 * 1024);
            case "large": return (Bound.After, 100 * 1024);
            case "verylarge": return (Bound.After, 1024 * 1024);
            case "huge": return (Bound.After, 5 * 1024 * 1024);
        }

        var bound = Bound.Exact;
        if (text.StartsWith('>')) { bound = Bound.After; text = text[1..]; }
        else if (text.StartsWith('<')) { bound = Bound.Before; text = text[1..]; }

        var multiplier = 1L;
        if (text.EndsWith("mb", StringComparison.Ordinal)) { multiplier = 1024 * 1024; text = text[..^2]; }
        else if (text.EndsWith("kb", StringComparison.Ordinal)) { multiplier = 1024; text = text[..^2]; }
        else if (text.EndsWith("gb", StringComparison.Ordinal)) { multiplier = 1024L * 1024 * 1024; text = text[..^2]; }
        else if (text.EndsWith('b')) { text = text[..^1]; }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (bound, (long)(number * multiplier))
            : null;
    }

    /// <summary>
    /// The reference's date words — today, yesterday, this week, last week, this month, last
    /// month, this year — and a date, with <c>&gt;</c> / <c>&lt;</c> for after / before.
    /// </summary>
    public static (DateTimeOffset? After, DateTimeOffset? Before)? ParseSpan(string value, DateTimeOffset today)
    {
        var text = value.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        var day = new DateTimeOffset(today.Date, today.Offset);
        DateTimeOffset StartOfWeek(DateTimeOffset d) => d.AddDays(-(int)d.DayOfWeek);

        var bound = Bound.Exact;
        if (text.StartsWith('>')) { bound = Bound.After; text = text[1..]; }
        else if (text.StartsWith('<')) { bound = Bound.Before; text = text[1..]; }

        // A date word is a span; with a bound it is what lies before its start or after its end
        // — due:<today is overdue, received:>lastweek is this week and on.
        var first = new DateTimeOffset(day.Year, day.Month, 1, 0, 0, 0, day.Offset);
        (DateTimeOffset Start, DateTimeOffset End)? word = text switch
        {
            "today" => (day, day.AddDays(1)),
            "yesterday" => (day.AddDays(-1), day),
            "last7days" or "lastsevendays" => (day.AddDays(-6), day.AddDays(1)),
            "thisweek" => (StartOfWeek(day), StartOfWeek(day).AddDays(7)),
            "lastweek" => (StartOfWeek(day).AddDays(-7), StartOfWeek(day)),
            "thismonth" => (first, first.AddMonths(1)),
            "lastmonth" => (first.AddMonths(-1), first),
            "thisyear" => (new DateTimeOffset(day.Year, 1, 1, 0, 0, 0, day.Offset), new DateTimeOffset(day.Year + 1, 1, 1, 0, 0, 0, day.Offset)),
            _ => null,
        };

        if (word is { } span)
        {
            return bound switch
            {
                Bound.After => (span.End, null),
                Bound.Before => (null, span.Start),
                _ => (span.Start, span.End),
            };
        }

        if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
            && !DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
            return null;
        }

        // The parsed date is a calendar day; it takes the anchor's offset rather than the
        // machine's, and Kind has to be Unspecified for the constructor to allow that.
        var start = new DateTimeOffset(DateTime.SpecifyKind(parsed.Date, DateTimeKind.Unspecified), today.Offset);
        return bound switch
        {
            Bound.After => (start.AddDays(1), null),
            Bound.Before => (null, start),
            _ => (start, start.AddDays(1)),
        };
    }
}
