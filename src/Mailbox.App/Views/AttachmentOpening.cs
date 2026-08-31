using Avalonia.Controls;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// The shared half of opening an attachment with the desktop: the one-time warning, and the
/// private place the file is written so the desktop can read it.
/// </summary>
/// <remarks>
/// Two strips open attachments — the reading side's and the compose window's carried parts —
/// and the rules are the same for both: a stranger's file gets the warning once, ever, and
/// the bytes go under the runtime directory (per-login tmpfs, 0700), never a world-readable
/// temporary path. A file the reader attached themselves is their own and skips the warning.
/// </remarks>
internal static class AttachmentOpening
{
    /// <summary>The settings key that remembers the open warning has been shown and accepted.</summary>
    internal const string WarnedKey = "mail.attachments.openwarned";

    /// <summary>The warning, shown once ever; true when opening may go ahead.</summary>
    internal static async Task<bool> ConfirmedAsync(Window owner, string safeName)
    {
        if (App.Settings.GetBool(WarnedKey)) return true;

        var opening = await Confirm.AskAsync(
            owner,
            "Open attachment",
            $"“{safeName}” came with a message, and opening it runs whatever program the "
            + "desktop uses for that kind of file. Only open attachments you are expecting. "
            + "This is asked once.",
            "Open",
            destructive: false);

        if (!opening)
        {
            Log.Info("An attachment open was declined at the warning.");
            return false;
        }

        App.Settings.Set(WarnedKey, true);
        return true;
    }

    /// <summary>
    /// Writes the file where the desktop can read it and nobody else can, and says where.
    /// </summary>
    /// <remarks>
    /// A fresh directory per open, so two attachments with one name cannot overwrite each
    /// other while one of them is on screen in another program. The runtime directory is
    /// per-login tmpfs and cleaned up with the session.
    /// </remarks>
    internal static string WriteForOpening(string safeName, Action<Stream> save)
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var root = string.IsNullOrEmpty(runtime)
            ? Path.Combine(Path.GetTempPath(), "mailbox-opened")
            : Path.Combine(runtime, "mailbox", "opened");

        Directory.CreateDirectory(root);

        // 0700, said explicitly: the runtime dir itself is private, but a fallback under /tmp
        // is not, and a directory of opened attachments is the reader's own mail.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var dir = Path.Combine(root, Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, safeName);
        using (var stream = File.Create(path))
        {
            save(stream);
        }

        return path;
    }
}
