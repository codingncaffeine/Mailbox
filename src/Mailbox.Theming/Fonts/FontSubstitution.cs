namespace Mailbox.Theming.Fonts;

/// <summary>How faithfully a substitute stands in for the font that was asked for.</summary>
public enum SubstitutionQuality
{
    /// <summary>The real font is installed. Nothing is being substituted.</summary>
    Exact,

    /// <summary>
    /// Identical advance widths and line spacing. Text occupies the same space and breaks in
    /// the same places, so a received message lays out exactly as the sender saw it.
    /// </summary>
    MetricCompatible,

    /// <summary>
    /// Similar design, different metrics. The message will reflow relative to what a Windows
    /// reader sees. Better than nothing; flagged in the font picker so the user knows.
    /// </summary>
    VisualOnly,

    /// <summary>Nothing close is available; falls back to the generic family.</summary>
    None,
}

/// <summary>One row of the substitution table.</summary>
/// <param name="Original">The Microsoft or Adobe font name as it appears in mail and in the picker.</param>
/// <param name="Substitute">The free face rendered in its place when the original is absent.</param>
/// <param name="Quality">How faithful that swap is.</param>
/// <param name="Generic">CSS generic family, used as the last resort in an outgoing font stack.</param>
/// <param name="Note">Why, when the answer is interesting.</param>
public sealed record FontSubstitute(
    string Original,
    string? Substitute,
    SubstitutionQuality Quality,
    string Generic,
    string? Note = null);

/// <summary>
/// The metric-compatible substitution table.
/// </summary>
/// <remarks>
/// Two problems get conflated here and only one is about the UI. The UI font is Segoe UI,
/// substituted by Selawik. The <em>content</em> fonts are what mail is written in, and they
/// matter more: an incoming message specifying <c>font-family: Calibri</c> has to lay out
/// correctly whether or not Calibri exists on this machine. Metric compatibility — identical
/// advance widths and line spacing — is what makes that work. A merely similar-looking face
/// reflows the message.
/// <para>
/// Entries marked <see cref="SubstitutionQuality.VisualOnly"/> are deliberately not claimed as
/// metric-compatible even where folklore says otherwise. DejaVu Sans is the notable case: it is
/// widely described as metric-compatible with Verdana and is not — a line of Verdana measures
/// wider. Claiming it would silently break layout in received mail.
/// </para>
/// </remarks>
public static class FontSubstitution
{
    private const string Sans = "sans-serif";
    private const string Serif = "serif";
    private const string Mono = "monospace";
    private const string Cursive = "cursive";
    private const string Fantasy = "fantasy";

    public static IReadOnlyList<FontSubstitute> Table { get; } =
    [
        // --- UI -------------------------------------------------------------------------
        new("Segoe UI", "Selawik", SubstitutionQuality.MetricCompatible, Sans,
            "Microsoft's own OFL substitute. Regular and Bold only; Segoe UI's Light, " +
            "Semilight and Semibold have no counterpart."),

        // --- Metric-compatible, verified ------------------------------------------------
        new("Calibri", "Carlito", SubstitutionQuality.MetricCompatible, Sans),
        new("Cambria", "Caladea", SubstitutionQuality.MetricCompatible, Serif),
        new("Arial", "Liberation Sans", SubstitutionQuality.MetricCompatible, Sans,
            "Arimo is the equivalent Croscore face if Liberation is absent."),
        new("Helvetica", "Nimbus Sans", SubstitutionQuality.MetricCompatible, Sans),
        new("Times New Roman", "Liberation Serif", SubstitutionQuality.MetricCompatible, Serif,
            "Tinos is the equivalent Croscore face."),
        new("Times", "Nimbus Roman", SubstitutionQuality.MetricCompatible, Serif),
        new("Courier New", "Liberation Mono", SubstitutionQuality.MetricCompatible, Mono,
            "Cousine is the equivalent Croscore face."),
        new("Courier", "Nimbus Mono PS", SubstitutionQuality.MetricCompatible, Mono),
        new("Arial Narrow", "Liberation Sans Narrow", SubstitutionQuality.MetricCompatible, Sans),
        new("Georgia", "Gelasio", SubstitutionQuality.MetricCompatible, Serif),
        new("Comic Sans MS", "Comic Relief", SubstitutionQuality.MetricCompatible, Cursive),
        new("Palatino Linotype", "P052", SubstitutionQuality.MetricCompatible, Serif),
        new("Book Antiqua", "P052", SubstitutionQuality.MetricCompatible, Serif),
        new("Century Schoolbook", "C059", SubstitutionQuality.MetricCompatible, Serif),
        new("Bookman Old Style", "URW Bookman", SubstitutionQuality.MetricCompatible, Serif),
        new("Century Gothic", "URW Gothic", SubstitutionQuality.MetricCompatible, Sans),
        new("Symbol", "Standard Symbols PS", SubstitutionQuality.MetricCompatible, Serif),
        new("Zapf Dingbats", "D050000L", SubstitutionQuality.MetricCompatible, Fantasy),

        // --- Visual only. Layout will differ from what a Windows reader sees. -----------
        new("Aptos", "Inter", SubstitutionQuality.VisualOnly, Sans,
            "The current Microsoft 365 default. No metric-compatible clone exists — too new."),
        new("Verdana", "DejaVu Sans", SubstitutionQuality.VisualOnly, Sans,
            "Commonly but wrongly described as metric-compatible. Verdana measures wider."),
        new("Tahoma", "DejaVu Sans", SubstitutionQuality.VisualOnly, Sans,
            "Wine ships a metric-targeted replacement; licensing and availability unverified."),
        new("Trebuchet MS", "Fira Sans", SubstitutionQuality.VisualOnly, Sans),
        new("Consolas", "Cascadia Code", SubstitutionQuality.VisualOnly, Mono,
            "DMCA Sans Serif claims matching metrics but is obscure and unmaintained."),
        new("Candara", "Carlito", SubstitutionQuality.VisualOnly, Sans, "ClearType Collection."),
        new("Corbel", "Carlito", SubstitutionQuality.VisualOnly, Sans, "ClearType Collection."),
        new("Constantia", "Caladea", SubstitutionQuality.VisualOnly, Serif, "ClearType Collection."),
        new("Impact", "Anton", SubstitutionQuality.VisualOnly, Sans),
        new("Arial Black", "Liberation Sans", SubstitutionQuality.VisualOnly, Sans,
            "Rendered bold; no black weight available."),
        new("Segoe UI Variable", "Selawik", SubstitutionQuality.VisualOnly, Sans,
            "Windows 11 UI font. Irrelevant while cloning classic Outlook."),
        new("Bierstadt", "Inter", SubstitutionQuality.VisualOnly, Sans, "Microsoft commission."),
        new("Grandview", "Inter", SubstitutionQuality.VisualOnly, Sans, "Microsoft commission."),
        new("Seaford", "Source Sans 3", SubstitutionQuality.VisualOnly, Sans, "Microsoft commission."),
        new("Skeena", "Source Sans 3", SubstitutionQuality.VisualOnly, Sans, "Microsoft commission."),
        new("Tenorite", "Inter", SubstitutionQuality.VisualOnly, Sans, "Microsoft commission."),

        // --- No substitute at all -------------------------------------------------------
        new("Wingdings", null, SubstitutionQuality.None, Fantasy,
            "Symbol font. No free equivalent, and cloning the glyph set is legally fraught."),
        new("Webdings", null, SubstitutionQuality.None, Fantasy, "As Wingdings."),
        new("Marlett", null, SubstitutionQuality.None, Fantasy, "Windows UI glyphs."),
    ];

    private static readonly Dictionary<string, FontSubstitute> ByOriginal =
        Table.ToDictionary(s => s.Original, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Faces that are metrically interchangeable with one another — the same designs shipped
    /// under different licences. Substituting within a class preserves metric compatibility.
    /// </summary>
    /// <remarks>
    /// This table is deliberately tiny, and must stay that way. "Same generic family" is not
    /// the same as "same metrics": Liberation Serif matches Times New Roman, not Cambria, so
    /// standing it in for a missing Caladea would silently reflow the message while claiming
    /// it hadn't. Only genuine equivalence classes belong here.
    /// </remarks>
    private static readonly Dictionary<string, string[]> MetricEquivalents =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Croscore and Liberation 2.x are the same Steve Matteson designs.
            ["Liberation Sans"] = ["Arimo"],
            ["Arimo"] = ["Liberation Sans"],
            ["Liberation Serif"] = ["Tinos"],
            ["Tinos"] = ["Liberation Serif"],
            ["Liberation Mono"] = ["Cousine"],
            ["Cousine"] = ["Liberation Mono"],
        };

    /// <summary>
    /// Best-effort stand-ins used only when the entry is already visual-only, so no metric
    /// claim is at stake. Choosing one never upgrades the reported quality.
    /// </summary>
    private static readonly Dictionary<string, string[]> VisualAlternates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Inter"] = ["Carlito", "Liberation Sans", "DejaVu Sans"],
            ["Source Sans 3"] = ["Carlito", "Liberation Sans", "DejaVu Sans"],
            ["Fira Sans"] = ["Carlito", "Liberation Sans", "DejaVu Sans"],
            ["Cascadia Code"] = ["Liberation Mono", "DejaVu Sans Mono"],
            ["Anton"] = ["Liberation Sans", "DejaVu Sans"],
            ["DejaVu Sans"] = ["Liberation Sans", "Arimo"],
            ["Selawik"] = ["Carlito", "Liberation Sans"],
            ["Carlito"] = ["Liberation Sans", "Arimo"],
            ["Caladea"] = ["Liberation Serif", "Tinos"],
        };

    public static FontSubstitute? Lookup(string family)
        => family is null ? null : ByOriginal.GetValueOrDefault(family);

    /// <summary>Faces interchangeable with <paramref name="substitute"/> at identical metrics.</summary>
    public static IReadOnlyList<string> MetricEquivalentsFor(string substitute)
        => MetricEquivalents.TryGetValue(substitute, out var alts) ? alts : [];

    /// <summary>Faces that merely look similar. Never preserves a metric-compatible claim.</summary>
    public static IReadOnlyList<string> VisualAlternatesFor(string substitute)
        => VisualAlternates.TryGetValue(substitute, out var alts) ? alts : [];

    /// <summary>Fonts Mailbox ships itself, because no distro reliably packages them.</summary>
    public static IReadOnlyList<string> Bundled { get; } = ["Selawik", "Gelasio", "Comic Relief"];

    /// <summary>
    /// Fonts expected from distro packages rather than bundled — smaller artifacts,
    /// distro-managed updates, no duplicate faces on disk.
    /// </summary>
    public static IReadOnlyList<string> ExpectedFromPackages { get; } =
    [
        "Carlito", "Caladea",
        "Liberation Sans", "Liberation Serif", "Liberation Mono", "Liberation Sans Narrow",
        "Nimbus Sans", "Nimbus Roman", "Nimbus Mono PS", "P052", "C059",
        "URW Bookman", "URW Gothic", "Standard Symbols PS", "D050000L",
        "DejaVu Sans",
    ];

    /// <summary>
    /// Microsoft faces that cannot be redistributed under any package. Detected and preferred
    /// when the user has installed them via <c>ttf-mscorefonts-installer</c> or an AUR
    /// equivalent; never shipped.
    /// </summary>
    public static IReadOnlyList<string> NonRedistributable { get; } =
    [
        "Segoe UI", "Segoe UI Variable", "Aptos", "Calibri", "Cambria", "Candara", "Corbel",
        "Constantia", "Consolas", "Arial", "Arial Black", "Times New Roman", "Courier New",
        "Georgia", "Verdana", "Tahoma", "Trebuchet MS", "Comic Sans MS", "Impact",
        "Andale Mono", "Webdings", "Wingdings",
    ];
}
