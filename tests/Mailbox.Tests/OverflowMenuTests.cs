using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

/// <summary>
/// What the Simplified bar's "…" holds.
/// </summary>
/// <remarks>
/// The bug this pins: flat, the menu ran to fifty-odd alphabetical entries — taller than the
/// screen, and with no scrollbar it opened with its head cut off. The pushed-off commands were in
/// the menu and could not be found in it. The shape that answers it is the reference's: each of
/// the tab's groups as a heading with that group's commands under it, in ribbon order, and
/// nothing repeated from the bar. So what is checked here is that everything the bar is not
/// showing is offered, exactly once, under a heading that names where it came from.
/// </remarks>
public class OverflowMenuTests
{
    private static readonly CommandCatalog Catalog = Seeded();

    private static CommandCatalog Seeded()
    {
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        catalog.RegisterRange(ViewCommands.All);
        return catalog;
    }
    private static RibbonTab HomeTab => DefaultRibbonLayouts.Mail.FindTab("home")!;

    private static IReadOnlyList<RibbonItem> HomeBar =>
        [.. DefaultRibbonLayouts.Mail.SimplifiedRows["home"]];

    private static List<OverflowEntry> Plan(params CommandId[] pushedOff)
        => [.. OverflowMenu.Plan(
            HomeTab, HomeBar, pushedOff, [.. Catalog.BeyondDefaultLayout], id => Catalog.TryGet(id, out _))];

    /// <summary>
    /// Everything the bar pushed off is offered, under the heading of the group it belongs to
    /// rather than in a block of its own.
    /// </summary>
    /// <remarks>
    /// A command that moves between two places in the menu depending on the window's width is
    /// harder to find again than one that is always under its own group, and the reference's own
    /// squeezed capture starts straight at its first heading rather than at what just left.
    /// </remarks>
    [Fact]
    public void WhatThePushedOffCommandsAreOfferedUnderIsTheirOwnGroup()
    {
        var plan = Plan(MailCommands.FollowUp.Id, ViewCommands.Apps.Id, MailCommands.SendReceiveAll.Id);

        foreach (var id in new[]
                 { MailCommands.FollowUp.Id, ViewCommands.Apps.Id, MailCommands.SendReceiveAll.Id })
        {
            var at = plan.FindIndex(e => e.Command == id);
            Assert.True(at > 0, $"{id.Value} was pushed off and is not in the menu");

            // Everything sits under a heading: walking back from a row always reaches one.
            Assert.Contains(plan.Take(at), e => e.IsHeader);
        }
    }

    /// <summary>The menu is headings and rows, and every heading has something under it.</summary>
    [Fact]
    public void TheMenuIsHeadingsWithCommandsUnderThem()
    {
        var plan = Plan();

        Assert.True(plan[0].IsHeader, "the menu starts at a heading.");

        // Headings and rows, and one submenu at the end for the commands on no tab.
        Assert.All(plan, e => Assert.True(e.IsHeader || e.Command is not null || e.IsSubmenu));

        for (var i = 0; i < plan.Count; i++)
        {
            if (!plan[i].IsHeader) continue;
            Assert.True(
                i + 1 < plan.Count && !plan[i + 1].IsHeader,
                $"“{plan[i].Label}” is a heading with nothing under it.");
        }
    }

    /// <summary>
    /// Pushing the whole bar off adds its commands to the menu rather than replacing what was
    /// already there — the worst case is every command the tab has, once each.
    /// </summary>
    /// <remarks>
    /// The menu is long by nature and the reference's is too: squeezed right down, its own runs
    /// past the bottom of the screen. Length is the presenter's problem — it is bounded and
    /// scrolls — not a reason to leave a command out.
    /// </remarks>
    [Fact]
    public void PushingTheWholeBarOffOffersTheWholeBar()
    {
        var everything = HomeBar
            .Where(i => i.Kind != RibbonItemKind.Separator)
            .Select(i => i.Command)
            .ToArray();

        var plan = Plan(everything);
        var offered = plan.Where(e => e.Command is { } c && Catalog.TryGet(c, out _)).Select(e => e.Command).ToList();

        foreach (var id in everything.Where(id => Catalog.TryGet(id, out _)))
        {
            Assert.Contains(id, offered);
        }

        Assert.True(plan.Count > Plan().Count, "the menu grows as the bar sheds.");
    }

    /// <summary>A command is offered once: pushed off, or under its group, never both.</summary>
    [Fact]
    public void NothingIsOfferedTwice()
    {
        var plan = Plan(MailCommands.FollowUp.Id, MailCommands.SendReceiveAll.Id, MailCommands.FollowUp.Id);

        var offered = plan
            .SelectMany(e => e.Command is { } c ? new[] { c } : [.. e.Children])
            .ToList();

        Assert.Equal(offered.Count, offered.Distinct().Count());
    }

    /// <summary>What the bar is already carrying is on the bar, and is not listed as missing.</summary>
    [Fact]
    public void WhatTheBarCarriesIsNotListedUnderItsGroup()
    {
        var onTheBar = HomeBar
            .Where(i => i.Kind != RibbonItemKind.Separator)
            .Select(i => i.Command)
            .ToHashSet();

        var offered = Plan().Where(e => e.Command is not null).Select(e => e.Command!.Value).ToList();

        Assert.DoesNotContain(offered, id => onTheBar.Contains(id));
        Assert.NotEmpty(offered);
    }

    /// <summary>
    /// A command pushed off the bar is still offered even though it belongs to a group whose
    /// other commands are all on the bar — Follow Up's group is Tags, which is otherwise there.
    /// </summary>
    [Fact]
    public void APushedOffCommandIsOfferedEvenWhenItsGroupIsNot()
    {
        var plan = Plan(MailCommands.FollowUp.Id);

        Assert.Contains(plan, e => e.Command == MailCommands.FollowUp.Id);
    }

    /// <summary>
    /// Snooze and the rest are in the catalogue and on no tab; the last row is a submenu holding
    /// them.
    /// </summary>
    /// <remarks>
    /// A submenu rather than a heading with sixty-odd rows under it. Inlined, this one section
    /// took the menu from twenty-nine rows to ninety-four and buried the groups — the part that
    /// is the reference's shape — under a list of everything the application can do.
    /// </remarks>
    [Fact]
    public void TheCommandsOnNoTabAreReachableFromHere()
    {
        var beyond = Plan().SingleOrDefault(e => e.IsSubmenu && e.Label == OverflowMenu.BeyondLabel);

        Assert.NotNull(beyond);
        Assert.Contains(MailCommands.Snooze.Id, beyond!.Children);
    }

    /// <summary>Every entry the plan offers resolves to a real command.</summary>
    [Fact]
    public void EveryOfferedCommandIsInTheCatalogue()
    {
        foreach (var id in Plan(MailCommands.SendReceiveAll.Id)
                     .SelectMany(e => e.Command is { } c ? new[] { c } : [.. e.Children]))
        {
            Assert.True(Catalog.TryGet(id, out _), $"{id.Value} is offered and is not a command");
        }
    }
}
