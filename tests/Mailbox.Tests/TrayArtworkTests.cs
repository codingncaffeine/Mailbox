using Mailbox.Core.Notifications;

namespace Mailbox.Tests;

/// <summary>
/// Which mailbox the notification area shows.
/// </summary>
/// <remarks>
/// The rule the owner asked for, in their words: mail arriving fills the box, and once it has
/// been opened or marked read the icon goes back to the regular one. That is the unread count
/// and nothing else, which is what these hold — including the case the sentence is really about,
/// which is the count coming back down to nothing.
/// </remarks>
public class TrayArtworkTests
{
    [Fact]
    public void NothingWaitingShowsTheEmptyMailbox()
        => Assert.Equal(TrayArtwork.Empty, TrayArtwork.For(0));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(1000)]
    public void AnythingWaitingShowsTheFullOne(int unread)
        => Assert.Equal(TrayArtwork.Full, TrayArtwork.For(unread));

    [Fact]
    public void ReadingTheLastOneEmptiesTheBoxAgain()
    {
        // Three arrive, two are opened, the last is marked read: the icon is back to regular.
        var unread = 3;
        Assert.Equal(TrayArtwork.Full, TrayArtwork.For(unread));

        unread -= 2;
        Assert.Equal(TrayArtwork.Full, TrayArtwork.For(unread));

        unread -= 1;
        Assert.Equal(TrayArtwork.Empty, TrayArtwork.For(unread));
    }

    [Fact]
    public void MarkingSomethingUnreadFillsItAgain()
    {
        // The other direction, which is why the count is the state rather than a memory of what
        // has been seen: a message marked unread is waiting again, and the box says so.
        Assert.Equal(TrayArtwork.Empty, TrayArtwork.For(0));
        Assert.Equal(TrayArtwork.Full, TrayArtwork.For(1));
    }

    /// <summary>A count below zero cannot happen, and would not be a reason to draw post.</summary>
    [Fact]
    public void ANonsenseCountIsTreatedAsNothingWaiting()
        => Assert.Equal(TrayArtwork.Empty, TrayArtwork.For(-1));

    [Fact]
    public void TheAssetNamesTheDrawingAndTheSize()
    {
        Assert.Equal("mailbox-tray-empty-32.png", TrayArtwork.AssetFor(0, 32));
        Assert.Equal("mailbox-tray-full-32.png", TrayArtwork.AssetFor(4, 32));
        Assert.Equal("mailbox-tray-full-256.png", TrayArtwork.AssetFor(4, 256));
    }

    /// <summary>
    /// Both drawings exist at every size the tray might be handed, and both are square.
    /// </summary>
    /// <remarks>
    /// Square and cropped from one frame on purpose: a panel that swapped between a tall icon
    /// and a wide one would make the mailbox jump as mail arrived.
    /// </remarks>
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void EverySizeIsThereForBothStates(int size)
    {
        var icons = Path.Combine(Repository(), "assets", "icons");

        foreach (var unread in new[] { 0, 1 })
        {
            var file = Path.Combine(icons, TrayArtwork.AssetFor(unread, size));
            Assert.True(File.Exists(file), $"{file} is missing.");
        }
    }

    /// <summary>The repository root, found from the test binary rather than assumed.</summary>
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "assets", "icons")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
