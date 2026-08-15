using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Mailbox.App.Views;

/// <summary>
/// Asks for one line of text, themed like the rest of the application.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="Confirm"/>, and there for the same reason: Avalonia has no
/// input box, and a dependency's would be the one window in a themed application that ignores
/// the theme.
/// </remarks>
public static class Prompt
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>The text entered, or null if the dialog was dismissed.</summary>
    public static async Task<string?> AskAsync(
        Window owner, string title, string label, string value = "")
    {
        string? answer = null;

        var caption = new TextBlock { Text = label };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var input = new TextBox { Text = value, Width = 280 };

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        Bind(window, TemplatedControl.BackgroundProperty, "dialog.background.brush");

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => window.Close();

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            answer = input.Text ?? string.Empty;
            window.Close();
        };

        window.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                caption,
                input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };

        // The name is what the dialog is for, so it opens ready to be typed over.
        window.Opened += (_, _) =>
        {
            input.SelectAll();
            input.Focus();
        };

        await window.ShowDialog(owner);
        return answer;
    }
}
