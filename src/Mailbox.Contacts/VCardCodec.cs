using System.Globalization;
using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Enums;
using FolkerKinzel.VCards.Models;
using FolkerKinzel.VCards.Models.Properties;

namespace Mailbox.Contacts;

/// <summary>
/// A contact to and from vCard text: 2.1, 3.0 and 4.0 in, 3.0 or 4.0 out.
/// </summary>
/// <remarks>
/// FolkerKinzel.VCards knows the file format — the versions, the folding, the encodings, the
/// character sets 2.1 files still arrive in. What is here is the shape above it: one record
/// whichever version it came from, the labels the reference's card puts on a number, and a
/// distribution list that survives the round trip in either version — 4.0 states a group with
/// <c>KIND</c> and <c>MEMBER</c>, and 3.0 has no such thing, so the 3.0 world writes Apple's
/// <c>X-ADDRESSBOOKSERVER-KIND</c> and <c>X-ADDRESSBOOKSERVER-MEMBER</c> and every server and
/// client that matters reads them.
/// </remarks>
public static class VCardCodec
{
    /// <summary>Apple's group extension, which is what a 3.0 distribution list is made of.</summary>
    internal const string GroupKindKey = "X-ADDRESSBOOKSERVER-KIND";
    internal const string GroupMemberKey = "X-ADDRESSBOOKSERVER-MEMBER";

    /// <summary>
    /// Every contact in a vCard file. A file may hold many, which is what an export is.
    /// </summary>
    /// <exception cref="FormatException">The text is not vCard at all.</exception>
    public static IReadOnlyList<Contact> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        IReadOnlyList<VCard> cards;
        try
        {
            cards = Vcf.Parse(text);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException("The vCard could not be read.", ex);
        }

        return cards.Count == 0
            ? throw new FormatException("The text holds no vCard.")
            : cards.Select(Read).ToList();
    }

    /// <summary>The first contact in a vCard file, which is what one resource holds.</summary>
    public static Contact ParseOne(string text) => Parse(text)[0];

    /// <summary>One contact as vCard text.</summary>
    public static string Serialize(Contact contact, VCardVersion version = VCardVersion.V3) => SerializeMany([contact], version);

    /// <summary>Several contacts as one vCard file, which is what an export is.</summary>
    public static string SerializeMany(IReadOnlyList<Contact> contacts, VCardVersion version = VCardVersion.V3)
    {
        ArgumentNullException.ThrowIfNull(contacts);

        var cards = contacts.Select(c => Write(c, version)).ToList();

        // Non-standard properties are opt-in on the way out, and a 3.0 distribution list is made
        // entirely of them: without this a group goes to the server as a card with a name and
        // nobody in it.
        return Vcf.AsString(
            cards,
            version == VCardVersion.V4 ? VCdVersion.V4_0 : VCdVersion.V3_0,
            options: VcfOpts.Default | VcfOpts.WriteNonStandardProperties);
    }

    // ---- vCard → Contact -----------------------------------------------------------------------

    private static Contact Read(VCard card)
    {
        var name = card.NameViews?.FirstOrDefault(n => n is { IsEmpty: false })?.Value;
        var organization = card.Organizations?.FirstOrDefault(o => o is { IsEmpty: false })?.Value;

        var group = card.Kind?.Value == Kind.Group
                    || Extension(card, GroupKindKey)?.Equals("group", StringComparison.OrdinalIgnoreCase) == true;

        return new Contact
        {
            Uid = Uid(card),
            DisplayName = Text(card.DisplayNames),
            FirstName = Part(name?.Given),
            MiddleName = Middle(name),
            // A "?" surname is the placeholder a writer puts in when a 3.0 card has no name to
            // state; it is not somebody's name and is not shown as one.
            LastName = Part(name?.Surnames) is "?" ? string.Empty : Part(name?.Surnames),
            Prefix = Part(name?.Prefixes),
            Suffix = Part(name?.Suffixes),
            NickName = card.NickNames?.FirstOrDefault(n => n is { IsEmpty: false })?.Value?.FirstOrDefault() ?? string.Empty,
            FileAs = Extension(card, "X-MAILBOX-FILEAS") ?? Sorted(card),
            // vCard 3.0 says it with CLASS and 4.0 has no such property, so a card written here
            // says it both ways and one read here believes either.
            IsPrivate = string.Equals(Extension(card, "X-MAILBOX-PRIVATE"), "TRUE", StringComparison.OrdinalIgnoreCase)
                        || card.Access?.Value == FolkerKinzel.VCards.Enums.Access.Private,
            Company = organization?.Name ?? string.Empty,
            Department = organization?.Units is { Count: > 0 } units ? string.Join(", ", units) : string.Empty,
            JobTitle = Text(card.Titles),
            Emails = ReadEmails(card),
            Phones = ReadPhones(card),
            Addresses = ReadAddresses(card),
            Urls = card.Urls?.Where(u => u is { IsEmpty: false }).Select(u => u!.Value!).ToList() ?? [],
            InstantMessaging = card.Messengers?.Where(m => m is { IsEmpty: false }).Select(m => m!.Value!).ToList() ?? [],
            Categories = card.Categories?.Where(c => c is { IsEmpty: false }).SelectMany(c => c!.Value!).Where(v => v is { Length: > 0 }).ToList() ?? [],
            Notes = Text(card.Notes),
            Birthday = ReadDate(card.BirthDayViews),
            Anniversary = ReadDate(card.AnniversaryViews),
            Photo = ReadPhoto(card),
            IsGroup = group,
            Members = group ? ReadMembers(card) : [],
            Links = ReadLinks(card),
            LastModified = card.Updated?.Value ?? DateTimeOffset.UtcNow,
        };
    }

    private static string Middle(Name? name)
    {
        // vCard's ADDITIONAL NAMES is the middle name; the library calls the field Generations in
        // 4.0's own vocabulary and keeps the 2.1/3.0 additional names there.
        var additional = name?.Generations;
        return additional is { Count: > 0 } ? string.Join(" ", additional) : string.Empty;
    }

    private static string Uid(VCard card)
    {
        var id = card.ContactID?.Value;
        if (id is null) return Contact.NewUid();
        if (id.Guid is { } guid) return guid.ToString("D");
        if (id.String is { Length: > 0 } text) return text;
        if (id.Uri is { } uri) return uri.ToString();
        return Contact.NewUid();
    }

    private static string Text(IEnumerable<TextProperty?>? properties)
        => properties?.FirstOrDefault(p => p is { IsEmpty: false })?.Value ?? string.Empty;

    private static string Part(IReadOnlyList<string>? parts)
        => parts is { Count: > 0 } ? string.Join(" ", parts.Where(p => p is { Length: > 0 })) : string.Empty;

    private static string? Extension(VCard card, string key)
        => card.NonStandards?.FirstOrDefault(p => p is { IsEmpty: false }
            && string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>
    /// A SORT-AS or SORT-STRING is somebody's decision about how this contact files, which is
    /// exactly what File As means.
    /// </summary>
    private static string Sorted(VCard card)
    {
        var sortAs = card.NameViews?.FirstOrDefault(n => n is { IsEmpty: false })?.Parameters.SortAs;
        if (sortAs is { Count: > 0 }) return string.Join(", ", sortAs.Where(s => s is { Length: > 0 }));
        return Extension(card, "X-EVOLUTION-FILE-AS") ?? string.Empty;
    }

    private static IReadOnlyList<ContactEmail> ReadEmails(VCard card)
        => card.EMails?
            .Where(e => e is { IsEmpty: false })
            .OrderBy(e => e!.Parameters.Preference)
            .Select(e => new ContactEmail(e!.Value!.Trim()))
            .Where(e => e.Address.Length > 0)
            .ToList() ?? [];

    private static IReadOnlyList<ContactPhone> ReadPhones(VCard card)
        => card.Phones?
            .Where(p => p is { IsEmpty: false })
            .OrderBy(p => p!.Parameters.Preference)
            .Select(p => new ContactPhone(p!.Value!.Trim(), PhoneKindOf(p.Parameters.PhoneType, p.Parameters.PropertyClass)))
            .Where(p => p.Number.Length > 0)
            .ToList() ?? [];

    /// <summary>
    /// The label the reference's card would put on a number, out of the TYPE parameters a vCard
    /// carries — which arrive in every combination, so the order they are tested in is the
    /// answer: a fax at work is a business fax, not a business number.
    /// </summary>
    private static PhoneKind PhoneKindOf(Tel? type, PCl? where)
    {
        var home = where == PCl.Home;
        if (type is { } tel)
        {
            if (tel.HasFlag(Tel.Fax)) return home ? PhoneKind.HomeFax : PhoneKind.BusinessFax;
            if (tel.HasFlag(Tel.Cell)) return PhoneKind.Mobile;
            if (tel.HasFlag(Tel.Pager)) return PhoneKind.Pager;
        }

        return home ? PhoneKind.Home : PhoneKind.Business;
    }

    private static IReadOnlyList<ContactAddress> ReadAddresses(VCard card)
        => card.Addresses?
            .Where(a => a is { IsEmpty: false })
            .Select(a => new ContactAddress
            {
                Kind = a!.Parameters.PropertyClass switch
                {
                    PCl.Home => AddressKind.Home,
                    PCl.Work => AddressKind.Business,
                    _ => AddressKind.Other,
                },
                Street = Street(a.Value),
                City = Part(a.Value.Locality),
                State = Part(a.Value.Region),
                PostalCode = Part(a.Value.PostalCode),
                Country = Part(a.Value.Country),
                PostOfficeBox = Part(a.Value.POBox),
            })
            .Where(a => !a.IsEmpty)
            .ToList() ?? [];

    /// <summary>
    /// The street, from whichever components hold it: 3.0 has one field, 4.0 can state the name
    /// and the number apart, and a card written by one is read by the other.
    /// </summary>
    private static string Street(Address address)
    {
        var plain = Part(address.Street);
        if (plain.Length > 0) return plain;

        var parts = new[] { Part(address.StreetNumber), Part(address.StreetName) }.Where(p => p.Length > 0);
        return string.Join(" ", parts);
    }

    private static DateOnly? ReadDate(IEnumerable<DateAndOrTimeProperty?>? properties)
    {
        var value = properties?.FirstOrDefault(p => p is { IsEmpty: false })?.Value;
        if (value?.DateOnly is { } date) return date;
        if (value?.DateTimeOffset is { } instant) return DateOnly.FromDateTime(instant.Date);
        return null;
    }

    private static ContactPhoto? ReadPhoto(VCard card)
    {
        var photo = card.Photos?.FirstOrDefault(p => p is { IsEmpty: false })?.Value;
        if (photo is null) return null;

        if (photo.Bytes is { Length: > 0 } bytes)
        {
            return new ContactPhoto([.. bytes], photo.MediaType ?? "image/jpeg");
        }

        return photo.Uri is { } uri ? new ContactPhoto(null, photo.MediaType ?? "image/jpeg", uri.ToString()) : null;
    }

    /// <summary>
    /// A group's members, from 4.0's MEMBER or Apple's extension: a <c>urn:uuid:</c> points at
    /// another card, a <c>mailto:</c> stands on its own, and both are kept.
    /// </summary>
    private static IReadOnlyList<GroupMember> ReadMembers(VCard card)
    {
        var members = new List<GroupMember>();

        foreach (var relation in card.Members?.Where(m => m is { IsEmpty: false }) ?? [])
        {
            var id = relation!.Value.ContactID;
            if (id?.Guid is { } guid) Merge(members, new GroupMember(Uid: guid.ToString("D")));
            else if (id?.Uri is { } uri) Merge(members, FromUri(uri.ToString()));
            else if (id?.String is { Length: > 0 } text) Merge(members, FromUri(text));
        }

        foreach (var property in card.NonStandards?.Where(p => p is { IsEmpty: false }
                     && (string.Equals(p.Key, GroupMemberKey, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(p.Key, MemberNameKey, StringComparison.OrdinalIgnoreCase))) ?? [])
        {
            Merge(members, FromUri(property!.Value!));
        }

        return members.Where(m => !m.IsEmpty).ToList();
    }

    /// <summary>
    /// Adds a member, or fills in what a second statement of the same one knows: the standard
    /// MEMBER carries the address and our own property carries the name, and they are one person.
    /// </summary>
    private static void Merge(List<GroupMember> members, GroupMember member)
    {
        if (member.IsEmpty) return;

        var same = members.FindIndex(m =>
            (member.Uid.Length > 0 && m.Uid == member.Uid)
            || (member.Address.Length > 0 && string.Equals(m.Address, member.Address, StringComparison.OrdinalIgnoreCase)));

        if (same < 0)
        {
            members.Add(member);
            return;
        }

        var known = members[same];
        members[same] = known with
        {
            Name = known.Name.Length > 0 ? known.Name : member.Name,
            Address = known.Address.Length > 0 ? known.Address : member.Address,
            Uid = known.Uid.Length > 0 ? known.Uid : member.Uid,
        };
    }

    /// <summary>A member URI as a member: a UID, or an address, or an address with a name on it.</summary>
    internal static GroupMember FromUri(string uri)
    {
        var text = uri.Trim();
        if (text.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) return new GroupMember(Uid: text[9..]);

        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = Uri.UnescapeDataString(text[7..]).Trim();

            // Apple writes mailto:Name <someone@example.com> in a group, which is not a URI at
            // all but is what the file holds.
            var open = rest.LastIndexOf('<');
            var close = rest.LastIndexOf('>');
            if (open >= 0 && close > open)
            {
                return new GroupMember(rest[(open + 1)..close].Trim(), rest[..open].Trim().Trim('"'));
            }

            return new GroupMember(rest);
        }

        return text.Contains('@', StringComparison.Ordinal) ? new GroupMember(text) : new GroupMember(Uid: text);
    }

    // ---- Contact → vCard -----------------------------------------------------------------------

    private static VCard Write(Contact contact, VCardVersion version)
    {
        var builder = VCardBuilder.Create(setContactID: false);

        builder.ContactID.Set(contact.Uid);
        builder.DisplayNames.Add(contact.Named());
        builder.Updated.Set(contact.LastModified);

        if (!contact.IsGroup)
        {
            // A card written with no N at all comes back with "?" for a surname: 3.0 requires the
            // property, so the library invents one. A contact who is a company rather than a
            // person — "3 Hills Catering" — has no name parts, and its own name is the answer.
            var bare = (contact.LastName + contact.FirstName + contact.MiddleName + contact.Prefix + contact.Suffix).Trim().Length == 0;
            var name = NameBuilder.Create()
                .AddSurname(bare ? contact.Named() : contact.LastName)
                .AddGiven(contact.FirstName)
                .AddGeneration(contact.MiddleName)
                .AddPrefix(contact.Prefix)
                .AddSuffix(contact.Suffix)
                .Build();
            builder.NameViews.Add(name);
        }

        if (contact.NickName.Length > 0) builder.NickNames.Add(contact.NickName);
        if (contact.FileAs.Length > 0) builder.NonStandards.Add("X-MAILBOX-FILEAS", contact.FileAs);

        if (contact.IsPrivate)
        {
            builder.Access.Set(FolkerKinzel.VCards.Enums.Access.Private);
            builder.NonStandards.Add("X-MAILBOX-PRIVATE", "TRUE");
        }

        // Written as a urn:uuid so it is a URI and not a name, which is the shape every other id
        // in this codec takes.
        foreach (var link in contact.Links.Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            builder.NonStandards.Add(
                LinkKey,
                link.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase) ? link : "urn:uuid:" + link);
        }

        if (contact.Company.Length > 0 || contact.Department.Length > 0)
        {
            builder.Organizations.Add(contact.Company, contact.Department.Length > 0 ? [contact.Department] : null);
        }

        if (contact.JobTitle.Length > 0) builder.Titles.Add(contact.JobTitle);

        foreach (var email in contact.Emails)
        {
            builder.EMails.Add(email.Address, p => p.EMailType = EMail.SMTP);
        }

        foreach (var phone in contact.Phones)
        {
            builder.Phones.Add(phone.Number, p =>
            {
                p.PhoneType = phone.Kind switch
                {
                    PhoneKind.Mobile => Tel.Cell,
                    PhoneKind.BusinessFax or PhoneKind.HomeFax => Tel.Fax,
                    PhoneKind.Pager => Tel.Pager,
                    _ => Tel.Voice,
                };
                p.PropertyClass = phone.Kind switch
                {
                    PhoneKind.Home or PhoneKind.HomeFax => PCl.Home,
                    PhoneKind.Other or PhoneKind.Pager => null,
                    _ => PCl.Work,
                };
            });
        }

        foreach (var address in contact.Addresses.Where(a => !a.IsEmpty))
        {
            var built = AddressBuilder.Create()
                .AddPOBox(address.PostOfficeBox)
                .AddStreet(address.Street)
                .AddLocality(address.City)
                .AddRegion(address.State)
                .AddPostalCode(address.PostalCode)
                .AddCountry(address.Country)
                .Build();

            builder.Addresses.Add(built, p => p.PropertyClass = address.Kind switch
            {
                AddressKind.Home => PCl.Home,
                AddressKind.Business => PCl.Work,
                _ => null,
            });
        }

        foreach (var url in contact.Urls) builder.Urls.Add(url);
        foreach (var im in contact.InstantMessaging) builder.Messengers.Add(im);
        if (contact.Categories.Count > 0) builder.Categories.Add(contact.Categories);
        if (contact.Notes.Length > 0) builder.Notes.Add(contact.Notes);

        if (contact.Birthday is { } birthday) builder.BirthDayViews.Add(birthday.Year, birthday.Month, birthday.Day);
        if (contact.Anniversary is { } anniversary) builder.AnniversaryViews.Add(anniversary.Year, anniversary.Month, anniversary.Day);

        if (contact.Photo is { IsEmpty: false } photo)
        {
            if (photo.Bytes is { Length: > 0 } bytes) builder.Photos.AddBytes(bytes, photo.MediaType);
            else if (photo.Url is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)) builder.Photos.AddUri(uri);
        }

        if (contact.IsGroup) WriteGroup(builder, contact, version);

        return builder.VCard;
    }

    /// <summary>
    /// A distribution list, in whichever way the version it is being written in states one.
    /// </summary>
    /// <remarks>
    /// 4.0 has KIND and MEMBER. 3.0 has neither, and a group written without Apple's extension
    /// is a card with a name and nothing in it — which is how a distribution list quietly loses
    /// everyone in it on the way to a server that speaks 3.0.
    /// </remarks>
    private static void WriteGroup(VCardBuilder builder, Contact contact, VCardVersion version)
    {
        if (version == VCardVersion.V4)
        {
            builder.Kind.Set(Kind.Group);
            foreach (var member in contact.Members.Where(m => !m.IsEmpty))
            {
                // As a URI, not as a string: handed a string the library takes it for a person's
                // name and writes a whole second vCard to point at. A MEMBER is a URI and nothing
                // else, so a name that belongs to no contact rides in a parameter beside it —
                // System.Uri will not have "mailto:B. Person <b@example.com>" at any escaping.
                builder.Members.Add(new Uri(MemberUri(member), UriKind.Absolute));

                // A MEMBER is a URI and nothing else, so somebody who is not a contact loses
                // their name on the way out — kept beside it under our own key, which other
                // clients ignore and this one reads back. Non-standard *parameters* would have
                // been the neater place, but asking the library for them costs every TYPE
                // parameter on the card: TEL;TYPE=WORK,CELL comes out as a bare TEL.
                if (member.Uid.Length == 0 && member.Name is { Length: > 0 } name)
                {
                    builder.NonStandards.Add(MemberNameKey, $"mailto:{name} <{member.Address}>");
                }
            }

            return;
        }

        builder.NameViews.Add(NameBuilder.Create().AddSurname(contact.Named()).Build());
        builder.NonStandards.Add(GroupKindKey, "group");
        foreach (var member in contact.Members.Where(m => !m.IsEmpty))
        {
            // The X- property's value is text rather than a URI, so a name goes inline here the
            // way Apple's own address book writes one.
            var value = member.Uid.Length == 0 && member.Name.Length > 0
                ? $"mailto:{member.Name} <{member.Address}>"
                : MemberUri(member);
            builder.NonStandards.Add(GroupMemberKey, value);
        }
    }

    /// <summary>Where a member's own name is kept, for somebody who is not a contact.</summary>
    internal const string MemberNameKey = "X-MAILBOX-MEMBER";

    /// <summary>
    /// Where a link to another card for the same person is kept.
    /// </summary>
    /// <remarks>
    /// Our own property rather than RFC 6350's <c>RELATED</c>, and the reason is meaning rather
    /// than convenience: <c>RELATED</c> has a vocabulary of relationships — spouse, colleague,
    /// agent — and no member of it means "this is another card for the same person". Writing a
    /// bare <c>RELATED</c> would say "these two are connected somehow", and **reading** somebody
    /// else's as a same-person link would quietly merge two colleagues into one card. So this is
    /// stated in a property that means only what it says, and nothing else is read as a link.
    /// </remarks>
    internal const string LinkKey = "X-MAILBOX-LINK";

    private static IReadOnlyList<string> ReadLinks(VCard card)
    {
        var links = new List<string>();

        foreach (var property in card.NonStandards?.Where(
                     p => p is { IsEmpty: false }
                          && string.Equals(p.Key, LinkKey, StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            var value = property!.Value!.Trim();
            if (value.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) value = value["urn:uuid:".Length..];
            if (value.Length > 0 && !links.Contains(value, StringComparer.OrdinalIgnoreCase)) links.Add(value);
        }

        return links;
    }

    /// <summary>How a member is written: by UID where there is one, by address where there is not.</summary>
    internal static string MemberUri(GroupMember member)
    {
        if (member.Uid is { Length: > 0 } uid)
        {
            return uid.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase) ? uid : "urn:uuid:" + uid;
        }

        return "mailto:" + member.Address;
    }

    /// <summary>A date as vCard 4.0 writes one, for the places that want the text.</summary>
    internal static string Stamp(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}

/// <summary>Which vCard version to write. 2.1 is read but never written: nothing wants it.</summary>
public enum VCardVersion
{
    V3,
    V4,
}
