namespace Mailbox.Core.Ribbon;

/// <summary>
/// How a large ribbon button's label breaks into lines — the reference's rule, as read off its
/// classic ribbon.
/// </summary>
/// <remarks>
/// A large button's label is two lines whenever it can be, and one when it cannot: "New Email"
/// is "New" over "Email", "Reply All" is "Reply" over "All", "All Apps" is "All" over "Apps"
/// though it would fit on one line, "Delete" and "Forward" stay whole because there is nowhere
/// to break them. Where there is more than one place to break, the reference takes the one that
/// makes the button narrowest — "Send/Receive All Folders" is "Send/Receive" over "All Folders",
/// not "Send/" over the rest — and a slash is a place to break, kept on the line it ends:
/// "Unread/Read" is "Unread/" over "Read". Never more than two lines, never inside a word. The
/// button is then as wide as its wider line, which is what lets a long label have a wide button
/// instead of a broken word.
/// <para>
/// A label was being wrapped by the text layout inside a fixed maximum width instead, which
/// broke "Signature" into "Signatur" and "e" and put "Send/Receive All Folders" on three lines.
/// </para>
/// </remarks>
public static class LargeButtonLabel
{
    /// <summary>
    /// The one or two lines <paramref name="label"/> is drawn on. <paramref name="width"/>
    /// measures a candidate line, so the balance is by what the text will actually measure;
    /// without it, by character count.
    /// </summary>
    public static IReadOnlyList<string> Lines(string label, Func<string, double>? width = null)
    {
        ArgumentNullException.ThrowIfNull(label);

        var text = label.Trim();
        if (text.Length == 0) return [string.Empty];

        width ??= s => s.Length;

        string[]? best = null;
        var bestWidth = double.PositiveInfinity;

        foreach (var (first, second) in Splits(text))
        {
            var widest = Math.Max(width(first), width(second));
            if (widest < bestWidth)
            {
                bestWidth = widest;
                best = [first, second];
            }
        }

        return best ?? [text];
    }

    /// <summary>
    /// Every way to break the label in two: after each space, which is dropped, and after each
    /// slash, which stays. Both halves are non-empty.
    /// </summary>
    private static IEnumerable<(string First, string Second)> Splits(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                var first = text[..i].TrimEnd();
                var second = text[(i + 1)..].TrimStart();
                if (first.Length > 0 && second.Length > 0) yield return (first, second);
            }
            else if (text[i - 1] == '/' && text[i] != ' ')
            {
                var first = text[..i];
                var second = text[i..].TrimStart();
                if (second.Length > 0) yield return (first, second);
            }
        }
    }
}
