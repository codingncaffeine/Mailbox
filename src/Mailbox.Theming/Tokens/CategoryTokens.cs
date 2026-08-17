namespace Mailbox.Theming.Tokens;

/// <summary>
/// Which of the six colour tokens a category's name asks for.
/// </summary>
/// <remarks>
/// A category is a name and a colour token in the store, but the things that draw <em>in</em> a
/// category's colour — a note's square, an entry on the timeline — hold the name alone until
/// Phase 14 makes the categories one set across the modules. Matching on the name is what the
/// reference's own defaults invite: its categories ship called "Blue Category" and "Red
/// Category", so a note carrying one is blue or red without anything else being read.
/// <para>
/// A category that names none of the six — "Invoices" — asks for nothing, and whatever is drawing
/// keeps its own default. Here rather than in either view because two modules want the same
/// answer and a second copy of it would drift.
/// </para>
/// </remarks>
public static class CategoryTokens
{
    private static readonly (string Word, string Token)[] Six =
    [
        ("red", TokenKeys.Category.Red),
        ("orange", TokenKeys.Category.Orange),
        ("yellow", TokenKeys.Category.Yellow),
        ("green", TokenKeys.Category.Green),
        ("blue", TokenKeys.Category.Blue),
        ("purple", TokenKeys.Category.Purple),
    ];

    /// <summary>The token this category's name asks for, or null when it names no colour.</summary>
    public static string? For(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;

        foreach (var (word, token) in Six)
        {
            if (category.Contains(word, StringComparison.CurrentCultureIgnoreCase)) return token;
        }

        return null;
    }

    /// <summary>The token the first category that names one asks for, in the order they are carried.</summary>
    public static string? First(IReadOnlyList<string>? categories)
    {
        if (categories is null) return null;

        foreach (var category in categories)
        {
            if (For(category) is { } token) return token;
        }

        return null;
    }
}
