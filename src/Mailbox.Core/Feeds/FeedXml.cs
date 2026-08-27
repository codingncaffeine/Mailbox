using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Mailbox.Core.Feeds;

/// <summary>
/// Getting a document out of what a publisher actually served.
/// </summary>
/// <remarks>
/// Feeds are XML in the way that web pages are HTML: the specification is clear and the wild is
/// not. A strict parse is tried first and is what nearly every feed takes; what follows is for
/// the rest, and it is worth the code because the failures are not exotic. An undeclared
/// <c>&amp;nbsp;</c> is the single most common one — it is an HTML entity, not an XML one, and a
/// feed carrying a single one of them parses to nothing at all.
/// <para>
/// The repair is deliberately narrow. It fixes what is unambiguous — an entity nobody declared, a
/// bare ampersand, a character XML does not allow, a byte-order mark in the middle of the text —
/// and refuses everything else, so a document that is not a feed still fails rather than being
/// coerced into an empty one.
/// </para>
/// <para>
/// <b>CDATA is copied verbatim.</b> An RSS description is nearly always a CDATA section holding
/// HTML, and inside one an ampersand is already literal text. Repairing there would turn a
/// publisher's <c>&amp;nbsp;</c> into a visible "&amp;#160;" in the reading pane — a repair that
/// breaks the common case to fix the rare one. The same goes for comments and processing
/// instructions.
/// </para>
/// </remarks>
public static class FeedXml
{
    /// <summary>The five entities XML declares itself, which are left exactly as they are.</summary>
    private static readonly HashSet<string> Builtin =
        new(StringComparer.Ordinal) { "amp", "lt", "gt", "quot", "apos" };

    /// <summary>Reads the text as XML, repairing it if a strict parse will not have it.</summary>
    /// <exception cref="FormatException">The text is not XML that can be repaired into XML.</exception>
    public static XDocument Load(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = Trim(text);
        if (trimmed.Length == 0) throw new FormatException("The text is empty.");

        try
        {
            return Read(trimmed);
        }
        catch (XmlException first)
        {
            try
            {
                return Read(Repair(trimmed));
            }
            catch (XmlException)
            {
                // The first failure is the one worth reporting: it says what was wrong with what
                // the publisher served, where the second says what was wrong with our repair.
                throw new FormatException($"The text is not a feed: {first.Message}", first);
            }
        }
    }

    /// <summary>
    /// The reader every parse goes through.
    /// </summary>
    /// <remarks>
    /// <c>DtdProcessing.Ignore</c> with no resolver is the safe half of two problems at once: a
    /// feed carrying a DOCTYPE parses rather than throwing, and nothing this reads can be talked
    /// into fetching an external entity — which is the XXE hole every feed reader has had at
    /// least once. <c>CheckCharacters</c> is off because the scrub below has already dealt with
    /// the characters, and leaving it on would refuse the document a second time for a reason
    /// that has been fixed.
    /// </remarks>
    private static XDocument Read(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CheckCharacters = false,
            IgnoreWhitespace = false,
        };

        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// Drops anything before the first tag: a byte-order mark left in the text, a blank line, or
    /// the stray whitespace a template engine put in front of the declaration.
    /// </summary>
    private static string Trim(string text)
    {
        var start = text.IndexOf('<');
        return start <= 0 ? text.Trim() : text[start..].TrimEnd();
    }

    /// <summary>
    /// The narrow repair: legal characters, declared entities, escaped ampersands — outside CDATA,
    /// comments and processing instructions, which are copied through untouched.
    /// </summary>
    private static string Repair(string text)
    {
        var output = new StringBuilder(text.Length + 64);
        var at = 0;

        while (at < text.Length)
        {
            // A section that must survive verbatim. Its end is found rather than assumed: an
            // unterminated one runs to the end of the document, which is what a browser does too.
            var verbatim = OpensVerbatim(text, at);
            if (verbatim is { } opener)
            {
                var end = text.IndexOf(opener.Close, at + opener.Open.Length, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + opener.Close.Length;
                output.Append(text, at, stop - at);
                at = stop;
                continue;
            }

            var c = text[at];
            if (c == '&')
            {
                at += Entity(text, at, output);
                continue;
            }

            if (IsLegal(c)) output.Append(c);
            at++;
        }

        return output.ToString();
    }

    private readonly record struct Verbatim(string Open, string Close);

    private static Verbatim? OpensVerbatim(string text, int at)
    {
        if (text[at] != '<') return null;

        foreach (var section in (Verbatim[])
                 [
                     new("<![CDATA[", "]]>"),
                     new("<!--", "-->"),
                     new("<?", "?>"),
                 ])
        {
            if (string.CompareOrdinal(text, at, section.Open, 0, section.Open.Length) == 0) return section;
        }

        return null;
    }

    /// <summary>
    /// Writes whatever the ampersand at <paramref name="at"/> should have been, and says how many
    /// characters of the input it accounted for.
    /// </summary>
    /// <remarks>
    /// Three outcomes. A reference XML already understands — the five built-ins and any numeric
    /// one — is copied. A reference HTML understands and XML does not is rewritten as the numeric
    /// reference for the same character, which every parser takes and which means the same thing.
    /// Anything else was never a reference at all: a bare ampersand in a title or a query string,
    /// which is escaped so the document parses and the ampersand survives as an ampersand.
    /// </remarks>
    private static int Entity(string text, int at, StringBuilder output)
    {
        var semicolon = text.IndexOf(';', at + 1);
        var name = semicolon > at + 1 && semicolon - at <= 34 ? text[(at + 1)..semicolon] : string.Empty;

        if (name.Length > 0 && (Builtin.Contains(name) || IsNumeric(name)))
        {
            output.Append(text, at, semicolon - at + 1);
            return semicolon - at + 1;
        }

        if (name.Length > 0 && IsName(name) && Decode(name) is { } decoded)
        {
            foreach (var rune in decoded.EnumerateRunes())
            {
                output.Append("&#").Append(rune.Value).Append(';');
            }

            return semicolon - at + 1;
        }

        output.Append("&amp;");
        return 1;
    }

    /// <summary>The character an HTML entity name stands for, or null when it is not one.</summary>
    private static string? Decode(string name)
    {
        var reference = $"&{name};";
        var decoded = WebUtility.HtmlDecode(reference);
        return decoded == reference ? null : decoded;
    }

    private static bool IsNumeric(string name)
        => name.Length > 1 && name[0] == '#'
           && (name[1] is 'x' or 'X'
               ? name.Length > 2 && name[2..].All(Uri.IsHexDigit)
               : name[1..].All(char.IsAsciiDigit));

    private static bool IsName(string name)
        => char.IsAsciiLetter(name[0]) && name.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// True for a character XML 1.0 allows. The control characters below space, apart from tab,
    /// newline and carriage return, are the ones publishers leak into a document from a database
    /// column, and a single one of them refuses the whole feed.
    /// </summary>
    private static bool IsLegal(char c)
        => c is not ((< ' ' and not ('\t' or '\n' or '\r')) or '\uFFFE' or '\uFFFF');

    // ---- Namespace-agnostic reading ----------------------------------------------------------

    /// <summary>
    /// Feeds in the wild put their elements in namespaces the specification does not mention, so
    /// everything here matches on the local name. What the namespace <em>is</em> still matters in
    /// one place — telling one publisher's extension from another's — and that is asked for
    /// separately by <see cref="Prefixed"/>.
    /// </summary>
    public static string Name(XElement element) => element.Name.LocalName;

    public static XElement? Child(XElement? element, string name)
        => Children(element, name).FirstOrDefault();

    public static IEnumerable<XElement> Children(XElement? element, string name)
        => element?.Elements().Where(e => Is(e, name)) ?? [];

    public static bool Is(XElement element, string name)
        => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Elements of a named extension module, anywhere under <paramref name="element"/>, matched
    /// by a fragment of their namespace.
    /// </summary>
    /// <remarks>
    /// The namespace matters here where it does not elsewhere, and it is matched loosely for the
    /// same reason: Media RSS is published under at least four spellings of its URI, and the one
    /// thing they share is the word in the middle. It cannot be matched on the local name alone
    /// like everything else, because <c>media:content</c> and Atom's own <c>content</c> are the
    /// same local name and mean entirely different things.
    /// <para>
    /// Descendants rather than children: Media RSS gathers alternatives inside a
    /// <c>media:group</c>, and an entry's picture is as likely to be in one as beside it.
    /// </para>
    /// </remarks>
    public static IEnumerable<XElement> InModule(XElement? element, string name, string namespaceFragment)
        => element?.Descendants()
               .Where(e => Is(e, name)
                           && e.Name.NamespaceName.Contains(namespaceFragment, StringComparison.OrdinalIgnoreCase))
           ?? [];

    public static string Attribute(XElement? element, string name)
        => element?.Attributes()
               .FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value
               .Trim()
           ?? string.Empty;

    public static string Text(XElement? element) => element?.Value.Trim() ?? string.Empty;
}
