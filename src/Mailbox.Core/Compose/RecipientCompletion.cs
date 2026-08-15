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
        for (var i = caret - 1; i >= 0; i--)
        {
            if (IsSeparator(text[i], commasSeparate))
            {
                start = i + 1;
                break;
            }
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
        var end = caret;
        while (end < text.Length && !IsSeparator(text[end], commasSeparate)) end++;

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
}
