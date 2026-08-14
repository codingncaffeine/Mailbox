using Mailbox.Theming.Fonts;

namespace Mailbox.Tests;

public class FontSubstitutionTableTests
{
    [Fact]
    public void TableHasNoDuplicateOriginals()
    {
        var duplicates = FontSubstitution.Table
            .GroupBy(e => e.Original, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicated: {string.Join(", ", duplicates)}");
    }

    [Theory]
    [InlineData("Segoe UI", "Selawik")]
    [InlineData("Calibri", "Carlito")]
    [InlineData("Cambria", "Caladea")]
    [InlineData("Georgia", "Gelasio")]
    [InlineData("Comic Sans MS", "Comic Relief")]
    [InlineData("Arial", "Liberation Sans")]
    [InlineData("Times New Roman", "Liberation Serif")]
    [InlineData("Courier New", "Liberation Mono")]
    public void VerifiedPairsAreMarkedMetricCompatible(string original, string substitute)
    {
        var entry = FontSubstitution.Lookup(original);
        Assert.NotNull(entry);
        Assert.Equal(substitute, entry!.Substitute);
        Assert.Equal(SubstitutionQuality.MetricCompatible, entry.Quality);
    }

    /// <summary>
    /// DejaVu Sans is widely described as metric-compatible with Verdana and is not — a line
    /// of Verdana measures wider. Claiming it would silently reflow received mail, so the
    /// table must keep calling it visual-only.
    /// </summary>
    [Fact]
    public void DejaVuIsNotClaimedAsMetricCompatibleWithVerdana()
    {
        var verdana = FontSubstitution.Lookup("Verdana");
        Assert.NotNull(verdana);
        Assert.Equal("DejaVu Sans", verdana!.Substitute);
        Assert.Equal(SubstitutionQuality.VisualOnly, verdana.Quality);
        Assert.Contains("metric", verdana.Note!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Aptos is the current Office default and has no clone. Do not pretend otherwise.</summary>
    [Fact]
    public void AptosIsHonestlyMarkedAsUnsubstituted()
    {
        var aptos = FontSubstitution.Lookup("Aptos");
        Assert.NotNull(aptos);
        Assert.Equal(SubstitutionQuality.VisualOnly, aptos!.Quality);
    }

    [Theory]
    [InlineData("Wingdings")]
    [InlineData("Webdings")]
    public void SymbolFontsHaveNoSubstitute(string family)
    {
        var entry = FontSubstitution.Lookup(family);
        Assert.NotNull(entry);
        Assert.Null(entry!.Substitute);
        Assert.Equal(SubstitutionQuality.None, entry.Quality);
    }

    [Fact]
    public void MicrosoftFacesAreNeverListedAsBundled()
    {
        foreach (var bundled in FontSubstitution.Bundled)
        {
            Assert.DoesNotContain(bundled, FontSubstitution.NonRedistributable);
        }
    }

    [Fact]
    public void BundledAndPackagedSetsDoNotOverlap()
    {
        foreach (var bundled in FontSubstitution.Bundled)
        {
            Assert.DoesNotContain(bundled, FontSubstitution.ExpectedFromPackages);
        }
    }

    [Fact]
    public void EveryEntryDeclaresAGenericFallback()
        => Assert.All(FontSubstitution.Table,
            e => Assert.False(string.IsNullOrWhiteSpace(e.Generic)));
}

public class FontResolverTests
{
    private static FontResolver With(params string[] installed) => new(installed);

    [Fact]
    public void RealFontWinsOverSubstitute()
    {
        var resolver = With("Calibri", "Carlito");
        var result = resolver.Resolve("Calibri");

        Assert.Equal("Calibri", result.Rendered);
        Assert.Equal(SubstitutionQuality.Exact, result.Quality);
        Assert.False(result.IsSubstituted);
    }

    [Fact]
    public void FallsBackToTheMetricCompatibleSubstitute()
    {
        var resolver = With("Carlito", "Liberation Sans");
        var result = resolver.Resolve("Calibri");

        Assert.Equal("Carlito", result.Rendered);
        Assert.Equal(SubstitutionQuality.MetricCompatible, result.Quality);
        Assert.True(result.IsSubstituted);
        Assert.False(result.MayReflow);
    }

    [Fact]
    public void FallsBackToAnAlternateWhenThePrimarySubstituteIsAbsent()
    {
        // Arimo is the Croscore equivalent of Liberation Sans.
        var resolver = With("Arimo");
        Assert.Equal("Arimo", resolver.Resolve("Arial").Rendered);
    }

    /// <summary>
    /// Regression: Liberation Serif is metric-compatible with Times New Roman, not Cambria.
    /// Standing it in for a missing Caladea must never inherit Caladea's metric claim, or the
    /// message reflows while the UI insists it hasn't.
    /// </summary>
    [Fact]
    public void LookalikeFallbackDowngradesTheMetricClaim()
    {
        var resolver = With("Liberation Serif");
        var result = resolver.Resolve("Cambria");

        Assert.Equal("Liberation Serif", result.Rendered);
        Assert.Equal(SubstitutionQuality.VisualOnly, result.Quality);
        Assert.True(result.MayReflow);
    }

    [Fact]
    public void FallsBackToTheGenericFamilyWhenNothingIsClose()
    {
        var resolver = With("Liberation Sans");
        var result = resolver.Resolve("Zapf Dingbats");

        Assert.Equal(SubstitutionQuality.None, result.Quality);
        Assert.True(result.MayReflow);
    }

    /// <summary>A true equivalence class keeps the claim: Arimo really is Liberation Sans.</summary>
    [Fact]
    public void MetricEquivalentPreservesTheClaim()
    {
        var result = With("Arimo").Resolve("Arial");

        Assert.Equal("Arimo", result.Rendered);
        Assert.Equal(SubstitutionQuality.MetricCompatible, result.Quality);
        Assert.False(result.MayReflow);
    }

    [Fact]
    public void UnknownFamilyResolvesToSansAndSaysSo()
    {
        var resolver = With("Liberation Sans");
        var result = resolver.Resolve("Nonexistent Face");

        Assert.Equal("Liberation Sans", result.Rendered);
        Assert.Equal(SubstitutionQuality.None, result.Quality);
        Assert.Contains("Nonexistent Face", result.Note!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wire/render split: outgoing HTML names the Microsoft font first so a Windows
    /// recipient sees the real face, then the metric-compatible substitute for everyone else.
    /// Because the metrics agree, both ends get the same layout.
    /// </summary>
    [Fact]
    public void WireStackNamesTheMicrosoftFontFirst()
    {
        var resolver = With("Carlito");
        Assert.Equal("Calibri, Carlito, sans-serif", resolver.WireStack("Calibri"));
    }

    [Fact]
    public void WireStackQuotesMultiWordFamilies()
    {
        var resolver = With("Liberation Serif");
        Assert.Equal("'Times New Roman', 'Liberation Serif', serif",
            resolver.WireStack("Times New Roman"));
    }

    [Fact]
    public void WireStackIsIndependentOfWhatIsInstalled()
    {
        // Whether or not the substitute exists locally, the recipient's stack is the same.
        Assert.Equal(
            With().WireStack("Calibri"),
            With("Calibri", "Carlito").WireStack("Calibri"));
    }

    [Fact]
    public void WireStackOmitsTheSubstituteWhenThereIsNone()
        => Assert.Equal("Wingdings, fantasy", With().WireStack("Wingdings"));

    [Fact]
    public void PickerListsEveryKnownFamily()
    {
        var resolver = With("Liberation Sans");
        Assert.Equal(FontSubstitution.Table.Count, resolver.PickerFamilies().Count);
    }

    [Fact]
    public void ReportsMissingPackagedSubstitutes()
    {
        var resolver = With("Carlito");
        var missing = resolver.MissingExpectedSubstitutes();

        Assert.DoesNotContain("Carlito", missing);
        Assert.Contains("Caladea", missing);
    }

    [Fact]
    public void ResolutionIsCached()
    {
        var resolver = With("Carlito");
        Assert.Same(resolver.Resolve("Calibri").Rendered, resolver.Resolve("Calibri").Rendered);
    }
}
