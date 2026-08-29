namespace Mailbox.Contacts;

/// <summary>
/// Two ways several cards become one person: shown together, and written together.
/// </summary>
/// <remarks>
/// <see cref="Display"/> is the linked-contacts one — the primary card's own fields with every way
/// of reaching them the other cards add, computed for the view and written nowhere, because the
/// cards staying separate is the whole point of a link as against a merge.
/// <see cref="Update"/> is the duplicate prompt's — one card taking another's information and
/// being written back, which is a merge and says so.
/// </remarks>
public static class ContactMerge
{
    /// <summary>One person out of several linked cards, for display only.</summary>
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

    /// <summary>
    /// One card taking another's information: the answer to "update the selected contact with the
    /// new information", which is a merge and not a replacement.
    /// </summary>
    /// <remarks>
    /// The reference compares the fields that hold something in both cards and copies the newer
    /// card's into the older one; a field the newer card says nothing about keeps what the older
    /// one had, and an address the older card was reachable at is kept beside the new one rather
    /// than dropped. Writing the newer card over the older one instead is the shape this had, and
    /// it threw away a birthday, a postal address, a photograph, three telephone numbers, a
    /// category and a note the moment somebody typed a name that already existed and chose to
    /// update rather than to keep both — an answer meaning "these are one person" that lost most
    /// of what was known about them.
    /// <para>
    /// Lists are unioned with the newer card's entries first, because that is what "the new
    /// information" means when there is nothing to compare against: a second address, a second
    /// number, another category. Numbers compare by their digits, as they do everywhere else.
    /// </para>
    /// </remarks>
    /// <param name="existing">The card already in the book, whole — not the list's summary of it.</param>
    /// <param name="incoming">The card just typed, which is the new information.</param>
    public static Contact Update(Contact existing, Contact incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        return existing with
        {
            DisplayName = Pick(existing.DisplayName, incoming.DisplayName),
            FirstName = Pick(existing.FirstName, incoming.FirstName),
            MiddleName = Pick(existing.MiddleName, incoming.MiddleName),
            LastName = Pick(existing.LastName, incoming.LastName),
            Prefix = Pick(existing.Prefix, incoming.Prefix),
            Suffix = Pick(existing.Suffix, incoming.Suffix),
            NickName = Pick(existing.NickName, incoming.NickName),
            FileAs = Pick(existing.FileAs, incoming.FileAs),
            Company = Pick(existing.Company, incoming.Company),
            Department = Pick(existing.Department, incoming.Department),
            JobTitle = Pick(existing.JobTitle, incoming.JobTitle),

            Emails = Union(incoming.Emails, existing.Emails, e => e.Address.Trim().ToLowerInvariant()),
            // By the last nine digits, as the duplicate finder compares them: "+44 20 7946 0000"
            // and "020 7946 0000" are one telephone written two ways, and a merge that kept both
            // would put the same number on the card twice — which is the noise a merge is for.
            Phones = Union(incoming.Phones, existing.Phones, p => Tail(Digits(p.Number))),
            Addresses = Union(
                incoming.Addresses.Where(a => !a.IsEmpty),
                existing.Addresses.Where(a => !a.IsEmpty),
                a => a.OneLine().ToLowerInvariant()),
            Urls = Union(incoming.Urls, existing.Urls, u => u.ToLowerInvariant()),
            InstantMessaging = Union(incoming.InstantMessaging, existing.InstantMessaging, i => i.ToLowerInvariant()),
            Categories = Union(incoming.Categories, existing.Categories, c => c.ToLowerInvariant()),

            Notes = Pick(existing.Notes, incoming.Notes),
            NotesHtml = Pick(existing.NotesHtml, incoming.NotesHtml),
            Birthday = incoming.Birthday ?? existing.Birthday,
            Anniversary = incoming.Anniversary ?? existing.Anniversary,
            Photo = incoming.Photo is { IsEmpty: false } ? incoming.Photo : existing.Photo,
            Members = incoming.Members.Count > 0 ? incoming.Members : existing.Members,

            // A link only one card knows about is a link the other end still names, so dropping
            // one here would leave the pair pointing at each other from one side only.
            Links = Union(incoming.Links, existing.Links, l => l.ToLowerInvariant()),

            // Private is a mark somebody puts on a card, and a card typed from scratch has it off
            // because nobody went near it rather than because they decided the person is not
            // private. Kept where either card carries it.
            IsPrivate = existing.IsPrivate || incoming.IsPrivate,

            // The flag lives beside the card and is this reader's own; a new card has none, and
            // an update is not a reason to forget that somebody meant to ring this person back.
            FollowUpDue = incoming.FollowUpDue ?? existing.FollowUpDue,
            FollowUpComplete = incoming.FollowUpDue is not null
                ? incoming.FollowUpComplete
                : existing.FollowUpComplete,

            LastModified = incoming.LastModified,
        };
    }

    /// <summary>The new card's word where it has one, and the old card's where it has not.</summary>
    private static string Pick(string existing, string incoming)
        => incoming.Trim().Length > 0 ? incoming : existing;

    /// <summary>
    /// Both lists, the newer card's first, with nothing said twice.
    /// </summary>
    /// <remarks>
    /// Compared on a key rather than on the whole record: "+44 20 7946 0000" written as
    /// "020 7946 0000" is one telephone, and an address that differs only in the case of its
    /// local part is one mailbox.
    /// </remarks>
    private static List<T> Union<T>(IEnumerable<T> first, IEnumerable<T> second, Func<T, string> key)
    {
        var kept = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in first.Concat(second))
        {
            var k = key(item);
            if (k.Length == 0 || !seen.Add(k)) continue;
            kept.Add(item);
        }

        return kept;
    }

    private static string Digits(string text) => new([.. text.Where(char.IsAsciiDigit)]);

    /// <summary>Enough of a number to be the subscriber's, without the country code agreeing.</summary>
    private static string Tail(string digits) => digits.Length <= 9 ? digits : digits[^9..];
}
