using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

public class ContextualTabTests
{
    private static RibbonTab Tab(string id, string? contextual = null) => new()
    {
        Id = id,
        Label = id,
        Groups = [],
        ContextualGroup = contextual,
    };

    private static readonly RibbonTab[] Tabs =
    [
        new() { Id = "file", Label = "File", Groups = [], IsBackstage = true },
        Tab("home"),
        Tab("view"),
        Tab("search", "search"),
        Tab("attachments", "attachment"),
    ];

    private static HashSet<string> Active(params string[] groups)
        => new(groups, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ContextualTabsAreAbsentUntilTheirSetIsActive()
        => Assert.Equal(
            ["file", "home", "view"],
            ContextualTabs.Visible(Tabs, Active()).Select(t => t.Id));

    [Fact]
    public void AnActiveSetBringsItsTabsBack()
        => Assert.Equal(
            ["file", "home", "view", "search"],
            ContextualTabs.Visible(Tabs, Active("search")).Select(t => t.Id));

    [Fact]
    public void SetsAreIndependentOfEachOther()
        => Assert.Equal(
            ["file", "home", "view", "search", "attachments"],
            ContextualTabs.Visible(Tabs, Active("search", "attachment")).Select(t => t.Id));

    [Fact]
    public void SetNamesAreMatchedWithoutRegardToCase()
        => Assert.Contains(
            ContextualTabs.Visible(Tabs, Active("SEARCH")),
            t => t.Id == "search");

    /// <summary>Declaring a set is what makes a tab contextual; there is no second flag to disagree with.</summary>
    [Fact]
    public void ATabIsContextualExactlyWhenItNamesASet()
    {
        Assert.False(Tab("home").IsContextual);
        Assert.True(Tab("search", "search").IsContextual);
    }

    [Fact]
    public void NoFallbackIsNeededWhileTheSelectedTabIsStillOnScreen()
        => Assert.Null(ContextualTabs.FallbackFor(Tabs, Active("search"), "search"));

    /// <summary>
    /// A set going away while one of its tabs is selected would otherwise leave the ribbon
    /// pointing at a tab that is not in the strip — an empty body under a strip with nothing
    /// highlighted, which reads as broken rather than as unfinished.
    /// </summary>
    [Fact]
    public void LosingTheSelectedTabFallsBackToTheFirstOrdinaryTab()
        => Assert.Equal("home", ContextualTabs.FallbackFor(Tabs, Active(), "search")?.Id);

    /// <summary>File opens the Backstage rather than a body, so it is never the fallback.</summary>
    [Fact]
    public void TheFallbackIsNeverTheBackstageTab()
        => Assert.NotEqual("file", ContextualTabs.FallbackFor(Tabs, Active(), "search")?.Id);

    [Fact]
    public void TheShippedMailLayoutDeclaresNoContextualTabsYet()
        => Assert.DoesNotContain(DefaultRibbonLayouts.Mail.Tabs, t => t.IsContextual);
}
