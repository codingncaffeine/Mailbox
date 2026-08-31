using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

public class RecolourTests
{
    [Fact]
    public void WashesFollowTheGround()
    {
        // Dark Gray's title bar takes the white washes; the system dialogs' light band the dark.
        Assert.Equal((Recolour.WashOverDark, Recolour.WashOverDarkPressed), Recolour.WashesFor("#555155"));
        Assert.Equal((Recolour.WashOverLight, Recolour.WashOverLightPressed), Recolour.WashesFor("#F3F3F3"));
    }

    [Fact]
    public void TheWashesAreTheBuiltInsOwn()
    {
        // The constants must be exactly what the built-ins carry — a fifth wash value is a bug.
        var darkGray = OfficeThemes.Build(OfficeThemes.DarkGray).Resolve();
        Assert.Equal(Recolour.WashOverDark, darkGray.GetString(TokenKeys.TitleBar.CaptionHover));
        Assert.Equal(Recolour.WashOverLightPressed, darkGray.GetString(TokenKeys.SystemDialog.CaptionPressed));
    }

    [Fact]
    public void InkFollowsItsGround()
    {
        var neutrals = new TokenSet();
        neutrals.Set("palette.neutral.white", "#FFFFFF");
        neutrals.Set("palette.neutral.primary", "#262626");

        Assert.Equal("{palette.neutral.white}", Recolour.InkFor("#101010", neutrals).Reference);
        Assert.Equal("{palette.neutral.primary}", Recolour.InkFor("#F0F0F0", neutrals).Reference);
    }

    [Fact]
    public void HoverMovesAwayFromTheGroundsOwnEnd()
    {
        // A light chrome darkens under the pointer; a dark one lightens — by 0.06, 0.10 pressed.
        var lightGround = Oklch.Parse("#F3F3F3")!.Value;
        var lightHover = Oklch.Parse(Recolour.Hover("#F3F3F3"))!.Value;
        Assert.True(lightHover.L < lightGround.L);
        Assert.Equal(0.06, lightGround.L - lightHover.L, 2);

        var darkGround = Oklch.Parse("#3D3D3D")!.Value;
        var darkPressed = Oklch.Parse(Recolour.Pressed("#3D3D3D"))!.Value;
        Assert.True(darkPressed.L > darkGround.L);
        Assert.Equal(0.10, darkPressed.L - darkGround.L, 2);
    }

    [Fact]
    public void FlattenCompositesOverTheGround()
    {
        Assert.Equal("#800000", Recolour.Flatten("#80FF0000", "#000000"));
        Assert.Equal("#112233", Recolour.Flatten("#FF112233", "#FFFFFF"));
        Assert.Equal("#112233", Recolour.Flatten("#112233", "#FFFFFF"));

        // Fully transparent means absent — let the base stand — never black.
        Assert.Null(Recolour.Flatten("#00FFFFFF", "#FFFFFF"));
    }

    [Fact]
    public void RepairMovesOnlyTheInksTheCallerWrote()
    {
        // A caller writes an unreadable caption pair onto Dark Gray; the repair fixes the ink
        // it wrote and leaves every token the base owns alone.
        var overlay = new TokenSet();
        overlay.Set(TokenKeys.TitleBar.Background, "#202020");
        overlay.Set(TokenKeys.TitleBar.Foreground, "#303030");

        var full = OfficeThemes.Build(OfficeThemes.DarkGray).OverlaidWith(overlay);
        var before = full.Resolve().GetString(TokenKeys.Rail.ItemText);

        var repaired = Recolour.RepairContrast(full, overlay);

        var moved = Assert.Single(repaired);
        Assert.Equal(TokenKeys.TitleBar.Foreground, moved.Token);
        Assert.True(moved.After >= ContrastAudit.MinimumRatio);
        Assert.True(ContrastAudit.Ratio(overlay[TokenKeys.TitleBar.Foreground]!, "#202020") >= ContrastAudit.MinimumRatio);
        Assert.Equal(before, full.Resolve().GetString(TokenKeys.Rail.ItemText));
    }

    [Fact]
    public void RepairLeavesUnwrittenPairsToTheReport()
    {
        // The caller writes only the ground; the base's own ink reference now fails, but the
        // repair does not touch what it did not write — that finding is the report's.
        var overlay = new TokenSet();
        overlay.Set(TokenKeys.Nav.Background, "#FEFEFE");

        var full = OfficeThemes.Build(OfficeThemes.White).OverlaidWith(overlay);
        full.Set(TokenKeys.Nav.ItemText, "#FFFFFF"); // the base's ink, standing unreadable

        Assert.Empty(Recolour.RepairContrast(full, overlay));
        Assert.Contains(ContrastAudit.Check(full.Resolve()), f => f.Ink == TokenKeys.Nav.ItemText);
    }
}
