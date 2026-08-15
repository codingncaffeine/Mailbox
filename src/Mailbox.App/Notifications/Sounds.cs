using System.Diagnostics;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Notifications;

/// <summary>
/// Plays a sound through whatever the desktop plays sounds with — the "play a sound" rule action
/// and, one day, the reminder chime.
/// </summary>
/// <remarks>
/// A file the rule names, or the desktop's own new-mail sound when it names none, through
/// <c>canberra-gtk-play</c> (the freedesktop sound theme player, present on every desktop that
/// has a sound theme) with <c>paplay</c> and <c>aplay</c> as fallbacks for a bare file. Never an
/// error: a machine with no sound is a machine with no sound.
/// </remarks>
internal static class Sounds
{
    private static bool _available = true;

    public static void Play(string? file)
    {
        if (!_available) return;

        var attempts = string.IsNullOrWhiteSpace(file)
            ? new[] { ("canberra-gtk-play", new[] { "--id=message-new-email" }) }
            : new[]
            {
                ("canberra-gtk-play", new[] { "--file=" + file }),
                ("paplay", new[] { file }),
                ("aplay", new[] { "-q", file }),
            };

        foreach (var (player, arguments) in attempts)
        {
            try
            {
                var start = new ProcessStartInfo { FileName = player, UseShellExecute = false, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);

                using var process = Process.Start(start);
                if (process is null) continue;

                // Not waited for: a sound is not something to hold a send/receive on.
                return;
            }
            catch (Exception)
            {
                // Try the next player.
            }
        }

        Log.Info("No sound player was found; rule sounds will be skipped.");
        _available = false;
    }
}
