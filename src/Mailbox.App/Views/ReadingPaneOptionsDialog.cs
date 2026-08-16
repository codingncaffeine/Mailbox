using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Mailbox.Core.Settings;

namespace Mailbox.App.Views;

/// <summary>
/// Reading Pane — Options › Mail's button: when a message counts as read for having been looked
/// at. The reference's three: after a wait in the pane, when the selection moves on, and the
/// space bar's single-key reading (which is the list's, and not offered until it is).
/// </summary>
public sealed class ReadingPaneOptionsDialog : Window
{
    public ReadingPaneOptionsDialog(MailOptions options)
    {
        Title = "Reading Pane";
        Width = 460;
        Height = 260;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var onView = ViewDialogKit.Ink(new CheckBox { Content = "Mark items as read when viewed in the Reading Pane", IsChecked = options.ReadingPaneMarkOnView });
        var seconds = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 999, Value = options.ReadingPaneMarkSeconds };
        var onChange = ViewDialogKit.Ink(new CheckBox { Content = "Mark item as read when selection changes", IsChecked = options.ReadingPaneMarkOnChange });

        void Enable() => seconds.IsEnabled = onView.IsChecked == true;
        onView.IsCheckedChanged += (_, _) => Enable();
        Enable();

        var wait = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(24, 0, 0, 0),
            Children = { ViewDialogKit.Label("Wait"), seconds, ViewDialogKit.Label("seconds before marking item as read") },
        };

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                ViewDialogKit.Label("Reading Pane options", bold: true),
                onView,
                wait,
                onChange,
                ViewDialogKit.Buttons(ViewDialogKit.Ok(() =>
                {
                    options.ReadingPaneMarkOnView = onView.IsChecked == true;
                    options.ReadingPaneMarkSeconds = (int)(seconds.Value ?? 5);
                    options.ReadingPaneMarkOnChange = onChange.IsChecked == true;
                    Close();
                }), ViewDialogKit.Cancel(this)),
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }
}
