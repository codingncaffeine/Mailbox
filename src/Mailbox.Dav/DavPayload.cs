using Mailbox.Contacts;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Dav;

/// <summary>
/// What differs between a calendar and an address book once the WebDAV part is done with.
/// </summary>
/// <remarks>
/// CalDAV and CardDAV are the same protocol with two nouns: the verbs, discovery, the ETag
/// preconditions, <c>sync-collection</c>, the CTag fallback and the offline queue are identical,
/// and what is not is the REPORT's name, the content type, and what the text in the resource
/// means. One engine and two of these, rather than two engines that drift apart at the first
/// server that behaves oddly.
/// </remarks>
public interface IDavPayload
{
    /// <summary>What the collections this payload syncs hold.</summary>
    CollectionKind Kind { get; }

    /// <summary>The type a PUT declares.</summary>
    string ContentType { get; }

    /// <summary>The REPORT that fetches the payloads of a batch of hrefs.</summary>
    string Multiget(IEnumerable<string> hrefs);

    /// <summary>
    /// Writes one server payload into the store, and says how many rows it came to — a calendar
    /// resource may hold a series and its overrides, an address book resource holds one card.
    /// </summary>
    int Store(PimRepository repository, long collectionId, string href, string? etag, string text, bool overLocalChanges);

    /// <summary>The whole resource a PUT of this row has to send.</summary>
    string Whole(PimRepository repository, PimItem item);

    /// <summary>What a refused write is called when the reader is shown the two copies.</summary>
    string Summarize(string text);
}

/// <summary>The two payloads there are, and which one a collection wants.</summary>
public static class DavPayloads
{
    public static readonly IDavPayload Calendar = new CalendarPayload();

    public static readonly IDavPayload AddressBook = new ContactPayload();

    /// <summary>
    /// Tasks and journals ride the calendar payload, being VTODO and VJOURNAL inside the same
    /// VCALENDAR; only an address book is different.
    /// </summary>
    public static IDavPayload For(CollectionKind kind)
        => kind == CollectionKind.Contacts ? AddressBook : Calendar;
}

/// <summary>iCalendar over CalDAV: a resource is one UID's whole family.</summary>
internal sealed class CalendarPayload : IDavPayload
{
    public CollectionKind Kind => CollectionKind.Events;

    public string ContentType => "text/calendar; charset=utf-8";

    public string Multiget(IEnumerable<string> hrefs) => DavXml.CalendarMultiget(hrefs);

    public int Store(PimRepository repository, long collectionId, string href, string? etag, string text, bool overLocalChanges)
        => DavSync.StoreCalendar(repository, collectionId, href, etag, text, overLocalChanges);

    /// <summary>
    /// A series' master and every override together, because a server keeps one resource per UID
    /// and a PUT of the master alone deletes the overrides.
    /// </summary>
    public string Whole(PimRepository repository, PimItem item)
    {
        var family = repository.ItemsByUid(item.CollectionId, item.Uid);
        if (family.Count > 1) return ICalendarCodec.SerializeCalendar(family.Select(PimEventCodec.FromItem).ToList());

        // A row made here keeps its VEVENT and nothing round it, which is all the store needs and
        // less than a server takes: Radicale answers a bare component
        // "400 Item type 'VEVENT' not supported in 'VCALENDAR' collection", and it is right to.
        // A payload that came from a server goes back verbatim.
        return item.RawPayload.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)
            ? item.RawPayload
            : ICalendarCodec.SerializeCalendar([PimEventCodec.FromItem(item)]);
    }

    public string Summarize(string text)
    {
        try
        {
            return ICalendarCodec.Parse(text).FirstOrDefault()?.Summary ?? string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}

/// <summary>vCard over CardDAV: a resource is one card, and one card is one row.</summary>
internal sealed class ContactPayload : IDavPayload
{
    public CollectionKind Kind => CollectionKind.Contacts;

    public string ContentType => "text/vcard; charset=utf-8";

    public string Multiget(IEnumerable<string> hrefs) => DavXml.AddressBookMultiget(hrefs);

    /// <summary>
    /// Writes a card into the store: the row, the addresses beside it and the photograph, which
    /// is what makes the contact findable at all.
    /// </summary>
    public int Store(PimRepository repository, long collectionId, string href, string? etag, string text, bool overLocalChanges)
    {
        IReadOnlyList<Contact> contacts;
        try
        {
            contacts = VCardCodec.Parse(text);
        }
        catch (FormatException)
        {
            return 0;
        }

        var written = 0;
        foreach (var contact in contacts)
        {
            var match = repository.ItemsByUid(collectionId, contact.Uid).FirstOrDefault();

            // A row whose own change has not reached the server is what a conflict is about; the
            // pull leaves it alone and the reader is asked instead.
            if (!overLocalChanges && match is { SyncState: not PimSyncState.Synced }) continue;

            var row = PimContactCodec.ToItem(contact, collectionId, match, PimSyncState.Synced) with
            {
                DavHref = href,
                Etag = etag,
                // The server's copy verbatim, for the same reason an appointment's is: sending
                // back a re-serialization would drop whatever that server keeps and we do not
                // model. Only when the resource holds one card — a file of several has to be
                // taken apart, and each row then carries its own.
                RawPayload = contacts.Count == 1 ? text : VCardCodec.Serialize(contact, PimContactCodec.StoredVersion),
            };

            var stored = match is null ? repository.AddItem(row) : Update(row);
            repository.SetContactFields(stored.Id, PimContactCodec.Fields(contact));
            repository.SetContactPhoto(stored.Id, contact.Photo?.Bytes, contact.Photo?.MediaType ?? "image/jpeg");
            written++;

            PimItem Update(PimItem item)
            {
                repository.UpdateItem(item);
                return item;
            }
        }

        return written;
    }

    /// <summary>One card, which is what an address book resource holds.</summary>
    public string Whole(PimRepository repository, PimItem item)
        => item.RawPayload.Contains("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)
            ? item.RawPayload
            : VCardCodec.Serialize(PimContactCodec.FromItem(item), PimContactCodec.StoredVersion);

    public string Summarize(string text)
    {
        try
        {
            return VCardCodec.Parse(text).FirstOrDefault()?.Named() ?? string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
