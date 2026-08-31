using Mailbox.Theming.Files;
using Mailbox.Theming.Palettes;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// The palette picker's engine: the curated schemes load with their attribution, every one of
/// them maps to a readable theme (the curation gate — a scheme that cannot be made readable is
/// dropped from the set, not shipped), and the mapping obeys the same laws as the importer.
/// </summary>
public class PaletteTests
{
    [Fact]
    public void TheCuratedSetLoadsWithAttribution()
    {
        Assert.True(ColourSchemes.Curated.Count >= 12, $"only {ColourSchemes.Curated.Count} schemes loaded");
        foreach (var scheme in ColourSchemes.Curated)
        {
            Assert.False(string.IsNullOrWhiteSpace(scheme.Author), $"{scheme.Id} names no author");
            Assert.True(scheme.Palette.Count >= 16, $"{scheme.Id} has {scheme.Palette.Count} slots");
        }

        Assert.NotNull(ColourSchemes.Find("ocean"));
    }

    [Fact]
    public void EveryCuratedSchemeMapsToACompleteReadableTheme()
    {
        foreach (var scheme in ColourSchemes.Curated)
        {
            var result = PaletteThemes.Map(scheme);
            var resolved = new ThemeLibrary([result.File]).Build(result.File.Id).Resolve();

            // Complete: the coverage gate's own list.
            foreach (var key in TokenKeys.Required)
            {
                Assert.True(resolved.Contains(key), $"{scheme.Id}: missing {key}");
            }

            // Readable: the curation gate. Every pair the audit checks clears the ratio —
            // repaired if the mapper had to, but never shipped failing.
            var findings = ContrastAudit.Check(resolved);
            Assert.True(findings.Count == 0,
                $"{scheme.Id} ships unreadable: {string.Join("; ", findings)}");
        }
    }

    [Fact]
    public void APaletteNeverTouchesContentAndTheDarkOnesKeepItLight()
    {
        foreach (var scheme in ColourSchemes.Curated)
        {
            var result = PaletteThemes.Map(scheme);
            foreach (var token in result.TokensWritten)
            {
                var area = TokenMap.AreaOf(token);
                Assert.False(area?.IsContent ?? false, $"{scheme.Id} wrote content key {token}");
                Assert.False(area?.IsDesktop ?? false, $"{scheme.Id} wrote desktop key {token}");
            }

            // The light-content assertion, per scheme: content is the base's, whatever the
            // scheme thinks a document looks like.
            var resolved = new ThemeLibrary([result.File]).Build(result.File.Id).Resolve();
            var baseResolved = OfficeThemes.Build(result.BaseId).Resolve();
            Assert.Equal(baseResolved.GetString(TokenKeys.Reading.Background),
                resolved.GetString(TokenKeys.Reading.Background));
            Assert.Equal(baseResolved.GetString(TokenKeys.Compose.BodyBackground),
                resolved.GetString(TokenKeys.Compose.BodyBackground));
        }
    }

    [Fact]
    public void OneAccentBuysTheWholeRampAndReadsOnTheRows()
    {
        var nord = ColourSchemes.Find("nord")!;
        var result = PaletteThemes.Map(nord);
        var primary = result.File.Tokens[AccentDerivation.Primary];
        Assert.NotNull(primary);

        // The accent keeps the scheme's hue and is walked to read on the base's own rows —
        // unread text reaches it through a reference, so a pale accent unrepaired would be
        // exactly the finding the curation gate exists to stop.
        var rowGround = OfficeThemes.Build(result.BaseId).Resolve().GetString(TokenKeys.List.RowBackground);
        Assert.True(ContrastAudit.Ratio(primary!, rowGround) >= ContrastAudit.MinimumRatio);
        var wanted = Oklch.Parse(nord.Slot("base0D"))!.Value;
        var got = Oklch.Parse(primary!)!.Value;
        Assert.True(Math.Abs(((got.H - wanted.H + 540) % 360) - 180) < 15, $"hue drifted: {wanted.H:0} → {got.H:0}");

        // The file writes one brand entry; the built theme carries the whole derived ramp,
        // shifted from the base's own relationships.
        var resolved = new ThemeLibrary([result.File]).Build(result.File.Id).Resolve();
        var baseResolved = OfficeThemes.Build(result.BaseId).Resolve();
        Assert.NotEqual(baseResolved.GetString(AccentDerivation.Dark), resolved.GetString(AccentDerivation.Dark));
        Assert.NotEqual(baseResolved.GetString(AccentDerivation.Light), resolved.GetString(AccentDerivation.Light));
    }

    [Fact]
    public void DarkSchemesBaseOnDarkGrayAndLightOnWhiteNeverBlack()
    {
        foreach (var scheme in ColourSchemes.Curated)
        {
            var result = PaletteThemes.Map(scheme);
            Assert.Equal(scheme.Dark ? OfficeThemes.DarkGray : OfficeThemes.White, result.BaseId);
            Assert.NotEqual(OfficeThemes.Black, result.BaseId);
        }
    }

    [Fact]
    public void TheDesktopsSchemeReadsFromItsOwnGrammar()
    {
        var scheme = ColourSchemes.FromKde("""
            [Colors:Window]
            BackgroundNormal=49,54,59
            ForegroundNormal=252,252,252

            [Colors:Selection]
            BackgroundNormal=61,174,233
            """);

        Assert.NotNull(scheme);
        Assert.True(scheme!.Dark);
        Assert.Equal("#31363B", scheme.Slot("base00"));
        Assert.Equal("#FCFCFC", scheme.Slot("base05"));
        Assert.Equal("#3DAEE9", scheme.Slot("base0D"));

        Assert.Null(ColourSchemes.FromKde("not a desktop file"));
    }

    [Fact]
    public void AnImageYieldsItsDominantGroundAndItsMostSaturatedAccent()
    {
        // Mostly deep blue, with a strip of vivid orange: the ground is the blue, the accent
        // the orange.
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 900; i++) pixels.Add((16, 24, 64));
        for (var i = 0; i < 100; i++) pixels.Add((230, 120, 20));

        var scheme = ColourSchemes.FromPixels(pixels);
        Assert.True(scheme.Dark);
        Assert.Equal("#101840", scheme.Slot("base00"));
        Assert.True(scheme.Palette.ContainsKey("base0D"));
        var accent = Oklch.Parse(scheme.Slot("base0D"))!.Value;
        Assert.True(accent.C > 0.04, $"accent {scheme.Slot("base0D")} has no chroma");
        Assert.InRange(accent.H, 20, 90); // orange, not blue

        // No pixels at all still yields a scheme rather than a crash.
        Assert.NotNull(ColourSchemes.FromPixels([]));
    }

    [Fact]
    public void WritingAPaletteIsAnOrdinaryThemeFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mailbox-palette-tests-{Guid.NewGuid():N}");
        var (result, path) = PaletteThemes.Write(ColourSchemes.Find("nord")!, directory);

        Assert.Equal("palette-nord", result.File.Id);
        var text = File.ReadAllText(path);
        Assert.Equal(text, ThemeFileFormat.Write(ThemeFileFormat.Parse(text)));

        // Re-applying the same palette replaces its earlier self — same id, one file.
        PaletteThemes.Write(ColourSchemes.Find("nord")!, directory);
        Assert.Single(Directory.GetFiles(directory, "palette-nord*"));
    }
}
