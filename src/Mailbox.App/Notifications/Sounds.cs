using System.Diagnostics;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.App.Notifications;

/// <summary>
/// Plays a sound through whatever the desktop plays sounds with — mail arriving, the reminder
/// chime, and the "play a sound" rule action.
/// </summary>
/// <remarks>
/// A file when one is named, or the desktop's own sound for an event when none is, through
/// <c>canberra-gtk-play</c> (the freedesktop sound theme player, present on every desktop that
/// has a sound theme) with <c>paplay</c>, <c>pw-play</c> and <c>aplay</c> as fallbacks for a bare
/// file. Never an error: a machine with no sound is a machine with no sound.
/// </remarks>
internal static class Sounds
{
    /// <summary>The sounds this build ships, beside the binary, when it ships any.</summary>
    private static string Bundled(string name) => Path.Combine(AppContext.BaseDirectory, "sounds", name);

    private static bool _available = true;

    /// <summary>Mail has arrived. The rule for which sound is <see cref="MailOptions.SoundFor"/>.</summary>
    public static void PlayArrival(string? chosen)
        => Play(MailOptions.SoundFor(chosen, Bundled("new-mail.ogg")), "message-new-email");

    /// <summary>A reminder has come due.</summary>
    public static void PlayReminder(string? chosen)
        => Play(MailOptions.SoundFor(chosen, Bundled("reminder.ogg")), "alarm-clock-elapsed");

    /// <summary>What the Options page should say is playing, given what is chosen.</summary>
    /// <remarks>
    /// Asked of the same rule the player uses, so the page cannot name one sound while another
    /// plays — including when a chosen file has gone missing, which the page should say rather
    /// than go on printing a name that no longer resolves.
    /// </remarks>
    public static string NameFor(string? chosen, string bundled)
        => MailOptions.SoundFor(chosen, Bundled(bundled)) switch
        {
            null => "Desktop sound theme",
            var path when path == Bundled(bundled) => "Mailbox default",
            var path => Path.GetFileName(path),
        };

    /// <summary>A file, or the desktop's own sound for an event id when there is none.</summary>
    public static void Play(string? file, string eventId = "message-new-email")
    {
        if (!_available) return;

        var attempts = string.IsNullOrWhiteSpace(file)
            ? [("canberra-gtk-play", new[] { "--id=" + eventId })]
            : Players(file);

        foreach (var (player, arguments) in attempts)
        {
            try
            {
                var start = new ProcessStartInfo { FileName = player, UseShellExecute = false, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);

                using var process = Process.Start(start);
                if (process is null) continue;

                Log.Info($"Sound: {file ?? eventId} through {player}.");

                // Not waited for: a sound is not something to hold a send/receive on.
                return;
            }
            catch (Exception)
            {
                // Try the next player.
            }
        }

        Log.Info("No sound player was found; sounds will be skipped.");
        _available = false;
    }

    /// <summary>
    /// The players that can be trusted with this particular file, in the order to try them.
    /// </summary>
    /// <remarks>
    /// <b><c>aplay</c> decodes nothing but WAVE.</b> Handed an Ogg or an MP3 it does not refuse:
    /// it announces "playing raw data", reads the compressed bytes as unsigned 8-bit 8kHz PCM,
    /// makes a noise nobody wants to hear at their desk, and exits 0 — so nothing downstream can
    /// tell it went wrong. It is offered only for the one format it understands; the others go
    /// through players that decode, and a machine with only <c>aplay</c> and an Ogg gets silence,
    /// which is the whole policy here.
    /// </remarks>
    private static (string Player, string[] Arguments)[] Players(string file)
    {
        var decoders = new (string, string[])[]
        {
            ("canberra-gtk-play", ["--file=" + file]),
            ("paplay", [file]),
            ("pw-play", [file]),
        };

        return file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            ? [.. decoders, ("aplay", ["-q", file])]
            : decoders;
    }
}
