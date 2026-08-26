namespace Mailbox.Core.Platform;

/// <summary>
/// The icon the panel draws for the application: the full mailbox while there is unread mail,
/// the empty one once it has all been read.
/// </summary>
/// <remarks>
/// This exists because a window icon does not reach the taskbar on this desktop. Mailbox sets
/// one — the X11 window carries the right drawing in <c>_NET_WM_ICON</c>, and it is updated as
/// the count changes — but Plasma's task manager never looks at it: it matches the window to
/// <c>mailbox.desktop</c> and draws whatever <c>Icon=</c> names, resolved through the icon theme.
/// So the only way to move the button is to change what that name resolves to, which means
/// writing the icon files themselves.
/// <para>
/// Two things follow from that. The first is that the change is global: the launcher, the
/// application menu and a shortcut on the desktop all name the same icon, so they all say
/// "there is post" together. That is the metaphor rather than a side effect — a mailbox with
/// its flag up means the same thing wherever it is drawn — and it stays true while the
/// application is closed, which a tray icon cannot.
/// </para>
/// <para>
/// The second is that the desktop caches the answer twice over, and rewriting the files changes
/// nothing anybody can see until both caches are told. Plasma keeps the pixmaps it has already
/// drawn for an icon name, and it keeps the icon it resolved for a desktop entry for the life of
/// the shell. <see cref="Refresh"/> clears both, in the order that works, and costs about a
/// quarter of a second. That is affordable because it only ever fires on a crossing — the last
/// unread message being read, or the first new one arriving — and never once per message.
/// </para>
/// <para>
/// Everything here is best-effort and silent on failure. A desktop with no icon theme of its
/// own, a read-only home, a session that is not Plasma: none of those are worth a word to
/// somebody who is reading their mail, and the tray icon says the same thing regardless.
/// </para>
/// </remarks>
public sealed class PanelIcon
{
    /// <summary>The icon name the desktop entries carry, and so the files this rewrites.</summary>
    public const string IconName = "mailbox";

    /// <summary>
    /// The sizes an icon theme is given. The same ladder <c>packaging/install-local.sh</c>
    /// installs, so a state change replaces every file the install put there and never leaves a
    /// size behind saying the opposite of its neighbours.
    /// </summary>
    public static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256, 512];

    private readonly string _theme;
    private readonly Func<string, int, Stream?> _artwork;
    private bool? _full;

    /// <param name="artwork">
    /// Opens the drawing for a state and size — <see cref="Notifications.TrayArtwork.Full"/> or
    /// <see cref="Notifications.TrayArtwork.Empty"/>. The application hands over its own embedded
    /// assets; a test hands over whatever it likes.
    /// </param>
    /// <param name="theme">The hicolor directory to write into. Defaults to the user's own.</param>
    public PanelIcon(Func<string, int, Stream?> artwork, string? theme = null)
    {
        _artwork = artwork ?? throw new ArgumentNullException(nameof(artwork));
        _theme = theme ?? DefaultTheme();
    }

    /// <summary><c>$XDG_DATA_HOME/icons/hicolor</c>, or <c>~/.local/share/icons/hicolor</c>.</summary>
    public static string DefaultTheme()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data))
        {
            data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(data, "icons", "hicolor");
    }

    /// <summary>
    /// Puts the drawing for this unread count on the panel, if it is not the one already there.
    /// </summary>
    /// <returns>True when the files were rewritten, which is when the desktop was told.</returns>
    /// <remarks>
    /// Only on a crossing. The count changes with every message read and the drawing has only
    /// two states, so comparing the state rather than the count is what keeps this off the path
    /// of ordinary reading — and the first call after start always writes, because the files on
    /// disk may be left over from a previous session that ended with the other answer.
    /// </remarks>
    public bool Show(int unread)
    {
        var full = unread > 0;
        if (_full == full) return false;

        // Recorded before the attempt rather than after it. A theme that cannot be written is
        // not going to become writable on the next message, and retrying on every count change
        // would put a failing file write on the reading path for the rest of the session.
        _full = full;

        var art = full ? Notifications.TrayArtwork.Full : Notifications.TrayArtwork.Empty;
        var written = 0;

        foreach (var size in Sizes)
        {
            if (Write(art, size)) written++;
        }

        if (written == 0) return false;

        Refresh();
        return true;
    }

    /// <summary>One size, written whole and then moved into place.</summary>
    /// <remarks>
    /// Through a temporary file beside the target, because the desktop reads these files at a
    /// moment of its own choosing and a half-written PNG is a broken icon rather than a stale
    /// one. A rename within a directory is atomic; a copy is not.
    /// </remarks>
    private bool Write(string art, int size)
    {
        var target = Path.Combine(_theme, $"{size}x{size}", "apps", $"{IconName}.png");
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

    /// <summary>Tells the desktop the icon files have changed.</summary>
    /// <remarks>
    /// Three announcements, in this order, because three caches are in play and no one of them
    /// covers the others. Rewriting the files alone changes nothing anybody can see.
    /// <list type="number">
    /// <item><c>gtk-update-icon-cache</c> rebuilds the index a GTK desktop reads.</item>
    /// <item><c>KIconLoader.iconChanged</c> is the signal that makes a running KDE application
    /// drop the pixmaps it has already drawn for an icon name. Without it the panel keeps
    /// painting the drawing it cached, however new the file is.</item>
    /// <item><c>kbuildsycoca</c> rebuilds the service database, which is what makes the task
    /// manager resolve its desktop entry's icon again rather than reuse the one it worked out
    /// at login. <c>--noincremental</c> because the entry itself has not changed — only the
    /// picture behind its name — and an incremental run finds nothing to do and says nothing.</item>
    /// </list>
    /// <para>
    /// Waited for, briefly and in order: the signal has to arrive before the rebuild or the
    /// panel re-resolves the name and caches the old drawing again. This runs on a background
    /// thread, so the wait costs the reader nothing, and a tool that hangs is abandoned rather
    /// than allowed to hold the thread.
    /// </para>
    /// </remarks>
    private void Refresh()
    {
        Run("gtk-update-icon-cache", ["-q", "-t", "-f", _theme]);

        Run("dbus-send", [
            "--session", "--type=signal",
            "/KIconLoader", "org.kde.KIconLoader.iconChanged", "int32:0",
        ]);

        // KDE 6 first, then 5. Whichever is installed answers; the other is simply not there.
        if (!Run("kbuildsycoca6", ["--noincremental"])) Run("kbuildsycoca5", ["--noincremental"]);
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
            // Not installed, or not allowed to run. Either way the files are already written and
            // the desktop will pick them up the next time it rebuilds for its own reasons.
            return false;
        }
    }
}
