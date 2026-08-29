namespace Mailbox.Core.Compose;

/// <summary>
/// The text of a recipient line as it is being typed: which entry the caret is in, and how to
/// swap that entry for a chosen name.
/// </summary>
/// <remarks>
/// Pure string arithmetic, kept out of the window so it can be tested without one. The line
/// is a list of entries separated by semicolons — and by commas when the Options page says
/// commas separate, which is why the separator set is a parameter rather than a constant.
/// </remarks>
public static class RecipientCompletion
{
    /// <summary>
    /// The entry the caret sits in: the text between the separator before the caret and the
    /// caret itself, with leading blanks skipped, and where in the line it starts.
    /// </summary>
    /// <remarks>
    /// Only what is <em>before</em> the caret counts. Somebody who clicks back into the middle
    /// of an address and types is completing what they have typed so far, and the tail of the
    /// old entry is what the replacement is about to remove.
    /// </remarks>
    public static (int Start, string Text) CurrentEntry(string? line, int caret, bool commasSeparate)
    {
        var text = line ?? string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);

        var start = 0;
        foreach (var boundary in SeparatorsIn(text, commasSeparate))
        {
            if (boundary >= caret) break;
            start = boundary + 1;
        }

        while (start < caret && char.IsWhiteSpace(text[start])) start++;

        return (start, text[start..caret]);
    }

    /// <summary>
    /// True when the entry is worth offering completions for: something has been typed, and it
    /// is not already a finished <c>Name &lt;address&gt;</c>, which is what a chosen entry
    /// looks like the moment after it is chosen.
    /// </summary>
    public static bool WantsSuggestions(string entry)
        => entry.Length > 0 && !(entry.Contains('<') && entry.EndsWith('>'));

    /// <summary>
    /// Replaces the entry the caret is in with a chosen recipient, closing it with the
    /// separator and a space so the next one can be typed straight away.
    /// </summary>
    /// <returns>The new line, and where the caret goes — after the separator.</returns>
    /// <remarks>
    /// What follows the caret is kept: an entry the caret was inside is completed rather than
    /// the rest of the line being thrown away. If the remainder already begins with a
    /// separator, the closing one is not doubled.
    /// </remarks>
    public static (string Text, int Caret) Replace(
        string? line, int caret, string chosen, bool commasSeparate)
    {
        var text = line ?? string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        var (start, _) = CurrentEntry(text, caret, commasSeparate);

        // The tail of the entry the caret was inside goes with the head, so a caret in the
        // middle of "ali|ce@" replaces the whole of it. The tail ends at the next separator.
        var end = text.Length;
        foreach (var boundary in SeparatorsIn(text, commasSeparate))
        {
            if (boundary < caret) continue;
            end = boundary;
            break;
        }

        // Either nothing, or the rest of the line from its separator on.
        var remainder = text[end..].TrimStart();
        var head = text[..start] + chosen;

        if (remainder.Length == 0)
        {
            var closed = head + "; ";
            return (closed, closed.Length);
        }

        // Keep the line's own separator, and put the caret after it and its space, which is
        // where typing the next entry starts.
        var rest = remainder[..1] + " " + remainder[1..].TrimStart();
        return (head + rest, head.Length + 2);
    }

    private static bool IsSeparator(char c, bool commasSeparate) => c == ';' || (commasSeparate && c == ',');

    /// <summary>
    /// Splits a recipient line into entries: semicolons always separate, commas when asked —
    /// and neither inside quotes or angle brackets, where <c>"Person, A." &lt;a@example.com&gt;</c>
    /// is one recipient however it is punctuated. The compose window and this completion both
    /// read the line through here, so the popup and the wire agree about where an entry ends.
    /// </summary>
    public static IEnumerable<string> SplitEntries(string? line, bool commasSeparate)
    {
        var text = line ?? string.Empty;
        var start = 0;

        foreach (var boundary in SeparatorsIn(text, commasSeparate))
        {
            var entry = text[start..boundary].Trim();
            if (entry.Length > 0) yield return entry;
            start = boundary + 1;
        }

        var last = text[start..].Trim();
        if (last.Length > 0) yield return last;
    }

    /// <summary>The separator positions that really separate — outside quotes and angles.</summary>
    private static IEnumerable<int> SeparatorsIn(string text, bool commasSeparate)
    {
        var quoted = false;
        var angled = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '<')
            {
                angled = true;
            }
            else if (!quoted && c == '>')
            {
                angled = false;
            }
            else if (!quoted && !angled && IsSeparator(c, commasSeparate))
            {
                yield return i;
            }
        }
    }
}

/// <summary>
/// One entry the Auto-Complete List offers, whichever list it came from: the addresses that have
/// been written to before, and the address book.
/// </summary>
/// <remarks>
/// A type of its own rather than the store's nickname record, because the two sources answer
/// differently. A remembered address can be forgotten and a contact cannot — the ✕ takes an entry
/// out of a cache, and taking somebody out of the address book is not what pressing it means. And
/// a distribution list has no one address at all: what goes on the line is everybody in it, which
/// is why what is inserted is carried separately from what is shown.
/// </remarks>
/// <param name="Key">What the entry dedupes by — an address, or a group's own id.</param>
/// <param name="Insert">What goes on the recipient line when it is chosen.</param>
/// <param name="Detail">What the row says on its right: "Contact", "3 members", or nothing.</param>
public sealed record RecipientSuggestion(
    string Key,
    string DisplayName,
    string Address,
    string Insert,
    string Detail = "",
    int Weight = 0,
    DateTimeOffset LastUsed = default,
    bool CanForget = true);
