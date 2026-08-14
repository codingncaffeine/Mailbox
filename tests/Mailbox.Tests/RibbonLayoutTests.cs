using Mailbox.Core;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

public class RibbonLayoutTests
{
    private static CommandCatalog Catalog()
    {
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        return catalog;
    }

    private static RibbonTab HomeTab()
        => DefaultRibbonLayouts.Mail.FindTab("home")
           ?? throw new InvalidOperationException("Mail layout has no Home tab.");

    [Fact]
    public void EveryPlacedCommandExistsInTheCatalogue()
    {
        var catalog = Catalog();
        catalog.RegisterRange(ViewCommands.All);

        foreach (var id in DefaultRibbonLayouts.Mail.PlacedCommands)
        {
            Assert.True(catalog.TryGet(id, out _), $"Layout places unknown command '{id}'.");
        }
    }

    /// <summary>
    /// the reference's Home tab, left to right. Order is the whole point of a clone — getting the
    /// commands right but the order wrong still reads as wrong.
    /// </summary>
    [Fact]
    public void HomeTabGroupsAreInOutlookOrder()
    {
        string[] expected =
        [
            "new", "delete", "respond", "quicksteps", "move", "tags", "find",
            "speech", "apps", "sendreceivegroup",
        ];
        Assert.Equal(expected, HomeTab().Groups.Select(g => g.Id).ToArray());
    }

    /// <summary>
    /// Current the reference application builds ship File, Home, Send/Receive, View, Help — no Folder tab.
    /// File is leftmost and opens the Backstage rather than swapping the ribbon beneath.
    /// </summary>
    [Fact]
    public void TabsAreInOutlookOrder()
    {
        string[] expected = ["file", "home", "sendreceive", "view", "help"];
        Assert.Equal(expected, DefaultRibbonLayouts.Mail.Tabs.Select(t => t.Id).ToArray());
    }

    [Fact]
    public void FileIsTheOnlyBackstageTabAndComesFirst()
    {
        var tabs = DefaultRibbonLayouts.Mail.Tabs;
        Assert.True(tabs[0].IsBackstage);
        Assert.Equal("file", tabs[0].Id);
        Assert.Single(tabs, t => t.IsBackstage);
    }

    /// <summary>
    /// Simplified is the reference application default, so every tab that shows commands needs a row
    /// for it — a tab with groups but no simplified row would render empty by default.
    /// </summary>
    [Fact]
    public void EveryTabWithCommandsHasASimplifiedRow()
    {
        foreach (var tab in DefaultRibbonLayouts.Mail.Tabs.Where(t => t.Groups.Count > 0))
        {
            Assert.True(DefaultRibbonLayouts.Mail.SimplifiedRows.ContainsKey(tab.Id),
                $"Tab '{tab.Id}' has classic groups but no Simplified row.");
        }
    }

    [Fact]
    public void SimplifiedRowsReferenceOnlyKnownCommands()
    {
        var catalog = Catalog();
        catalog.RegisterRange(ViewCommands.All);

        foreach (var (tabId, items) in DefaultRibbonLayouts.Mail.SimplifiedRows)
        {
            foreach (var item in items.Where(i => i.Kind != RibbonItemKind.Separator))
            {
                Assert.True(catalog.TryGet(item.Command, out _),
                    $"Simplified row '{tabId}' places unknown command '{item.Command}'.");
            }
        }
    }

    /// <summary>The Home row opens with New Email and ends with Send/Receive All Folders.</summary>
    [Fact]
    public void SimplifiedHomeRowMatchesOutlookOrder()
    {
        var row = DefaultRibbonLayouts.Mail.SimplifiedRows["home"]
            .Where(i => i.Kind != RibbonItemKind.Separator)
            .Select(i => i.Command.Value)
            .ToArray();

        Assert.Equal("mail.new", row[0]);
        Assert.Equal("app.sendreceive.all", row[^1]);

        // Reply / Reply All / Forward stay adjacent and in order.
        var reply = Array.IndexOf(row, "mail.reply");
        Assert.Equal("mail.reply.all", row[reply + 1]);
        Assert.Equal("mail.forward", row[reply + 2]);
    }

    [Fact]
    public void RespondGroupHasReplyReplyAllForwardAsLargeButtonsInOrder()
    {
        var respond = HomeTab().Groups.Single(g => g.Id == "respond");
        var large = respond.Items
            .Where(i => i.Size == RibbonItemSize.Large)
            .Select(i => i.Command.Value)
            .ToArray();

        Assert.Equal(["mail.reply", "mail.reply.all", "mail.forward"], large);
    }

    [Fact]
    public void DeleteGroupPutsDeleteAndArchiveLargeAfterTheSmallStack()
    {
        var delete = HomeTab().Groups.Single(g => g.Id == "delete");

        // Ignore / Clean Up / Junk stack small, then Delete and Archive large.
        Assert.Equal(
            ["mail.ignore", "mail.cleanup", "mail.junk"],
            delete.Items.Where(i => i.Size == RibbonItemSize.Small).Select(i => i.Command.Value).ToArray());

        Assert.Equal(
            ["mail.delete", "mail.archive"],
            delete.Items.Where(i => i.Size == RibbonItemSize.Large).Select(i => i.Command.Value).ToArray());
    }

    [Fact]
    public void TagsGroupIsUnreadCategorizeFollowUp()
    {
        var tags = HomeTab().Groups.Single(g => g.Id == "tags");
        Assert.Equal(
            ["mail.markunread", "item.categorize", "item.followup"],
            tags.Items.Select(i => i.Command.Value).ToArray());

        // Unread/Read is the large button; Categorize and Follow Up stack small beside it.
        Assert.Equal(RibbonItemSize.Large, tags.Items[0].Size);
        Assert.All(tags.Items.Skip(1), i => Assert.Equal(RibbonItemSize.Small, i.Size));
    }

    /// <summary>the reference application ships Send/Receive All Folders and Undo on the QAT, in that order.</summary>
    [Fact]
    public void QuickAccessToolbarMatchesOutlookDefault()
        => Assert.Equal(
            ["app.sendreceive.all", "app.undo"],
            DefaultRibbonLayouts.Mail.QuickAccess.Select(c => c.Value).ToArray());

    /// <summary>
    /// Rule 5 at the layout level: the shipped ribbon places only what the reference application places.
    /// Additions live in the catalogue and reach the ribbon through Customize Ribbon.
    /// </summary>
    [Fact]
    public void DefaultLayoutPlacesNothingBeyondOutlookParity()
    {
        var catalog = Catalog();
        var placed = DefaultRibbonLayouts.Mail.PlacedCommands.ToHashSet();

        foreach (var command in catalog.BeyondDefaultLayout)
        {
            Assert.DoesNotContain(command.Id, placed);
        }
    }

    [Fact]
    public void SnoozeAndViewSourceAreAbsentFromTheShippedRibbon()
    {
        var placed = DefaultRibbonLayouts.Mail.PlacedCommands.Select(c => c.Value).ToHashSet();

        Assert.DoesNotContain("mail.snooze", placed);
        Assert.DoesNotContain("mail.viewsource", placed);
        Assert.DoesNotContain("mail.trackers", placed);
    }

    [Fact]
    public void CollapsePrioritiesAreDistinctWithinATab()
    {
        foreach (var tab in DefaultRibbonLayouts.Mail.Tabs.Where(t => t.Groups.Count > 1))
        {
            var duplicates = tab.Groups
                .GroupBy(g => g.CollapsePriority)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0,
                $"Tab '{tab.Id}' reuses collapse priority {string.Join(", ", duplicates)}; " +
                "the degrade order would be undefined.");
        }
    }

    /// <summary>Respond is the last group the reference application collapses, because it is the most used.</summary>
    [Fact]
    public void RespondCollapsesLast()
    {
        var groups = HomeTab().Groups;
        var respond = groups.Single(g => g.Id == "respond");
        Assert.Equal(groups.Min(g => g.CollapsePriority), respond.CollapsePriority);
    }

    [Fact]
    public void TabKeyTipsAreUniqueAndWellFormed()
    {
        var keyTips = DefaultRibbonLayouts.Mail.Tabs
            .Where(t => t.KeyTip is not null)
            .Select(t => t.KeyTip!)
            .ToList();

        Assert.Equal(keyTips.Count, keyTips.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(keyTips, tip =>
        {
            Assert.InRange(tip.Length, 1, 3);
            Assert.Equal(tip.ToUpperInvariant(), tip);
        });
    }

    [Fact]
    public void GroupsWithNoItemsStillDeclareALabel()
        => Assert.All(DefaultRibbonLayouts.Mail.Tabs.SelectMany(t => t.Groups),
            g => Assert.False(string.IsNullOrWhiteSpace(g.Label)));

    [Fact]
    public void UnknownModuleGetsAnEmptyLayoutRatherThanThrowing()
    {
        var layout = DefaultRibbonLayouts.For(MailboxModule.Journal);
        Assert.Empty(layout.Tabs);
        Assert.Equal(MailboxModule.Journal, layout.Module);
    }
}

public class ShellLayoutModeTests
{
    [Theory]
    [InlineData("modern", ShellLayoutMode.Modern)]
    [InlineData("MODERN", ShellLayoutMode.Modern)]
    [InlineData("new", ShellLayoutMode.Modern)]
    [InlineData("classic", ShellLayoutMode.Classic)]
    [InlineData("nonsense", ShellLayoutMode.Classic)]
    [InlineData(null, ShellLayoutMode.Classic)]
    public void ResolvesFromTheEnvironment(string? value, ShellLayoutMode expected)
    {
        var previous = Environment.GetEnvironmentVariable(ShellLayoutModes.Variable);
        try
        {
            Environment.SetEnvironmentVariable(ShellLayoutModes.Variable, value);
            Assert.Equal(expected, ShellLayoutModes.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShellLayoutModes.Variable, previous);
        }
    }

    /// <summary>Classic is the default, because the clone is the product.</summary>
    [Fact]
    public void DefaultsToClassic()
    {
        var previous = Environment.GetEnvironmentVariable(ShellLayoutModes.Variable);
        try
        {
            Environment.SetEnvironmentVariable(ShellLayoutModes.Variable, null);
            Assert.Equal(ShellLayoutMode.Classic, ShellLayoutModes.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShellLayoutModes.Variable, previous);
        }
    }
}
