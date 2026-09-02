namespace Mailbox.Contacts.Directory;

/// <summary>How a directory is reached, and how far down it to look.</summary>
public enum DirectoryScope
{
    /// <summary>The base entry and everything under it, which is what a company directory wants.</summary>
    Subtree,

    /// <summary>Only the entries directly under the base — a flat book kept in one container.</summary>
    OneLevel,
}

/// <summary>
/// One directory this application can look people up in: where it is, how to reach it, and
/// which of its attributes mean what.
/// </summary>
/// <remarks>
/// Read-only by design. A company directory is somebody else's record of who works there, and a
/// mail client that wrote to it would be claiming an authority it does not have — so there is no
/// save path, no delete, and nothing that syncs. What it is for is the three places a name is
/// needed and the address book has none: searching, autocomplete while addressing a message, and
/// Select Names.
/// <para>
/// It is a <em>setting</em> rather than a collection in the store. Nothing of the directory's is
/// kept — an entry is fetched, shown, and forgotten — so there is no row for it to be a
/// collection of, and a directory that stopped answering would otherwise leave a permanent empty
/// address book behind it. The password is the exception and does not live here either: it goes
/// to the desktop keyring like every other one.
/// </para>
/// </remarks>
public sealed record LdapDirectory
{
    /// <summary>What the reader called it, and what every picker lists it as.</summary>
    public string Name { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    /// <summary>389 in the clear or with StartTLS, 636 for LDAPS — the two conventions.</summary>
    public int Port { get; init; } = 389;

    /// <summary>
    /// Whether the connection is encrypted, and it is on unless somebody turns it off.
    /// </summary>
    /// <remarks>
    /// A directory search sends the name of everyone being looked up, and a bind sends a
    /// password. Neither belongs in the clear on a network the reader does not own. The port
    /// decides which kind: 636 is LDAPS, wrapped from the first byte, and anything else is
    /// StartTLS, negotiated on the plain port. One tick rather than two, because "encrypted"
    /// is the question somebody setting this up can answer and "which of the two mechanisms"
    /// is not.
    /// </remarks>
    public bool UseTls { get; init; } = true;

    /// <summary>Where in the tree to search from — <c>ou=people,dc=example,dc=org</c>.</summary>
    public string BaseDn { get; init; } = string.Empty;

    /// <summary>
    /// Who to sign in as, or empty to bind anonymously — which is how most read-only
    /// directories are meant to be read.
    /// </summary>
    public string BindDn { get; init; } = string.Empty;

    public DirectoryScope Scope { get; init; } = DirectoryScope.Subtree;

    /// <summary>
    /// How many entries to accept from one search.
    /// </summary>
    /// <remarks>
    /// A limit rather than everything: a university directory holds tens of thousands of people
    /// and a search for "a" matches most of them. The server has its own limit and usually a
    /// lower one; this is the one that keeps a slow server from filling this machine's memory
    /// before its own limit is reached.
    /// </remarks>
    public int MaxResults { get; init; } = 100;

    /// <summary>
    /// How long to wait, in seconds. Short on purpose: this runs while somebody is typing an
    /// address, and a directory that has stopped answering must not be able to hold the
    /// autocomplete open behind it.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 8;

    /// <summary>Whether this directory is searched at all, without having to remove it.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Enough to reach it: everything else has a working default.</summary>
    public bool IsUsable => Host.Trim().Length > 0 && BaseDn.Trim().Length > 0;

    /// <summary>How the keyring is asked for this directory's password.</summary>
    public string PasswordKey => $"{BindDn}@{Host}:{Port}";

    /// <summary>What the Address Books tab writes in its Type column.</summary>
    public string Kind => UseTls ? "LDAP (encrypted)" : "LDAP";

    /// <summary>Where it is, in one line, for a list that has room for one.</summary>
    public string Where()
    {
        var scheme = Port == 636 ? "ldaps" : "ldap";
        return $"{scheme}://{Host}:{Port}/{BaseDn}";
    }
}
