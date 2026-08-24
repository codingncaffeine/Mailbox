namespace Mailbox.Contacts;

/// <summary>
/// One person out of several linked cards, for display only: the primary card's own fields,
/// with every way of reaching them the other cards add. Nothing here is written anywhere —
/// the cards stay separate, which is the point of a link as against a merge.
/// </summary>
public static class ContactMerge
{
    public static Contact Display(Contact primary, IReadOnlyList<Contact> linked)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(linked);

        if (linked.Count == 0) return primary;

        var emails = primary.Emails.ToList();
        var phones = primary.Phones.ToList();
        var addresses = primary.Addresses.ToList();
        var urls = primary.Urls.ToList();
        var im = primary.InstantMessaging.ToList();

        foreach (var other in linked)
        {
            emails.AddRange(other.Emails.Where(e => e.Address.Length > 0
                && !emails.Any(x => string.Equals(x.Address.Trim(), e.Address.Trim(), StringComparison.OrdinalIgnoreCase))));

            // Numbers compare by their digits: "+44 20 7946 0958" and "020 7946 0958" are one
            // telephone written two ways, exactly as the duplicate finder reads them.
            phones.AddRange(other.Phones.Where(p => Digits(p.Number).Length > 0
                && !phones.Any(x => Digits(x.Number) == Digits(p.Number))));

            addresses.AddRange(other.Addresses.Where(a => !a.IsEmpty
                && !addresses.Any(x => string.Equals(x.OneLine(), a.OneLine(), StringComparison.OrdinalIgnoreCase))));

            urls.AddRange(other.Urls.Where(u => !urls.Contains(u, StringComparer.OrdinalIgnoreCase)));
            im.AddRange(other.InstantMessaging.Where(i => !im.Contains(i, StringComparer.OrdinalIgnoreCase)));
        }

        return primary with
        {
            Emails = emails,
            Phones = phones,
            Addresses = addresses,
            Urls = urls,
            InstantMessaging = im,
        };
    }

    private static string Digits(string text) => new([.. text.Where(char.IsAsciiDigit)]);
}
