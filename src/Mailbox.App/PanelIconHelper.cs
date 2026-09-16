using Avalonia.Platform;
using Mailbox.Core.Platform;

namespace Mailbox.App;

/// <summary>
/// <c>mailbox --panel-icon &lt;request&gt;</c>: puts the mailbox a sandboxed run asked for on the
/// taskbar.
/// </summary>
/// <remarks>
/// Started by systemd, not by anybody's click. The launcher sets up a path unit outside its
/// sandbox that runs this whenever the application replaces its request file — see
/// <see cref="PanelIcon"/> for why the application cannot draw the icon itself from inside the
/// wall. This is the same drawing code, run where the icon theme and the service database can be
/// written: it reads one word, draws that mailbox at every size and tells the desktop, and does
/// nothing else. No log is opened — that would roll the application's — and nothing of the
/// application starts; what it has to say goes to the journal through standard output.
/// </remarks>
internal static class PanelIconHelper
{
    /// <summary>The command-line switch that makes a <c>mailbox</c> process this one.</summary>
    public const string Switch = "--panel-icon";

    /// <summary>How many times one run follows a request that keeps changing under it.</summary>
    private const int Passes = 3;

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine($"usage: mailbox {Switch} <request file>");
            return 2;
        }

        var request = args[1];
        bool? drawn = null;

        // Read again once drawn. A request replaced while this was drawing starts no second run —
        // systemd does not start a unit that is still running — so the last word is looked for
        // here: mail arriving the moment the last message is read must not leave the empty
        // mailbox up.
        for (var pass = 0; pass < Passes; pass++)
        {
            if (PanelIcon.ReadRequest(request) is not { } full)
            {
                if (drawn is not null) break;

                Console.Error.WriteLine($"Mailbox taskbar icon: {request} does not ask for either mailbox; nothing was changed.");
                return 2;
            }

            if (full == drawn) break;
            if (!Draw(full)) return 1;
            drawn = full;
        }

        return 0;
    }

    private static bool Draw(bool full)
    {
        var art = full ? Mailbox.Core.Notifications.TrayArtwork.Full : Mailbox.Core.Notifications.TrayArtwork.Empty;

        // The drawings are the application's own embedded ones, read without starting the toolkit:
        // the loader is all this needs of it, and a display connection is not something a
        // background service should have to have.
        var assets = new StandardAssetLoader(typeof(PanelIconHelper).Assembly);

        var icon = new PanelIcon((drawing, size) =>
        {
            try
            {
                return assets.Open(new Uri($"avares://mailbox/Assets/Icons/{drawing}-{size}.png"));
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Mailbox taskbar icon: {drawing} at {size} could not be read ({ex.Message}).");
                return null;
            }
        });

        if (!icon.Show(full ? 1 : 0))
        {
            Console.Error.WriteLine($"Mailbox taskbar icon: the {art} drawing could not be written to {PanelIcon.DefaultTheme()}.");
            return false;
        }

        Console.WriteLine($"Mailbox taskbar icon: {art}, written to {PanelIcon.DefaultTheme()}; the desktop was asked to draw it again.");
        return true;
    }
}
