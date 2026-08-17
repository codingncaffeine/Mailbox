using Avalonia.Media;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Notes;

/// <summary>
/// What colour a note is: the colour of the category on it, or the reference's yellow.
/// </summary>
/// <remarks>
/// A note's colour has never been a property of the note — the reference retired its own five
/// colours in favour of the colour categories, and a note is drawn in the colour of the first one
/// it carries. Which category names which colour is <see cref="CategoryTokens"/>'s answer, shared
/// with the journal's timeline; what is here is only the fallback, which is the module's own.
/// </remarks>
public static class NoteColours
{
    /// <summary>
    /// The colour of the first category that names one, resolved through <paramref name="colour"/>
    /// — which is the view's own token lookup, so nothing here names a colour either.
    /// </summary>
    public static Color For(IReadOnlyList<string> categories, Func<string, Color> colour)
    {
        ArgumentNullException.ThrowIfNull(colour);
        return colour(CategoryTokens.First(categories) ?? TokenKeys.Notes.Default);
    }
}
