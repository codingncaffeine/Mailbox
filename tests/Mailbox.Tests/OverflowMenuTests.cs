using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

/// <summary>
/// What the Simplified bar's "…" holds.
/// </summary>
/// <remarks>
/// The bug this pins: flat, the menu ran to fifty-odd alphabetical entries — taller than the
/// screen, and with no scrollbar it opened with its head cut off. The pushed-off commands were
/// in the menu and could not be found in it. So two things are checked here: that everything the
/// bar took away is offered, and that the menu stays short enough to be read.
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

    private static IReadOnlyList<OverflowEntry> Plan(params CommandId[] pushedOff)
        => OverflowMenu.Plan(
            HomeTab, HomeBar, pushedOff, [.. Catalog.BeyondDefaultLayout], id => Catalog.TryGet(id, out _));

    [Fact]
    public void EverythingTheBarPushedOffIsOfferedFirstAndInOrder()
    {
        var plan = Plan(MailCommands.FollowUp.Id, ViewCommands.Apps.Id, MailCommands.SendReceiveAll.Id);

        Assert.Equal(MailCommands.FollowUp.Id, plan[0].Command);
        Assert.Equal(ViewCommands.Apps.Id, plan[1].Command);
        Assert.Equal(MailCommands.SendReceiveAll.Id, plan[2].Command);
        Assert.True(plan[3].IsRule);
    }

    /// <summary>Nothing pushed off means no rule hanging at the top of the menu.</summary>
    [Fact]
    public void WithNothingPushedOffTheMenuStartsAtTheGroups()
    {
        var plan = Plan();

        Assert.DoesNotContain(plan, e => e.IsRule);
        Assert.All(plan, e => Assert.True(e.IsSubmenu));
    }

    /// <summary>
    /// The reason for the grouping: a menu taller than the screen loses its head, and its head
    /// is the part that matters.
    /// </summary>
    [Fact]
    public void TheMenuStaysShortEnoughToRead()
    {
        var everything = HomeBar
            .Where(i => i.Kind != RibbonItemKind.Separator)
            .Select(i => i.Command)
            .ToArray();

        var plan = Plan(everything);

        // Worst case — the whole bar in the menu — and still a screenful.
        Assert.True(plan.Count <= 24, $"the menu came to {plan.Count} rows");
        Assert.True(Plan().Count <= 12, "the menu is long before anything is even pushed off");
    }

    /// <summary>A command is offered once: pushed off, or under its group, never both.</summary>
    [Fact]
    public void NothingIsOfferedTwice()
    {
        var plan = Plan(MailCommands.FollowUp.Id, MailCommands.SendReceiveAll.Id, MailCommands.FollowUp.Id);

        var offered = plan
            .SelectMany(e => e.Command is { } c ? [c] : e.Children)
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

        var underGroups = Plan().SelectMany(e => e.Children).ToList();

        Assert.DoesNotContain(underGroups, id => onTheBar.Contains(id));
        Assert.NotEmpty(underGroups);
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

    /// <summary>Snooze and the rest are in the catalogue and on no tab; the last submenu has them.</summary>
    [Fact]
    public void TheCommandsOnNoTabAreReachableFromHere()
    {
        var beyond = Plan().SingleOrDefault(e => e.Label == OverflowMenu.BeyondLabel);

        Assert.NotNull(beyond);
        Assert.Contains(MailCommands.Snooze.Id, beyond!.Children);
    }

    /// <summary>Every entry the plan offers resolves to a real command.</summary>
    [Fact]
    public void EveryOfferedCommandIsInTheCatalogue()
    {
        foreach (var id in Plan(MailCommands.SendReceiveAll.Id)
                     .SelectMany(e => e.Command is { } c ? [c] : e.Children))
        {
            Assert.True(Catalog.TryGet(id, out _), $"{id.Value} is offered and is not a command");
        }
    }
}
