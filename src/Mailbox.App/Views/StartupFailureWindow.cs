using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// The window a startup that cannot proceed shows instead of dying silently.
/// </summary>
/// <remarks>
/// Seven launches died invisibly in one evening to a store written by a newer build: the
/// process logged the exact sentence that explained it and exited 1, and the reader saw
/// nothing at all — which reads as "the application is broken", not as the repair the log
/// already named. The framework is up by the time startup's failures are caught, so the
/// catch makes this window the main window and lets the application run just long enough
/// to say what happened; closing it ends a process whose exit code still says failure.
/// System-dialog chrome like the rest of its family, and the details are selectable and
/// copyable because the message is exactly what a bug report needs.
/// </remarks>
public sealed class StartupFailureWindow : Window
{
    public StartupFailureWindow(Exception failure)
    {
        Title = "Mailbox";
        Width = 560;
        Height = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var details = new TextBox
        {
            Text = failure.ToString(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var copy = SystemInkKit.Ok(
            () =>
            {
                if (Clipboard is { } clipboard)
                {
                    _ = Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(
                        clipboard, Avalonia.Input.DataFormat.Text, failure.ToString());
                }
            },
            "Copy Details");

        var message = new TextBlock
        {
            Text = failure.Message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        };

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                SystemInkKit.Buttons(copy, SystemInkKit.Ok(Close, "Close")),
                SystemInkKit.Label("Mailbox cannot start.", bold: true),
                message,
                SystemInkKit.Boxed(details),
            },
        };

        body.Children[0][DockPanel.DockProperty] = Dock.Bottom;
        body.Children[1][DockPanel.DockProperty] = Dock.Top;
        body.Children[2][DockPanel.DockProperty] = Dock.Top;

        SystemDialogChrome.Apply(this, body);
    }
}
