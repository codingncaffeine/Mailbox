using Mailbox.Controls.Ribbon;

namespace Mailbox.Tests;

/// <summary>
/// The tab strip's narrowing rules, against widths rather than windows.
/// </summary>
/// <remarks>
/// The reference gives the strip two states past the width where its tabs stop fitting, and the
/// squished captures hold one of each: the message window at 499 across clips its seven
/// tabs but wants no chevron, and the shell at 347 clips its five to cells of 38 and puts a chevron at the end. Both are pinned here, because the difference between
/// them is one comparison and the wrong side of it is either a chevron that never appears — the
/// later tabs unreachable, which is the defect this replaced — or one that appears while there is
/// still room.
/// </remarks>
public class TabStripFitTests
{
    /// <summary>The five the shell carries: File, Home, Send / Receive, View, Help.</summary>
    private static readonly double[] Shell = [44, 52, 104, 50, 46];

    [Fact]
    public void NothingIsSqueezedWhileThereIsRoom()
    {
        var (cap, squeezing, scrolling, _) = TabStripPanel.Fit(Shell, 0, 600);

        Assert.True(double.IsPositiveInfinity(cap));
        Assert.False(squeezing);
        Assert.False(scrolling);
    }

    /// <summary>
    /// A tab already narrower than the cap keeps its own width: the cap is a ceiling, not a size.
    /// </summary>
    /// <remarks>
    /// The reference's squeezed strip is what says so — "Send", "View" and "Help" are whole while
    /// "Home" and the long promotional tab beside Help are cut, which only happens if the short ones were left
    /// alone rather than given an equal share.
    /// </remarks>
    [Fact]
    public void TheCapIsACeilingAndTheShortTabsAreLeftAlone()
    {
        // 296 natural; at 260 the widest has to come in but the rest still fit under it.
        var (cap, squeezing, scrolling, _) = TabStripPanel.Fit(Shell, 0, 260);

        Assert.True(squeezing);
        Assert.False(scrolling);

        // Everything at or under the cap is untouched, so what the row costs is the sum of the
        // minimum of the two — and it fits.
        Assert.True(Shell.Sum(w => Math.Min(w, cap)) <= 260);
        Assert.True(cap < 104, $"the widest tab must come in; the cap was {cap}.");
        Assert.True(cap >= 52, $"a tab that already fits must not be squeezed; the cap was {cap}.");
    }

    /// <summary>
    /// Past the least cell the reference squeezes to, the strip scrolls rather than squeezing
    /// further — and the chevron's own room is counted before that is decided.
    /// </summary>
    [Fact]
    public void PastTheLeastCellTheStripScrolls()
    {
        // Five tabs at the measured floor of 38 is 190, and the chevron wants 24 on top.
        var (cap, squeezing, scrolling, _) = TabStripPanel.Fit(Shell, 0, 180);

        Assert.True(squeezing);
        Assert.True(scrolling);
        Assert.Equal(RibbonMetrics.TabSqueezedWidth, cap);
    }

    /// <summary>
    /// The compose window's own state: seven tabs and "Tell me what you want to do" after them,
    /// squeezed but not scrolling, which is what the reference shows at 499.
    /// </summary>
    /// <remarks>
    /// The hint keeps its width — it is a sentence, not a tab — so it comes off the room the tabs
    /// are fitted into rather than being squeezed alongside them.
    /// </remarks>
    [Fact]
    public void TheHintKeepsItsWidthAndComesOffTheRoom()
    {
        double[] compose = [44, 74, 56, 62, 88, 62, 46];
        const double hint = 90;

        var (cap, squeezing, scrolling, _) = TabStripPanel.Fit(compose, hint, 420);

        Assert.True(squeezing);
        Assert.False(scrolling);
        Assert.True(
            compose.Sum(w => Math.Min(w, cap)) <= 420 - hint,
            "the tabs must fit in what the hint leaves, not in the whole strip.");
    }

    /// <summary>
    /// The hint is dropped rather than the strip scrolling behind it, and dropping it is then
    /// allowed to be the answer: with the room it gives back the tabs fit, so no chevron appears.
    /// </summary>
    /// <remarks>
    /// The order matters and getting it wrong is visible: deciding to scroll <em>and then</em>
    /// hiding the hint drew a chevron at a width where, with the hint gone, every tab fitted with
    /// room to spare — a control offering to reveal what was already on screen. The hint's width
    /// has to come back into the arithmetic, not just off the strip.
    /// </remarks>
    [Fact]
    public void DroppingTheHintIsAllowedToBeTheAnswer()
    {
        double[] compose = [44, 74, 56, 62, 88, 62, 46];

        // 432 natural. At 360 with a 150-wide hint the tabs cannot fit beside it — but without it
        // they fit squeezed, and that is the answer rather than a chevron.
        var fit = TabStripPanel.Fit(compose, 150, 360);

        Assert.False(fit.KeepTrailing);
        Assert.True(fit.Squeezing);
        Assert.False(fit.Scrolling);
        Assert.True(
            compose.Sum(w => Math.Min(w, fit.Cap)) <= 360,
            "with the hint gone the tabs must be fitted against the whole strip.");
    }

    /// <summary>An empty strip asks for nothing and squeezes nothing.</summary>
    [Fact]
    public void NoTabsIsNotASqueeze()
    {
        var (_, squeezing, scrolling, _) = TabStripPanel.Fit([], 0, 10);

        Assert.False(squeezing);
        Assert.False(scrolling);
    }
}
