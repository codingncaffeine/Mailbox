using Avalonia.Controls;
using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// The one way a context menu is shown, so a posed run can read back what opened.
/// </summary>
/// <remarks>
/// A popup is not a window in the application's window list: the in-process capture photographs
/// the shell behind it, and a menu that opened empty — a 2×2 presenter — looks exactly like a
/// success. Two S1s shipped that way. So every flyout goes through here, and under a posed run
/// each one logs itself through <see cref="FlyoutProbe"/> once its presenter has laid out:
/// entries, greyed states, and the popup's real size, which is the claim.
/// <para>
/// In an ordinary run this is <c>ShowAt</c> and nothing else. The described log line waits for
/// <see cref="DispatcherPriority.Background"/> so the presenter it measures is the one the
/// reader would see, not the empty shell of a menu still opening.
/// </para>
/// </remarks>
public static class MenuProbe
{
    /// <summary>The menu shown last, for a door that wants to press or re-describe it.</summary>
    public static (string What, MenuFlyout Menu)? Last { get; private set; }

    /// <summary>Shows the menu at the control, and under a posed run logs what opened.</summary>
    public static void Show(string what, MenuFlyout menu, Control at, bool atPointer = false)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(at);

        Record(what, menu);
        menu.ShowAt(at, showAtPointer: atPointer);
    }

    /// <summary>
    /// Shows a content flyout — a picker, a calendar — through the same door, so a posed run
    /// reads back its size the way it reads a menu's.
    /// </summary>
    public static void Show(string what, Flyout flyout, Control at, bool atPointer = false)
    {
        ArgumentNullException.ThrowIfNull(flyout);
        ArgumentNullException.ThrowIfNull(at);

        if (WindowCapture.IsRequested)
        {
            Dispatcher.UIThread.Post(
                () => Log.Info($"Harness: {FlyoutProbe.Describe(what, flyout)}"),
                DispatcherPriority.Background);
        }

        flyout.ShowAt(at, showAtPointer: atPointer);
    }

    /// <summary>
    /// Remembers a menu something else is about to show — the ribbon's own
    /// <c>OpenMenuUnder</c> — and under a posed run logs it once it has laid out.
    /// </summary>
    public static void Record(string what, MenuFlyout menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        Last = (what, menu);

        if (!WindowCapture.IsRequested) return;

        Dispatcher.UIThread.Post(
            () => Log.Info($"Harness: {FlyoutProbe.Describe(what, menu)}"),
            DispatcherPriority.Background);
    }
}
