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
