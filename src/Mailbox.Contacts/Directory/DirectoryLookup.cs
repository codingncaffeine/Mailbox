namespace Mailbox.Contacts.Directory;

/// <summary>
/// Asks every directory at once and puts the answers together.
/// </summary>
/// <remarks>
/// A machine can have more than one — a company directory and a university's, or a directory and
/// a departmental subtree of it — and everything that looks a person up wants one list rather
/// than a list per server.
/// <para>
/// One directory failing does not take the others down with it. A refusal is carried alongside
/// whatever the rest found, because "the second of your three directories is refusing that
/// password" and "nobody by that name" are different answers and a search that returned neither
/// would leave the reader with an address book that had quietly stopped working.
/// </para>
/// </remarks>
public static class DirectoryLookup
{
    /// <summary>
    /// Everyone matching what was typed, across every directory given.
    /// </summary>
    /// <param name="directories">The directories to ask.</param>
    /// <param name="password">The bind password for one of them, or null for an anonymous bind.</param>
    /// <param name="typed">What the reader typed. Empty asks nobody.</param>
    /// <param name="onlyAddressable">Whether to insist on an e-mail address.</param>
    public static DirectoryResult Search(
        IEnumerable<LdapDirectory> directories,
        Func<LdapDirectory, string?> password,
        string? typed,
        bool onlyAddressable = false)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(password);

        var filter = LdapFilter.ForTyping(typed, onlyAddressable);
        if (filter is null) return new DirectoryResult([]);

        var people = new List<Contact>();

        // By distinguished name, which is unique on one server and long enough to be unique
        // across several: a person in two directories is listed once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refusals = new List<string>();
        var truncated = false;

        foreach (var directory in directories)
        {
            var result = LdapDirectorySearch.Search(directory, filter, password(directory));

            if (!result.Worked)
            {
                // Named, because a machine with three directories needs to know which one.
                refusals.Add($"{directory.Name}: {result.Refusal}");
                continue;
            }

            truncated |= result.Truncated;
            foreach (var person in result.People)
            {
                if (seen.Add(person.Uid)) people.Add(person);
            }
        }

        return new DirectoryResult(people, string.Join("  ", refusals), truncated);
    }
}
