using System.Collections.Frozen;
using Avalonia.Media;

namespace Mailbox.Theming.Fonts;

/// <summary>What happened when a requested font family was resolved.</summary>
/// <param name="Requested">The family the message or theme asked for.</param>
/// <param name="Rendered">The family actually used to draw it.</param>
/// <param name="Quality">How faithful the swap is.</param>
/// <param name="Note">Explanation for the font picker, when there is one worth showing.</param>
public sealed record FontResolution(
    string Requested,
    string Rendered,
    SubstitutionQuality Quality,
    string? Note)
{
    public bool IsSubstituted => Quality != SubstitutionQuality.Exact;

    /// <summary>True when layout will differ from what a Windows reader sees.</summary>
    public bool MayReflow => Quality is SubstitutionQuality.VisualOnly or SubstitutionQuality.None;
}

/// <summary>
/// Maps font families that mail asks for onto families this machine can actually draw.
/// </summary>
/// <remarks>
/// The design decision that makes this correct is the <b>wire/render split</b>. Rendering uses
/// the metric-compatible substitute, so an incoming <c>Calibri</c> message occupies exactly the
/// space the sender intended. Outgoing mail, meanwhile, names the Microsoft font first —
/// <c>font-family: Calibri, Carlito, sans-serif</c> — so a Windows recipient gets real Calibri,
/// a Linux recipient gets Carlito, and because the metrics match, both see the same layout.
/// <para>
/// Real fonts always win. Anyone who has installed the Microsoft core fonts gets the genuine
/// article with no configuration; the substitution table is a fallback chain, not a policy.
/// </para>
/// </remarks>
public sealed class FontResolver
{
    private readonly FrozenSet<string> _installed;
    private readonly Dictionary<string, FontResolution> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FontResolver(IEnumerable<string> installedFamilies)
    {
        _installed = installedFamilies.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a resolver from the fonts Avalonia can see, including the typefaces Mailbox
    /// bundles itself. <see cref="BundledFonts.Register"/> must have run first.
    /// </summary>
    public static FontResolver FromSystem()
        => new(FontManager.Current.SystemFonts.Select(f => f.Name)
            .Concat(BundledFonts.Families));

    public IReadOnlyCollection<string> InstalledFamilies => _installed;

    public bool IsInstalled(string family) => _installed.Contains(family);

    /// <summary>
    /// Resolves a requested family to one that can actually be drawn, preferring the real font,
    /// then the metric-compatible substitute, then its alternates, then the generic family.
    /// </summary>
    public FontResolution Resolve(string requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);

        if (_cache.TryGetValue(requested, out var cached)) return cached;

        var result = ResolveUncached(requested);
        _cache[requested] = result;
        return result;
    }

    private FontResolution ResolveUncached(string requested)
    {
        // The real thing beats every substitute.
        if (_installed.Contains(requested))
        {
            return new FontResolution(requested, requested, SubstitutionQuality.Exact, null);
        }

        var entry = FontSubstitution.Lookup(requested);

        if (entry is null)
        {
            // Not a font we know about — an unusual family in a received message. Nothing
            // sensible to substitute, so let the generic sans handle it.
            return new FontResolution(
                requested,
                FallbackGeneric("sans-serif"),
                SubstitutionQuality.None,
                $"'{requested}' is not installed and has no known substitute.");
        }

        if (entry.Substitute is { } primary)
        {
            if (_installed.Contains(primary))
            {
                return new FontResolution(requested, primary, entry.Quality, entry.Note);
            }

            // Only a genuine metric equivalent may inherit a metric-compatible claim.
            foreach (var equivalent in FontSubstitution.MetricEquivalentsFor(primary))
            {
                if (_installed.Contains(equivalent))
                {
                    return new FontResolution(requested, equivalent, entry.Quality, entry.Note);
                }
            }

            // Anything else is a lookalike, so the claim is downgraded. Standing Liberation
            // Serif in for a missing Caladea would reflow the message; saying so is the point.
            foreach (var alternate in FontSubstitution.VisualAlternatesFor(primary))
            {
                if (_installed.Contains(alternate))
                {
                    return new FontResolution(
                        requested,
                        alternate,
                        SubstitutionQuality.VisualOnly,
                        $"{primary} is not installed; {alternate} substituted at different metrics.");
                }
            }
        }

        var note = entry.Substitute is null
            ? entry.Note
            : $"Neither {requested} nor {entry.Substitute} is installed. " +
              (entry.Note is null ? string.Empty : entry.Note);

        return new FontResolution(
            requested,
            FallbackGeneric(entry.Generic),
            SubstitutionQuality.None,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim());
    }

    /// <summary>
    /// The font stack to write into outgoing HTML: the Microsoft name first so Windows
    /// recipients see the real face, then the metric-compatible substitute for everyone else,
    /// then the generic. Both ends get the same layout because the metrics agree.
    /// </summary>
    public string WireStack(string requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);

        var entry = FontSubstitution.Lookup(requested);
        if (entry is null) return $"{Quote(requested)}, sans-serif";

        return entry.Substitute is { } substitute
            ? $"{Quote(entry.Original)}, {Quote(substitute)}, {entry.Generic}"
            : $"{Quote(entry.Original)}, {entry.Generic}";
    }

    /// <summary>
    /// Every family offered in the compose font picker, in Outlook's own order, each carrying
    /// its resolution so the UI can mark entries that will not match Windows exactly.
    /// </summary>
    public IReadOnlyList<FontResolution> PickerFamilies()
        => FontSubstitution.Table.Select(e => Resolve(e.Original)).ToList();

    /// <summary>
    /// Substitutes expected from distro packages that are missing. Drives the one-time,
    /// non-blocking first-run hint about installing font packages.
    /// </summary>
    public IReadOnlyList<string> MissingExpectedSubstitutes()
        => FontSubstitution.ExpectedFromPackages.Where(f => !_installed.Contains(f)).ToList();

    private string FallbackGeneric(string generic)
    {
        // Avalonia will not resolve a CSS generic name, so pick something concrete that is
        // almost certainly present on a Linux system.
        string[] candidates = generic switch
        {
            "serif" => ["Liberation Serif", "Tinos", "DejaVu Serif", "Noto Serif"],
            "monospace" => ["Liberation Mono", "Cousine", "DejaVu Sans Mono", "Noto Sans Mono"],
            _ => ["Liberation Sans", "Arimo", "DejaVu Sans", "Noto Sans"],
        };

        foreach (var candidate in candidates)
        {
            if (_installed.Contains(candidate)) return candidate;
        }

        // Deliberately does not consult FontManager.Current: that requires an initialised
        // Avalonia application, which would make this whole library untestable without a UI
        // thread. Naming a family the platform may not have is safe — the text stack does its
        // own last-resort fallback below us.
        return candidates[0];
    }

    private static string Quote(string family)
        => family.Contains(' ') ? $"'{family}'" : family;
}
