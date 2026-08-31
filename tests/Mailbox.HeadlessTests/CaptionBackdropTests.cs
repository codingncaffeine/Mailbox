using Mailbox.App.Theming;
using Mailbox.App.Views;
using Mailbox.Core.Settings;
using Mailbox.Theming;
using Mailbox.Theming.Fonts;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The Mailbox Background choice, pressed through the real machinery: the settings store the
/// row writes, the appearance slot the service composes, and the alignment grammar a drag
/// speaks. The claim throughout is what the theme service then resolves, never the control.
/// </summary>
public class CaptionBackdropTests
{
    private static ThemeService Service()
        => new(new FontResolver(["Liberation Sans", "Liberation Serif", "Liberation Mono"]));

    private static SettingsStore Scratch()
        => SettingsStore.ScratchCopy(Path.Combine(
            Path.GetTempPath(), $"mailbox-backdrop-tests-{Guid.NewGuid():N}.json"));

    [Fact]
    public void ChoosingAPatternResolvesItAndChoosingTheThemeClearsIt()
    {
        var settings = Scratch();
        var themes = Service();

        BackdropChoice.Choose(settings, themes, "pattern:stitches");
        Assert.Equal("pattern:stitches", themes.Tokens.GetString(TokenKeys.TitleBar.Backdrop));

        // The clean return: back to "(From the theme)" is back to the theme's own answer.
        BackdropChoice.Choose(settings, themes, string.Empty);
        Assert.Equal(string.Empty, themes.Tokens.GetString(TokenKeys.TitleBar.Backdrop));
        Assert.Null(themes.Appearance);
    }

    [Fact]
    public void AnImageDrawsWholeAndAPatternKeepsTheThemesSubtlety()
    {
        var settings = Scratch();
        var themes = Service();

        BackdropChoice.Choose(settings, themes, "images/own/background.png");
        Assert.Equal("1", themes.Tokens.GetString(TokenKeys.TitleBar.BackdropOpacity));
        Assert.Equal("cover", themes.Tokens.GetString(TokenKeys.TitleBar.BackdropSize));

        BackdropChoice.Choose(settings, themes, "pattern:waves");
        var factory = OfficeThemes.Build(themes.ThemeId).Resolve();
        Assert.Equal(factory.GetString(TokenKeys.TitleBar.BackdropOpacity),
            themes.Tokens.GetString(TokenKeys.TitleBar.BackdropOpacity));
    }

    [Fact]
    public void TheChoiceSurvivesAThemeSwitchAndAlignmentTravelsWithIt()
    {
        var settings = Scratch();
        var themes = Service();

        BackdropChoice.Choose(settings, themes, "images/own/background.png");
        BackdropChoice.Align(settings, themes, "25% 60%");
        themes.ApplyFresh(OfficeThemes.Black);

        Assert.Equal("images/own/background.png", themes.Tokens.GetString(TokenKeys.TitleBar.Backdrop));
        Assert.Equal("25% 60%", themes.Tokens.GetString(TokenKeys.TitleBar.BackdropAlignment));

        // Restore reads the same store back into a fresh service — the next launch agrees.
        var next = Service();
        BackdropChoice.Restore(settings, next);
        Assert.Equal("images/own/background.png", next.Tokens.GetString(TokenKeys.TitleBar.Backdrop));
        Assert.Equal("25% 60%", next.Tokens.GetString(TokenKeys.TitleBar.BackdropAlignment));
    }

    [Fact]
    public void ChoosingNoneBeatsAThemeThatBringsABackdrop()
    {
        var settings = Scratch();
        var themes = Service();

        // "(None)" is an explicit choice, distinct from "(From the theme)": it must override
        // whatever a theme says, which is the whole reason the two entries are separate.
        BackdropChoice.Choose(settings, themes, "none");
        Assert.NotNull(themes.Appearance);
        Assert.Equal(string.Empty, themes.Tokens.GetString(TokenKeys.TitleBar.Backdrop));
        Assert.Equal(string.Empty, settings.GetString(BackdropChoice.AlignmentSetting));
    }

    [Fact]
    public void TheAlignmentGrammarReadsKeywordsAndPercentages()
    {
        Assert.Equal((1, 0), CaptionBackdrop.ParseAlignment("right top"));
        Assert.Equal((0, 1), CaptionBackdrop.ParseAlignment("left bottom"));
        Assert.Equal((0.5, 0.5), CaptionBackdrop.ParseAlignment("center"));
        Assert.Equal((0.25, 0.6), CaptionBackdrop.ParseAlignment("25% 60%"));
        Assert.Equal((0.5, 0.5), CaptionBackdrop.ParseAlignment("nonsense"));
    }

    [Fact]
    public void EveryShippedPatternIsKnownAndNamed()
    {
        Assert.Equal(6, CaptionPatterns.Names.Count);
        foreach (var name in CaptionPatterns.Names)
        {
            Assert.True(CaptionPatterns.IsKnown(name));
            Assert.NotEqual(name, CaptionPatterns.DisplayName(name)); // capitalised for the row
        }

        Assert.False(CaptionPatterns.IsKnown("tartan"));
    }
}
