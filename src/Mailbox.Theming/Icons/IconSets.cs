namespace Mailbox.Theming.Icons;

/// <summary>
/// Which icon set is drawing — the theme's choice, carried by the <c>icons.set</c> token and
/// applied when a theme is. Every glyph lookup and the icon font itself route through
/// <see cref="Active"/>, which is what lets a five-line theme swap the whole set without a
/// single command definition knowing.
/// </summary>
public static class IconSets
{
    /// <summary>The default: Fluent UI System Icons, outline artwork.</summary>
    public const string Regular = "fluent-regular";

    /// <summary>The bundled alternative: the same icons' filled artwork.</summary>
    public const string Filled = "fluent-filled";

    public static IReadOnlyList<string> Known => [Regular, Filled];

    public static string Active { get; private set; } = Regular;

    public static bool IsKnown(string id) =>
        string.Equals(id, Regular, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, Filled, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies a set by id. An unknown id keeps the regular set — a theme file naming a set
    /// this build does not carry still loads, its icons simply staying outline.
    /// </summary>
    public static void Apply(string? id)
    {
        Active = string.Equals(id, Filled, StringComparison.OrdinalIgnoreCase) ? Filled : Regular;
    }
}
