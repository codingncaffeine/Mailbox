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
            MenuItem item => $"{Named(item)}{(item.IsEnabled ? string.Empty : " (greyed)")}",
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

    /// <summary>
    /// What a menu row says. Its header when that is text; otherwise the name it states for a
    /// screen reader.
    /// </summary>
    /// <remarks>
    /// A row whose header is a control — the bar's "…" draws its group headings that way, so that
    /// a heading is not greyed out like a disabled command — stringifies to the control's class
    /// name. The log then read "Avalonia.Controls.TextBlock" where the menu says "Respond", which
    /// makes the one instrument that can see inside a popup useless for checking what is in it.
    /// </remarks>
    private static string Named(MenuItem item)
        => item.Header switch
        {
            string text => text,
            null => Automation(item) ?? "(unnamed)",
            var other => Automation(item) ?? other.ToString() ?? "(unnamed)",
        };

    private static string? Automation(MenuItem item)
        => Avalonia.Automation.AutomationProperties.GetName(item) is { Length: > 0 } name
            ? name
            : null;

    /// <summary>
    /// Describes a content flyout — a picker, a calendar — the same way: what it holds and the
    /// popup's real size, because <c>IsOpen</c> proves as little here as it does for a menu.
    /// </summary>
    public static string Describe(string what, Flyout flyout)
    {
        ArgumentNullException.ThrowIfNull(flyout);

        var content = flyout.Content as Control;
        var root = content is null ? null : TopLevel.GetTopLevel(content);

        var size = root is null
            ? "not presented — built but never shown, or shown with nothing in it"
            : $"{root.ClientSize.Width:0}x{root.ClientSize.Height:0}"
              + (root is Window ? " (the shell's own size — the content is not in a popup)" : string.Empty);

        return $"{what}: open={flyout.IsOpen}, holding {content?.GetType().Name ?? "nothing"}, popup {size}";
    }
}
