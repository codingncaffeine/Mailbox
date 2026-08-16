using Mailbox.Core.Folders;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

public class FavouritesTests
{
    [Fact]
    public void AFreshProfileIsSeededOnceWithTheDefaultAccountsThree()
    {
        var settings = SettingsStore.Transient();
        var favourites = new Favourites(settings);
        Assert.False(favourites.IsSeeded);

        Assert.True(favourites.SeedIfFresh("you@example.com", ["Inbox", "Sent Items", "Deleted Items"]));
        Assert.Equal(["Inbox", "Sent Items", "Deleted Items"], favourites.All.Select(f => f.Path));
        Assert.All(favourites.All, f => Assert.Equal("you@example.com", f.Address));

        // Seeded means written: a second start finds the section and does not seed again.
        Assert.True(favourites.IsSeeded);
        Assert.False(new Favourites(settings).SeedIfFresh("other@example.com", ["Inbox"]));
    }

    [Fact]
    public void AnEmptiedSectionStaysEmpty()
    {
        var settings = SettingsStore.Transient();
        var favourites = new Favourites(settings);
        favourites.SeedIfFresh("you@example.com", ["Inbox"]);
        favourites.Remove("you@example.com", "Inbox");

        var back = new Favourites(settings);
        Assert.Empty(back.All);
        Assert.False(back.SeedIfFresh("you@example.com", ["Inbox"]));
    }

    [Fact]
    public void ShowInFavoritesAppendsOnceAndRemoveTakesItOut()
    {
        var favourites = new Favourites(SettingsStore.Transient());
        var changes = 0;
        favourites.Changed += (_, _) => changes++;

        favourites.Add("you@example.com", "Projects/Mailbox");
        favourites.Add("you@example.com", "Projects/Mailbox");
        favourites.Add("work@example.net", "Inbox");

        Assert.Equal(2, favourites.All.Count);
        Assert.True(favourites.Contains("You@Example.com", "Projects/Mailbox"));
        Assert.Equal(2, changes);

        Assert.True(favourites.Remove("you@example.com", "Projects/Mailbox"));
        Assert.False(favourites.Remove("you@example.com", "Projects/Mailbox"));
        Assert.Equal(["work@example.net"], favourites.All.Select(f => f.Address));
    }

    [Fact]
    public void TheListRoundTripsThroughTheSettingsFile()
    {
        var settings = SettingsStore.Transient();
        var favourites = new Favourites(settings);
        favourites.Add("you@example.com", "Inbox");
        favourites.Add("work@example.net", "Projects/2026");

        var back = new Favourites(settings);
        Assert.Equal(favourites.All, back.All);
        Assert.Contains("\"path\":\"Projects/2026\"", settings.GetString(Favourites.Key));
    }

    [Fact]
    public void MoveUpAndDownReorderAndClamp()
    {
        var favourites = new Favourites(SettingsStore.Transient());
        favourites.Add("a", "One");
        favourites.Add("a", "Two");
        favourites.Add("a", "Three");

        Assert.True(favourites.Move("a", "Three", -1));
        Assert.Equal(["One", "Three", "Two"], favourites.All.Select(f => f.Path));
        Assert.False(favourites.Move("a", "One", -1));
        Assert.True(favourites.Move("a", "One", 5));
        Assert.Equal(["Three", "Two", "One"], favourites.All.Select(f => f.Path));
    }

    [Fact]
    public void ARenamedOrMovedFolderKeepsItsPlaceInTheSection()
    {
        var favourites = new Favourites(SettingsStore.Transient());
        favourites.Add("a", "Projects");
        favourites.Add("a", "Projects/Mailbox");
        favourites.Add("b", "Projects");

        favourites.Repath("a", "Projects", "Work");

        Assert.Equal(["Work", "Work/Mailbox", "Projects"], favourites.All.Select(f => f.Path));
        Assert.Equal("b", favourites.All[2].Address);
    }

    [Fact]
    public void AHandEditThatIsNotJsonOrLacksAFieldIsSkippedNotFatal()
    {
        var settings = SettingsStore.Transient();
        settings.Set(Favourites.Key, "not json");
        Assert.Empty(new Favourites(settings).All);

        settings.Set(Favourites.Key, """[{"account":"a","path":"Inbox"},{"account":"b"},{"path":"x"}]""");
        Assert.Equal([new FavouriteFolder("a", "Inbox")], new Favourites(settings).All);
    }
}
