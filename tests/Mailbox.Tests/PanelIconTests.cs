using System.Text;
using Mailbox.Core.Notifications;
using Mailbox.Core.Platform;

namespace Mailbox.Tests;

/// <summary>
/// The icon the desktop draws for the application, which on Plasma is the only one the taskbar
/// reads.
/// </summary>
/// <remarks>
/// The behaviour under test is the file writing and the crossing rule, not the desktop: the
/// three cache-clearing tools are recorded rather than run, since run they would tell whatever
/// desktop the tests are on. What
/// matters here is that every size the installer put down is replaced together — a ladder left
/// half full and half empty draws one mailbox at one panel size and the other mailbox at the
/// next — and that ordinary reading never touches the disk.
/// </remarks>
public class PanelIconTests : IDisposable
{
    private readonly string _theme = Path.Combine(
        Path.GetTempPath(), $"mailbox-panelicon-{Guid.NewGuid():N}");

    private readonly List<string> _asked = [];

    /// <summary>Stands in for the application's embedded artwork: the state's name, as bytes.</summary>
    private Stream? Artwork(string art, int size)
    {
        _asked.Add($"{art}-{size}");
        return new MemoryStream(Encoding.UTF8.GetBytes($"{art}-{size}"));
    }

    private readonly List<string> _ran = [];

    /// <summary>
    /// Stands in for the desktop's tools, which would otherwise tell the desktop this runs on — a
    /// signal to every KDE application and a rebuild of its service database, on every test run.
    /// </summary>
    private bool Tool(string command, string[] arguments)
    {
        _ran.Add(command);
        return true;
    }

    private string FileFor(int size)
        => Path.Combine(_theme, $"{size}x{size}", "apps", $"{PanelIcon.IconName}.png");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_theme)) Directory.Delete(_theme, recursive: true); }
        catch (IOException) { /* a temporary directory that outlives the test harms nobody */ }
    }

    [Fact]
    public void MailWaitingPutsTheFullMailboxAtEverySize()
    {
        var icon = new PanelIcon(Artwork, _theme, tool: Tool);

        Assert.True(icon.Show(3));

        foreach (var size in PanelIcon.Sizes)
        {
            Assert.Equal($"{TrayArtwork.Full}-{size}", File.ReadAllText(FileFor(size)));
        }
    }

    [Fact]
    public void ReadingTheLastOnePutsTheEmptyOneBack()
    {
        var icon = new PanelIcon(Artwork, _theme, tool: Tool);
        icon.Show(3);

        Assert.True(icon.Show(0));

        foreach (var size in PanelIcon.Sizes)
        {
            Assert.Equal($"{TrayArtwork.Empty}-{size}", File.ReadAllText(FileFor(size)));
        }
    }

    [Fact]
    public void TheFirstCallWritesEvenWithNothingWaiting()
    {
        // The files on disk are whatever the last session left there, so a run that starts with
        // an empty mailbox still has to say so.
        var icon = new PanelIcon(Artwork, _theme, tool: Tool);

        Assert.True(icon.Show(0));
        Assert.Equal($"{TrayArtwork.Empty}-32", File.ReadAllText(FileFor(32)));
    }

    [Fact]
    public void ReadingMessagesWithoutEmptyingTheBoxTouchesNothing()
    {
        // The count changes with every message read and the drawing has two states. Only the
        // crossing costs anything; nine messages read out of ten cost nothing at all.
        var icon = new PanelIcon(Artwork, _theme, tool: Tool);
        icon.Show(10);

        var afterFirst = _asked.Count;

        Assert.False(icon.Show(9));
        Assert.False(icon.Show(4));
        Assert.False(icon.Show(1));
        Assert.Equal(afterFirst, _asked.Count);

        Assert.True(icon.Show(0));
    }

    [Fact]
    public void ArtworkThatCannotBeOpenedIsNotReportedAsShown()
    {
        var icon = new PanelIcon((_, _) => null, _theme, tool: Tool);

        Assert.False(icon.Show(5));
        Assert.False(Directory.Exists(_theme) && File.Exists(FileFor(32)));
    }

    [Fact]
    public void NoHalfWrittenFileIsLeftBehindWhenTheDrawingFails()
    {
        // A stream that dies part way through is the case the temporary file exists for: what
        // must never happen is a truncated PNG sitting where the desktop will read it.
        var icon = new PanelIcon((_, _) => new FailingStream(), _theme, tool: Tool);

        Assert.False(icon.Show(1));
        Assert.False(File.Exists(FileFor(32)));

        var directory = Path.GetDirectoryName(FileFor(32))!;
        Assert.True(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0);
    }

    [Fact]
    public void TheDesktopIsToldOncePerCrossingInTheOrderThatWorks()
    {
        // The pixmap cache is told before the service database is rebuilt: the other way round,
        // the panel resolves the name again and caches the old drawing a second time.
        var icon = new PanelIcon(Artwork, _theme, tool: Tool);

        icon.Show(2);
        Assert.Equal(["gtk-update-icon-cache", "dbus-send", "kbuildsycoca6"], _ran);

        icon.Show(1);
        Assert.Equal(3, _ran.Count);
    }

    [Fact]
    public void NothingDrawnIsNothingAnnounced()
    {
        var icon = new PanelIcon((_, _) => null, _theme, tool: Tool);

        icon.Show(2);
        Assert.Empty(_ran);
    }

    // ---- Asked for, under the launcher's sandbox -------------------------------------------

    private string RequestFile => Path.Combine(_theme, "runtime", "panel-icon");

    [Fact]
    public void UnderTheSandboxTheStateIsAskedForAndNothingIsDrawn()
    {
        var icon = new PanelIcon(Artwork, _theme, request: RequestFile, tool: Tool);

        Assert.True(icon.Asks);
        Assert.True(icon.Show(2));

        Assert.Equal(PanelIcon.FullWord, File.ReadAllText(RequestFile));
        Assert.True(PanelIcon.ReadRequest(RequestFile));
        Assert.Empty(_asked);
        Assert.Empty(_ran);
        Assert.False(File.Exists(FileFor(32)));
    }

    [Fact]
    public void AskingFollowsTheSameCrossingsDrawingDoes()
    {
        var icon = new PanelIcon(Artwork, _theme, request: RequestFile, tool: Tool);
        Assert.True(icon.Show(4));

        var asked = File.GetLastWriteTimeUtc(RequestFile);
        File.SetLastWriteTimeUtc(RequestFile, asked.AddHours(-1));

        // Reading without emptying the box asks for nothing new.
        Assert.False(icon.Show(3));
        Assert.Equal(asked.AddHours(-1), File.GetLastWriteTimeUtc(RequestFile));

        Assert.True(icon.Show(0));
        Assert.False(PanelIcon.ReadRequest(RequestFile));
    }

    [Fact]
    public void AskingLeavesNoTemporaryFileBehind()
    {
        var icon = new PanelIcon(Artwork, _theme, request: RequestFile, tool: Tool);
        icon.Show(1);
        icon.Show(0);

        Assert.Equal(["panel-icon"], Directory.GetFiles(Path.GetDirectoryName(RequestFile)!).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("full", true)]
    [InlineData("empty", false)]
    [InlineData("full\n", true)]
    [InlineData(" empty \n", false)]
    public void ARequestSaysWhichMailbox(string written, bool full)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RequestFile)!);
        File.WriteAllText(RequestFile, written);

        Assert.Equal(full, PanelIcon.ReadRequest(RequestFile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Full")]
    [InlineData("fullfullfullfullfull")]
    [InlineData("half")]
    public void AnythingElseAsksForNothing(string written)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RequestFile)!);
        File.WriteAllText(RequestFile, written);

        Assert.Null(PanelIcon.ReadRequest(RequestFile));
    }

    [Fact]
    public void AWordThatOnlyLooksRightAsksForNothing()
    {
        // "füll" in UTF-8, one letter from a word that means something.
        Directory.CreateDirectory(Path.GetDirectoryName(RequestFile)!);
        File.WriteAllBytes(RequestFile, [(byte)'f', 0xC3, 0xBC, (byte)'l', (byte)'l']);

        Assert.Null(PanelIcon.ReadRequest(RequestFile));
    }

    [Fact]
    public void AMissingRequestAsksForNothing()
        => Assert.Null(PanelIcon.ReadRequest(RequestFile));

    [Fact]
    public void ALinkIsNotFollowed()
    {
        // The file is written from inside the sandbox and read from outside it: a link planted in
        // its place must not point the reader at something else, even something that says "full".
        var target = Path.Combine(_theme, "elsewhere");
        Directory.CreateDirectory(Path.GetDirectoryName(RequestFile)!);
        File.WriteAllText(target, PanelIcon.FullWord);
        File.CreateSymbolicLink(RequestFile, target);

        Assert.Null(PanelIcon.ReadRequest(RequestFile));
    }

    /// <summary>Reads a few bytes and then gives up, the way a truncated asset would.</summary>
    private sealed class FailingStream : Stream
    {
        private int _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read > 0) throw new IOException("the drawing stopped part way through");
            _read = Math.Min(count, 8);
            return _read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
