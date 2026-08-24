using Mailbox.Store.Pim;

namespace Mailbox.Contacts;

/// <summary>
/// One entry in the People list: the contact as its columns describe it, and the row it came
/// from so that acting on it can find it again.
/// </summary>
/// <remarks>
/// Built from the columns and the field table rather than from the vCard, which is what the
/// columns are for: a list of five hundred people would otherwise parse five hundred cards every
/// time it drew itself. The whole card is read when one is opened.
/// </remarks>
public sealed record ContactRow(long Id, long CollectionId, string CollectionName, Contact Contact, bool IsReadOnly)
{
    public string Named() => Contact.Named();

    /// <summary>
    /// The names of the linked cards collapsed into this row, when it stands for several — so a
    /// search for any member's name still finds the person they collapsed into.
    /// </summary>
    public IReadOnlyList<string> AlsoNamed { get; init; } = [];
}

/// <summary>
/// Turns what the PIM store holds into what the People module reads, and writes it back: the
/// row, the addresses beside it, and the photograph, which are three tables and one save.
/// </summary>
public sealed class ContactBook(PimRepository repository)
{
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>The store underneath, for the panes that act on a whole address book.</summary>
    public PimRepository Repository => _repository;

    /// <summary>The address books, in the order the navigation pane lists them.</summary>
    public IReadOnlyList<Collection> AddressBooks() => _repository.Collections(CollectionKind.Contacts);

    /// <summary>
    /// The address book new contacts go into — made, named "Contacts", when there is none, as
    /// the reference starts with one.
    /// </summary>
    public Collection Default()
    {
        var books = AddressBooks();
        return books.FirstOrDefault(b => b.IsDefault)
               ?? books.FirstOrDefault()
               ?? _repository.AddCollection(CollectionKind.Contacts, "Contacts");
    }

    /// <summary>Everything on the visible address books, in File As order.</summary>
    public IReadOnlyList<ContactRow> Rows(IReadOnlyCollection<long>? collectionIds = null)
    {
        var books = AddressBooks().ToDictionary(b => b.Id);
        return _repository.Contacts(collectionIds)
            .Select(item => Row(item, books))
            .ToList();
    }

    /// <summary>
    /// The rows the People module lists: one per person, linked cards collapsed into the member
    /// whose File As sorts first — the reference shows a linked person once. Everything else —
    /// the Address Book, Select Names, autocomplete — reads <see cref="Rows"/>, because a picker
    /// wants every card: each is an addressable thing whoever it belongs to.
    /// </summary>
    /// <remarks>
    /// Grouping reads the links <em>column</em> (step 7), never the cards — which also means a
    /// card saved before the column existed lists separately until its next save writes the
    /// mirror. A link naming a uid that is not here — another book, a card deleted elsewhere —
    /// simply contributes nothing.
    /// </remarks>
    public IReadOnlyList<ContactRow> People(IReadOnlyCollection<long>? collectionIds = null)
    {
        var rows = Rows(collectionIds);
        if (rows.All(r => r.Contact.Links.Count == 0)) return rows;

        var here = new HashSet<string>(rows.Select(r => r.Contact.Uid), StringComparer.OrdinalIgnoreCase);

        // Union-find over uids, so three cards linked pairwise are one person however the pairs
        // were made.
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Find(string uid)
        {
            var root = uid;
            while (parent.TryGetValue(root, out var up)
                   && !string.Equals(up, root, StringComparison.OrdinalIgnoreCase))
            {
                root = up;
            }

            return root;
        }

        foreach (var row in rows)
        {
            parent.TryAdd(row.Contact.Uid, row.Contact.Uid);
            foreach (var link in row.Contact.Links.Where(here.Contains))
            {
                parent.TryAdd(link, link);
                var a = Find(row.Contact.Uid);
                var b = Find(link);
                if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) parent[b] = a;
            }
        }

        var members = rows
            .GroupBy(r => Find(r.Contact.Uid), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Rows arrive in File As order, so the first member met leads its set and the output
        // keeps the list's own order.
        var output = new List<ContactRow>(rows.Count);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var root = Find(row.Contact.Uid);
            if (!done.Add(root)) continue;

            var set = members[root];
            if (set.Count == 1)
            {
                output.Add(row);
                continue;
            }

            var others = set.Where(r => r.Id != row.Id).ToList();
            output.Add(row with
            {
                Contact = ContactMerge.Display(row.Contact, [.. others.Select(o => o.Contact)]),
                AlsoNamed = [.. others.Select(o => o.Named())],
            });
        }

        return output;
    }

    /// <summary>One row, or null when the id names nothing.</summary>
    public ContactRow? Row(long id)
    {
        var item = _repository.Item(id);
        return item is { Kind: CollectionKind.Contacts } ? Row(item, AddressBooks().ToDictionary(b => b.Id)) : null;
    }

    /// <summary>
    /// The whole contact behind a row, parsed from its vCard — everything the columns do not
    /// carry: addresses, the notes, the birthday, the members of a group, the photograph.
    /// </summary>
    public Contact? Full(long id)
    {
        var item = _repository.Item(id);
        if (item is not { Kind: CollectionKind.Contacts }) return null;

        var contact = PimContactCodec.FromItem(item);
        if (contact.Photo is not null) return contact;

        // A photograph is kept beside the row as well as inside the card, and the card is what a
        // server round-trips; either may be the one that has it.
        return _repository.ContactPhoto(id) is { } photo
            ? contact with { Photo = new ContactPhoto(photo.Bytes, photo.MediaType) }
            : contact;
    }

    /// <summary>Who holds this address, over every address book.</summary>
    public IReadOnlyList<ContactRow> WithAddress(string address)
    {
        var books = AddressBooks().ToDictionary(b => b.Id);
        return _repository.ContactsWithAddress(address).Select(item => Row(item, books)).ToList();
    }

    /// <summary>Contacts whose name, company or address begins with what has been typed.</summary>
    public IReadOnlyList<ContactRow> Matching(string prefix, int limit = 20)
    {
        var books = AddressBooks().ToDictionary(b => b.Id);
        return _repository.FindContacts(prefix, limit).Select(item => Row(item, books)).ToList();
    }

    /// <summary>
    /// Writes a contact: the row, the addresses and numbers beside it, and the photograph.
    /// </summary>
    /// <remarks>
    /// Three tables and one call, because a caller that wrote the row and forgot the addresses
    /// would leave a contact nobody could be found by — the search index is built from them.
    /// </remarks>
    public PimItem Save(Contact contact, long collectionId, PimItem? existing = null)
    {
        ArgumentNullException.ThrowIfNull(contact);

        var row = PimContactCodec.ToItem(contact, collectionId, existing);
        var written = existing is null ? _repository.AddItem(row) : Store(row);

        _repository.SetContactFields(written.Id, PimContactCodec.Fields(contact));
        _repository.SetContactPhoto(written.Id, contact.Photo?.Bytes, contact.Photo?.MediaType ?? "image/jpeg");
        return written;

        PimItem Store(PimItem item)
        {
            _repository.UpdateItem(item);
            return item;
        }
    }

    /// <summary>
    /// The other cards for this person — the ones this card's own links name.
    /// </summary>
    /// <remarks>
    /// This card's links and not a search for cards that name it, which would be the other half of
    /// the same question. <see cref="Link"/> writes both ends, so for anything made here the two
    /// answers are the same; catching the case where another client wrote only one end would mean
    /// **parsing every card in the book** on every open, the links living in the vCard text rather
    /// than in a column, and that is the wrong price for a rare shape.
    /// </remarks>
    public IReadOnlyList<ContactRow> Linked(long id)
    {
        if (Full(id) is not { Links.Count: > 0 } contact) return [];

        return
        [
            .. Rows()
                .Where(other => other.Id != id
                                && contact.Links.Contains(other.Contact.Uid, StringComparer.OrdinalIgnoreCase))
                .OrderBy(other => other.Named(), StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <summary>
    /// Links two cards as the same person, writing both ends.
    /// </summary>
    /// <remarks>
    /// Both, because a link only one card knows about is one the reader can only see from one
    /// side — and because a card that goes to a server and comes back keeps what it was written
    /// with, not what something else says about it.
    /// </remarks>
    public bool Link(long id, long otherId)
    {
        if (id == otherId) return false;
        if (Row(id) is not { } mine || Row(otherId) is not { } theirs) return false;
        if (Full(id) is not { } here || Full(otherId) is not { } there) return false;

        var changed = false;

        if (!here.Links.Contains(there.Uid, StringComparer.OrdinalIgnoreCase))
        {
            Save(here with { Links = [.. here.Links, there.Uid] }, mine.CollectionId, _repository.Item(id));
            changed = true;
        }

        if (!there.Links.Contains(here.Uid, StringComparer.OrdinalIgnoreCase))
        {
            Save(there with { Links = [.. there.Links, here.Uid] }, theirs.CollectionId, _repository.Item(otherId));
            changed = true;
        }

        return changed;
    }

    /// <summary>Takes a link off, both ends again.</summary>
    public bool Unlink(long id, long otherId)
    {
        if (Row(id) is not { } mine || Row(otherId) is not { } theirs) return false;
        if (Full(id) is not { } here || Full(otherId) is not { } there) return false;

        var changed = false;

        if (here.Links.Contains(there.Uid, StringComparer.OrdinalIgnoreCase))
        {
            Save(
                here with { Links = [.. here.Links.Where(l => !string.Equals(l, there.Uid, StringComparison.OrdinalIgnoreCase))] },
                mine.CollectionId,
                _repository.Item(id));
            changed = true;
        }

        if (there.Links.Contains(here.Uid, StringComparer.OrdinalIgnoreCase))
        {
            Save(
                there with { Links = [.. there.Links.Where(l => !string.Equals(l, here.Uid, StringComparison.OrdinalIgnoreCase))] },
                theirs.CollectionId,
                _repository.Item(otherId));
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Cards that may already be this person, for the prompt on save.
    /// </summary>
    /// <remarks>
    /// Over every address book rather than the one being written to: a duplicate in another book
    /// is still a duplicate, and the reason somebody has two is usually that the second came from
    /// somewhere else.
    /// </remarks>
    public IReadOnlyList<DuplicateMatch> Duplicates(Contact candidate, long? ignoreId = null)
        => ContactDuplicates.Find(candidate, Rows(), ignoreId);

    private ContactRow Row(PimItem item, IReadOnlyDictionary<long, Collection> books)
    {
        var contact = PimContactCodec.FromColumns(item);
        var fields = _repository.ContactFields(item.Id);

        contact = contact with
        {
            Emails = [.. fields.Where(f => f.Kind == "email").OrderBy(f => f.Ordinal).Select(f => new ContactEmail(f.Value, f.Label))],
            Phones = [.. fields.Where(f => f.Kind == "phone").OrderBy(f => f.Ordinal).Select(f => new ContactPhone(f.Value, PimContactCodec.KindOf(f.Label)))],
            InstantMessaging = [.. fields.Where(f => f.Kind == "im").OrderBy(f => f.Ordinal).Select(f => f.Value)],
        };

        var book = books.TryGetValue(item.CollectionId, out var found) ? found : null;
        return new ContactRow(item.Id, item.CollectionId, book?.DisplayName ?? string.Empty, contact, book?.IsReadOnly ?? false);
    }
}
