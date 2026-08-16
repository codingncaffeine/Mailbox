using Mailbox.Controls.Ribbon;
using static Mailbox.Controls.Ribbon.SimplifiedRowPanel;

namespace Mailbox.Tests;

/// <summary>
/// The Simplified bar's narrowing rules, against widths rather than windows.
/// </summary>
/// <remarks>
/// The reference's bar gives way in a fixed order — labels off a rank at a time from the
/// lowest, then whole controls off from the right into the "…" — and this pins that order the
/// way <c>RibbonCollapseTests</c> pins the classic ladder. The panel itself only turns these
/// answers into visibility.
/// </remarks>
public class SimplifiedRowFitTests
{
    /// <summary>A labelled button: 30 of icon, 50 of label.</summary>
    private static SimplifiedFit Labelled(bool primary = false, int rank = Normal)
        => new(80, 50, primary ? Primary : rank, false);

    private static SimplifiedFit IconOnly() => new(30, 0, Normal, false);

    private static SimplifiedFit Rule() => new(9, 0, Normal, true);

    private const int Sheddable = Mailbox.Core.Ribbon.RibbonItem.SheddableLabelRank;
    private const int Normal = Mailbox.Core.Ribbon.RibbonItem.NormalLabelRank;
    private const int Primary = Mailbox.Core.Ribbon.RibbonItem.PrimaryLabelRank;

    [Fact]
    public void EverythingFitsWhenThereIsRoom()
    {
        var (labelled, shown) = Fit([Labelled(primary: true), Rule(), Labelled(), IconOnly()], 500);

        Assert.All(shown, Assert.True);
        Assert.True(labelled[0]);
        Assert.True(labelled[2]);
    }

    /// <summary>
    /// A rank goes together, and the primary's label stays while any other rank remains.
    /// </summary>
    /// <remarks>
    /// Together rather than one at a time because half a cluster labelled is what nothing in
    /// the reference looks like: at 1447 all five Respond and Tags words are gone at once.
    /// </remarks>
    [Fact]
    public void ARankOfLabelsGoesTogetherSparingThePrimary()
    {
        // Full: 80 + 9 + 80 + 80 = 249. At 210 the normal rank's two labels both go.
        var (labelled, shown) = Fit([Labelled(primary: true), Rule(), Labelled(), Labelled()], 210);

        Assert.All(shown, Assert.True);
        Assert.True(labelled[0]);
        Assert.False(labelled[2]);
        Assert.False(labelled[3]);
    }

    /// <summary>
    /// The lowest rank goes first even when it is further left — which is the reference at
    /// 1447, where Reply and Categorize have lost their words while Unread/Read, to their
    /// right, has not.
    /// </summary>
    [Fact]
    public void TheLowestRankGoesFirstWhereverItIsOnTheBar()
    {
        // Full 249. At 210 only the sheddable label need go, and it is the leftmost of the two.
        var (labelled, shown) = Fit(
            [Labelled(primary: true), Rule(), Labelled(rank: Sheddable), Labelled(rank: Normal)], 210);

        Assert.All(shown, Assert.True);
        Assert.True(labelled[0]);
        Assert.False(labelled[2]);
        Assert.True(labelled[3]);
    }

    [Fact]
    public void ThePrimarysLabelGoesLastAndBeforeAnyControl()
    {
        // Full 249; all non-primary labels off leaves 149; at 110 the primary's label goes
        // too (99), and everything is still on the bar.
        var (labelled, shown) = Fit([Labelled(primary: true), Rule(), Labelled(), Labelled()], 110);

        Assert.All(shown, Assert.True);
        Assert.All(labelled, Assert.False);
    }

    /// <summary>Then whole controls, from the right, never a truncation.</summary>
    [Fact]
    public void ControlsArePushedOffFromTheRight()
    {
        // Icons only: 30 + 9 + 30 + 30 = 99. At 75 the rightmost control goes.
        var (labelled, shown) = Fit([Labelled(primary: true), Rule(), Labelled(), Labelled()], 75);

        Assert.True(shown[0]);
        Assert.True(shown[1]);
        Assert.True(shown[2]);
        Assert.False(shown[3]);
        Assert.All(labelled, Assert.False);
    }

    /// <summary>A rule whose whole cluster has gone is a line hanging off the bar. It goes too.</summary>
    [Fact]
    public void AStrandedRuleIsSweptWithItsCluster()
    {
        // At 35 only the first icon fits: both trailing controls go, and the rule between the
        // clusters goes with them rather than dangling at the end.
        var (_, shown) = Fit([Labelled(primary: true), Rule(), Labelled(), Labelled()], 35);

        Assert.True(shown[0]);
        Assert.False(shown[1]);
        Assert.False(shown[2]);
        Assert.False(shown[3]);
    }

    [Fact]
    public void ARuleStaysWhileAnythingRealRemainsAfterIt()
    {
        // 30 + 9 + 30 = 69 fits at 75: the rule still divides two live clusters.
        var (_, shown) = Fit([Labelled(primary: true), Rule(), Labelled(), Labelled()], 75);

        Assert.True(shown[1]);
    }

    /// <summary>The same width gives the same answer, which is what lets the layout settle.</summary>
    [Fact]
    public void TheAnswerIsAFunctionOfTheWidthAlone()
    {
        SimplifiedFit[] entries = [Labelled(primary: true), Rule(), Labelled(), IconOnly(), Labelled()];

        foreach (var width in new[] { 500.0, 240, 160, 90, 40 })
        {
            var first = Fit(entries, width);
            var second = Fit(entries, width);

            Assert.Equal(first.Labelled, second.Labelled);
            Assert.Equal(first.Shown, second.Shown);
        }
    }

    /// <summary>Nothing is ever half-shown: an entry is on the bar or off it.</summary>
    [Fact]
    public void NarrowerNeverShowsMore()
    {
        SimplifiedFit[] entries = [Labelled(primary: true), Rule(), Labelled(), IconOnly(), Labelled()];

        var previous = int.MaxValue;

        foreach (var width in new[] { 400.0, 300, 200, 150, 100, 60, 30 })
        {
            var (_, shown) = Fit(entries, width);
            var count = shown.Count(s => s);

            Assert.True(count <= previous, $"narrowing to {width} showed more, not fewer");
            previous = count;
        }
    }
}
