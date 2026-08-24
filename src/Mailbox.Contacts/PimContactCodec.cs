using System.Globalization;
using Mailbox.Store.Pim;

namespace Mailbox.Contacts;

/// <summary>
/// A contact to and from the row the PIM store keeps for it. The row's raw vCard text is the
/// truth; every other column is derived from the contact here, so a query on the columns and a
/// parse of the text always agree.
/// </summary>
/// <remarks>
/// The same bargain <see cref="Mailbox.Scheduling.PimEventCodec"/> strikes for an appointment,
/// and for the same reason: a server gets back what it sent plus what changed, and a card this
/// application cannot fully model still round-trips through it intact.
/// </remarks>
public static class PimContactCodec
{
    /// <summary>
    /// The version a contact made here is written in.
    /// </summary>
    /// <remarks>
    /// 3.0 rather than 4.0: it is what every CardDAV server and every other client reads without
    /// argument, and a distribution list written in it carries Apple's extension, which is the
    /// only group representation the 3.0 world has.
    /// </remarks>
    public const VCardVersion StoredVersion = VCardVersion.V3;

    /// <summary>
    /// The row for a contact. <paramref name="existing"/> carries the identity and sync
    /// bookkeeping (Id, DavHref, Etag) forward when a stored contact is edited.
    /// </summary>
    public static PimItem ToItem(Contact contact, long collectionId, PimItem? existing = null, PimSyncState? syncState = null)
    {
        ArgumentNullException.ThrowIfNull(contact);

        return new PimItem
        {
            Id = existing?.Id ?? 0,
            CollectionId = collectionId,
            Uid = contact.Uid,
            Kind = CollectionKind.Contacts,
            RawPayload = VCardCodec.Serialize(contact, StoredVersion),
            Summary = contact.Named(),
            Description = contact.Notes,
            // The card's own line for where somebody is, so a contact list can show one without
            // parsing the vCard for it.
            Location = contact.Addresses.FirstOrDefault(a => !a.IsEmpty)?.OneLine() ?? string.Empty,
            Categories = string.Join(",", contact.Categories),
            LastModified = contact.LastModified,
            SyncState = syncState ?? (existing is null ? PimSyncState.New : existing.SyncState == PimSyncState.New ? PimSyncState.New : PimSyncState.Modified),
            DavHref = existing?.DavHref,
            Etag = existing?.Etag,
            FileAs = contact.FiledAs(),
            FirstName = contact.FirstName,
            LastName = contact.LastName,
            Company = contact.Company,
            JobTitle = contact.JobTitle,
            IsGroup = contact.IsGroup,
            IsPrivate = contact.IsPrivate,

            // Mirrored from the card's own X-MAILBOX-LINK lines so the People list can group
            // linked cards without parsing every card in the book (step 7).
            Links = contact.Links,

            // The flag is the reader's own and is not written into the card (see Contact).
            FollowUpDue = contact.FollowUpDue,
            FollowUpComplete = contact.FollowUpComplete,

            // A birthday is a date the calendar will want; kept as the row's own start so a
            // future birthday view has something to read without parsing every card. The time is
            // appended rather than formatted: a DateOnly refuses a format with a time in it.
            StartsLocal = contact.Birthday is { } birthday
                ? birthday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00"
                : null,
            AllDay = contact.Birthday is not null,
        };
    }

    /// <summary>
    /// The contact a row holds: parsed from its vCard text, or — when the text will not parse —
    /// rebuilt from the columns, so a damaged row still shows in the list and can be fixed.
    /// </summary>
    public static Contact FromItem(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            var parsed = VCardCodec.Parse(item.RawPayload);

            // The card is the truth for everything the card says. The follow-up flag is the one
            // thing it does not say — it is kept beside the card on purpose — so it comes off the
            // row, or reading a contact would quietly forget it every time.
            if (parsed.Count > 0)
            {
                return parsed[0] with
                {
                    FollowUpDue = item.FollowUpDue,
                    FollowUpComplete = item.FollowUpComplete,
                };
            }
        }
        catch (FormatException)
        {
            // Fall through to the columns.
        }

        return FromColumns(item);
    }

    /// <summary>The contact the columns alone describe.</summary>
    public static Contact FromColumns(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new Contact
        {
            Uid = item.Uid,
            DisplayName = item.Summary,
            FirstName = item.FirstName,
            LastName = item.LastName,
            FileAs = item.FileAs,
            Company = item.Company,
            JobTitle = item.JobTitle,
            Notes = item.Description,
            IsGroup = item.IsGroup,
            IsPrivate = item.IsPrivate,
            Links = item.Links,
            FollowUpDue = item.FollowUpDue,
            FollowUpComplete = item.FollowUpComplete,
            Categories = item.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LastModified = item.LastModified,
        };
    }

    /// <summary>
    /// The addresses and numbers a row's side table holds, in the order the card shows them.
    /// </summary>
    public static IReadOnlyList<ContactField> Fields(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var fields = new List<ContactField>();

        var ordinal = 0;
        foreach (var email in contact.Emails) fields.Add(new ContactField("email", email.Address, email.Name, ordinal++));

        ordinal = 0;
        foreach (var phone in contact.Phones) fields.Add(new ContactField("phone", phone.Number, Label(phone.Kind), ordinal++));

        ordinal = 0;
        foreach (var im in contact.InstantMessaging) fields.Add(new ContactField("im", im, string.Empty, ordinal++));

        return fields;
    }

    /// <summary>What a number's label is called in the store, and back again.</summary>
    internal static string Label(PhoneKind kind) => kind switch
    {
        PhoneKind.Home => "home",
        PhoneKind.Mobile => "mobile",
        PhoneKind.BusinessFax => "businessfax",
        PhoneKind.HomeFax => "homefax",
        PhoneKind.Pager => "pager",
        PhoneKind.Other => "other",
        _ => "business",
    };

    internal static PhoneKind KindOf(string label) => label switch
    {
        "home" => PhoneKind.Home,
        "mobile" => PhoneKind.Mobile,
        "businessfax" => PhoneKind.BusinessFax,
        "homefax" => PhoneKind.HomeFax,
        "pager" => PhoneKind.Pager,
        "other" => PhoneKind.Other,
        _ => PhoneKind.Business,
    };
}
