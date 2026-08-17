using System.Globalization;
using Mailbox.Store.Pim;

namespace Mailbox.Scheduling;

/// <summary>How the Journal module is showing what it holds, which is its Current View group.</summary>
public enum JournalArrangement
{
    /// <summary>The timeline, which is what the module opens in: entries hung under the day they happened.</summary>
    Timeline,

    /// <summary>One row an entry, grouped by what kind of thing it was.</summary>
    EntryList,

    /// <summary>The same rows, kept to the calls.</summary>
    PhoneCalls,

    /// <summary>The same rows, kept to the week just gone.</summary>
    LastSevenDays,
}

/// <summary>How wide a slice of time the timeline is showing.</summary>
public enum TimelineScale
{
    Day,
    Week,
    Month,
}

/// <summary>One journal entry as a view draws it: the row it came from, and what is on it.</summary>
public sealed record JournalRow
{
    public required long ItemId { get; init; }
    public required long CollectionId { get; init; }
    public required JournalEntry Entry { get; init; }

    /// <summary>When it started, which is where the timeline hangs it.</summary>
    public required DateTime Start { get; init; }

    /// <summary>What kind of thing it was — the reference's open list, not an enum.</summary>
    public string EntryType => Entry.EntryType.Length > 0 ? Entry.EntryType : "Note";

    public string Subject => Entry.Summary.Length > 0 ? Entry.Summary : "(No subject)";

    public TimeSpan? Duration => Entry.Duration;

    /// <summary>Whoever it was with, as the reference's own Contact column writes them.</summary>
    public string Contacts => string.Join("; ", Entry.Contacts);

    public IReadOnlyList<string> Categories => Entry.Categories;

    public string StartText(IFormatProvider? culture = null)
        => Start.ToString("g", culture ?? CultureInfo.CurrentCulture);

    /// <summary>How long it took, as the list writes it, or nothing for an entry that says nothing.</summary>
    public string DurationText(IFormatProvider? culture = null)
        => Duration is { } span ? JournalCodec.DurationText(span, culture) : string.Empty;
}

/// <summary>
/// The journal and what is in it: the store's rows as the Journal module draws them.
/// </summary>
/// <remarks>
/// The other reading of the rows <see cref="NoteBook"/> reads — same component, same collections,
/// same table, split on <see cref="JournalEntry.IsNote"/>. An entry is anything that says what
/// kind of thing it was; a note is what says nothing, because a note is the default and its text
/// should be what any other client would have written.
/// <para>
/// <b>The reference has long since stopped developing this module</b> and hides it behind Ctrl+8;
/// it is here for the completeness §9 asks for, and because a VJOURNAL that carries a duration and
/// a contact is exactly what it is for.
/// </para>
/// </remarks>
public sealed class JournalBook(PimRepository repository)
{
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>
    /// The reference's own list of activities, in the order its dropdown offers them. Open rather
    /// than closed: an entry another client wrote saying something else keeps what it says.
    /// </summary>
    public static IReadOnlyList<string> Types { get; } =
    [
        "Phone call", "Meeting", "E-mail Message", "Conversation", "Letter", "Fax",
        "Document", "Note", "Task", "Task request", "Remote session",
    ];

    /// <summary>What a call is called, which is what the Phone Calls view keeps.</summary>
    public const string PhoneCall = "Phone call";

    /// <summary>The journals, in the order the navigation pane shows them.</summary>
    public IReadOnlyList<Collection> Lists() => _repository.Collections(CollectionKind.Journal);

    /// <summary>
    /// Every entry on the visible journals, newest first — the reference's own arrangement, and
    /// the one that puts what just happened at the timeline's right-hand end.
    /// </summary>
    public IReadOnlyList<JournalRow> Rows(
        JournalArrangement arrangement,
        DateOnly today,
        IReadOnlyCollection<long>? collectionIds = null)
    {
        var rows = new List<JournalRow>();
        var since = today.AddDays(-7);

        foreach (var list in Lists())
        {
            if (collectionIds is { Count: > 0 } ? !collectionIds.Contains(list.Id) : !list.IsVisible) continue;

            foreach (var item in _repository.Items(list.Id))
            {
                // Deleted on a server-backed folder means kept, marked and queued — and off the
                // timeline from the moment the reader said so (`NoteBook` reads the same rule).
                if (item.SyncState == PimSyncState.Deleted) continue;

                var entry = PimJournalCodec.FromColumns(item);
                if (entry.IsNote) continue;

                var start = NoteBook.Made(entry, item);
                if (arrangement == JournalArrangement.LastSevenDays && DateOnly.FromDateTime(start) < since) continue;
                if (arrangement == JournalArrangement.PhoneCalls
                    && !string.Equals(entry.EntryType, PhoneCall, StringComparison.OrdinalIgnoreCase)) continue;

                rows.Add(new JournalRow
                {
                    ItemId = item.Id,
                    CollectionId = list.Id,
                    Entry = entry,
                    Start = start,
                });
            }
        }

        rows.Sort(Compare);
        return rows;
    }

    /// <summary>The whole entry, parsed, which is what opening one wants.</summary>
    public JournalEntry? Open(long itemId)
        => _repository.Item(itemId) is { } item ? PimJournalCodec.FromItem(item) : null;

    /// <summary>
    /// The rows grouped as the Entry List view groups them: by type, the types in the order the
    /// reference lists them and anything else after, alphabetically.
    /// </summary>
    public static IReadOnlyList<(string Type, IReadOnlyList<JournalRow> Rows)> ByType(IEnumerable<JournalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows
            .GroupBy(r => r.EntryType, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => Rank(g.Key))
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (g.Key, (IReadOnlyList<JournalRow>)[.. g]))];
    }

    /// <summary>Where a type sits in the reference's own list, or past its end for one of ours.</summary>
    private static int Rank(string type)
    {
        for (var i = 0; i < Types.Count; i++)
        {
            if (string.Equals(Types[i], type, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return Types.Count;
    }

    /// <summary>Newest first, and by subject where two started together.</summary>
    private static int Compare(JournalRow a, JournalRow b)
    {
        var byStart = b.Start.CompareTo(a.Start);
        return byStart != 0 ? byStart : string.Compare(a.Subject, b.Subject, StringComparison.CurrentCultureIgnoreCase);
    }
}
