using Mailbox.Theming;
using Mailbox.Theming.Files;
using Mailbox.Theming.Fonts;
using Mailbox.Theming.Icons;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// The swappable icon sets (§8): every icon exists in both, the active set decides which glyph
/// a name resolves to, and the choice rides the <c>icons.set</c> token so a five-line theme
/// swaps the whole set. The active set is process state, so every test here puts it back.
/// </summary>
public class IconSetTests : IDisposable
{
    public void Dispose() => IconSets.Apply(IconSets.Regular);

    [Fact]
    public void EveryIconHasAFilledCounterpart()
    {
        // The two fonts are siblings from one project; a name that fell out of the filled set
        // would silently draw outline in a filled theme, which this keeps loud instead.
        Assert.All(IconGlyphs.Names, name => Assert.True(IconGlyphs.HasFilled(name), $"{name} has no filled variant."));
    }

    [Fact]
    public void TheActiveSetDecidesTheFontAndTheMap()
    {
        // The artwork difference lives in the font — the two largely share codepoints, though
        // not everywhere — so the family switching is the load-bearing half, and the map
        // switching is proven by any name whose codepoints do differ between the sets.
        IconSets.Apply(IconSets.Filled);
        Assert.Contains("Filled", IconFont.Family.Name);
        var filledDiffers = IconGlyphs.Names.Any(name =>
            IconGlyphs.Sizes.Any(size =>
            {
                var filled = IconGlyphs.Get(name, size);
                IconSets.Apply(IconSets.Regular);
                var regular = IconGlyphs.Get(name, size);
                IconSets.Apply(IconSets.Filled);
                return filled != regular;
            }));
        Assert.True(filledDiffers, "No glyph differs between the sets: the filled map is not being consulted.");

        IconSets.Apply(IconSets.Regular);
        Assert.DoesNotContain("Filled", IconFont.Family.Name);
    }

    [Fact]
    public void AnUnknownSetKeepsTheRegularOne()
    {
        IconSets.Apply("no-such-set");
        Assert.Equal(IconSets.Regular, IconSets.Active);

        IconSets.Apply(null);
        Assert.Equal(IconSets.Regular, IconSets.Active);
    }

    [Fact]
    public void AThemeCarriesItsIconSetAndSwitchingThemesSwitchesBack()
    {
        var tokens = new TokenSet();
        tokens.Set(TokenKeys.Icons.Set, IconSets.Filled);
        var theme = new ThemeFile("filled-icons", "Filled Icons", "colorful", IsDark: null, tokens);
        var service = new ThemeService(new FontResolver([]), new ThemeLibrary([theme]));

        service.Apply("filled-icons");
        Assert.Equal(IconSets.Filled, IconSets.Active);

        // Back to a built-in, which names no set: the default returns with it.
        service.Apply("colorful");
        Assert.Equal(IconSets.Regular, IconSets.Active);
    }
}
