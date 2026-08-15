using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// Quick Steps: the shipped five, their persistence, the commands they stand for, and the way
/// the gallery is rendered from the list without disturbing the shipped layout.
/// </summary>
public class QuickStepTests
{
    [Fact]
    public void TheShippedStepsAreTheReferencesAndTheFirstThreeKeepTheirCommandIds()
    {
        var steps = new QuickSteps(SettingsStore.Transient()).All;

        Assert.Equal(["Move to: ?", "To Manager", "Team Email", "Done", "Reply & Delete"], steps.Select(s => s.Name));
        Assert.Equal(ViewCommands.MoveToQuick.Id, steps[0].CommandId);
        Assert.Equal(ViewCommands.ToManager.Id, steps[1].CommandId);
        Assert.Equal(ViewCommands.TeamEmail.Id, steps[2].CommandId);
        Assert.Equal(new CommandId("quickstep.done"), steps[3].CommandId);

        // The three that ask for a folder or an address are set up on first use.
        Assert.True(steps[0].NeedsSetup);
        Assert.True(steps[1].NeedsSetup);
        Assert.True(steps[2].NeedsSetup);
        Assert.True(steps[3].NeedsSetup);
        Assert.False(steps[4].NeedsSetup);
    }

    [Fact]
    public void StepsPersistAndReloadWithTheirActionsAndShortcut()
    {
        var settings = SettingsStore.Transient();
        var quick = new QuickSteps(settings);
        var changed = 0;
        quick.Changed += (_, _) => changed++;

        var mine = new QuickStep
        {
            Id = QuickSteps.NewId(),
            Name = "File and flag",
            Icon = "flag",
            Shortcut = 3,
            Tooltip = "Files it and flags it.",
            Actions =
            [
                new QuickStepAction(QuickStepKind.MoveToFolder) { FolderId = 9, FolderName = "Projects" },
                new QuickStepAction(QuickStepKind.FlagMessage) { Level = 1 },
                new QuickStepAction(QuickStepKind.Categorize) { Values = ["Blue Category"] },
            ],
        };
        quick.Upsert(mine);
        Assert.Equal(1, changed);

        var reloaded = new QuickSteps(settings);
        var back = reloaded.Find(mine.Id)!;
        Assert.Equal("File and flag", back.Name);
        Assert.Equal(3, back.Shortcut);
        Assert.Equal(3, back.Actions.Count);
        Assert.Equal("Projects", back.Actions[0].FolderName);
        Assert.Equal(1, back.Actions[1].Level);
        Assert.Equal(["Blue Category"], back.Actions[2].Values);
        Assert.False(back.NeedsSetup);

        var command = back.ToCommand();
        Assert.Equal(new CommandId("quickstep." + mine.Id), command.Id);
        Assert.Equal("Ctrl+Shift+3", command.DefaultGesture);
        Assert.Equal("Files it and flags it.", command.Description);
        Assert.False(command.InDefaultLayout);

        // Upsert replaces in place; Remove removes; Reset brings the shipped five back.
        quick.Upsert(back with { Name = "Renamed" });
        Assert.Equal("Renamed", reloaded.Find(mine.Id) is null ? quick.Find(mine.Id)!.Name : quick.Find(mine.Id)!.Name);
        quick.Remove(mine.Id);
        Assert.Null(quick.Find(mine.Id));
        quick.Reset();
        Assert.Equal(5, quick.All.Count);
    }

    [Fact]
    public void ADescriptionNamesEachActionAndItsValue()
    {
        Assert.Equal("Move to folder: Projects", new QuickStepAction(QuickStepKind.MoveToFolder) { FolderName = "Projects" }.Describe());
        Assert.Equal("Move to folder: (choose on first use)", new QuickStepAction(QuickStepKind.MoveToFolder).Describe());
        Assert.Equal("Forward to: a@example.com; b@example.com", new QuickStepAction(QuickStepKind.Forward) { Values = ["a@example.com", "b@example.com"] }.Describe());
        Assert.Equal("Flag message: tomorrow", new QuickStepAction(QuickStepKind.FlagMessage) { Level = 1 }.Describe());
        Assert.Equal("Set importance: High", new QuickStepAction(QuickStepKind.SetImportance) { Level = 2 }.Describe());
    }

    [Fact]
    public void TheGalleryIsRenderedFromTheListAndTheShippedLayoutIsUnchangedByTheDefaults()
    {
        var shipped = DefaultRibbonLayouts.Mail;
        var defaults = new QuickSteps(SettingsStore.Transient()).All;

        var injected = QuickStepsRibbon.Inject(shipped, defaults);
        var group = injected.Tabs.First(t => t.Id == "home").Groups.First(g => g.Id == "quicksteps");

        // Five entries — the shipped three first, in the shipped order, then Done and Reply & Delete.
        Assert.Equal(
            [ViewCommands.MoveToQuick.Id, ViewCommands.ToManager.Id, ViewCommands.TeamEmail.Id, new("quickstep.done"), new("quickstep.replydelete")],
            group.Items.Select(i => i.Command));

        // The Simplified bar's boxed entry is still the first step, so the capture is unchanged.
        var cluster = injected.Simplified["home"].Groups.First(g => g.Id == "quicksteps");
        Assert.Equal(ViewCommands.MoveToQuick.Id, cluster.Items[0].Command);
        Assert.Equal(RibbonItemKind.BoxedButton, cluster.Items[0].Kind);

        // Every other group is exactly the shipped one.
        var before = shipped.Tabs.First(t => t.Id == "home").Groups.Where(g => g.Id != "quicksteps").ToList();
        var after = injected.Tabs.First(t => t.Id == "home").Groups.Where(g => g.Id != "quicksteps").ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void AReorderedListReordersTheGalleryAndTheBoxedEntry()
    {
        var quick = new QuickSteps(SettingsStore.Transient());
        var reversed = quick.All.Reverse().ToList();
        quick.Replace(reversed);

        var injected = QuickStepsRibbon.Inject(DefaultRibbonLayouts.Mail, quick.All);
        var group = injected.Tabs.First(t => t.Id == "home").Groups.First(g => g.Id == "quicksteps");
        Assert.Equal(new CommandId("quickstep.replydelete"), group.Items[0].Command);
        Assert.Equal(new CommandId("quickstep.replydelete"), injected.Simplified["home"].Groups.First(g => g.Id == "quicksteps").Items[0].Command);
    }
}
