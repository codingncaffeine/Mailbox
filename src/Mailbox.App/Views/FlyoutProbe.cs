using Avalonia.Controls;

namespace Mailbox.App.Views;

/// <summary>
/// Reads an open flyout back: what it holds, and how big the window it opened in actually is.
/// </summary>
/// <remarks>
/// The audit's rule about popups is that the size is the claim and <c>IsOpen == true</c> proves
/// nothing — a 2×2 presenter is an empty menu that reports itself open. The in-process capture
/// cannot photograph one either, because a popup is not in the application's window list, so a
/// run that opens a menu photographs the shell behind it and looks like a success.
/// <para>
/// A flyout's entries do live in a visual tree once it is shown: their root is the popup's own
/// top level, and that root has a size. Reading it from inside the application is the
/// measurement the whole-screen screenshot would have given, without needing the window to be
/// on a screen — which it never is under the harness.
/// </para>
/// </remarks>
public static class FlyoutProbe
{
    /// <summary>Describes a menu flyout: open state, entries with their greyed state, popup size.</summary>
    public static string Describe(string what, MenuFlyout flyout)
    {
        ArgumentNullException.ThrowIfNull(flyout);

        // Items first, ItemsSource second: a flyout built by handing it a fresh array every time
        // it opens carries its entries on the source rather than on the collection.
        var entries = flyout.Items.OfType<Control>().ToList();
        if (entries.Count == 0 && flyout.ItemsSource is { } source)
        {
            entries = source.OfType<Control>().ToList();
        }

        var described = entries.Select(e => e switch
        {
            MenuItem { Header: { } header } item =>
                $"{header}{(item.IsEnabled ? string.Empty : " (greyed)")}",
            Separator => "—",
            _ => e.GetType().Name,
        });

        // Any entry will do: they share one top level, and for a flyout that top level is the
        // popup rather than the window the flyout was opened from.
        var root = entries.Select(TopLevel.GetTopLevel).FirstOrDefault(t => t is not null);

        var size = root is null
            ? "not presented — built but never shown, or shown with nothing in it"
            : $"{root.ClientSize.Width:0}x{root.ClientSize.Height:0}"
              + (root is Window ? " (the shell's own size — the entries are not in a popup)" : string.Empty);

        return $"{what}: open={flyout.IsOpen}, {entries.Count} entries "
               + $"[{string.Join(" | ", described)}], popup {size}";
    }
}
