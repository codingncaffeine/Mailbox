using System.Text;

namespace Mailbox.Core.Localization;

/// <summary>One translated string: what it says, and what it says about several.</summary>
/// <param name="Forms">
/// The translations by plural form. One entry for a string with no plural; as many as the
/// language has forms for one with.
/// </param>
public sealed record PoEntry(IReadOnlyList<string> Forms)
{
    /// <summary>The form for an index, or the last one written — never nothing.</summary>
    public string Form(int index)
        => Forms.Count == 0 ? string.Empty : Forms[Math.Clamp(index, 0, Forms.Count - 1)];
}

/// <summary>
/// A gettext <c>.po</c> file, read.
/// </summary>
/// <remarks>
/// <b>Why this format rather than one of our own.</b> Everything else here is built in house
/// because building it is how it gets to be right. A translation catalogue is the opposite case:
/// its value is entirely in the ecosystem around it. <c>.po</c> is what Poedit, Weblate,
/// Transifex and every distribution's translation team already speak, what translators already
/// know, and what carries plural forms and disambiguating context as first-class ideas rather
/// than as conventions somebody has to be told. Inventing a format here would mean asking people
/// to learn one to help, which is the surest way to receive no translations at all.
/// <para>
/// The key is the English string itself, which is <c>msgid</c>'s own meaning and the property
/// that makes adoption gradual: an untranslated string, an unadopted surface and a missing
/// catalogue all render exactly what they render today.
/// </para>
/// <para>
/// A subset, deliberately. <c>msgid</c>, <c>msgid_plural</c>, <c>msgstr</c>, <c>msgstr[n]</c>,
/// <c>msgctxt</c>, string continuation and comments — which is every construct a catalogue
/// produced by a translator's tool actually contains. Obsolete entries (<c>#~</c>) are skipped
/// rather than read, being the tool's record of what a string used to be.
/// </para>
/// </remarks>
public static class PoCatalog
{
    /// <summary>
    /// The separator between a context and its string, as gettext itself joins them.
    /// </summary>
    /// <remarks>
    /// U+0004. gettext uses this byte to key a contextual entry, so a context and a plain string
    /// with the same words cannot collide and neither can appear in ordinary text.
    /// </remarks>
    public const char ContextSeparator = '';

    /// <summary>Builds the key an entry is found by: the string, or the context and the string.</summary>
    public static string Key(string? context, string english)
        => string.IsNullOrEmpty(context) ? english : context + ContextSeparator + english;

    /// <summary>
    /// Reads a catalogue. Anything malformed costs its own entry and nothing else.
    /// </summary>
    /// <remarks>
    /// The header is the entry whose <c>msgid</c> is empty; its <c>msgstr</c> is a block of
    /// RFC 822-ish fields, and <c>Plural-Forms</c> is the one that matters. Returned separately
    /// rather than left in the table, because it is not a translation of anything.
    /// </remarks>
    public static (IReadOnlyDictionary<string, PoEntry> Entries, string? Header) Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var entries = new Dictionary<string, PoEntry>(StringComparer.Ordinal);
        string? header = null;

        string? context = null;
        string? id = null;
        var plural = false;
        var forms = new List<string>();

        // Which of the four a continuation line belongs to, since "..." on its own line appends
        // to whichever came last.
        var target = Target.None;
        var index = 0;

        void Flush()
        {
            if (id is null) return;

            if (id.Length == 0 && context is null)
            {
                header = forms.Count > 0 ? forms[0] : null;
            }
            else if (forms.Count > 0 && forms.Any(f => f.Length > 0))
            {
                // An entry every one of whose forms is empty is one nobody has translated yet.
                // Storing it would shadow the English with nothing.
                entries[Key(context, id)] = new PoEntry([.. forms]);
            }

            context = null;
            id = null;
            plural = false;
            forms = [];
            target = Target.None;
            index = 0;
        }

        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();

            // A blank line ends an entry; a comment says nothing about the strings, and an
            // obsolete entry is the tool's memory rather than a translation.
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#~", StringComparison.Ordinal)) Flush();
                continue;
            }

            if (Starts(line, "msgctxt", out var rest))
            {
                Flush();
                context = Unquote(rest);
                target = Target.Context;
            }
            else if (Starts(line, "msgid_plural", out rest))
            {
                plural = true;
                target = Target.Plural;
            }
            else if (Starts(line, "msgid", out rest))
            {
                // A msgid with no msgctxt before it starts a new entry; one after a msgctxt
                // continues the entry that context opened.
                if (target is not Target.Context) Flush();
                id = Unquote(rest);
                target = Target.Id;
            }
            else if (Starts(line, "msgstr", out rest))
            {
                index = 0;

                // msgstr[n] for a plural entry, plain msgstr otherwise.
                if (rest.StartsWith('['))
                {
                    var close = rest.IndexOf(']', StringComparison.Ordinal);
                    if (close > 1 && int.TryParse(rest[1..close], out var n)) index = n;
                    rest = close > 0 ? rest[(close + 1)..].Trim() : rest;
                }

                while (forms.Count <= index) forms.Add(string.Empty);
                forms[index] = Unquote(rest);
                target = Target.Text;
            }
            else if (line.StartsWith('"'))
            {
                // A continuation of whichever line came last.
                var more = Unquote(line);
                switch (target)
                {
                    case Target.Context: context += more; break;
                    case Target.Id when !plural: id += more; break;
                    case Target.Text when forms.Count > index: forms[index] += more; break;
                    default: break;
                }
            }
        }

        Flush();
        return (entries, header);
    }

    private enum Target
    {
        None,
        Context,
        Id,
        Plural,
        Text,
    }

    private static bool Starts(string line, string keyword, out string rest)
    {
        if (line.StartsWith(keyword, StringComparison.Ordinal)
            && line.Length > keyword.Length
            && (char.IsWhiteSpace(line[keyword.Length]) || line[keyword.Length] == '['))
        {
            rest = line[keyword.Length..].Trim();
            return true;
        }

        rest = string.Empty;
        return false;
    }

    /// <summary>
    /// The text inside a quoted PO string, with its escapes undone.
    /// </summary>
    /// <remarks>
    /// The escapes gettext writes, and no more: a backslash before anything else is kept as the
    /// two characters it is, because inventing an interpretation would silently change somebody's
    /// translation.
    /// </remarks>
    private static string Unquote(string quoted)
    {
        var text = quoted.Trim();
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"') return string.Empty;

        var inner = text[1..^1];
        if (!inner.Contains('\\', StringComparison.Ordinal)) return inner;

        var built = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                built.Append(inner[i]);
                continue;
            }

            switch (inner[++i])
            {
                case 'n': built.Append('\n'); break;
                case 't': built.Append('\t'); break;
                case 'r': built.Append('\r'); break;
                case '"': built.Append('"'); break;
                case '\\': built.Append('\\'); break;
                default: built.Append('\\').Append(inner[i]); break;
            }
        }

        return built.ToString();
    }

    /// <summary>Writes a string as a PO file quotes it — the inverse, for the template writer.</summary>
    public static string Quote(string text)
    {
        var built = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': built.Append("\\\""); break;
                case '\\': built.Append("\\\\"); break;
                case '\n': built.Append("\\n"); break;
                case '\t': built.Append("\\t"); break;
                case '\r': break;
                default: built.Append(c); break;
            }
        }

        return built.Append('"').ToString();
    }
}
