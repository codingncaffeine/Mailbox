using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Mailbox.Theming.Fonts;

/// <summary>
/// Typefaces Mailbox ships itself, because no distribution reliably packages them.
/// Everything else — Carlito, Caladea, Liberation, URW Base35 — is a package dependency.
/// </summary>
public static class BundledFonts
{
    private const string Root = "avares://Mailbox.Theming/Assets/Fonts/";

    /// <summary>
    /// Selawik, Microsoft's OFL-licensed metric-compatible substitute for Segoe UI.
    /// </summary>
    /// <remarks>
    /// Ships Light, Semilight, Regular, Semibold and Bold, so the weight range Outlook's chrome
    /// uses is covered. Metric compatibility means chrome laid out against Segoe UI measurements
    /// does not shift when Selawik stands in for it.
    /// </remarks>
    public const string SelawikFamily = "Selawik";

    /// <summary>
    /// Family names the bundled resources provide. The resolver treats these as installed even
    /// though fontconfig has never heard of them, because <see cref="Register"/> has made them
    /// resolvable by plain family name.
    /// </summary>
    public static IReadOnlyList<string> Families { get; } = [SelawikFamily];

    /// <summary>
    /// Registers the bundled typefaces with Avalonia so they resolve by plain family name —
    /// <c>Selawik</c> rather than an <c>avares://</c> URI. Call once at startup, before the
    /// first <see cref="FontResolver"/> is built.
    /// </summary>
    /// <remarks>
    /// Avalonia discovers every face in the directory and groups them into families by the name
    /// embedded in each file, so all five Selawik weights arrive as a single family.
    /// </remarks>
    public static void Register()
        => FontManager.Current.AddFontCollection(new EmbeddedFontCollection(
            new Uri("fonts:Mailbox", UriKind.Absolute),
            new Uri(Root, UriKind.Absolute)));
}
