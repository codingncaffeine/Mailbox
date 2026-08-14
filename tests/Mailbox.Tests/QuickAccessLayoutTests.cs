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
}
