using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// A yes-or-no question, themed like the rest of the application.
/// </summary>
/// <remarks>
/// Avalonia has no message box, and the alternative is a dependency whose dialogs are styled by
/// somebody else — which in a themed application means one window that ignores the theme. This
/// is small enough to own.
/// <para>
/// The destructive button is never the default. A confirmation that deletes on Enter is a
/// confirmation that did not happen.
/// </para>
/// </remarks>
public static class Confirm
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public static async Task<bool> AskAsync(Window owner, string title, string message,
        string confirmLabel, bool destructive = true)
        => (await AskAsync(owner, title, message, confirmLabel, destructive, dontShowAgain: null)).Confirmed;

    /// <summary>
    /// The same question, with the reference's "Don't show this message again" beneath it when a
    /// label is given. The caller remembers the answer; this only reports the tick.
    /// </summary>
    public static async Task<(bool Confirmed, bool DontShowAgain)> AskAsync(Window owner, string title, string message,
        string confirmLabel, bool destructive, string? dontShowAgain)
    {
        var answer = false;
        CheckBox? again = null;

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = !destructive };
        cancel.Click += (_, _) => window.Close();

        var confirm = new Button { Content = confirmLabel, IsDefault = !destructive };
        confirm.Click += (_, _) => { answer = true; window.Close(); };
        if (destructive) Bind(confirm, TemplatedControl.ForegroundProperty, "status.danger.brush");

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children = { text },
        };

        if (dontShowAgain is { Length: > 0 })
        {
            again = new CheckBox { Content = dontShowAgain };
            Bind(again, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
            body.Children.Add(again);
        }

        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, confirm },
        });

        DialogChrome.Apply(window, body);

        await window.ShowDialog(owner);
        return (answer, again?.IsChecked == true);
    }
}
