using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// Signatures, and which account uses which.
/// </summary>
/// <remarks>
/// Stored as one JSON string beside the rest of the preferences, so the settings file stays
/// something a person can read and edit — which is why half of this is about what happens when
/// somebody has edited it badly.
/// </remarks>
public class SignatureTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"mailbox-sig-{Guid.NewGuid():n}.json");

    private Signatures Fresh() => new(new SettingsStore(_path));

    private static Signature Block(string name = "Work") => new()
    {
        Name = name,
        Text = "A. Person\nMailbox",
        Html = "<p>A. Person</p><p>Mailbox</p>",
    };

    // ---- Keeping them ---------------------------------------------------------------------

    [Fact]
    public void ASignatureSurvivesARestart()
    {
        Fresh().Save(Block());

        var found = Assert.Single(Fresh().All);
        Assert.Equal("Work", found.Name);
        Assert.Equal("<p>A. Person</p><p>Mailbox</p>", found.Html);
        Assert.Equal("A. Person\nMailbox", found.Text);
    }

    /// <summary>
    /// Both halves are stored rather than one derived from the other. A message goes out as HTML
    /// with a plain text alternative beside it, and a signature that existed only as markup
    /// would arrive in the text half as angle brackets.
    /// </summary>
    [Fact]
    public void BothFormsAreKept()
    {
        var signature = Fresh();
        signature.Save(Block());

        var found = Assert.Single(signature.All);
        Assert.NotEqual(found.Html, found.Text);
        Assert.DoesNotContain("<p>", found.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingTheSameNameReplacesRatherThanDuplicates()
    {
        var signatures = Fresh();
        signatures.Save(Block());
        signatures.Save(Block() with { Text = "Changed" });

        Assert.Equal("Changed", Assert.Single(signatures.All).Text);
    }

    [Fact]
    public void OneCanBeRemoved()
    {
        var signatures = Fresh();
        signatures.Save(Block());
        signatures.Remove("Work");

        Assert.Empty(Fresh().All);
    }

    [Fact]
    public void ASignatureWithNoNameIsNotSaved()
    {
        var signatures = Fresh();
        signatures.Save(new Signature { Name = "   ", Text = "x" });

        Assert.Empty(signatures.All);
    }

    [Fact]
    public void AnEmptyOneKnowsItIs()
    {
        Assert.True(new Signature { Name = "Blank" }.IsEmpty);
        Assert.False(Block().IsEmpty);
    }

    // ---- Which account uses which -----------------------------------------------------------

    /// <summary>
    /// None is the default and has to stay a real choice. A client that puts a block of text on
    /// the first message somebody writes is one they have to go and find a setting to stop.
    /// </summary>
    [Fact]
    public void NothingIsChosenToBeginWith()
    {
        var signatures = Fresh();
        signatures.Save(Block());

        Assert.Null(signatures.ForNew("you@example.com"));
        Assert.Null(signatures.ForReply("you@example.com"));
    }

    [Fact]
    public void AnAccountsChoiceSurvivesARestart()
    {
        var signatures = Fresh();
        signatures.Save(Block());
        signatures.UseForNew("you@example.com", "Work");

        Assert.Equal("Work", Fresh().ForNew("you@example.com")?.Name);
    }

    /// <summary>New messages and replies are chosen separately, which is the point of having both.</summary>
    [Fact]
    public void NewAndReplyAreChosenSeparately()
    {
        var signatures = Fresh();
        signatures.Save(Block("Full"));
        signatures.Save(Block("Short"));

        signatures.UseForNew("you@example.com", "Full");
        signatures.UseForReply("you@example.com", "Short");

        Assert.Equal("Full", signatures.ForNew("you@example.com")?.Name);
        Assert.Equal("Short", signatures.ForReply("you@example.com")?.Name);
    }

    [Fact]
    public void TwoAccountsChooseIndependently()
    {
        var signatures = Fresh();
        signatures.Save(Block("Home"));
        signatures.Save(Block("Work"));

        signatures.UseForNew("me@example.com", "Home");
        signatures.UseForNew("me@example.net", "Work");

        Assert.Equal("Home", signatures.ForNew("me@example.com")?.Name);
        Assert.Equal("Work", signatures.ForNew("me@example.net")?.Name);
    }

    /// <summary>Keyed off the address, so it does not matter how it was typed.</summary>
    [Fact]
    public void TheAddressIsMatchedWithoutCase()
    {
        var signatures = Fresh();
        signatures.Save(Block());
        signatures.UseForNew("You@Example.com", "Work");

        Assert.Equal("Work", signatures.ForNew("you@example.com")?.Name);
    }

    [Fact]
    public void AChoiceCanBeTakenBack()
    {
        var signatures = Fresh();
        signatures.Save(Block());
        signatures.UseForNew("you@example.com", "Work");
        signatures.UseForNew("you@example.com", null);

        Assert.Null(signatures.ForNew("you@example.com"));
    }

    /// <summary>A signature removed is not one an account still signs with.</summary>
    [Fact]
    public void ChoosingOneThatIsGoneYieldsNothing()
    {
        var signatures = Fresh();
        signatures.Save(Block());
        signatures.UseForNew("you@example.com", "Work");
        signatures.Remove("Work");

        Assert.Null(signatures.ForNew("you@example.com"));
    }

    // ---- A settings file somebody edited ------------------------------------------------------

    /// <summary>
    /// One bad entry costs that signature rather than all of them, and rather than the
    /// application starting. The settings file is one a person may edit by hand, and this is
    /// what makes that safe to do.
    /// </summary>
    [Fact]
    public void AMalformedEntryCostsOnlyItself()
    {
        var settings = new SettingsStore(_path);
        settings.Set(Signatures.Key,
            """[{"name":"Good","html":"<p>x</p>","text":"x"},{"html":"no name"},{"name":"Also"}]""");

        var signatures = new Signatures(settings);

        Assert.Equal(2, signatures.All.Count);
        Assert.Equal(["Good", "Also"], signatures.All.Select(s => s.Name));
    }

    [Fact]
    public void RubbishInTheFileMeansNoSignaturesRatherThanNoApplication()
    {
        var settings = new SettingsStore(_path);
        settings.Set(Signatures.Key, "not json at all");

        Assert.Empty(new Signatures(settings).All);
    }

    [Fact]
    public void RubbishInTheChoiceMeansNoChoiceRatherThanNoApplication()
    {
        var settings = new SettingsStore(_path);
        settings.Set(Signatures.Key, """[{"name":"Work","html":"<p>x</p>","text":"x"}]""");
        settings.Set("mail.signatures.new", "{ not json");

        Assert.Null(new Signatures(settings).ForNew("you@example.com"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception)
        {
            // A scratch file that will not delete is not a test failure.
        }
    }
}
