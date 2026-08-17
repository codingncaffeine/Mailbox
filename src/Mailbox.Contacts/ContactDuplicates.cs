namespace Mailbox.Contacts;

/// <summary>How sure we are that two cards are the same person.</summary>
public enum DuplicateStrength
{
    /// <summary>An address in common. Two cards sharing one are the same person or a mistake.</summary>
    Certain,

    /// <summary>The same name and something else agreeing — a company, or a number.</summary>
    Likely,

    /// <summary>The same name and nothing else. Two people really are called that sometimes.</summary>
    Possible,
}

/// <summary>One card that may be the same person as another, and why we think so.</summary>
/// <param name="Reason">
/// A phrase for the prompt, so the reader is told what matched rather than being asked to guess —
/// "they share the address a.person@example.com" is answerable and "this looks like a duplicate"
/// is not.
/// </param>
public sealed record DuplicateMatch(ContactRow Row, DuplicateStrength Strength, string Reason);

/// <summary>
/// Whether a card about to be saved is somebody the address book already has.
/// </summary>
/// <remarks>
/// Asked on save, when the reader is still there to answer — a duplicate found by a sweep
/// afterwards is a chore, and one found silently is a decision made on somebody's behalf. The
/// Options page's own switch is what turns it on (`people.duplicates.check`, on by default).
/// <para>
/// It reports rather than merges. Two cards for one person are sometimes deliberate — a personal
/// and a work card for the same colleague — and a matcher confident enough to merge without asking
/// is a matcher confident enough to lose an address.
/// </para>
/// </remarks>
public static class ContactDuplicates
{
    /// <summary>
    /// Everything that might be the same person, strongest first.
    /// </summary>
    /// <param name="candidate">The card about to be saved.</param>
    /// <param name="existing">What the address books already hold.</param>
    /// <param name="ignoreId">
    /// The row being edited, which is not its own duplicate. Null when the card is new.
    /// </param>
    public static IReadOnlyList<DuplicateMatch> Find(
        Contact candidate, IReadOnlyList<ContactRow> existing, long? ignoreId = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);

        var matches = new List<DuplicateMatch>();

        foreach (var row in existing)
        {
            if (row.Id == ignoreId) continue;

            // A card already linked to this one has been looked at and settled: the reader said
            // these are the same person and wanted both kept.
            if (candidate.Links.Contains(row.Contact.Uid, StringComparer.OrdinalIgnoreCase)) continue;
            if (string.Equals(row.Contact.Uid, candidate.Uid, StringComparison.OrdinalIgnoreCase)) continue;

            // A group and a person are never each other, whatever they are called.
            if (row.Contact.IsGroup != candidate.IsGroup) continue;

            if (Match(candidate, row) is { } found) matches.Add(found);
        }

        return [.. matches.OrderBy(m => m.Strength).ThenBy(m => m.Row.Named(), StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>True when anything at all came back, which is what the save path asks.</summary>
    public static bool Any(Contact candidate, IReadOnlyList<ContactRow> existing, long? ignoreId = null)
        => Find(candidate, existing, ignoreId).Count > 0;

    private static DuplicateMatch? Match(Contact candidate, ContactRow row)
    {
        // An address in common settles it. Compared whole and case-insensitively: the local part
        // is technically case-sensitive and no provider on earth treats it that way, and treating
        // it that way here would mean two cards for one person whenever somebody capitalised.
        if (SharedAddress(candidate, row.Contact) is { Length: > 0 } address)
        {
            return new DuplicateMatch(row, DuplicateStrength.Certain, $"they share the address {address}");
        }

        var name = Normalise(candidate.Named());
        if (name.Length == 0 || name != Normalise(row.Named())) return null;

        if (Normalise(candidate.Company) is { Length: > 0 } company
            && company == Normalise(row.Contact.Company))
        {
            return new DuplicateMatch(row, DuplicateStrength.Likely,
                $"they have the same name and both are at {candidate.Company}");
        }

        if (SharedNumber(candidate, row.Contact) is { Length: > 0 } number)
        {
            return new DuplicateMatch(row, DuplicateStrength.Likely,
                $"they have the same name and share the number {number}");
        }

        return new DuplicateMatch(row, DuplicateStrength.Possible, "they have the same name");
    }

    private static string SharedAddress(Contact a, Contact b)
    {
        foreach (var mine in a.Emails)
        {
            if (mine.Address.Length == 0) continue;
            foreach (var theirs in b.Emails)
            {
                if (string.Equals(mine.Address.Trim(), theirs.Address.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return mine.Address.Trim();
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// A number in common, compared by its digits.
    /// </summary>
    /// <remarks>
    /// "+44 20 7946 0958" and "020 7946 0958" are one telephone written two ways, and comparing
    /// the strings would find nothing. Compared on the last nine digits, which is enough to be the
    /// subscriber number nearly everywhere without the country code having to agree — a card
    /// written before somebody moved abroad still matches.
    /// </remarks>
    private static string SharedNumber(Contact a, Contact b)
    {
        var theirs = b.Phones.Select(p => Digits(p.Number)).Where(n => n.Length >= 7).ToList();

        foreach (var mine in a.Phones)
        {
            var digits = Digits(mine.Number);
            if (digits.Length < 7) continue;
            if (theirs.Any(t => Tail(t) == Tail(digits))) return mine.Number.Trim();
        }

        return string.Empty;
    }

    private static string Digits(string text) => new([.. text.Where(char.IsAsciiDigit)]);

    private static string Tail(string digits) => digits.Length <= 9 ? digits : digits[^9..];

    /// <summary>
    /// A name reduced to what two people writing it down would agree on: case, spacing and
    /// punctuation removed, so "de Vries, Anne" and "Anne de Vries" still differ but
    /// "Anne  de Vries" and "Anne de Vries" do not.
    /// </summary>
    private static string Normalise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var kept = text.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c));
        var words = new string([.. kept]).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', words).ToLowerInvariant();
    }
}
