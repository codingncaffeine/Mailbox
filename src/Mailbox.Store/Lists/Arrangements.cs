using System.Globalization;

namespace Mailbox.Store.Lists;

/// <summary>
/// What a list can be arranged by.
/// </summary>
/// <remarks>
/// An arrangement is a grouping and a sort together, not a sort alone. Arranging by Date groups
/// into Today, Yesterday and the rest; arranging by From groups by sender. That pairing is why
/// the reference calls it an arrangement rather than a sort order, and why the group headers
/// change when it does.
/// </remarks>
public enum Arrangement
{
    Date,
    From,
    To,
    Subject,
    Size,
    Flag,
    Importance,
    Attachments,
    Categories,
    Account,
    Type,
}

/// <summary>The properties an arrangement needs, so the same engine serves mail, tasks and notes.</summary>
public interface IArrangeable
{
    string DisplayFrom { get; }

    string Subject { get; }

    DateTimeOffset Received { get; }

    long SizeBytes { get; }

    bool IsFlagged { get; }

    bool HasAttachment { get; }
}

/// <summary>One group of rows, with the header the list draws above them.</summary>
public sealed record ItemGroup<T>(string Header, IReadOnlyList<T> Items)
{
    public int Count => Items.Count;
}

/// <summary>
/// Groups and sorts a list the way the reference does.
/// </summary>
/// <remarks>
/// Pure, and separate from anything that draws: the buckets are the part with rules worth
/// checking — a message from four days ago belongs in "Last Week" and one from six months ago
/// in the month it arrived, and getting that wrong is the kind of thing nobody notices until
/// they are looking for something.
/// </remarks>
public static class Arrangements
{
    /// <summary>Every arrangement, in the order the menu offers them.</summary>
    public static readonly IReadOnlyList<Arrangement> All =
    [
        Arrangement.Date, Arrangement.From, Arrangement.To, Arrangement.Categories,
        Arrangement.Flag, Arrangement.Size, Arrangement.Subject, Arrangement.Type,
        Arrangement.Attachments, Arrangement.Account, Arrangement.Importance,
    ];

    public static string Label(Arrangement arrangement) => arrangement switch
    {
        Arrangement.Attachments => "Attachments",
        Arrangement.Categories => "Categories",
        Arrangement.Importance => "Importance",
        _ => arrangement.ToString(),
    };

    /// <summary>
    /// Groups and orders. Descending means newest, largest or Z-to-A first, which is the
    /// direction the reference starts a date arrangement in.
    /// </summary>
    public static IReadOnlyList<ItemGroup<T>> Group<T>(
        IEnumerable<T> items,
        Arrangement arrangement,
        bool descending = true,
        DateTimeOffset? today = null)
        where T : IArrangeable
    {
        var now = today ?? DateTimeOffset.Now;
        var rows = items.ToList();

        // Rows are ordered first, then bucketed, so a group's contents follow the same
        // direction as the groups themselves.
        var ordered = Sort(rows, arrangement, descending);

        var groups = new List<ItemGroup<T>>();
        string? current = null;
        List<T>? bucket = null;

        foreach (var row in ordered)
        {
            var header = HeaderFor(row, arrangement, now);
            if (header != current)
            {
                if (bucket is { Count: > 0 }) groups.Add(new ItemGroup<T>(current!, bucket));
                current = header;
                bucket = [];
            }

            bucket!.Add(row);
        }

        if (bucket is { Count: > 0 }) groups.Add(new ItemGroup<T>(current!, bucket));
        return groups;
    }

    private static IEnumerable<T> Sort<T>(List<T> rows, Arrangement arrangement, bool descending)
        where T : IArrangeable
    {
        // Date is always the tiebreak, newest first. Two messages from the same sender with the
        // same subject should still read in the order they arrived.
        IOrderedEnumerable<T> ordered = arrangement switch
        {
            Arrangement.From or Arrangement.To or Arrangement.Account => Direction(
                rows, r => r.DisplayFrom, descending, StringComparer.CurrentCultureIgnoreCase),
            Arrangement.Subject => Direction(
                rows, r => NormalisedSubject(r.Subject), descending,
                StringComparer.CurrentCultureIgnoreCase),
            Arrangement.Size => Direction(rows, r => r.SizeBytes, descending),
            Arrangement.Flag => Direction(rows, r => r.IsFlagged, descending),
            Arrangement.Attachments => Direction(rows, r => r.HasAttachment, descending),
            _ => Direction(rows, r => r.Received, descending),
        };

        return ordered.ThenByDescending(r => r.Received);
    }

    private static IOrderedEnumerable<T> Direction<T, TKey>(
        List<T> rows, Func<T, TKey> key, bool descending, IComparer<TKey>? comparer = null)
        => descending
            ? rows.OrderByDescending(key, comparer)
            : rows.OrderBy(key, comparer);

    /// <summary>Which group a row belongs to.</summary>
    internal static string HeaderFor<T>(T row, Arrangement arrangement, DateTimeOffset now)
        where T : IArrangeable
        => arrangement switch
        {
            Arrangement.From or Arrangement.To or Arrangement.Account => row.DisplayFrom,
            Arrangement.Subject => FirstLetter(NormalisedSubject(row.Subject)),
            Arrangement.Size => SizeBand(row.SizeBytes),
            Arrangement.Flag => row.IsFlagged ? "Flagged" : "Unflagged",
            Arrangement.Attachments => row.HasAttachment ? "With attachments" : "No attachments",
            Arrangement.Categories => "No category",
            Arrangement.Importance => "Normal",
            Arrangement.Type => "Message",
            _ => DateBand(row.Received, now),
        };

    /// <summary>
    /// The date buckets. Relative near the present and absolute further back, which is what
    /// makes a long list readable: "Yesterday" is useful, "Yesterday" for something six months
    /// old would not be.
    /// </summary>
    internal static string DateBand(DateTimeOffset received, DateTimeOffset now)
    {
        var day = received.ToLocalTime().Date;
        var todayDate = now.ToLocalTime().Date;

        if (day > todayDate) return "Later";
        if (day == todayDate) return "Today";
        if (day == todayDate.AddDays(-1)) return "Yesterday";

        // The reference's own sequence: named days for the past week, then counted weeks, then
        // Last Month, then the month itself. The counted weeks run out at four, which is why
        // there is no "Earlier This Month" — the weeks already cover it.
        var age = (todayDate - day).Days;
        if (age < 7) return day.ToString("dddd", CultureInfo.CurrentCulture);
        if (age < 14) return "Last Week";
        if (age < 21) return "Two Weeks Ago";
        if (age < 28) return "Three Weeks Ago";

        var previousMonth = todayDate.AddMonths(-1);
        if (day.Year == previousMonth.Year && day.Month == previousMonth.Month) return "Last Month";

        if (day.Year == todayDate.Year) return day.ToString("MMMM", CultureInfo.CurrentCulture);

        return day.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    }

    internal static string SizeBand(long bytes) => bytes switch
    {
        < 10 * 1024 => "Tiny (under 10 KB)",
        < 25 * 1024 => "Small (10 to 25 KB)",
        < 100 * 1024 => "Medium (25 to 100 KB)",
        < 500 * 1024 => "Large (100 to 500 KB)",
        < 5 * 1024 * 1024 => "Very Large (500 KB to 5 MB)",
        _ => "Enormous (over 5 MB)",
    };

    /// <summary>
    /// A subject without its reply and forward prefixes, so "Re: Budget" files under B with
    /// "Budget" rather than under R with every other reply ever sent.
    /// </summary>
    internal static string NormalisedSubject(string subject)
    {
        var trimmed = subject.AsSpan().Trim();

        while (true)
        {
            var before = trimmed.Length;
            foreach (var prefix in (string[])["re:", "fw:", "fwd:"])
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed[prefix.Length..].TrimStart();
                }
            }

            if (trimmed.Length == before) break;
        }

        return trimmed.ToString();
    }

    private static string FirstLetter(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsLetter(rune))
            {
                return System.Text.Rune.ToUpper(rune, CultureInfo.CurrentCulture).ToString();
            }

            if (System.Text.Rune.IsDigit(rune)) return "0–9";
        }

        return "(none)";
    }
}
