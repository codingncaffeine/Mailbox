using System.Globalization;
using Mailbox.Store.Pim;

namespace Mailbox.Scheduling;

/// <summary>How the Notes module is showing what it holds, which is its Current View group.</summary>
public enum NoteArrangement
{
    /// <summary>The sticky squares, which is what the module opens in.</summary>
    Icons,

    /// <summary>One row a note, with what it says beside its title.</summary>
    List,

    /// <summary>The same rows, kept to the week just gone.</summary>
    LastSevenDays,
}

/// <summary>One note as a view draws it: the row it came from, and what is on its face.</summary>
public sealed record NoteRow
{
    public required long ItemId { get; init; }
    public required long CollectionId { get; init; }
    public required JournalEntry Entry { get; init; }

    /// <summary>When it was written, which is what the module sorts and files by.</summary>
    public required DateTime Made { get; init; }

    /// <summary>The first line, or the reference's own stand-in for one.</summary>
    public string Title => Entry.Titled();

    /// <summary>
    /// Everything after the first line as one line, which is what a square has room to say under
    /// its title and what the list writes in its Contents column.
    /// </summary>
    public string Preview
    {
        get
        {
            var body = Entry.Description;
            var breakAt = body.AsSpan().IndexOfAny('\r', '\n');
            if (breakAt < 0) return string.Empty;
            return string.Join(' ', body[breakAt..].Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    public IReadOnlyList<string> Categories => Entry.Categories;

    /// <summary>
    /// When it was made, as the reference writes it: the time alone for one made today, the date
    /// otherwise — a list of notes is mostly recent, and the time is what tells two of them apart.
    /// </summary>
    public string MadeText(DateOnly today, IFormatProvider? culture = null)
    {
        var format = culture ?? CultureInfo.CurrentCulture;
        return DateOnly.FromDateTime(Made) == today
            ? Made.ToString("t", format)
            : Made.ToString("g", format);
    }
}

/// <summary>
/// The note lists and what is on them: the store's rows as the Notes module draws them.
/// </summary>
/// <remarks>
/// The reading half of the module, as <c>TaskBook</c> is of Tasks — rows built from the columns,
/// the whole VJOURNAL parsed only when one is opened. Writing stays with the shell, which owns
/// the queue a change has to join.
/// <para>
/// Notes and journal entries share a component, a collection kind and a table: what separates
/// them is <see cref="JournalEntry.IsNote"/>, which is the entry's type and lives in the status
/// column. So the two modules are two readings of one set of rows, and a note written here is a
/// note on every client that reads VJOURNAL at all.
/// </para>
/// </remarks>
public sealed class NoteBook(PimRepository repository)
{
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>The note lists, in the order the navigation pane shows them.</summary>
    public IReadOnlyList<Collection> Lists() => _repository.Collections(CollectionKind.Journal);

    /// <summary>
    /// Every note on the visible lists, newest first — which is what the reference's own Icons
    /// view is arranged by, and what makes the square just written the one nearest the corner.
    /// </summary>
    /// <param name="arrangement">Which of the three the Current View group has chosen.</param>
    /// <param name="today">Today, as the module believes it — pinned by the harness.</param>
    /// <param name="collectionIds">Only these lists; null for every visible one.</param>
    public IReadOnlyList<NoteRow> Rows(
        NoteArrangement arrangement,
        DateOnly today,
        IReadOnlyCollection<long>? collectionIds = null)
    {
        var rows = new List<NoteRow>();
        var since = today.AddDays(-7);

        foreach (var list in Lists())
        {
            if (collectionIds is { Count: > 0 } ? !collectionIds.Contains(list.Id) : !list.IsVisible) continue;

            foreach (var item in _repository.Items(list.Id))
            {
                // A delete on a server-backed folder keeps the row, marks it and queues it, so
                // that a delete made offline still reaches the server. It is off the wall as far
                // as the reader is concerned from the moment they said so.
                if (item.SyncState == PimSyncState.Deleted) continue;

                // From the columns, not the text: a wall of five hundred notes would otherwise
                // parse five hundred VJOURNALs to draw five hundred squares.
                var entry = PimJournalCodec.FromColumns(item);
                if (!entry.IsNote) continue;

                var made = Made(entry, item);
                if (arrangement == NoteArrangement.LastSevenDays && DateOnly.FromDateTime(made) < since) continue;

                rows.Add(new NoteRow
                {
                    ItemId = item.Id,
                    CollectionId = list.Id,
                    Entry = entry,
                    Made = made,
                });
            }
        }

        rows.Sort(Compare);
        return rows;
    }

    /// <summary>The whole note, parsed, which is what opening one wants.</summary>
    public JournalEntry? Open(long itemId)
        => _repository.Item(itemId) is { } item ? PimJournalCodec.FromItem(item) : null;

    /// <summary>
    /// When a note was written: the moment it carries, falling back to when the row was last
    /// touched for one another client wrote without a DTSTART.
    /// </summary>
    public static DateTime Made(JournalEntry entry, PimItem item)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(item);
        return entry.When?.Wall ?? item.LastModified.LocalDateTime;
    }

    /// <summary>Newest first, and by title where two were written in the same minute.</summary>
    private static int Compare(NoteRow a, NoteRow b)
    {
        var byMade = b.Made.CompareTo(a.Made);
        return byMade != 0 ? byMade : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
    }
}
