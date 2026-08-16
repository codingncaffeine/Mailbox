using System.Globalization;

namespace Mailbox.Contacts;

/// <summary>What a phone number is for, as the reference's contact card labels them.</summary>
public enum PhoneKind
{
    Business,
    Home,
    Mobile,
    BusinessFax,
    HomeFax,
    Pager,
    Other,
}

/// <summary>Which of a person's addresses this is.</summary>
public enum AddressKind
{
    Business,
    Home,
    Other,
}

/// <summary>One of a contact's e-mail addresses, in the order the card lists them.</summary>
/// <param name="Address">The address itself.</param>
/// <param name="Name">What to show instead of the address, where the vCard says so.</param>
public sealed record ContactEmail(string Address, string Name = "")
{
    public override string ToString() => Name.Length > 0 ? $"{Name} <{Address}>" : Address;
}

public sealed record ContactPhone(string Number, PhoneKind Kind = PhoneKind.Business);

/// <summary>A postal address, kept in parts because the card shows it in parts.</summary>
public sealed record ContactAddress
{
    public AddressKind Kind { get; init; } = AddressKind.Business;
    public string Street { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string PostOfficeBox { get; init; } = string.Empty;

    public bool IsEmpty => (Street + City + State + PostalCode + Country + PostOfficeBox).Trim().Length == 0;

    /// <summary>The address on the card: the parts that are there, in postal order.</summary>
    public string OneLine()
    {
        var parts = new[] { PostOfficeBox, Street, City, State, PostalCode, Country }
            .Where(p => p is { Length: > 0 });
        return string.Join(", ", parts);
    }
}

/// <summary>A contact's picture: the bytes and what they are, or an address to fetch it from.</summary>
public sealed record ContactPhoto(byte[]? Bytes, string MediaType = "image/jpeg", string? Url = null)
{
    public bool IsEmpty => (Bytes is null || Bytes.Length == 0) && string.IsNullOrEmpty(Url);

    // A record compares arrays by reference, which for a photograph means two identical pictures
    // are never equal and a round trip never compares — the same trap the calendar's lists had.
    public bool Equals(ContactPhoto? other)
        => other is not null
           && MediaType == other.MediaType
           && Url == other.Url
           && ((Bytes is null && other.Bytes is null) || (Bytes is not null && other.Bytes is not null && Bytes.SequenceEqual(other.Bytes)));

    public override int GetHashCode() => HashCode.Combine(MediaType, Url, Bytes?.Length ?? 0);
}

/// <summary>
/// Somebody in a distribution list: the contact it points at where it says so, the address
/// where that is all it has.
/// </summary>
/// <remarks>
/// Both, because both are what real files hold: vCard 4.0's MEMBER is a URI that may be a
/// <c>urn:uuid:</c> pointing at another card or a <c>mailto:</c> standing on its own, and Apple's
/// 3.0 extension does the same. A list that could only hold one of them would lose half of every
/// group it read.
/// </remarks>
public sealed record GroupMember(string Address = "", string Name = "", string Uid = "")
{
    public bool IsEmpty => Address.Length == 0 && Uid.Length == 0;
}

/// <summary>
/// A person or a group as the application thinks of one: one record whichever vCard version it
/// arrived in, and the one every view, the card and the autocomplete read.
/// </summary>
public sealed record Contact
{
    public required string Uid { get; init; }

    /// <summary>FN — what the card is headed with.</summary>
    public string DisplayName { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;
    public string MiddleName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Prefix { get; init; } = string.Empty;
    public string Suffix { get; init; } = string.Empty;
    public string NickName { get; init; } = string.Empty;

    /// <summary>
    /// How the list files this contact — the reference's own File As, which is what a contact
    /// list sorts by and what its index letters come from.
    /// </summary>
    public string FileAs { get; init; } = string.Empty;

    public string Company { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;

    public IReadOnlyList<ContactEmail> Emails { get; init; } = [];
    public IReadOnlyList<ContactPhone> Phones { get; init; } = [];
    public IReadOnlyList<ContactAddress> Addresses { get; init; } = [];
    public IReadOnlyList<string> Urls { get; init; } = [];
    public IReadOnlyList<string> InstantMessaging { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];

    public string Notes { get; init; } = string.Empty;
    public DateOnly? Birthday { get; init; }
    public DateOnly? Anniversary { get; init; }
    public ContactPhoto? Photo { get; init; }

    /// <summary>KIND:group — a distribution list rather than a person.</summary>
    public bool IsGroup { get; init; }

    public IReadOnlyList<GroupMember> Members { get; init; } = [];

    public DateTimeOffset LastModified { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The first address, which is the one a message goes to unless another is chosen.</summary>
    public string PrimaryEmail => Emails.Count > 0 ? Emails[0].Address : string.Empty;

    /// <summary>A fresh UID, as RFC 6350 wants one: unique across the world, opaque.</summary>
    public static string NewUid() => Guid.NewGuid().ToString("D") + "@mailbox";

    /// <summary>
    /// What the card is headed with when the vCard says nothing: the name, then the company,
    /// then the address — never nothing, because a row with no text in it cannot be picked out
    /// of a list.
    /// </summary>
    public string Named()
    {
        if (DisplayName.Length > 0) return DisplayName;
        var name = string.Join(" ", new[] { FirstName, MiddleName, LastName }.Where(p => p.Length > 0));
        if (name.Length > 0) return name;
        if (Company.Length > 0) return Company;
        if (PrimaryEmail.Length > 0) return PrimaryEmail;
        return "(no name)";
    }

    /// <summary>
    /// How this contact files, given the order the People page asks for.
    /// </summary>
    /// <remarks>
    /// The stored File As wins where there is one — it is a decision somebody made, and the
    /// reference keeps it through a re-file. A group files under its own name, having no surname
    /// to put first.
    /// </remarks>
    public string FiledAs(FileAsOrder order = FileAsOrder.LastFirst)
    {
        if (FileAs.Length > 0) return FileAs;
        if (IsGroup) return Named();

        var last = LastName.Trim();
        var first = string.Join(" ", new[] { FirstName, MiddleName }.Where(p => p.Trim().Length > 0)).Trim();

        return order switch
        {
            FileAsOrder.FirstLast => Named(),
            FileAsOrder.Company => Company.Length > 0 ? Company : Named(),
            FileAsOrder.LastFirstCompany when last.Length > 0 && Company.Length > 0
                => $"{Join(last, first)} ({Company})",
            _ => last.Length > 0 ? Join(last, first) : Named(),
        };

        static string Join(string last, string first) => first.Length > 0 ? $"{last}, {first}" : last;
    }

    /// <summary>
    /// The letter a contact sits under in the index down the side of the list. Anything that
    /// does not start with a letter goes under <c>123</c>, as the reference's index has it.
    /// </summary>
    public char IndexLetter(FileAsOrder order = FileAsOrder.LastFirst)
    {
        var filed = FiledAs(order).TrimStart();
        if (filed.Length == 0) return '#';
        var first = char.ToUpperInvariant(filed[0]);
        return char.IsLetter(first) ? first : '#';
    }

    // Records compare collections by reference, so a contact read back out of its own text would
    // never equal the one that wrote it — the same trap CalendarEvent has.
    public bool Equals(Contact? other)
        => other is not null
           && Uid == other.Uid && DisplayName == other.DisplayName
           && FirstName == other.FirstName && MiddleName == other.MiddleName && LastName == other.LastName
           && Prefix == other.Prefix && Suffix == other.Suffix && NickName == other.NickName
           && FileAs == other.FileAs && Company == other.Company && Department == other.Department
           && JobTitle == other.JobTitle && Notes == other.Notes
           && Birthday == other.Birthday && Anniversary == other.Anniversary
           && IsGroup == other.IsGroup && Equals(Photo, other.Photo)
           && Emails.SequenceEqual(other.Emails) && Phones.SequenceEqual(other.Phones)
           && Addresses.SequenceEqual(other.Addresses) && Members.SequenceEqual(other.Members)
           && Urls.SequenceEqual(other.Urls, StringComparer.Ordinal)
           && InstantMessaging.SequenceEqual(other.InstantMessaging, StringComparer.Ordinal)
           && Categories.SequenceEqual(other.Categories, StringComparer.Ordinal)
           && LastModified == other.LastModified;

    public override int GetHashCode() => HashCode.Combine(Uid, DisplayName, LastName, Company, IsGroup, LastModified);

    public override string ToString() => Named();
}

/// <summary>The File As orders the People page offers, in its own order.</summary>
public enum FileAsOrder
{
    LastFirst,
    FirstLast,
    Company,
    LastFirstCompany,
}

/// <summary>Reading a File As order out of the number the Options page stores.</summary>
public static class FileAsOrders
{
    public static FileAsOrder FromIndex(int index) => index switch
    {
        1 => FileAsOrder.FirstLast,
        2 => FileAsOrder.Company,
        3 => FileAsOrder.LastFirstCompany,
        _ => FileAsOrder.LastFirst,
    };

    public static string Describe(FileAsOrder order) => order switch
    {
        FileAsOrder.FirstLast => "First Last",
        FileAsOrder.Company => "Company",
        FileAsOrder.LastFirstCompany => "Last, First (Company)",
        _ => "Last, First",
    };

    /// <summary>A date as a vCard states it, for the fields that are dates rather than instants.</summary>
    internal static string Stamp(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
