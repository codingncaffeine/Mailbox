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
    /// The same question, asked only while the Advanced page's "Prompt for confirmation before
    /// permanently deleting items" is on. Returns true — go ahead — when it is off.
    /// </summary>
    /// <remarks>
    /// One gate over the four places that ask it: Delete Permanently on a selection, Empty Folder,
    /// Empty Deleted Items from the Backstage, and Mailbox Clean-up's own Empty. They ask the same
    /// question about the same operation, and four copies of the check are four chances for one of
    /// them to keep asking after the reader said not to. What the switch removes is the asking —
    /// the delete itself is unchanged, and is still recoverable for as long as the Advanced page's
    /// retention says.
    /// </remarks>
    public static async Task<bool> AskBeforePermanentDeleteAsync(
        Window owner, string title, string message, string confirmLabel = "Delete")
    {
        if (!App.MailOptions.ConfirmPermanentDelete)
        {
            Mailbox.Core.Diagnostics.Log.Info(
                $"“{title}” went ahead without asking: confirmation before a permanent delete is switched off.");
            return true;
        }

        return await AskAsync(owner, title, message, confirmLabel);
    }

    /// <summary>
    /// A statement made of controls rather than of a sentence: one OK button, and whatever the
    /// caller wants shown above it. For the keyboard-shortcut list, which is a table.
    /// </summary>
    public static async Task ShowAsync(Window owner, string title, Control content)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(content);

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "OK", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => window.Close();
        ok.IsDefault = true;
        ok.IsCancel = true;

        DialogChrome.Apply(window, new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children = { content, ok },
        });

        await window.ShowDialog(owner);
    }

    /// <summary>
    /// A statement rather than a question: one OK button, which is also the cancel. For
    /// telling the user something happened, or why it did not.
    /// </summary>
    public static async Task TellAsync(Window owner, string title, string message)
    {
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

        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => window.Close();

        DialogChrome.Apply(window, new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children =
            {
                text,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok },
                },
            },
        });

        await window.ShowDialog(owner);
    }

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
