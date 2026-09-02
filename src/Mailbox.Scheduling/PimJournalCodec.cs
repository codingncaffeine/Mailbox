using Mailbox.Store.Pim;

namespace Mailbox.Scheduling;

/// <summary>
/// A note or journal entry to and from the row the PIM store keeps for it, on the same terms as
/// the other two: the raw VJOURNAL text is the truth and the columns are derived from it.
/// </summary>
public static class PimJournalCodec
{
    public static PimItem ToItem(JournalEntry entry, long collectionId, PimItem? existing = null, PimSyncState? syncState = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var ends = entry.When is { } when && entry.Duration is { } duration ? when.Add(duration) : entry.When;

        return new PimItem
        {
            Id = existing?.Id ?? 0,
            CollectionId = collectionId,
            Uid = entry.Uid,
            Kind = CollectionKind.Journal,
            RawPayload = JournalCodec.Serialize(entry),
            // The list shows the first line, which is what the title is; the body is the column
            // a search reads, so an untitled note is still findable by what is in it.
            Summary = entry.Summary,
            Description = entry.Description,
            // The entry's type goes in the status column rather than a new one: it is what a
            // journal groups by, and a note simply says "Note".
            Status = entry.EntryType,
            StartsUtc = entry.When?.ToUtc(),
            EndsUtc = ends?.ToUtc(),
            StartsLocal = entry.When?.ToLocalText(),
            EndsLocal = ends?.ToLocalText(),
            TzId = entry.When is { AllDay: false } timed ? timed.TzId : null,
            AllDay = entry.When?.AllDay ?? false,
            Sequence = entry.Sequence,
            Categories = string.Join(",", entry.Categories),
            Organizer = string.Join(",", entry.Contacts),
            // The cards the entry names, mirrored from its own X-MAILBOX-LINK lines exactly as a
            // vCard's are: the question "what has this person had to do with me" is asked from a
            // contact and answered over the whole journal, and it should not parse every entry in
            // it to find out. The text is still the truth; the column is what a lookup may read.
            Links = entry.Links,
            // The company goes in the column the contact rows already use, for the reason the
            // type rides in Status: the Entry List groups by it, and a list reads columns.
            Company = entry.Company,
            IsPrivate = entry.IsPrivate,
            LastModified = entry.LastModified,
            SyncState = syncState ?? (existing is null ? PimSyncState.New : existing.SyncState == PimSyncState.New ? PimSyncState.New : PimSyncState.Modified),
            DavHref = existing?.DavHref,
            Etag = existing?.Etag,
        };
    }

    /// <summary>The entry a row holds, or what its columns say when the text will not parse.</summary>
    public static JournalEntry FromItem(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            if (JournalCodec.Parse(item.RawPayload).FirstOrDefault() is { } parsed) return parsed;
        }
        catch (FormatException)
        {
            // Fall through to the columns.
        }

        return FromColumns(item);
    }

    public static JournalEntry FromColumns(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var when = EventTime.FromLocalText(item.StartsLocal, item.TzId, item.AllDay);
        var ends = EventTime.FromLocalText(item.EndsLocal, item.TzId, item.AllDay);

        return new JournalEntry
        {
            Uid = item.Uid,
            Summary = item.Summary,
            Description = item.Description,
            When = when,
            Duration = when is not null && ends is not null && ends.Wall > when.Wall ? ends.Wall - when.Wall : null,
            EntryType = item.Status.Length > 0 ? item.Status : JournalEntry.NoteType,
            Categories = item.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Contacts = item.Organizer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Links = item.Links,
            Company = item.Company,
            IsPrivate = item.IsPrivate,
            Sequence = item.Sequence,
            LastModified = item.LastModified,
        };
    }
}
