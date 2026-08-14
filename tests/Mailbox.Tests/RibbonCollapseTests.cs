using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

/// <summary>
/// The degrade order, tested against widths rather than against a window that has to be sized
/// and photographed. This is the reason <see cref="RibbonCollapsePolicy"/> holds no UI type.
/// </summary>
public class RibbonCollapseTests
{
    /// <summary>Three groups of equal width, collapsing in priority order 3 → 2 → 1.</summary>
    private static RibbonGroupWidth[] Three() =>
    [
        new("first", CollapsePriority: 1, Normal: 100, Compact: 60, Popup: 30),
        new("second", CollapsePriority: 2, Normal: 100, Compact: 60, Popup: 30),
        new("third", CollapsePriority: 3, Normal: 100, Compact: 60, Popup: 30),
    ];

    [Fact]
    public void NothingCollapsesWhenEverythingFits()
        => Assert.All(
            RibbonCollapsePolicy.Choose(Three(), available: 300),
            v => Assert.Equal(RibbonGroupVariant.Normal, v));

    [Fact]
    public void ExactFitIsAFitRatherThanAnOverflow()
        => Assert.All(
            RibbonCollapsePolicy.Choose(Three(), available: 300),
            v => Assert.Equal(RibbonGroupVariant.Normal, v));

    /// <summary>The highest priority number is the first to give way, not the last group drawn.</summary>
    [Fact]
    public void TheHighestPriorityGivesWayFirst()
    {
        var chosen = RibbonCollapsePolicy.Choose(Three(), available: 299);

        Assert.Equal(
            [RibbonGroupVariant.Normal, RibbonGroupVariant.Normal, RibbonGroupVariant.Compact],
            chosen);
    }

    /// <summary>
    /// A group is spent completely before the next one is touched. Degrading every group by one
    /// step would also fit, and would make the whole ribbon worse at once.
    /// </summary>
    [Fact]
    public void AGroupDegradesFullyBeforeTheNextIsTouched()
    {
        Assert.Equal(
            [RibbonGroupVariant.Normal, RibbonGroupVariant.Normal, RibbonGroupVariant.Popup],
            RibbonCollapsePolicy.Choose(Three(), available: 250));

        Assert.Equal(
            [RibbonGroupVariant.Normal, RibbonGroupVariant.Compact, RibbonGroupVariant.Popup],
            RibbonCollapsePolicy.Choose(Three(), available: 200));
    }

    [Fact]
    public void EverythingCollapsesBeforeAnythingIsGivenUpOn()
        => Assert.All(
            RibbonCollapsePolicy.Choose(Three(), available: 100),
            v => Assert.Equal(RibbonGroupVariant.Popup, v));

    /// <summary>
    /// Narrower than every group's popup put together. There is nothing left to give, so the
    /// answer is "all collapsed" and the host clips — not a loop looking for a fit that is not
    /// there.
    /// </summary>
    [Fact]
    public void ImpossiblyNarrowSettlesRatherThanSpinning()
        => Assert.All(
            RibbonCollapsePolicy.Choose(Three(), available: 1),
            v => Assert.Equal(RibbonGroupVariant.Popup, v));

    /// <summary>
    /// A gallery group has no large buttons, so its compact rendering is the same width as its
    /// normal one. The step still has to count as progress or the loop never ends.
    /// </summary>
    [Fact]
    public void AVariantThatSavesNothingStillTerminates()
    {
        RibbonGroupWidth[] gallery =
            [new("gallery", CollapsePriority: 1, Normal: 100, Compact: 100, Popup: 30)];

        Assert.Equal(
            [RibbonGroupVariant.Popup],
            RibbonCollapsePolicy.Choose(gallery, available: 50));
    }

    /// <summary>The separators between groups take width that no group can collapse away.</summary>
    [Fact]
    public void FurnitureCountsAgainstTheAvailableWidth()
    {
        Assert.All(
            RibbonCollapsePolicy.Choose(Three(), available: 320, furniture: 0),
            v => Assert.Equal(RibbonGroupVariant.Normal, v));

        Assert.Equal(
            [RibbonGroupVariant.Normal, RibbonGroupVariant.Normal, RibbonGroupVariant.Compact],
            RibbonCollapsePolicy.Choose(Three(), available: 320, furniture: 30));
    }

    /// <summary>
    /// The first measure pass routinely arrives unconstrained. Collapsing there would settle the
    /// ribbon into a shape it then has to climb back out of, one frame later and visibly.
    /// </summary>
    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnUnknownWidthCollapsesNothing(double available)
        => Assert.All(
            RibbonCollapsePolicy.Choose(Three(), available),
            v => Assert.Equal(RibbonGroupVariant.Normal, v));

    [Fact]
    public void NoGroupsIsNotAnError()
        => Assert.Empty(RibbonCollapsePolicy.Choose([], available: 100));

    /// <summary>
    /// The priorities the shipped Home tab actually declares, at a width where only one group
    /// can stay whole. Respond is authored lowest precisely so that it is the one.
    /// </summary>
    [Fact]
    public void RespondIsTheLastGroupStanding()
    {
        var groups = DefaultRibbonLayouts.Mail.FindTab("home")!.Groups;

        var widths = groups
            .Select(g => new RibbonGroupWidth(g.Id, g.CollapsePriority, 100, 60, 30))
            .ToArray();

        var chosen = RibbonCollapsePolicy.Choose(widths, available: 400);

        var whole = groups
            .Where((_, i) => chosen[i] == RibbonGroupVariant.Normal)
            .Select(g => g.Id)
            .ToArray();

        Assert.Equal(["respond"], whole);
    }
}
