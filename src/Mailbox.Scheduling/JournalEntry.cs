namespace Mailbox.Scheduling;

/// <summary>
/// A note or a journal entry: a piece of writing with a moment attached.
/// </summary>
/// <remarks>
/// One record for the reference's two modules, because they are one component — VJOURNAL — and
/// differ in what the reader does with it: a note is a sticky square with its first line as its
/// title, and a journal entry is a record of something that took time. <see cref="EntryType"/> is
/// what tells them apart, and it is a string rather than an enum because the reference's own list
/// is open — "Phone call", "Meeting", "Document", and whatever else somebody typed.
/// <para>
/// VJOURNAL is the unusual one: servers support it and almost no client does, which is why the
/// notes here sync at all.
/// </para>
/// </remarks>
public sealed record JournalEntry
{
    /// <summary>What a note is, and the default for anything that does not say.</summary>
    public const string NoteType = "Note";

    public required string Uid { get; init; }

    /// <summary>
    /// The title. For a note the reference takes it from the body's first line rather than
    /// asking for one, which <see cref="WithBody"/> does here.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The writing itself.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>When it happened, or when the note was made.</summary>
    public EventTime? When { get; init; }

    /// <summary>How long it took, for a journal entry that timed something.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>"Note" for a note; the reference's own list of activities for a journal entry.</summary>
    public string EntryType { get; init; } = NoteType;

    /// <summary>A note's colour is a category, which is how one colour set covers every module.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Whoever the entry is about, which the reference's journal keeps beside it.</summary>
    public IReadOnlyList<string> Contacts { get; init; } = [];

    public int Sequence { get; init; }

    public DateTimeOffset LastModified { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when this is a note rather than a record of something done.</summary>
    public bool IsNote => string.Equals(EntryType, NoteType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same entry carrying this body, with its title taken from the body's first line — which
    /// is the whole of a note's editing: there is no title field to fill in.
    /// </summary>
    public JournalEntry WithBody(string body)
    {
        var text = body ?? string.Empty;
        var first = text.AsSpan();
        var breakAt = first.IndexOfAny('\r', '\n');
        var title = (breakAt >= 0 ? first[..breakAt] : first).Trim().ToString();
        return this with { Description = text, Summary = title };
    }

    /// <summary>What the reference writes in a list when a note has nothing in it yet.</summary>
    public const string Untitled = "(Empty)";

    /// <summary>The title a list shows, which is the first line or a stand-in for one.</summary>
    public string Titled() => Summary.Length > 0 ? Summary : Untitled;

    public static string NewUid() => CalendarEvent.NewUid();

    public bool Equals(JournalEntry? other)
        => other is not null
           && Uid == other.Uid && Summary == other.Summary && Description == other.Description
           && When == other.When && Duration == other.Duration && EntryType == other.EntryType
           && Categories.SequenceEqual(other.Categories, StringComparer.Ordinal)
           && Contacts.SequenceEqual(other.Contacts, StringComparer.Ordinal)
           && Sequence == other.Sequence && LastModified == other.LastModified;

    public override int GetHashCode() => HashCode.Combine(Uid, Summary, Description, When, EntryType, LastModified);
}
