using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

public class TokenMapTests
{
    [Fact]
    public void EveryRequiredTokenHasExactlyOneArea()
    {
        foreach (var token in TokenKeys.Required)
        {
            Assert.NotNull(TokenMap.AreaOf(token));
            Assert.Equal(1, TokenMap.Areas.Count(a => a.Tokens.Contains(token, StringComparer.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void AuditPairsCarryInkAndGroundRoles()
    {
        foreach (var (ink, ground) in ContrastAudit.Pairs)
        {
            Assert.Equal(TokenRole.Ink, TokenMap.RoleOf(ink));
            Assert.Equal(TokenRole.Ground, TokenMap.RoleOf(ground));
        }
    }

    [Fact]
    public void TheLightContentRuleIsInTheMap()
    {
        // The surfaces the rule protects are content, and nothing automated may write them.
        foreach (var token in new[]
                 {
                     TokenKeys.List.RowBackground, TokenKeys.Reading.Background,
                     TokenKeys.Compose.BodyBackground, TokenKeys.Calendar.Background,
                     TokenKeys.Notes.Background, TokenKeys.Journal.Background,
                     TokenKeys.People.CardBackground, TokenKeys.Text.Primary, TokenKeys.Surface.Ground,
                 })
        {
            var area = TokenMap.AreaOf(token);
            Assert.NotNull(area);
            Assert.True(area!.IsContent, $"{token} should be content, is in \"{area.Id}\".");
            Assert.False(area.MayAutomate);
        }

        // The chrome an automated door may recolour.
        foreach (var token in new[]
                 {
                     TokenKeys.TitleBar.Background, TokenKeys.Ribbon.Background,
                     TokenKeys.Nav.Background, TokenKeys.Rail.Background,
                     TokenKeys.List.Background, TokenKeys.StatusBar.Background,
                 })
        {
            var area = TokenMap.AreaOf(token);
            Assert.NotNull(area);
            Assert.True(area!.MayAutomate, $"{token} should be automatable chrome, is in \"{area.Id}\".");
        }

        // The desktop's own dialogs: neither content nor ours to recolour.
        var system = TokenMap.AreaOf(TokenKeys.SystemDialog.Background);
        Assert.NotNull(system);
        Assert.True(system!.IsDesktop);
        Assert.False(system.MayAutomate);
    }

    [Fact]
    public void OnlyTheTitleBarMayTakeAnImageToday()
    {
        var imageAreas = TokenMap.Areas.Where(a => a.MayTakeImage).Select(a => a.Id).ToList();
        Assert.Equal(["titlebar"], imageAreas);
    }

    [Fact]
    public void GeometryIsNotAColourChoice()
    {
        foreach (var token in new[]
                 {
                     TokenKeys.TitleBar.Height, TokenKeys.Rail.Width, TokenKeys.List.RowHeight,
                     TokenKeys.Typography.UiFamily, TokenKeys.Elevation.Ribbon,
                     TokenKeys.Calendar.ChipTint, TokenKeys.Icons.Set, TokenKeys.Workspace.Inset,
                 })
        {
            Assert.Equal(TokenRole.Geometry, TokenMap.RoleOf(token));
        }
    }

    [Fact]
    public void InksOnServesTheAuditsPairs()
    {
        Assert.Contains(TokenKeys.TitleBar.Foreground, TokenMap.InksOn(TokenKeys.TitleBar.Background));
        Assert.Contains(TokenKeys.List.UnreadText, TokenMap.InksOn(TokenKeys.List.RowBackground));
        Assert.Empty(TokenMap.InksOn("no.such.ground"));
    }
}
