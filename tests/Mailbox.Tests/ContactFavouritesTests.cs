using Mailbox.Core.People;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// The favourite contacts: the short list the To-Do Bar's People section shows.
/// </summary>
public class ContactFavouritesTests
{
    [Fact]
    public void AFavouriteIsKeptInTheOrderItWasAdded()
    {
        var settings = SettingsStore.Transient();
        var favourites = new ContactFavourites(settings);

        favourites.Add("a@example.com");
        favourites.Add("b@example.com");
        favourites.Add("a@example.com");   // once

        Assert.Equal(["a@example.com", "b@example.com"], favourites.All);
        Assert.True(favourites.Contains("A@EXAMPLE.COM"));
        Assert.False(favourites.Contains("c@example.com"));
    }

    [Fact]
    public void TheOneGestureIsAToggle()
    {
        var settings = SettingsStore.Transient();
        var favourites = new ContactFavourites(settings);

        Assert.True(favourites.Toggle("uid-1"));
        Assert.Single(favourites.All);
        Assert.False(favourites.Toggle("uid-1"));
        Assert.Empty(favourites.All);
    }

    [Fact]
    public void TheListSurvivesARestart()
    {
        var settings = SettingsStore.Transient();
        new ContactFavourites(settings).Add("uid-1");

        // Kept by the card's UID, so a store restored with new row ids still names the same people.
        Assert.Equal(["uid-1"], new ContactFavourites(settings).All);
    }

    [Fact]
    public void ASettingsFileSomebodyEditedByHandDoesNotCostTheAddressBook()
    {
        var settings = SettingsStore.Transient();
        settings.Set(ContactFavourites.Key, "{ not json ]");

        Assert.Empty(new ContactFavourites(settings).All);
    }
}
