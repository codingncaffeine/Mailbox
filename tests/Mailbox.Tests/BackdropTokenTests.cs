using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// The caption backdrop family: optional by design, empty in every built-in, and placed where
/// an editor will look for it.
/// </summary>
public class BackdropTokenTests
{
    [Fact]
    public void EveryBuiltInCarriesAnEmptyBackdropByDefault()
    {
        foreach (var id in OfficeThemes.All)
        {
            var resolved = OfficeThemes.Build(id).Resolve();
            Assert.Equal(string.Empty, resolved.GetString(TokenKeys.TitleBar.Backdrop));
            Assert.Equal("right top", resolved.GetString(TokenKeys.TitleBar.BackdropAlignment));
            Assert.Equal("no-repeat", resolved.GetString(TokenKeys.TitleBar.BackdropTiling));
            Assert.Equal("auto", resolved.GetString(TokenKeys.TitleBar.BackdropSize));
        }
    }

    [Fact]
    public void TheBackdropIsOptionalNotRequired()
    {
        // Requiring it would break every theme file a reader already wrote: "no backdrop" is a
        // legitimate state, not an unpainted surface.
        Assert.DoesNotContain(TokenKeys.TitleBar.Backdrop, TokenKeys.Required);
        Assert.DoesNotContain(TokenKeys.TitleBar.BackdropAlignment, TokenKeys.Required);
    }

    [Fact]
    public void TheMapPlacesTheFamilyOnTheTitleBar()
    {
        var area = TokenMap.AreaOf(TokenKeys.TitleBar.Backdrop);
        Assert.NotNull(area);
        Assert.Equal("titlebar", area!.Id);
        Assert.True(area.MayTakeImage);
        Assert.Equal(TokenRole.Geometry, TokenMap.RoleOf(TokenKeys.TitleBar.Backdrop));
    }
}
