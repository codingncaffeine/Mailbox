using System.Text;

namespace Mailbox.Contacts.Directory;

/// <summary>
/// Turns what somebody typed into an LDAP search filter.
/// </summary>
/// <remarks>
/// The escaping is the point. A filter is a little language and the words being put into it come
/// from a text box, so <c>*</c>, <c>(</c>, <c>)</c> and <c>\</c> all mean something there —
/// a name with a bracket in it would otherwise build a filter the server rejects, and one with a
/// star in it would silently widen the search. RFC 4515 §3 says what to do: each of those, and
/// the null byte, is written as a backslash and two hex digits. Everything else, accents and
/// non-Latin scripts included, goes through untouched, because the value travels as UTF-8 and
/// escaping it byte by byte would only make it unreadable in a log.
/// <para>
/// Kept apart from the connection so it can be checked with no directory to talk to — which is
/// most of what can go wrong here, and the half that would otherwise need a server on the machine
/// running the tests.
/// </para>
/// </remarks>
public static class LdapFilter
{
    /// <summary>The attributes a typed name is matched against, in the order they are tried.</summary>
    /// <remarks>
    /// The four the reference's own directory search uses, and the four every schema in practice
    /// has: the common name, the display name, the surname, and the address itself — because
    /// somebody who has been given an address and wants the person behind it types the address.
    /// <c>uid</c> follows for the directories where a login name is what colleagues know each
    /// other by.
    /// </remarks>
    public static IReadOnlyList<string> SearchedAttributes { get; } =
        ["cn", "displayName", "sn", "givenName", "mail", "uid"];

    /// <summary>
    /// A filter matching people whose name or address begins with what was typed.
    /// </summary>
    /// <remarks>
    /// Begins-with rather than contains: it is what an index can answer, and a directory big
    /// enough to need searching is one where a leading wildcard means a full scan on somebody
    /// else's server. The one exception is a query that already carries a star, which is somebody
    /// writing their own pattern and is passed through as written — with everything else in it
    /// still escaped.
    /// </remarks>
    /// <param name="typed">What the reader typed. Empty asks for nobody, not everybody.</param>
    /// <param name="onlyAddressable">
    /// Whether to insist on an address. True for addressing a message, where an entry nothing can
    /// be sent to is noise; false for a search somebody is reading themselves.
    /// </param>
    public static string? ForTyping(string? typed, bool onlyAddressable = false)
    {
        var text = typed?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;

        var wildcards = text.Contains('*', StringComparison.Ordinal);
        var value = wildcards ? EscapeKeepingStars(text) : Escape(text) + "*";

        var any = new StringBuilder("(|");
        foreach (var attribute in SearchedAttributes) any.Append('(').Append(attribute).Append('=').Append(value).Append(')');
        any.Append(')');

        // A person, not a printer or a room: a directory holds every kind of object and only one
        // of them belongs in an address book. Both classes, because inetOrgPerson is the one in
        // practice and organizationalPerson is the one a directory that predates it uses.
        var person = "(|(objectClass=inetOrgPerson)(objectClass=organizationalPerson)(objectClass=person))";

        return onlyAddressable
            ? $"(&{person}(mail=*){any})"
            : $"(&{person}{any})";
    }

    /// <summary>Everybody with an address, for the "show me the book" case.</summary>
    public static string Everyone()
        => "(&(|(objectClass=inetOrgPerson)(objectClass=organizationalPerson)(objectClass=person))(mail=*))";

    /// <summary>One exact address, which is what checking a typed recipient asks.</summary>
    public static string ForAddress(string address) => $"(mail={Escape(address?.Trim() ?? string.Empty)})";

    /// <summary>RFC 4515 §3: the four characters that mean something, and the null byte.</summary>
    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var built = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': built.Append("\\5c"); break;
                case '*': built.Append("\\2a"); break;
                case '(': built.Append("\\28"); break;
                case ')': built.Append("\\29"); break;
                case '\0': built.Append("\\00"); break;
                default: built.Append(c); break;
            }
        }

        return built.ToString();
    }

    /// <summary>The same, for a query somebody wrote stars into themselves.</summary>
    private static string EscapeKeepingStars(string value)
    {
        var built = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': built.Append("\\5c"); break;
                case '(': built.Append("\\28"); break;
                case ')': built.Append("\\29"); break;
                case '\0': built.Append("\\00"); break;
                default: built.Append(c); break;
            }
        }

        return built.ToString();
    }
}
