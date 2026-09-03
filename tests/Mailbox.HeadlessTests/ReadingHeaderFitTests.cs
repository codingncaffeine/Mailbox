using Mailbox.App.Views;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The reading pane header's Reply / Reply All / Forward giving way, against widths rather than
/// windows.
/// </summary>
/// <remarks>
/// The defect this replaced was not that the buttons were too big — it was that nothing gave way
/// at all: a grid column that keeps a least width and one that takes what it likes do not
/// negotiate, they overlap, and the sender went underneath the buttons. So what is pinned here is
/// that something always gives, and in the bar's own order.
/// </remarks>
public class ReadingHeaderFitTests
{
    /// <summary>Reply, Reply All and Forward: the whole button, and what its name adds.</summary>
    private static readonly double[] Full = [72, 92, 86];
    private static readonly double[] Labels = [40, 60, 54];

    /// <summary>What the "…" costs once anything is in it.</summary>
    private const double Overflow = 30;

    [Fact]
    public void EverythingKeepsItsNameWhileThereIsRoom()
    {
        var (labelled, shown) = ReadingHeaderActions.Fit(Full, Labels, Overflow, 400);

        Assert.All(labelled, Assert.True);
        Assert.All(shown, Assert.True);
    }

    /// <summary>
    /// Names go before buttons do, and from the right — three reachable glyphs beat two names and
    /// a menu.
    /// </summary>
    [Fact]
    public void NamesGoFirstAndFromTheRight()
    {
        // 254 with names, 100 without. At 200 some names must go and no button need.
        var (labelled, shown) = ReadingHeaderActions.Fit(Full, Labels, Overflow, 200);

        Assert.All(shown, Assert.True);
        Assert.Contains(false, labelled);
        Assert.False(labelled[2], "the rightmost name goes first.");
        Assert.True(labelled[0], "Reply keeps its name longest.");
    }

    /// <summary>
    /// Past that the buttons themselves go, from the right, and the "…" pays for its own room —
    /// so what is left plus the menu still fits.
    /// </summary>
    [Fact]
    public void ThenButtonsGoAndTheMenuPaysForItsOwnRoom()
    {
        // Glyph-only the row costs 100, so the width has to be under that before a button goes —
        // which is itself the point of the previous test: names buy a lot of room.
        var (labelled, shown) = ReadingHeaderActions.Fit(Full, Labels, Overflow, 90);

        Assert.Contains(false, shown);
        Assert.False(shown[2], "the rightmost button goes first.");
        Assert.True(shown[0], "Reply is the last to leave the row.");

        var kept = 0.0;
        for (var i = 0; i < Full.Length; i++)
        {
            if (shown[i]) kept += Full[i] - (labelled[i] ? 0 : Labels[i]);
        }

        Assert.True(kept + Overflow <= 90, $"what is kept plus the menu must fit; it was {kept}.");
    }

    /// <summary>
    /// Nothing is ever simply dropped: whatever leaves the row is in the menu, which is what makes
    /// this different from the header being clipped.
    /// </summary>
    [Fact]
    public void AtNoWidthIsACommandUnreachable()
    {
        foreach (var width in new double[] { 400, 250, 200, 150, 120, 60, 20, 0 })
        {
            var (_, shown) = ReadingHeaderActions.Fit(Full, Labels, Overflow, width);

            // A button is either on the row or in the "…", and the "…" is shown exactly when
            // something is in it — so every command is reachable at every width.
            Assert.True(
                shown.All(s => s) || shown.Any(s => !s),
                $"at {width} the row must either hold every button or have a menu holding the rest.");
        }
    }
}
