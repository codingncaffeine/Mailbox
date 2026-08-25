using Mailbox.Controls.Ribbon;
using Mailbox.Core.Commands;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// The drawn ribbon icons (§20): the three two-tone ones carry the reference's own pixels as a
/// grid per size, so what is asserted here is the transcription — a row a character short draws
/// a figure that is wrong everywhere it is used and reads as a rendering fault.
/// </summary>
public class RibbonArtworkTests
{
    [Fact]
    public void EveryFigureIsTheSizeItSaysItIs()
    {
        Assert.All(RibbonArtwork.Figures, entry => Assert.All(entry.Value, figure =>
        {
            Assert.Equal(figure.Height, figure.Rows.Length);
            Assert.All(figure.Rows, row =>
                Assert.True(row.Length == figure.Width,
                    $"{entry.Key} at {figure.Width}: a row is {row.Length} wide, not {figure.Width}."));
        }));
    }

    [Fact]
    public void EveryPixelIsARoleThereIsAColourFor()
    {
        // A stray character draws nothing, silently: the row is the right length, the figure is
        // simply missing a pixel wherever the typo landed.
        Assert.All(RibbonArtwork.Figures, entry => Assert.All(entry.Value, figure =>
            Assert.All(figure.Rows, row => Assert.All(row.ToCharArray(), role =>
                Assert.True(role is '.' or '#' or 'o' or 'G' or 'g',
                    $"{entry.Key} at {figure.Width} holds '{role}', which is not a role.")))));
    }

    [Fact]
    public void EveryStrokeRunsThroughTheFigureItBelongsTo()
    {
        Assert.All(RibbonArtwork.Figures, entry => Assert.All(entry.Value, figure =>
            Assert.All(figure.Strokes ?? [], stroke =>
            {
                Assert.True(stroke.Points.Length > 1, $"{entry.Key}: a stroke of one point draws nothing.");
                Assert.All(stroke.Points, point =>
                {
                    Assert.InRange(point.X, 0, figure.Width);
                    Assert.InRange(point.Y, 0, figure.Height);
                });
            })));
    }

    [Theory]
    [InlineData("mail-new", 32, 30)]
    [InlineData("mail-new", 18, 19)]
    [InlineData("mail-new", 16, 19)]
    [InlineData("archive", 32, 28)]
    [InlineData("archive", 18, 18)]
    [InlineData("move", 32, 28)]
    [InlineData("move", 18, 18)]
    [InlineData("move", 16, 16)]
    public void TheBoxPicksTheFigureDrawnForIt(string drawing, double box, int width)
    {
        // The reference ships a drawing per size rather than one scaled, so the classic ribbon's
        // 32 box, the Simplified bar's 18 and a small button's 16 must each land on their own.
        Assert.Equal(width, RibbonArtwork.Nearest(RibbonArtwork.Figures[drawing], box).Width);
    }

    [Fact]
    public void ABadgeStandsOffWhatItIsDrawnOver()
    {
        // The arrow crosses the tray's top edge, and the pixel either side of it is left unpainted
        // so the surface behind shows through — which is what makes it read as being in front.
        var move = RibbonArtwork.Nearest(RibbonArtwork.Figures["move"], 18);
        var edge = Array.FindIndex(move.Rows, row => row.Contains("#G#", StringComparison.Ordinal));
        Assert.True(edge >= 0, "the Move tray's top edge is not crossed by the arrow at all.");

        var shaft = move.Rows[edge].IndexOf('G');
        Assert.Equal('.', RibbonArtwork.Role(move.Rows, shaft - 1, edge));
        Assert.Equal('.', RibbonArtwork.Role(move.Rows, shaft + 1, edge));
    }

    [Fact]
    public void ALidIsNotKnockedOutByWhatItEncloses()
    {
        // Archive's lid is a fill inside an outline of its own, so its two colours touch
        // everywhere. Only 'G' knocks a hole and only through '#' or 'o' — a rule that cut
        // between a badge and its own outline would erase the lid in every theme at once.
        Assert.False(RibbonArtwork.Knocks('g'));

        var archive = RibbonArtwork.Nearest(RibbonArtwork.Figures["archive"], 18);
        var lid = Array.FindIndex(archive.Rows, row => row.Contains('G'));
        Assert.All(archive.Rows[lid].Select((_, x) => x),
            x => Assert.Equal(archive.Rows[lid][x], RibbonArtwork.Role(archive.Rows, x, lid)));
    }

    [Fact]
    public void EveryDrawingACommandAsksForIsOneThatExists()
    {
        // People's Follow Up button spent a phase drawing Categorize because its command named a
        // drawing this class does not have, and an unknown name renders as nothing at all.
        string[] drawn = ["categorize", "followup"];
        Assert.All(CommandCatalogTests.Everything().All.Where(c => c.IconArtwork is { Length: > 0 }), command =>
            Assert.True(
                RibbonArtwork.Figures.ContainsKey(command.IconArtwork!) || drawn.Contains(command.IconArtwork),
                $"{command.Id} asks for the '{command.IconArtwork}' drawing, which is not one."));
    }

    [Fact]
    public void TheTwoToneIconsAreTintedByTokensEveryThemeDefines()
    {
        // The fill and the cross are the two colours this work added; both are in the coverage
        // gate, so a theme that forgot one cannot load rather than drawing the icon in the dark.
        Assert.Contains(TokenKeys.RibbonIcon.Fill, TokenKeys.Required);
        Assert.Contains(TokenKeys.RibbonIcon.Plus, TokenKeys.Required);
    }
}
