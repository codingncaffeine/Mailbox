using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

/// <summary>
/// What the shell's panes do as the window narrows, against widths rather than windows.
/// </summary>
/// <remarks>
/// The load-bearing property is not which pane goes first — it is that <b>nothing changes at or
/// above the width the window used to stop at</b>. Everything these rules govern was unreachable
/// before the floor came down, so a reader who never drags the window narrow sees exactly what
/// they saw. A threshold that crept above 760 would take the reading pane away from somebody who
/// had it, which is the one outcome this must not have.
/// </remarks>
public class PaneSheddingTests
{
    /// <summary>The width the shell refused to go below until the panes learned to give way.</summary>
    private const double OldFloor = 760;

    [Fact]
    public void NothingShedsAtOrAboveTheWidthTheWindowUsedToStopAt()
    {
        foreach (var width in new double[] { OldFloor, 800, 1000, 1280, 1600, 2560 })
        {
            Assert.False(
                PaneShedding.HidesReadingPane(width),
                $"the reading pane must survive {width}, which was always reachable.");
            Assert.False(
                PaneShedding.MinimisesFolderPane(width),
                $"the folder pane must survive {width}, which was always reachable.");
        }
    }

    /// <summary>
    /// The reading pane goes first: it is the pane that wants the most room, and a list beside a
    /// pane too narrow to read is worse than a list with the window to itself.
    /// </summary>
    [Fact]
    public void TheReadingPaneGoesBeforeTheFolderPane()
    {
        Assert.True(
            PaneShedding.FolderPaneFloor < PaneShedding.ReadingPaneFloor,
            "the folder pane must outlast the reading pane as the window narrows.");

        // A width between the two: reading pane gone, folder pane still whole.
        const double between = 700;
        Assert.True(PaneShedding.HidesReadingPane(between));
        Assert.False(PaneShedding.MinimisesFolderPane(between));
    }

    /// <summary>At the window's own floor both have given way, which is what makes it usable.</summary>
    [Fact]
    public void AtTheFloorBothHaveGivenWay()
    {
        Assert.True(PaneShedding.HidesReadingPane(PaneShedding.ShellFloor));
        Assert.True(PaneShedding.MinimisesFolderPane(PaneShedding.ShellFloor));
    }

    /// <summary>
    /// The floor leaves a message list worth having: the rail and the minimised strip come off it
    /// first, and what is left is what the reader reads.
    /// </summary>
    [Fact]
    public void TheFloorLeavesAListWorthReading()
    {
        // The rail as the reference draws it, and the strip as this measures it.
        const double rail = 56;
        const double strip = 48;

        Assert.True(
            PaneShedding.ShellFloor - rail - strip >= 240,
            "at the floor the message list must still be wide enough to read a row in.");
    }
}
