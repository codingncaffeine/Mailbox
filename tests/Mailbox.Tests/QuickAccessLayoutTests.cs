using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

public class QuickAccessLayoutTests
{
    private static readonly CommandId SendReceive = new("app.sendreceive.all");
    private static readonly CommandId Undo = new("app.undo");
    private static readonly CommandId NewEmail = new("mail.new");

    private static QuickAccessLayout Fresh(SettingsStore? settings = null)
        => new(settings ?? SettingsStore.Transient(), [SendReceive, Undo]);

    [Fact]
    public void StartsFromTheShippedToolbarWhenNothingIsStored()
        => Assert.Equal([SendReceive, Undo], Fresh().Commands);

    /// <summary>
    /// Modify…: a name of this reader's own, an icon of their own, or both — kept against the
    /// command's stable id, so reordering the bar cannot move somebody's choice onto a different
    /// button.
    /// </summary>
    [Fact]
    public void ModifyKeepsANameAndAnIconAgainstTheCommandAndSurvivesARestart()
    {
        var settings = SettingsStore.Transient();
        var layout = Fresh(settings);

        Assert.Null(layout.OverrideFor(SendReceive));

        layout.Modify(SendReceive, "Get Mail", "arrow-sync");
        layout.Modify(Undo, name: null, icon: "arrow-undo");

        var again = Fresh(settings);
        Assert.Equal(new QuickAccessOverride("Get Mail", "arrow-sync"), again.OverrideFor(SendReceive));
        Assert.Equal(new QuickAccessOverride(null, "arrow-undo"), again.OverrideFor(Undo));

        // Reordering the bar leaves both where they were: the entry is against the id.
        again.Add(NewEmail);
        again.MoveAt(0, 1);
        Assert.Equal("Get Mail", again.OverrideFor(SendReceive)!.Name);
    }

    /// <summary>Reset is both fields empty, which takes the entry out rather than storing nulls.</summary>
    [Fact]
    public void ModifyingBackToNothingForgetsTheCommandEntirely()
    {
        var settings = SettingsStore.Transient();
        var layout = Fresh(settings);

        layout.Modify(SendReceive, "Get Mail", "arrow-sync");
        layout.Modify(SendReceive, null, null);

        Assert.Null(Fresh(settings).OverrideFor(SendReceive));
    }

    [Fact]
    public void ShowingCommandLabelsIsOffUntilItIsAskedForAndThenSurvivesARestart()
    {
        var settings = SettingsStore.Transient();
        Assert.False(Fresh(settings).ShowLabels);

        var layout = Fresh(settings);
        layout.ShowLabels = true;

        Assert.True(Fresh(settings).ShowLabels);
    }

    [Fact]
    public void AddedCommandsGoOnTheEnd()
    {
        var layout = Fresh();
        layout.Add(NewEmail);

        Assert.Equal([SendReceive, Undo, NewEmail], layout.Commands);
    }

    [Fact]
    public void AddingTwiceIsNotTwoButtons()
    {
        var layout = Fresh();
        layout.Add(NewEmail);
        layout.Add(NewEmail);

        Assert.Single(layout.Commands, id => id == NewEmail);
    }

    [Fact]
    public void TogglePlacesAndThenRemoves()
    {
        var layout = Fresh();

        layout.Toggle(NewEmail);
        Assert.True(layout.Contains(NewEmail));

        layout.Toggle(NewEmail);
        Assert.False(layout.Contains(NewEmail));
    }

    [Fact]
    public void MoveIsClampedRatherThanWrapping()
    {
        var layout = Fresh();

        Assert.False(layout.Move(SendReceive, -1));
        Assert.Equal([SendReceive, Undo], layout.Commands);

        Assert.True(layout.Move(SendReceive, 5));
        Assert.Equal([Undo, SendReceive], layout.Commands);
    }

    [Fact]
    public void ResetRestoresTheShippedToolbarAndItsPlacement()
    {
        var layout = Fresh();
        layout.Add(NewEmail);
        layout.Remove(SendReceive);
        layout.Placement = QuickAccessPlacement.BelowRibbon;
        layout.IsVisible = false;

        layout.Reset();

        Assert.Equal([SendReceive, Undo], layout.Commands);
        Assert.Equal(QuickAccessPlacement.AboveRibbon, layout.Placement);
        Assert.True(layout.IsVisible);
    }

    /// <summary>
    /// An empty toolbar is a choice, and reading it back as "never customized" would restore
    /// the shipped buttons on the next launch — silently undoing what the user did.
    /// </summary>
    [Fact]
    public void AnEmptyToolbarSurvivesAReload()
    {
        var settings = SettingsStore.Transient();

        var first = Fresh(settings);
        first.Replace([]);

        Assert.Empty(new QuickAccessLayout(settings, [SendReceive, Undo]).Commands);
    }

    [Fact]
    public void CommandsPlacementAndVisibilityAllSurviveAReload()
    {
        var settings = SettingsStore.Transient();

        var first = Fresh(settings);
        first.Replace([NewEmail, Undo]);
        first.Placement = QuickAccessPlacement.BelowRibbon;
        first.IsVisible = false;

        var second = new QuickAccessLayout(settings, [SendReceive, Undo]);

        Assert.Equal([NewEmail, Undo], second.Commands);
        Assert.Equal(QuickAccessPlacement.BelowRibbon, second.Placement);
        Assert.False(second.IsVisible);
    }

    /// <summary>
    /// The settings file is meant to be editable by hand, so it is allowed to be wrong. One bad
    /// id costs one button, not the launch.
    /// </summary>
    [Fact]
    public void AMalformedStoredIdIsDroppedRatherThanThrown()
    {
        var settings = SettingsStore.Transient();
        settings.Set(QuickAccessLayout.CommandsKey, "app.undo,NOT A COMMAND,mail.new");

        Assert.Equal([Undo, NewEmail], new QuickAccessLayout(settings, [SendReceive]).Commands);
    }

    /// <summary>Posing is for the harness, so it must leave the settings file alone.</summary>
    [Fact]
    public void PosingDoesNotPersist()
    {
        var settings = SettingsStore.Transient();

        var posed = Fresh(settings);
        posed.Pose(QuickAccessPlacement.BelowRibbon, visible: false);

        Assert.Equal(QuickAccessPlacement.BelowRibbon, posed.Placement);
        Assert.False(posed.IsVisible);

        var reloaded = new QuickAccessLayout(settings, [SendReceive, Undo]);
        Assert.Equal(QuickAccessPlacement.AboveRibbon, reloaded.Placement);
        Assert.True(reloaded.IsVisible);
    }

    /// <summary>Every command the flyout offers has to be a command the catalogue knows.</summary>
    [Fact]
    public void EveryCustomizeCandidateExistsInTheCatalogue()
    {
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        catalog.RegisterRange(ViewCommands.All);

        foreach (var id in DefaultRibbonLayouts.QuickAccessCandidates)
        {
            Assert.True(catalog.TryGet(id, out _), $"Candidate '{id}' is not registered.");
        }
    }

    /// <summary>The shipped toolbar is offered in the flyout, or it cannot be put back.</summary>
    [Fact]
    public void TheShippedToolbarIsAmongTheCandidates()
        => Assert.All(
            DefaultRibbonLayouts.Mail.QuickAccess,
            id => Assert.Contains(id, DefaultRibbonLayouts.QuickAccessCandidates));

    /// <summary>
    /// A rule is furniture rather than a command, so unlike a command it may appear more than
    /// once — a toolbar of three clusters needs two of them.
    /// </summary>
    [Fact]
    public void RulesMayRepeatWhereCommandsMayNot()
    {
        var layout = Fresh();
        layout.AddSeparator();
        layout.AddSeparator();

        Assert.Equal(
            [SendReceive, Undo, RibbonItem.SeparatorId, RibbonItem.SeparatorId],
            layout.Commands);
    }

    /// <summary>
    /// Which is why the editor works by position: removing the second rule by id would take
    /// the first.
    /// </summary>
    [Fact]
    public void RemovingByPositionTakesTheOneMeant()
    {
        var layout = Fresh();
        layout.AddSeparator();
        layout.Add(NewEmail);
        layout.AddSeparator();

        Assert.True(layout.RemoveAt(4));

        Assert.Equal([SendReceive, Undo, RibbonItem.SeparatorId, NewEmail], layout.Commands);
    }

    [Fact]
    public void MovingByPositionWalksAnEntryAlong()
    {
        var layout = Fresh();
        layout.Add(NewEmail);

        Assert.True(layout.MoveAt(2, -2));
        Assert.Equal([NewEmail, SendReceive, Undo], layout.Commands);

        Assert.False(layout.MoveAt(0, -1));
        Assert.False(layout.MoveAt(2, 1));
    }

    [Fact]
    public void RulesSurviveBeingStoredAndReadBack()
    {
        var settings = SettingsStore.Transient();

        var layout = Fresh(settings);
        layout.AddSeparator();

        Assert.Equal(layout.Commands, Fresh(settings).Commands);
    }
}
