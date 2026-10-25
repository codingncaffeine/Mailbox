namespace Mailbox.Core.Platform;

/// <summary>
/// The icon the panel draws for the application: the full mailbox while there is unread mail,
/// the empty one once it has all been read.
/// </summary>
/// <remarks>
/// This exists because a window icon does not reach the taskbar on this desktop. Mailbox sets
/// one — the X11 window carries the right drawing in <c>_NET_WM_ICON</c>, and it is updated as
/// the count changes — but Plasma's task manager never looks at it: it matches the window to
/// <c>mailbox.desktop</c> and draws whatever that entry's <c>Icon=</c> names. So the way to move
/// the button is to change the name the entry carries.
/// <para>
/// Two icons are installed beside the application's own, <see cref="EmptyIconName"/> and
/// <see cref="FullIconName"/>, and they never change. What changes is which of the two the
/// desktop entry names: a crossing rewrites that one line and has the service database rebuilt,
/// and the task manager, which resolves an entry's icon again whenever that database changes,
/// draws the other picture. The launcher, the application menu and anything else that reads the
/// entry follow it, which is the metaphor rather than a side effect — a mailbox with its flag up
/// means the same thing wherever it is drawn — and it stays true while the application is closed,
/// which a tray icon cannot.
/// </para>
/// <para>
/// <b>Never by redrawing a name.</b> This used to overwrite the files behind <c>mailbox</c>
/// itself, and then had to make every running KDE application forget the pixmaps it had cached
/// for that name, which is the <c>org.kde.KIconLoader.iconChanged</c> broadcast. That broadcast
/// takes the compositor down. It changes the icon-theme key in every process that hears it, and
/// a theme icon whose key has changed throws away its engine and builds a new one the next time
/// anything touches it — without a lock. KWin hands a window's icon to its worker threads to
/// stream to the panel, two of them can touch the same icon at once, and both free the old
/// engine: a double free inside <c>KIconEngine::~KIconEngine</c>, under
/// <c>QDataStream &lt;&lt; QIcon</c>, and the session's compositor is gone. Two names that each
/// mean one picture forever leave nothing cached that could be wrong, so nothing has to be
/// forgotten and nobody's theme key moves. Rebuilding the service database does not move it
/// either, and KWin does not listen for that rebuild at all.
/// </para>
/// <para>
/// The entry that gets rewritten is the reader's own copy. An entry the reader installed under
/// their own data directory — <c>install-local.sh</c> puts one there — is edited where it is,
/// one line. Otherwise the packaged entry is copied there with the line changed, and marked as
/// this class's copy, because a package's files are not the application's to edit. The copy is
/// made again from the packaged one on every crossing, and the first call of every session is a
/// crossing, so a package upgrade reaches it the next time the application runs; it keeps the
/// packaged <c>TryExec</c>, so if the package is removed the desktop hides the copy rather than
/// offering a launcher for a program that is no longer there.
/// </para>
/// <para>
/// Everything here is best-effort and silent on failure. A desktop with no service database, a
/// read-only home, a session that is not Plasma: none of those are worth a word to somebody who
/// is reading their mail, and the tray icon says the same thing regardless.
/// </para>
/// <para>
/// <b>Asked for, under the launcher's sandbox.</b> Neither half can be done from inside it: the
/// reader's data directory is read-only there, and so is the cache directory the service database
/// is rebuilt into — both on purpose, since both are every application's. So a sandboxed run does
/// not change the entry at all. It writes the state it wants, one word, to the file
/// <see cref="RequestVariable"/> names, and the launcher has set up a watcher outside the wall
/// that runs <c>mailbox --panel-icon</c> on it, which is this class again, drawing.
/// </para>
/// </remarks>
public sealed class PanelIcon
{
    /// <summary>The application's own icon: the name the packaged desktop entry carries.</summary>
    public const string IconName = "mailbox";

    /// <summary>The icon the entry names while nothing is waiting.</summary>
    public const string EmptyIconName = "mailbox-empty";

    /// <summary>The icon the entry names while there is unread mail.</summary>
    public const string FullIconName = "mailbox-full";

    /// <summary>The desktop entry's file name, the same in every data directory.</summary>
    public const string EntryName = "mailbox.desktop";

    /// <summary>
    /// The key that marks an entry as this class's copy of the packaged one, rather than an entry
    /// the reader installed themselves. A copy is replaced whole; the reader's own is only edited.
    /// </summary>
    public const string CopyKey = "X-Mailbox-Panel-Icon";

    /// <summary>
    /// Where the launcher wants the state written instead of drawn, when it is running the
    /// application in its sandbox.
    /// </summary>
    public const string RequestVariable = "MAILBOX_PANEL_ICON";

    /// <summary>The words a request holds.</summary>
    public const string FullWord = "full";

    public const string EmptyWord = "empty";

    /// <summary>The longest file a request can be; anything longer is not one.</summary>
    private const int RequestLimit = 16;

    /// <summary>
    /// The sizes an icon theme is given. The same ladder <c>packaging/install-desktop-files.sh</c>
    /// installs, so an icon written here as a fallback is as complete as an installed one.
    /// </summary>
    public static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256, 512];

    /// <summary>The two icons and the drawings behind them.</summary>
    private static readonly (string Name, string Art)[] Icons =
    [
        (EmptyIconName, Notifications.TrayArtwork.Empty),
        (FullIconName, Notifications.TrayArtwork.Full),
    ];

    private readonly string _dataHome;
    private readonly IReadOnlyList<string> _dataDirs;
    private readonly Func<string, int, Stream?> _artwork;
    private readonly string? _request;
    private readonly Func<string, string[], bool> _tool;
    private bool? _full;

    /// <param name="artwork">
    /// Opens the drawing for a state and size — <see cref="Notifications.TrayArtwork.Full"/> or
    /// <see cref="Notifications.TrayArtwork.Empty"/>. The application hands over its own embedded
    /// assets; a test hands over whatever it likes.
    /// </param>
    /// <param name="dataHome">
    /// The reader's data directory, which the entry and any fallback icons are written under.
    /// Defaults to <c>$XDG_DATA_HOME</c>.
    /// </param>
    /// <param name="request">
    /// The file to write the state to instead of drawing it — what <see cref="RequestVariable"/>
    /// names under the sandbox. Null draws.
    /// </param>
    /// <param name="tool">
    /// Runs one of the desktop's housekeeping tools and says whether it ran. The real ones by
    /// default; a test hands over a recorder, because the real ones tell the desktop the test is
    /// running on, rebuilding its service database every time.
    /// </param>
    /// <param name="dataDirs">
    /// The system data directories, where the packaged entry and icons are. Defaults to
    /// <c>$XDG_DATA_DIRS</c>.
    /// </param>
    public PanelIcon(
        Func<string, int, Stream?> artwork, string? dataHome = null, string? request = null,
        Func<string, string[], bool>? tool = null, IReadOnlyList<string>? dataDirs = null)
    {
        _artwork = artwork ?? throw new ArgumentNullException(nameof(artwork));
        _dataHome = dataHome ?? DefaultDataHome();
        _dataDirs = dataDirs ?? DefaultDataDirs();
        _request = string.IsNullOrWhiteSpace(request) ? null : request;
        _tool = tool ?? Run;
    }

    /// <summary>True when this asks for the icon rather than drawing it.</summary>
    public bool Asks => _request is not null;

    /// <summary><c>$XDG_DATA_HOME</c>, or <c>~/.local/share</c>.</summary>
    public static string DefaultDataHome()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(data)) return data;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    /// <summary><c>$XDG_DATA_DIRS</c>, or the specification's default when it is not set.</summary>
    public static IReadOnlyList<string> DefaultDataDirs()
    {
        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrWhiteSpace(dirs)) dirs = "/usr/local/share:/usr/share";

        return dirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The reader's copy of the entry, which is the one the desktop reads first.</summary>
    public string EntryFile => Path.Combine(_dataHome, "applications", EntryName);

    /// <summary>The reader's own icon theme, where fallback icons go.</summary>
    private string Theme => Path.Combine(_dataHome, "icons", "hicolor");

    /// <summary>
    /// Puts the mailbox for this unread count on the panel, if it is not the one already there.
    /// </summary>
    /// <returns>
    /// True when the entry names that mailbox and the desktop was told — or, asking, when the
    /// request was written.
    /// </returns>
    /// <remarks>
    /// Only on a crossing. The count changes with every message read and the drawing has only
    /// two states, so comparing the state rather than the count is what keeps this off the path
    /// of ordinary reading — and the first call after start always writes, because the entry on
    /// disk may be left over from a previous session that ended with the other answer.
    /// </remarks>
    public bool Show(int unread)
    {
        var full = unread > 0;
        if (_full == full) return false;

        // Recorded before the attempt rather than after it. An entry that cannot be written is
        // not going to become writable on the next message, and retrying on every count change
        // would put a failing file write on the reading path for the rest of the session.
        _full = full;

        if (_request is not null) return Ask(_request, full);

        // Both names, whichever is wanted now: the next crossing must not be the one that finds
        // its icon missing and has the panel draw a blank.
        var themeChanged = RemoveLeftovers();
        foreach (var (name, art) in Icons)
        {
            if (Installed(name)) continue;
            if (!InstallFallback(name, art)) return false;
            themeChanged = true;
        }

        if (!PointEntryAt(full ? FullIconName : EmptyIconName)) return false;

        Refresh(themeChanged);
        return true;
    }

    /// <summary>
    /// True when some icon theme the desktop searches has this icon at every size — the package's
    /// copy, as a rule, or a fallback already written.
    /// </summary>
    private bool Installed(string name)
        => new[] { _dataHome }.Concat(_dataDirs).Any(root => Sizes.All(size =>
            File.Exists(Path.Combine(root, "icons", "hicolor", $"{size}x{size}", "apps", $"{name}.png"))));

    /// <summary>
    /// Writes an icon into the reader's own theme, for an install that did not bring it — a
    /// build run from its source tree, or an older package.
    /// </summary>
    /// <remarks>
    /// A name that nothing has drawn yet, so nothing holds a stale picture of it and the desktop
    /// does not need to be told to forget one.
    /// </remarks>
    private bool InstallFallback(string name, string art)
    {
        var written = 0;
        foreach (var size in Sizes)
        {
            if (Write(art, size, Path.Combine(Theme, $"{size}x{size}", "apps", $"{name}.png"))) written++;
        }

        return written == Sizes.Length;
    }

    /// <summary>
    /// Removes the full mailbox from where earlier versions drew it: over the application's own
    /// icon, in the reader's theme. True when anything was removed.
    /// </summary>
    /// <remarks>
    /// Left there, it would be what <see cref="IconName"/> means for good — the launcher of a
    /// reader with no mail waiting saying there is some. Only a file that is that drawing, byte for
    /// byte, is removed: the empty mailbox there is the same picture the package installs, and an
    /// entry <c>install-local.sh</c> wrote names that file as its own icon.
    /// </remarks>
    private bool RemoveLeftovers()
    {
        var removed = false;
        foreach (var size in Sizes)
        {
            var file = Path.Combine(Theme, $"{size}x{size}", "apps", $"{IconName}.png");

            try
            {
                if (!File.Exists(file) || _artwork(Notifications.TrayArtwork.Full, size) is not { } art) continue;

                byte[] drawing;
                using (art)
                using (var copy = new MemoryStream())
                {
                    art.CopyTo(copy);
                    drawing = copy.ToArray();
                }

                if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(drawing)) continue;

                File.Delete(file);
                removed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left as it is; the entry no longer names it, so the panel is right either way.
            }
        }

        return removed;
    }

    /// <summary>
    /// Has the reader's entry name this icon: the reader's own entry edited in place, or a copy
    /// of the packaged one. True when it does.
    /// </summary>
    private bool PointEntryAt(string name)
    {
        string? packaged;
        try
        {
            if (File.Exists(EntryFile))
            {
                var own = File.ReadAllText(EntryFile);
                if (!IsCopy(own)) return WriteEntry(Repoint(own, name));
            }

            packaged = PackagedEntry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // No packaged entry to copy: an install with no desktop integration, which has no
        // launcher for the panel to draw either.
        if (packaged is null) return false;

        return WriteEntry(MarkAsCopy(Repoint(packaged, name)));
    }

    /// <summary>The first packaged entry the desktop would find, or null.</summary>
    private string? PackagedEntry()
    {
        foreach (var dir in _dataDirs)
        {
            var file = Path.Combine(dir, "applications", EntryName);
            if (File.Exists(file)) return File.ReadAllText(file);
        }

        return null;
    }

    /// <summary>True when the entry is this class's copy rather than one the reader installed.</summary>
    private static bool IsCopy(string entry)
        => entry.Split('\n').Any(line => line.TrimEnd('\r') == $"{CopyKey}=true");

    /// <summary>
    /// Points every <c>Icon=</c> line that names one of the application's three icons at
    /// <paramref name="name"/>. A line that names anything else was set by somebody on purpose
    /// and is left alone.
    /// </summary>
    public static string Repoint(string entry, string name)
    {
        var lines = entry.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (!line.StartsWith("Icon=", StringComparison.Ordinal)) continue;

            var value = line["Icon=".Length..].Trim();
            if (value is IconName or EmptyIconName or FullIconName) lines[i] = $"Icon={name}";
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Marks a copy of the packaged entry as this class's, under its group header, with a word
    /// for whoever finds the file.
    /// </summary>
    private static string MarkAsCopy(string entry)
    {
        const string header = "[Desktop Entry]";
        var at = entry.IndexOf(header, StringComparison.Ordinal);
        if (at < 0) return entry;

        var end = entry.IndexOf('\n', at);
        if (end < 0) return entry;

        return entry[..(end + 1)]
            + "# Mailbox's copy of its packaged entry, so that the taskbar can show the full mailbox\n"
            + "# while mail is waiting. Made again from the packaged one every time Mailbox starts.\n"
            + $"{CopyKey}=true\n"
            + entry[(end + 1)..];
    }

    /// <summary>Writes the entry whole and moves it into place, unless it already says this.</summary>
    /// <remarks>
    /// Through a temporary file beside it, because the desktop reads it at a moment of its own
    /// choosing and a half-written entry is a missing launcher rather than a stale one.
    /// </remarks>
    private bool WriteEntry(string text)
    {
        var temporary = EntryFile + $".{Environment.ProcessId}.tmp";

        try
        {
            if (File.Exists(EntryFile) && File.ReadAllText(EntryFile) == text) return true;

            Directory.CreateDirectory(Path.GetDirectoryName(EntryFile)!);
            File.WriteAllText(temporary, text);
            File.Move(temporary, EntryFile, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception) { /* nothing left to do about it */ }
            return false;
        }
    }

    /// <summary>One size of one drawing, written whole and then moved into place.</summary>
    /// <remarks>
    /// Through a temporary file beside the target, because the desktop reads these files at a
    /// moment of its own choosing and a half-written PNG is a broken icon rather than a missing
    /// one. A rename within a directory is atomic; a copy is not.
    /// </remarks>
    private bool Write(string art, int size, string target)
    {
        var temporary = target + $".{Environment.ProcessId}.tmp";

        try
        {
            if (_artwork(art, size) is not { } source) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using (source)
            using (var file = File.Create(temporary))
            {
                source.CopyTo(file);
            }

            File.Move(temporary, target, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try { File.Delete(temporary); } catch (Exception) { /* nothing left to do about it */ }
            return false;
        }
    }

    /// <summary>Writes the state to the request file, whole, for the watcher to pick up.</summary>
    /// <remarks>
    /// Beside it first and then moved over it, so the watcher — which fires when the name is
    /// replaced — never reads a file that is still being written.
    /// </remarks>
    private static bool Ask(string request, bool full)
    {
        var temporary = request + $".{Environment.ProcessId}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request))!);
            File.WriteAllText(temporary, full ? FullWord : EmptyWord);
            File.Move(temporary, request, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try { File.Delete(temporary); } catch (Exception) { /* nothing left to do about it */ }
            return false;
        }
    }

    /// <summary>
    /// What a request file asks for: true for the full mailbox, false for the empty one, null when
    /// it asks for nothing this can do.
    /// </summary>
    /// <remarks>
    /// Read by the process outside the sandbox, from a file the process inside it wrote, so it is
    /// read as something that may have been made to lie. A link is not followed, a file of no
    /// length is not opened — a named pipe has none, and opening one would wait for a writer that
    /// never comes — and anything longer than the longer word, or other than the two words, is
    /// nothing. The worst a request can do is choose between the two drawings.
    /// </remarks>
    public static bool? ReadRequest(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null) return null;
            if (file.Length is 0 or > RequestLimit) return null;

            using var stream = file.OpenRead();
            var buffer = new byte[RequestLimit];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            return System.Text.Encoding.ASCII.GetString(buffer, 0, read).Trim() switch
            {
                FullWord => true,
                EmptyWord => false,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Tells the desktop the entry has changed.</summary>
    /// <remarks>
    /// <c>kbuildsycoca</c> rebuilds the service database, which is what makes the task manager
    /// resolve the entry's icon again. An ordinary incremental run: the entry's file has changed,
    /// which is exactly what it looks for. GNOME and the others watch the applications
    /// directories themselves and need nothing.
    /// <para>
    /// <c>gtk-update-icon-cache</c> only when this wrote to or removed from the reader's icon
    /// theme, so that an index written there earlier does not hide the change from a GTK desktop.
    /// It writes a file and nothing running hears it.
    /// </para>
    /// <para>
    /// Never <c>org.kde.KIconLoader.iconChanged</c>: see the class remarks — it is the broadcast
    /// that crashes the compositor, and with two fixed names nothing needs it.
    /// </para>
    /// </remarks>
    private void Refresh(bool themeChanged)
    {
        if (themeChanged) _tool("gtk-update-icon-cache", ["-q", "-t", "-f", Theme]);

        // KDE 6 first, then 5. Whichever is installed answers; the other is simply not there.
        if (!_tool("kbuildsycoca6", [])) _tool("kbuildsycoca5", []);
    }

    /// <summary>Runs one housekeeping tool and waits a moment for it. True when it ran.</summary>
    private static bool Run(string command, string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return false;

            // Long enough for tools that take a tenth of a second, short enough that a broken
            // one is simply left behind. Its output is redirected and discarded either way.
            return process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Not installed, or not allowed to run. Either way the entry is already written and
            // the desktop will pick it up the next time it rebuilds for its own reasons.
            return false;
        }
    }
}
