using Mailbox.Core.Platform;

namespace Mailbox.Tests;

/// <summary>
/// How a write the launcher's unit refused is explained: the unit is named only where it is the
/// cause, and then with the folders that would have worked.
/// </summary>
public class ConfinementTests
{
    private static readonly IOException ReadOnly =
        new("Read-only file system : '/home/reader/Desktop/order.pdf'");

    private static readonly string[] Opened = ["/home/reader/Desktop", "/home/reader/Downloads"];

    [Fact]
    public void TheLauncherListIsColonSeparatedAndBlanksAreDropped()
    {
        Assert.Empty(Confinement.Places(null));
        Assert.Empty(Confinement.Places(" : "));
        Assert.Equal(new[] { "/a", "/b c" }, Confinement.Places("/a:/b c:"));
    }

    [Fact]
    public void WithinIsByDirectoryNotByPrefix()
    {
        Assert.True(Confinement.IsWithin("/home/reader/Desktop/order.pdf", Opened));
        Assert.True(Confinement.IsWithin("/home/reader/Desktop/deep/er/order.pdf", Opened));
        Assert.False(Confinement.IsWithin("/home/reader/Desktop2/order.pdf", Opened));
        Assert.False(Confinement.IsWithin("/home/reader/order.pdf", Opened));
        Assert.False(Confinement.IsWithin("/home/reader/order.pdf", []));
    }

    [Fact]
    public void OutsideTheOpenedFoldersTheUnitIsNamedWithTheFoldersAndTheEscape()
    {
        var text = Confinement.ExplainWriteFailure(
            "/home/reader/Projects/order.pdf", ReadOnly, confined: true, Opened);

        Assert.StartsWith("Cannot create file: order.pdf.", text);
        Assert.Contains(ReadOnly.Message, text);
        Assert.Contains("/home/reader/Desktop", text);
        Assert.Contains("/home/reader/Downloads", text);
        Assert.Contains(Confinement.Escape, text);
    }

    [Fact]
    public void InsideAnOpenedFolderTheSystemsReasonStandsAlone()
    {
        var text = Confinement.ExplainWriteFailure(
            "/home/reader/Desktop/order.pdf", new IOException("No space left on device"),
            confined: true, Opened);

        Assert.Equal("Cannot create file: order.pdf." + Environment.NewLine + "No space left on device", text);
    }

    [Fact]
    public void UnconfinedNoUnitIsBlamed()
    {
        var text = Confinement.ExplainWriteFailure(
            "/home/reader/Projects/order.pdf", ReadOnly, confined: false, Opened);

        Assert.DoesNotContain("confined", text);
        Assert.DoesNotContain(Confinement.Escape, text);
    }

    [Fact]
    public void ConfinedWithNothingOpenedOffersOnlyTheEscape()
    {
        var text = Confinement.ExplainWriteFailure(
            "/home/reader/Desktop/order.pdf", ReadOnly, confined: true, []);

        Assert.Contains("cannot save outside its own folders", text);
        Assert.Contains(Confinement.Escape, text);
        Assert.DoesNotContain("Choose one", text);
    }
}
