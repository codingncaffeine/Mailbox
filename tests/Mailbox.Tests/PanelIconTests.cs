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
/// three cache-clearing tools are absent on a build machine and are best-effort anyway. What
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
        var icon = new PanelIcon(Artwork, _theme);

        Assert.True(icon.Show(3));

        foreach (var size in PanelIcon.Sizes)
        {
            Assert.Equal($"{TrayArtwork.Full}-{size}", File.ReadAllText(FileFor(size)));
        }
    }

    [Fact]
    public void ReadingTheLastOnePutsTheEmptyOneBack()
    {
        var icon = new PanelIcon(Artwork, _theme);
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
        var icon = new PanelIcon(Artwork, _theme);

        Assert.True(icon.Show(0));
        Assert.Equal($"{TrayArtwork.Empty}-32", File.ReadAllText(FileFor(32)));
    }

    [Fact]
    public void ReadingMessagesWithoutEmptyingTheBoxTouchesNothing()
    {
        // The count changes with every message read and the drawing has two states. Only the
        // crossing costs anything; nine messages read out of ten cost nothing at all.
        var icon = new PanelIcon(Artwork, _theme);
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
        var icon = new PanelIcon((_, _) => null, _theme);

        Assert.False(icon.Show(5));
        Assert.False(Directory.Exists(_theme) && File.Exists(FileFor(32)));
    }

    [Fact]
    public void NoHalfWrittenFileIsLeftBehindWhenTheDrawingFails()
    {
        // A stream that dies part way through is the case the temporary file exists for: what
        // must never happen is a truncated PNG sitting where the desktop will read it.
        var icon = new PanelIcon((_, _) => new FailingStream(), _theme);

        Assert.False(icon.Show(1));
        Assert.False(File.Exists(FileFor(32)));

        var directory = Path.GetDirectoryName(FileFor(32))!;
        Assert.True(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0);
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
