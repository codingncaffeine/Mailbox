using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// The addresses an account may send as, beyond its own.
/// </summary>
/// <remarks>
/// Stored as one JSON string beside the rest of the preferences, so the settings file stays
/// something a person can read and edit — which is why a share of this is about what happens
/// when somebody has edited it badly.
/// </remarks>
public class IdentityTests : IDisposable
{
    private const string Account = "you@example.com";

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"mailbox-identity-{Guid.NewGuid():n}.json");

    private Identities Fresh() => new(new SettingsStore(_path));

    private static Identity Alias(string address = "sales@example.com") => new()
    {
        Address = address,
        DisplayName = "Sales",
        ReplyTo = "you@example.com",
        Organization = "Example Ltd",
    };

    // ---- The account's own ------------------------------------------------------------------

    /// <summary>
    /// An account with nothing stored still has one identity: itself. Anything else would mean a
    /// From menu that offers nothing on a fresh profile.
    /// </summary>
    [Fact]
    public void AnAccountAlwaysHasItsOwnIdentity()
    {
        var only = Assert.Single(Fresh().Of(Account, "A. Person"));

        Assert.Equal(Account, only.Address);
        Assert.Equal("A. Person", only.DisplayName);
        Assert.True(only.IsAccountDefault);
    }

    /// <summary>
    /// The account's own is synthesized rather than stored, so renaming the account renames the
    /// identity — a stored copy would be the one place the old name survived.
    /// </summary>
    [Fact]
    public void TheAccountsOwnIdentityFollowsTheAccount()
    {
        var identities = Fresh();
        identities.Save(Account, [Alias()]);

        Assert.Equal("The New Name", Fresh().Of(Account, "The New Name")[0].DisplayName);
    }

    /// <summary>Saving the account's own address back is not a second identity for it.</summary>
    [Fact]
    public void TheAccountsOwnAddressIsNeverStoredAsAnExtra()
    {
        Fresh().Save(Account, [new Identity { Address = Account, DisplayName = "Again" }]);

        Assert.Empty(Fresh().Extras(Account));
        Assert.Single(Fresh().Of(Account, "A. Person"));
    }

    // ---- Keeping them -----------------------------------------------------------------------

    [Fact]
    public void AnIdentitySurvivesARestart()
    {
        Fresh().Save(Account, [Alias()]);

        var stored = Assert.Single(Fresh().Extras(Account));
        Assert.Equal("sales@example.com", stored.Address);
        Assert.Equal("Sales", stored.DisplayName);
        Assert.Equal("you@example.com", stored.ReplyTo);
        Assert.Equal("Example Ltd", stored.Organization);
        Assert.False(stored.IsAccountDefault);
    }

    /// <summary>The order is part of what is being said, so it is kept.</summary>
    [Fact]
    public void TheOrderIsKept()
    {
        Fresh().Save(Account, [Alias("second@example.com"), Alias("first@example.com")]);

        Assert.Equal(
            [Account, "second@example.com", "first@example.com"],
            Fresh().Of(Account, "A. Person").Select(i => i.Address));
    }

    /// <summary>Saving replaces that account's list whole, which is how a removal is written.</summary>
    [Fact]
    public void SavingReplacesTheAccountsListRatherThanAddingToIt()
    {
        var identities = Fresh();
        identities.Save(Account, [Alias("one@example.com"), Alias("two@example.com")]);
        identities.Save(Account, [Alias("two@example.com")]);

        Assert.Equal(["two@example.com"], Fresh().Extras(Account).Select(i => i.Address));
    }

    /// <summary>One account's identities are not another's.</summary>
    [Fact]
    public void OneAccountsIdentitiesAreItsOwn()
    {
        var identities = Fresh();
        identities.Save(Account, [Alias("mine@example.com")]);
        identities.Save("work@example.net", [Alias("theirs@example.net")]);

        Assert.Equal(["mine@example.com"], Fresh().Extras(Account).Select(i => i.Address));
        Assert.Equal(["theirs@example.net"], Fresh().Extras("work@example.net").Select(i => i.Address));
    }

    /// <summary>An account being removed takes its identities with it.</summary>
    [Fact]
    public void RemovingAnAccountForgetsItsIdentities()
    {
        var identities = Fresh();
        identities.Save(Account, [Alias()]);
        identities.Save("work@example.net", [Alias("theirs@example.net")]);

        identities.Forget(Account);

        Assert.Empty(Fresh().Extras(Account));
        Assert.Single(Fresh().Extras("work@example.net"));
    }

    // ---- Finding the account behind an address ----------------------------------------------

    /// <summary>
    /// The lookup the compose window's send path lives on: an alias is not an account, and the
    /// message still has to go out through one.
    /// </summary>
    [Fact]
    public void AnIdentityKnowsWhichAccountCarriesIt()
    {
        var identities = Fresh();
        identities.Save(Account, [Alias()]);

        Assert.Equal(Account, identities.AccountFor("sales@example.com"));
        Assert.Equal(Account, identities.AccountFor("SALES@EXAMPLE.COM"));
    }

    /// <summary>An address nobody claims is null rather than a guess.</summary>
    [Fact]
    public void AnUnknownAddressBelongsToNoAccount()
    {
        Fresh().Save(Account, [Alias()]);

        Assert.Null(Fresh().AccountFor("stranger@example.org"));
        Assert.Null(Fresh().AccountFor(string.Empty));
    }

    /// <summary>
    /// The account's own address is not a stored identity, so the lookup does not claim it —
    /// the caller finds that one among the accounts before it asks here.
    /// </summary>
    [Fact]
    public void TheAccountsOwnAddressIsNotFoundHere()
    {
        Fresh().Save(Account, [Alias()]);

        Assert.Null(Fresh().AccountFor(Account));
    }

    // ---- What a person may have typed into the file -----------------------------------------

    /// <summary>One unreadable entry costs that identity rather than all of them.</summary>
    [Fact]
    public void OneBadEntryDoesNotCostTheRest()
    {
        var settings = new SettingsStore(_path);
        settings.Set(Identities.Key,
            """[{"account":"you@example.com","address":"good@example.com"},{"account":"you@example.com"},{"address":"orphan@example.com"}]""");

        Assert.Equal(["good@example.com"], new Identities(settings).Extras(Account).Select(i => i.Address));
    }

    /// <summary>A file that is not a list at all starts empty rather than throwing.</summary>
    [Fact]
    public void RubbishInTheFileLeavesEveryAccountWithItsOwnAddress()
    {
        var settings = new SettingsStore(_path);
        settings.Set(Identities.Key, "not json at all");

        Assert.Empty(new Identities(settings).Extras(Account));
        Assert.Single(new Identities(settings).Of(Account, "A. Person"));
    }

    /// <summary>An entry with no address is not an identity, however it got there.</summary>
    [Fact]
    public void AnIdentityWithNoAddressIsNotSaved()
    {
        Fresh().Save(Account, [new Identity { Address = "   " }, Alias()]);

        Assert.Equal(["sales@example.com"], Fresh().Extras(Account).Select(i => i.Address));
    }

    // ---- How it reads ------------------------------------------------------------------------

    /// <summary>The From menu's wording: the name and the address, or the address alone.</summary>
    [Fact]
    public void TheLabelIsTheNameAndTheAddress()
    {
        Assert.Equal("Sales  (sales@example.com)", Alias().Label);
        Assert.Equal("bare@example.com", new Identity { Address = "bare@example.com" }.Label);
        Assert.Equal(
            "same@example.com",
            new Identity { Address = "same@example.com", DisplayName = "same@example.com" }.Label);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (File.Exists(_path)) File.Delete(_path);
    }
}
