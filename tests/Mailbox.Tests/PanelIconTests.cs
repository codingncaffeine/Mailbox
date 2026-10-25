using System.Text;
using Mailbox.Core.Notifications;
using Mailbox.Core.Platform;

namespace Mailbox.Tests;

/// <summary>
/// The icon the desktop draws for the application, which on Plasma is the only one the taskbar
/// reads.
/// </summary>
/// <remarks>
/// The behaviour under test is which icon the reader's desktop entry names, and what the desktop
/// is told about it — not the desktop: the housekeeping tools are recorded rather than run, since
/// run they would tell whatever desktop the tests are on. What matters here is that every
/// crossing moves the entry from one fixed name to the other, that no icon file behind a name is
/// ever redrawn, and that the desktop is never sent the icon-theme broadcast that took the
/// compositor down — while ordinary reading never touches the disk at all.
/// </remarks>
public class PanelIconTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mailbox-panelicon-{Guid.NewGuid():N}");

    private string Home => Path.Combine(_root, "home");

    private string System => Path.Combine(_root, "system");

    private const string Packaged =
        "[Desktop Entry]\nType=Application\nName=Mailbox\nExec=mailbox %U\nTryExec=mailbox\nIcon=mailbox\n"
        + "MimeType=x-scheme-handler/mailto;\nActions=Compose;\n\n"
        + "[Desktop Action Compose]\nName=New Email\nExec=mailbox --compose\nIcon=mailbox\n";

    private readonly List<string> _asked = [];

    public PanelIconTests()
    {
        Directory.CreateDirectory(Path.Combine(System, "applications"));
        File.WriteAllText(Path.Combine(System, "applications", PanelIcon.EntryName), Packaged);
    }

    /// <summary>Stands in for the application's embedded artwork: the state's name, as bytes.</summary>
    private Stream? Artwork(string art, int size)
    {
        _asked.Add($"{art}-{size}");
        return new MemoryStream(Encoding.UTF8.GetBytes($"{art}-{size}"));
    }

    private readonly List<string> _ran = [];

    /// <summary>
    /// Stands in for the desktop's tools, which would otherwise tell the desktop this runs on —
    /// a rebuild of its service database on every test run. A <c>dbus-send</c> is recorded with
    /// what it would have said, because that is the one call that must never be made.
    /// </summary>
    private bool Tool(string command, string[] arguments)
    {
        _ran.Add(command == "dbus-send" ? $"dbus-send {string.Join(' ', arguments)}" : command);
        return true;
    }

    private PanelIcon Icon(Func<string, int, Stream?>? artwork = null, string? request = null)
        => new(artwork ?? Artwork, Home, request, Tool, [System]);

    private string Entry => File.ReadAllText(Path.Combine(Home, "applications", PanelIcon.EntryName));

    private static string[] IconLines(string entry)
        => entry.Split('\n').Where(line => line.StartsWith("Icon=", StringComparison.Ordinal)).ToArray();

    private static string ThemeFile(string root, string name, int size)
        => Path.Combine(root, "icons", "hicolor", $"{size}x{size}", "apps", $"{name}.png");

    /// <summary>Installs both mailboxes where a package puts them.</summary>
    private void InstallPackagedIcons()
    {
        foreach (var name in new[] { PanelIcon.EmptyIconName, PanelIcon.FullIconName })
        {
            foreach (var size in PanelIcon.Sizes)
            {
                var file = ThemeFile(System, name, size);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, $"packaged {name}-{size}");
            }
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temporary directory that outlives the test harms nobody */ }
    }

    [Fact]
    public void MailWaitingPointsTheEntryAtTheFullMailbox()
    {
        InstallPackagedIcons();

        Assert.True(Icon().Show(3));

        // The launcher's own line and every action's: a menu drawing the empty mailbox beside a
        // panel drawing the full one would contradict it.
        Assert.Equal(["Icon=mailbox-full", "Icon=mailbox-full"], IconLines(Entry));
    }

    [Fact]
    public void ReadingTheLastOnePointsItAtTheEmptyOne()
    {
        InstallPackagedIcons();
        var icon = Icon();
        icon.Show(3);

        Assert.True(icon.Show(0));
        Assert.Equal(["Icon=mailbox-empty", "Icon=mailbox-empty"], IconLines(Entry));
    }

    [Fact]
    public void TheFirstCallWritesEvenWithNothingWaiting()
    {
        // The entry on disk is whatever the last session left there, so a run that starts with
        // an empty mailbox still has to say so.
        InstallPackagedIcons();
        Icon().Show(5);

        Assert.True(Icon().Show(0));
        Assert.Equal(["Icon=mailbox-empty", "Icon=mailbox-empty"], IconLines(Entry));
    }

    [Fact]
    public void NoIconFileIsEverRedrawnAndTheDesktopIsNeverToldToForgetOne()
    {
        // The regression this class was rebuilt for. Redrawing the files behind a name needs the
        // KIconLoader iconChanged broadcast, which changes the icon-theme key in every KDE
        // process and lets KWin free one icon engine twice. A crossing is now the entry and the
        // service database, nothing else.
        InstallPackagedIcons();
        var icon = Icon();

        icon.Show(2);
        icon.Show(0);
        icon.Show(7);

        Assert.Equal(["kbuildsycoca6", "kbuildsycoca6", "kbuildsycoca6"], _ran);
        Assert.Empty(_asked);
        Assert.False(Directory.Exists(Path.Combine(Home, "icons")));
    }

    [Fact]
    public void ReadingMessagesWithoutEmptyingTheBoxTouchesNothing()
    {
        // The count changes with every message read and the drawing has two states. Only the
        // crossing costs anything; nine messages read out of ten cost nothing at all.
        InstallPackagedIcons();
        var icon = Icon();
        icon.Show(10);

        var written = File.GetLastWriteTimeUtc(Path.Combine(Home, "applications", PanelIcon.EntryName));
        var ran = _ran.Count;

        Assert.False(icon.Show(9));
        Assert.False(icon.Show(4));
        Assert.False(icon.Show(1));
        Assert.Equal(ran, _ran.Count);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(Home, "applications", PanelIcon.EntryName)));

        Assert.True(icon.Show(0));
    }

    [Fact]
    public void TheCopyKeepsEverythingButTheIconAndSaysWhoseItIs()
    {
        InstallPackagedIcons();
        Icon().Show(1);

        var entry = Entry;
        Assert.Contains($"{PanelIcon.CopyKey}=true", entry);
        Assert.Contains("TryExec=mailbox\n", entry);
        Assert.Contains("MimeType=x-scheme-handler/mailto;\n", entry);
        Assert.Equal(Packaged, PanelIcon.Repoint(
            string.Join('\n', entry.Split('\n').Where(line =>
                !line.StartsWith('#') && line != $"{PanelIcon.CopyKey}=true")), PanelIcon.IconName));
    }

    [Fact]
    public void TheCopyIsMadeAgainFromThePackagedEntry()
    {
        // A package upgrade changes the packaged entry; the next session's first crossing has to
        // carry that over rather than keep serving the old copy.
        InstallPackagedIcons();
        Icon().Show(1);

        File.WriteAllText(Path.Combine(System, "applications", PanelIcon.EntryName),
            Packaged.Replace("Name=Mailbox\n", "Name=Mailbox\nKeywords=email;\n", StringComparison.Ordinal));

        Icon().Show(1);
        Assert.Contains("Keywords=email;\n", Entry);
        Assert.Equal(["Icon=mailbox-full", "Icon=mailbox-full"], IconLines(Entry));
    }

    [Fact]
    public void TheReadersOwnEntryIsEditedRatherThanReplaced()
    {
        // install-local.sh puts the entry itself under the reader's data directory, pointed at
        // its own binary. That one is the reader's: only its icon line may change.
        InstallPackagedIcons();
        var own = "[Desktop Entry]\nType=Application\nName=Mailbox\nExec=/home/r/.local/bin/mailbox %U\n"
            + "Icon=mailbox\nNoDisplay=false\n";
        Directory.CreateDirectory(Path.Combine(Home, "applications"));
        File.WriteAllText(Path.Combine(Home, "applications", PanelIcon.EntryName), own);

        var icon = Icon();
        Assert.True(icon.Show(1));
        Assert.Equal(own.Replace("Icon=mailbox\n", "Icon=mailbox-full\n", StringComparison.Ordinal), Entry);

        Assert.True(icon.Show(0));
        Assert.Equal(own.Replace("Icon=mailbox\n", "Icon=mailbox-empty\n", StringComparison.Ordinal), Entry);
    }

    [Theory]
    [InlineData("Icon=mailbox", "Icon=mailbox-full")]
    [InlineData("Icon=mailbox-empty", "Icon=mailbox-full")]
    [InlineData("Icon=mailbox-full\r", "Icon=mailbox-full")]
    [InlineData("Icon=/home/r/pictures/my-own.png", "Icon=/home/r/pictures/my-own.png")]
    [InlineData("Icon=mailbox-app", "Icon=mailbox-app")]
    [InlineData("Icon[de]=mailbox", "Icon[de]=mailbox")]
    public void OnlyTheApplicationsOwnIconNamesAreRepointed(string line, string after)
        => Assert.Equal($"Name=x\n{after}\n", PanelIcon.Repoint($"Name=x\n{line}\n", PanelIcon.FullIconName));

    [Fact]
    public void WithoutAnyEntryThereIsNothingToPointAndNothingIsSaid()
    {
        InstallPackagedIcons();
        File.Delete(Path.Combine(System, "applications", PanelIcon.EntryName));

        Assert.False(Icon().Show(2));
        Assert.Empty(_ran);
        Assert.False(File.Exists(Path.Combine(Home, "applications", PanelIcon.EntryName)));
    }

    [Fact]
    public void AnInstallWithoutTheTwoIconsGetsThemInTheReadersTheme()
    {
        // A build run from its source tree, or a package from before the two icons were shipped.
        // Both are written, whichever is wanted now, so the other crossing finds its icon too.
        Assert.True(Icon().Show(1));

        foreach (var size in PanelIcon.Sizes)
        {
            Assert.Equal($"{TrayArtwork.Empty}-{size}", File.ReadAllText(ThemeFile(Home, PanelIcon.EmptyIconName, size)));
            Assert.Equal($"{TrayArtwork.Full}-{size}", File.ReadAllText(ThemeFile(Home, PanelIcon.FullIconName, size)));
        }

        Assert.Equal(["gtk-update-icon-cache", "kbuildsycoca6"], _ran);
    }

    [Fact]
    public void FallbackIconsAreWrittenOnceAndNeverAgain()
    {
        var icon = Icon();
        icon.Show(1);

        var asked = _asked.Count;
        icon.Show(0);
        Icon().Show(4);

        Assert.Equal(asked, _asked.Count);
    }

    [Fact]
    public void TheFullMailboxEarlierVersionsDrewOverTheAppIconIsRemoved()
    {
        // Earlier versions redrew mailbox.png itself in the reader's theme. Left there, the full
        // mailbox would be the application's icon for good. The empty one is the same picture
        // the package installs, and is what install-local.sh writes there on purpose: it stays.
        InstallPackagedIcons();
        foreach (var size in PanelIcon.Sizes)
        {
            var file = ThemeFile(Home, PanelIcon.IconName, size);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, size == 48 ? $"{TrayArtwork.Empty}-{size}" : $"{TrayArtwork.Full}-{size}");
        }

        Assert.True(Icon().Show(0));

        foreach (var size in PanelIcon.Sizes)
        {
            Assert.Equal(size == 48, File.Exists(ThemeFile(Home, PanelIcon.IconName, size)));
        }

        Assert.Equal(["gtk-update-icon-cache", "kbuildsycoca6"], _ran);
    }

    [Fact]
    public void ArtworkThatCannotBeOpenedIsNotReportedAsShown()
    {
        Assert.False(Icon((_, _) => null).Show(5));
        Assert.False(File.Exists(Path.Combine(Home, "applications", PanelIcon.EntryName)));
        Assert.Empty(_ran);
    }

    [Fact]
    public void NoHalfWrittenFileIsLeftBehindWhenTheDrawingFails()
    {
        // A stream that dies part way through is the case the temporary file exists for: what
        // must never happen is a truncated PNG sitting where the desktop will read it.
        Assert.False(Icon((_, _) => new FailingStream()).Show(1));

        var directory = Path.GetDirectoryName(ThemeFile(Home, PanelIcon.EmptyIconName, 32))!;
        Assert.True(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0);
        Assert.False(File.Exists(Path.Combine(Home, "applications", PanelIcon.EntryName)));
    }

    [Fact]
    public void TheEntryIsWrittenWholeAndNothingIsLeftBeside()
    {
        InstallPackagedIcons();
        var icon = Icon();
        icon.Show(1);
        icon.Show(0);

        Assert.Equal([PanelIcon.EntryName],
            Directory.GetFiles(Path.Combine(Home, "applications")).Select(Path.GetFileName));
    }

    // ---- Asked for, under the launcher's sandbox -------------------------------------------

    private string RequestFile => Path.Combine(_root, "runtime", "panel-icon");

    [Fact]
    public void UnderTheSandboxTheStateIsAskedForAndNothingIsDrawn()
    {
        var icon = Icon(request: RequestFile);

        Assert.True(icon.Asks);
        Assert.True(icon.Show(2));

        Assert.Equal(PanelIcon.FullWord, File.ReadAllText(RequestFile));
        Assert.True(PanelIcon.ReadRequest(RequestFile));
        Assert.Empty(_asked);
        Assert.Empty(_ran);
        Assert.False(Directory.Exists(Home));
    }

    [Fact]
    public void AskingFollowsTheSameCrossingsDrawingDoes()
    {
        var icon = Icon(request: RequestFile);
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
        var icon = Icon(request: RequestFile);
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
        var target = Path.Combine(_root, "elsewhere");
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
