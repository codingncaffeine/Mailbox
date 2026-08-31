using Mailbox.Theming;
using Mailbox.Theming.Fonts;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// The clean-return guarantee: choosing a built-in always returns the application to its
/// original look, whatever any theme, override or appearance choice did before. The residue
/// that historical theme editors leave — switch back and something is still changed — must be
/// impossible here.
/// </summary>
[Collection("theme state")]
public class CleanReturnTests
{
    private static ThemeService Service()
        => new(new FontResolver(["Liberation Sans", "Liberation Serif", "Liberation Mono"]));

    [Fact]
    public void ApplyFreshDropsSessionOverrides()
    {
        var service = Service();
        var overrides = new TokenSet();
        overrides.Set(TokenKeys.TitleBar.Background, "#FF00FF");
        service.Apply(OfficeThemes.Colorful, overrides: overrides);
        Assert.Equal("#FF00FF", service.Tokens.GetString(TokenKeys.TitleBar.Background));

        service.ApplyFresh(OfficeThemes.DarkGray);

        Assert.Null(service.UserOverrides);
        Assert.NotEqual("#FF00FF", service.Tokens.GetString(TokenKeys.TitleBar.Background));
    }

    [Fact]
    public void AppearanceSurvivesTheEditorsResetAndTheThemeSwitch()
    {
        // A kept personal choice is not scratch: the editor's Reset All and a theme switch
        // both leave it standing, and only clearing the slot removes it.
        var service = Service();
        var appearance = new TokenSet();
        appearance.Set(TokenKeys.TitleBar.Background, "#123456");
        service.SetAppearance(appearance);
        Assert.Equal("#123456", service.Tokens.GetString(TokenKeys.TitleBar.Background));

        service.ClearOverrides();
        Assert.Equal("#123456", service.Tokens.GetString(TokenKeys.TitleBar.Background));

        service.ApplyFresh(OfficeThemes.White);
        Assert.Equal("#123456", service.Tokens.GetString(TokenKeys.TitleBar.Background));

        service.SetAppearance(null);
        var factory = OfficeThemes.Build(OfficeThemes.White).Resolve();
        Assert.Equal(factory.GetString(TokenKeys.TitleBar.Background),
            service.Tokens.GetString(TokenKeys.TitleBar.Background));
    }

    [Fact]
    public void SessionOverridesBeatAppearanceWhileTheyLast()
    {
        // The editor previews over everything, including a kept backdrop choice — and its
        // Reset All hands the appearance value back rather than the theme's.
        var service = Service();
        var appearance = new TokenSet();
        appearance.Set(TokenKeys.TitleBar.Background, "#123456");
        service.SetAppearance(appearance);

        var overrides = new TokenSet();
        overrides.Set(TokenKeys.TitleBar.Background, "#654321");
        service.Apply(service.ThemeId, overrides: overrides);
        Assert.Equal("#654321", service.Tokens.GetString(TokenKeys.TitleBar.Background));

        service.ClearOverrides();
        Assert.Equal("#123456", service.Tokens.GetString(TokenKeys.TitleBar.Background));
    }

    [Fact]
    public void ABuiltInResolvesIdenticallyAfterAnyChurn()
    {
        var fresh = Service();
        var factory = fresh.Tokens.AsPairs().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        var churned = Service();
        var overrides = new TokenSet();
        overrides.Set(TokenKeys.TitleBar.Background, "#111111");
        overrides.Set(TokenKeys.Ribbon.Background, "#222222");
        churned.Apply(OfficeThemes.Black, overrides: overrides);
        var appearance = new TokenSet();
        appearance.Set(TokenKeys.TitleBar.Background, "#333333");
        churned.SetAppearance(appearance);
        churned.ApplyFresh(OfficeThemes.DarkGray);
        churned.SetAppearance(null);
        churned.ApplyFresh(fresh.ThemeId);

        var after = churned.Tokens.AsPairs().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(factory.Count, after.Count);
        foreach (var (key, value) in factory) Assert.Equal(value, after[key]);
    }
}
