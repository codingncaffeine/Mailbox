namespace Mailbox.Contacts.Directory;

/// <summary>
/// Turns a directory entry into a contact card.
/// </summary>
/// <remarks>
/// One entry as a dictionary of attribute name to its values, because that is what every LDAP
/// library hands back and it is also what can be written down in a test. The connection is
/// somewhere else entirely: this is where the schema knowledge lives, and schema knowledge is
/// exactly what is worth being able to check without a server.
/// <para>
/// The attributes are the standard ones — RFC 4519's <c>cn</c>, <c>sn</c>, <c>givenName</c>,
/// <c>telephoneNumber</c>, <c>o</c>, <c>ou</c>, <c>title</c>, and RFC 4524's <c>mail</c> — plus
/// the two that are not standard and are everywhere anyway: <c>displayName</c> and <c>mobile</c>.
/// Active Directory speaks all of them, and so does every OpenLDAP schema anyone ships.
/// </para>
/// </remarks>
public static class LdapEntries
{
    /// <summary>Everything asked for, so a search fetches what it needs in one round trip.</summary>
    public static IReadOnlyList<string> Attributes { get; } =
    [
        "cn", "displayName", "sn", "givenName", "mail", "telephoneNumber", "mobile",
        "facsimileTelephoneNumber", "title", "o", "company", "ou", "department", "uid",
        "l", "st", "postalCode", "c", "street", "physicalDeliveryOfficeName",
    ];

    /// <summary>
    /// One entry as a card, or null when there is nothing on it worth showing.
    /// </summary>
    /// <remarks>
    /// The distinguished name is the identity. It is the one thing an entry is guaranteed to have
    /// and the one thing that is unique on that server, so it is what a directory card's UID is —
    /// which also means a card fetched twice is recognisably the same person, and a directory
    /// entry can never be confused with a card in the local book.
    /// </remarks>
    public static Contact? ToContact(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<string>> entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var dn = distinguishedName?.Trim() ?? string.Empty;
        if (dn.Length == 0) return null;

        var display = First(entry, "displayName");
        var common = First(entry, "cn");
        var given = First(entry, "givenName");
        var family = First(entry, "sn");

        // Something has to be readable. An entry with no name and no address is a row in somebody
        // else's database, not a person, and putting it in a picker would offer the reader a
        // blank line to select.
        var addresses = All(entry, "mail");
        if (display.Length == 0 && common.Length == 0 && given.Length == 0 && family.Length == 0
            && addresses.Count == 0)
        {
            return null;
        }

        var phones = new List<ContactPhone>();
        foreach (var number in All(entry, "telephoneNumber")) phones.Add(new ContactPhone(number));
        foreach (var number in All(entry, "mobile")) phones.Add(new ContactPhone(number, PhoneKind.Mobile));
        foreach (var number in All(entry, "facsimileTelephoneNumber")) phones.Add(new ContactPhone(number, PhoneKind.BusinessFax));

        var street = First(entry, "street");
        var city = First(entry, "l");
        var addressed = street.Length > 0 || city.Length > 0
            || First(entry, "postalCode").Length > 0 || First(entry, "c").Length > 0;

        return new Contact
        {
            Uid = dn,
            DisplayName = display.Length > 0 ? display : common,
            FirstName = given,
            LastName = family,

            // Two spellings of the same field: o is the standard one, company is what Active
            // Directory writes. Whichever the server has.
            Company = First(entry, "o") is { Length: > 0 } o ? o : First(entry, "company"),
            Department = First(entry, "department") is { Length: > 0 } d ? d : First(entry, "ou"),
            JobTitle = First(entry, "title"),
            Emails = [.. addresses.Select(a => new ContactEmail(a))],
            Phones = phones,
            Addresses = addressed
                ?
                [
                    new ContactAddress
                    {
                        Street = street,
                        City = city,
                        State = First(entry, "st"),
                        PostalCode = First(entry, "postalCode"),
                        Country = First(entry, "c"),
                    },
                ]
                : [],

            // What the People list would sort by. Written here rather than left to be worked out,
            // because a directory's cn is often already "Surname, Given" and recomputing it from
            // the parts would rewrite what the directory says people are called.
            FileAs = common.Length > 0 ? common : display,
        };
    }

    /// <summary>The first value of an attribute, or nothing — attribute names are case-blind.</summary>
    private static string First(IReadOnlyDictionary<string, IReadOnlyList<string>> entry, string attribute)
        => All(entry, attribute).FirstOrDefault() ?? string.Empty;

    private static IReadOnlyList<string> All(IReadOnlyDictionary<string, IReadOnlyList<string>> entry, string attribute)
    {
        foreach (var (key, values) in entry)
        {
            if (!string.Equals(key, attribute, StringComparison.OrdinalIgnoreCase)) continue;
            return [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())];
        }

        return [];
    }
}
