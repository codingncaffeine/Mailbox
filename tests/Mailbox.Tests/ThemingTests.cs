using Mailbox.Theming;
using Mailbox.Theming.Fonts;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

public class TokenSetTests
{
    [Fact]
    public void ExpandsReferences()
    {
        var tokens = new TokenSet();
        tokens.Set("palette.blue", "#0F6CBD");
        tokens.Set("accent.rest", "{palette.blue}");
        tokens.Set("ribbon.tab.selected", "{accent.rest}");

        var resolved = tokens.Resolve();
        Assert.Equal("#0F6CBD", resolved.GetString("ribbon.tab.selected"));
    }

    [Fact]
    public void DetectsReferenceCycles()
    {
        var tokens = new TokenSet();
        tokens.Set("a", "{b}");
        tokens.Set("b", "{a}");

        var ex = Assert.Throws<ThemeResolutionException>(() => tokens.Resolve());
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportsDanglingReferences()
    {
        var tokens = new TokenSet();
        tokens.Set("accent.rest", "{palette.missing}");

        var ex = Assert.Throws<ThemeResolutionException>(() => tokens.Resolve());
        Assert.Contains("palette.missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayIsLastWins()
    {
        var basis = new TokenSet();
        basis.Set("accent.rest", "#111111");
        basis.Set("text.primary", "#222222");

        var overrides = new TokenSet();
        overrides.Set("accent.rest", "#FF0000");

        var merged = basis.OverlaidWith(overrides).Resolve();
        Assert.Equal("#FF0000", merged.GetString("accent.rest"));
        Assert.Equal("#222222", merged.GetString("text.primary"));
    }

    /// <summary>
    /// The five-line theme. Overriding one primitive must restyle everything downstream —
    /// this is what lets a theme author ignore the layer system entirely.
    /// </summary>
    [Fact]
    public void OverridingOnePrimitiveCascadesThroughEveryLayer()
    {
        var basis = new TokenSet();
        basis.Set("palette.brand.primary", "#0F6CBD");
        basis.Set("accent.rest", "{palette.brand.primary}");
        basis.Set("list.row.unread.bar", "{accent.rest}");
        basis.Set("nav.unreadcount", "{accent.rest}");

        var overrides = new TokenSet();
        overrides.Set("palette.brand.primary", "#B4009E");

        var resolved = basis.OverlaidWith(overrides).Resolve();
        Assert.Equal("#B4009E", resolved.GetString("list.row.unread.bar"));
        Assert.Equal("#B4009E", resolved.GetString("nav.unreadcount"));
    }

    [Theory]
    [InlineData("palette.blue.60", TokenLayer.Primitive)]
    [InlineData("type.ui.size", TokenLayer.Primitive)]
    [InlineData("surface.ground", TokenLayer.Semantic)]
    [InlineData("accent.rest", TokenLayer.Semantic)]
    [InlineData("ribbon.tab.selected", TokenLayer.Component)]
    [InlineData("list.row.unread.bar", TokenLayer.Component)]
    public void InfersLayerFromKey(string key, TokenLayer expected)
        => Assert.Equal(expected, TokenLayerExtensions.InferLayer(key));
}

public class OfficeThemeTests
{
    public static TheoryData<string> AllThemes()
    {
        var data = new TheoryData<string>();
        foreach (var id in OfficeThemes.All) data.Add(id);
        return data;
    }

    /// <summary>
    /// The coverage gate. This is the requirement Thunderbird missed: a theme with holes leaves
    /// some surface — its compose window, in Thunderbird's case — permanently unthemeable.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryBuiltInDefinesEveryRequiredToken(string themeId)
    {
        var resolved = OfficeThemes.Build(themeId).Resolve();

        var missing = TokenKeys.Required.Where(k => !resolved.Contains(k)).ToList();
        Assert.True(missing.Count == 0,
            $"Theme '{themeId}' is missing: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryColourTokenParses(string themeId)
    {
        var resolved = OfficeThemes.Build(themeId).Resolve();

        foreach (var key in TokenKeys.Required)
        {
            var raw = resolved.GetString(key);
            if (!raw.StartsWith('#')) continue;

            var ex = Record.Exception(() => resolved.GetColor(key));
            Assert.True(ex is null, $"{themeId}/{key} = '{raw}' did not parse: {ex?.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void GeometryTokensAreNumeric(string themeId)
    {
        var resolved = OfficeThemes.Build(themeId).Resolve();

        foreach (var key in (string[])
                 [TokenKeys.Ribbon.Height, TokenKeys.Ribbon.TabStripHeight, TokenKeys.Nav.Width,
                  TokenKeys.List.Width, TokenKeys.List.RowHeight, TokenKeys.List.RowHeightCompact,
                  TokenKeys.List.UnreadBarWidth, TokenKeys.List.GroupHeaderHeight,
                  TokenKeys.StatusBar.Height])
        {
            Assert.True(resolved.GetDouble(key) > 0, $"{themeId}/{key} should be positive");
        }
    }

    /// <summary>
    /// Built-ins are authored as complete explicit sets, never derived from one another. If
    /// Black were an inversion of Colorful the two would share values; they must not.
    /// </summary>
    [Fact]
    public void BuiltInsAreIndependentlyAuthoredNotDerived()
    {
        var colorful = OfficeThemes.Build(OfficeThemes.Colorful).Resolve();
        var black = OfficeThemes.Build(OfficeThemes.Black).Resolve();

        Assert.NotEqual(colorful.GetString(TokenKeys.Surface.Ground),
                        black.GetString(TokenKeys.Surface.Ground));
        Assert.NotEqual(colorful.GetString(TokenKeys.Text.Primary),
                        black.GetString(TokenKeys.Text.Primary));

        // Geometry is shared on purpose: Office themes change colour, not layout.
        Assert.Equal(colorful.GetDouble(TokenKeys.List.RowHeight),
                     black.GetDouble(TokenKeys.List.RowHeight));
    }

    [Fact]
    public void OnlyBlackIsDark()
    {
        Assert.True(OfficeThemes.IsDark(OfficeThemes.Black));
        Assert.False(OfficeThemes.IsDark(OfficeThemes.Colorful));
        Assert.False(OfficeThemes.IsDark(OfficeThemes.White));
        Assert.False(OfficeThemes.IsDark(OfficeThemes.DarkGray));
    }

    [Fact]
    public void UnknownThemeIdThrows()
        => Assert.Throws<ArgumentException>(() => OfficeThemes.Build("teal"));
}

public class ThemeServiceTests
{
    private static ThemeService Service()
        => new(new FontResolver(["Liberation Sans", "Liberation Serif", "Liberation Mono"]));

    [Fact]
    public void DefaultsToColorfulCozy()
    {
        var service = Service();
        Assert.Equal(OfficeThemes.Colorful, service.ThemeId);
        Assert.Equal(Density.Cozy, service.Density);
    }

    [Fact]
    public void RaisesChangedOnApply()
    {
        var service = Service();
        var fired = 0;
        service.Changed += (_, _) => fired++;

        service.Apply(OfficeThemes.Black);

        Assert.Equal(1, fired);
        Assert.Equal(OfficeThemes.Black, service.ThemeId);
        Assert.True(service.IsDark);
    }

    /// <summary>Density touches spacing only. Colour must be untouched by it.</summary>
    [Fact]
    public void DensityChangesGeometryButNotColour()
    {
        var service = Service();
        var cozyAccent = service.Tokens.GetString(TokenKeys.Accent.Rest);
        var cozyRow = service.Tokens.GetDouble(TokenKeys.List.RowHeight);

        service.SetDensity(Density.Compact);

        Assert.Equal(cozyAccent, service.Tokens.GetString(TokenKeys.Accent.Rest));
        Assert.True(service.Tokens.GetDouble(TokenKeys.List.RowHeight) < cozyRow);

        service.SetDensity(Density.Comfortable);
        Assert.True(service.Tokens.GetDouble(TokenKeys.List.RowHeight) > cozyRow);
    }

    [Fact]
    public void UserOverridesApplyOverTheBuiltIn()
    {
        var service = Service();
        var overrides = new TokenSet();
        overrides.Set("palette.brand.primary", "#B4009E");

        service.Apply(OfficeThemes.Colorful, overrides: overrides);

        Assert.Equal("#B4009E", service.Tokens.GetString(TokenKeys.Accent.Rest));
        Assert.Equal("#B4009E", service.Tokens.GetString(TokenKeys.List.UnreadBar));
    }

    [Fact]
    public void ClearingOverridesRestoresTheBuiltInExactly()
    {
        var service = Service();
        var original = service.Tokens.GetString(TokenKeys.Accent.Rest);

        var overrides = new TokenSet();
        overrides.Set("palette.brand.primary", "#B4009E");
        service.Apply(OfficeThemes.Colorful, overrides: overrides);
        service.ClearOverrides();

        Assert.Equal(original, service.Tokens.GetString(TokenKeys.Accent.Rest));
    }

    /// <summary>
    /// Typography tokens name logical families; the resolver rewrites them to something this
    /// machine can actually draw before the UI ever sees them.
    /// </summary>
    [Fact]
    public void TypographyTokensResolveToInstalledFamilies()
    {
        var service = Service();
        Assert.Equal("Liberation Sans", service.Tokens.GetString(TokenKeys.Typography.UiFamily));
    }

    /// <summary>
    /// A token that happens to equal the surface behind it renders as nothing at all. Both of
    /// these have shipped broken: the search placeholder drawn in the fill colour, and the
    /// active tab's rule drawn in the tab strip's own colour, each invisible in exactly one
    /// theme. The coverage audit cannot catch it, because the token is present — just useless.
    /// <para>
    /// Only ink-on-ground pairs belong here. A border matching its fill is not a bug: two of
    /// the four themes draw no border around the search box at all.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(TokenKeys.Ribbon.TabUnderline, TokenKeys.Ribbon.TabStripBackground)]
    [InlineData(TokenKeys.Ribbon.TabText, TokenKeys.Ribbon.TabStripBackground)]
    [InlineData(TokenKeys.Ribbon.TabTextSelected, TokenKeys.Ribbon.TabStripBackground)]
    [InlineData(TokenKeys.TitleBar.SearchText, TokenKeys.TitleBar.Search)]
    [InlineData(TokenKeys.TitleBar.Foreground, TokenKeys.TitleBar.Background)]
    [InlineData(TokenKeys.Rail.Indicator, TokenKeys.Rail.Background)]
    [InlineData(TokenKeys.Rail.ItemText, TokenKeys.Rail.Background)]
    [InlineData(TokenKeys.List.UnreadText, TokenKeys.List.RowBackground)]
    [InlineData(TokenKeys.List.ReadText, TokenKeys.List.RowBackground)]
    public void ForegroundTokensAreDistinctFromWhatSitsBehindThem(string ink, string ground)
    {
        foreach (var id in OfficeThemes.All)
        {
            var service = Service();
            service.Apply(id);

            var a = service.Tokens.GetString(ink);
            var b = service.Tokens.GetString(ground);

            Assert.False(
                string.Equals(a, b, StringComparison.OrdinalIgnoreCase),
                $"{id}: {ink} and {ground} are both {a}, so {ink} cannot be seen.");
        }
    }

    /// <summary>
    /// Base chrome colours, taken off captures of the real thing with the modal colour of each
    /// flat region. These are observations, not preferences: if one changes, either the theme
    /// drifted or someone remeasured, and both are worth stopping for.
    /// </summary>
    /// <remarks>
    /// Colorful and White are deliberately identical below the title bar — in the reference the
    /// blue is the only difference between them.
    /// </remarks>
    [Theory]
    // theme, titlebar, search fill, tab strip, ribbon, rail, nav, list
    [InlineData("colorful", "#0078D4", "#CCE4F6", "#E9EEF2", "#FFFFFF", "#EFE9E6", "#F5F5F5", "#FFFFFF")]
    [InlineData("white",    "#E9EEF2", "#FAFAFA", "#E9EEF2", "#FFFFFF", "#EFE9E6", "#F5F5F5", "#FFFFFF")]
    [InlineData("black",    "#1B2127", "#1F1F1F", "#1A2126", "#292929", "#201A17", "#141414", "#262626")]
    [InlineData("darkgray", "#555155", "#BDBDBD", "#535154", "#BDBDBD", "#57524F", "#3D3D3D", "#666666")]
    public void BaseChromeMatchesTheMeasuredReference(
        string theme, string titleBar, string search, string tabStrip,
        string ribbon, string rail, string nav, string list)
    {
        var service = Service();
        service.Apply(theme);

        Assert.Equal(titleBar, service.Tokens.GetString(TokenKeys.TitleBar.Background));
        Assert.Equal(search, service.Tokens.GetString(TokenKeys.TitleBar.Search));
        Assert.Equal(tabStrip, service.Tokens.GetString(TokenKeys.Ribbon.TabStripBackground));
        Assert.Equal(ribbon, service.Tokens.GetString(TokenKeys.Ribbon.Background));
        Assert.Equal(rail, service.Tokens.GetString(TokenKeys.Rail.Background));
        Assert.Equal(nav, service.Tokens.GetString(TokenKeys.Nav.Background));
        Assert.Equal(list, service.Tokens.GetString(TokenKeys.List.Background));
    }
}
