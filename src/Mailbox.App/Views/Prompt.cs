using Avalonia;
using Avalonia.Controls;
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
        Window owner, string title, string label, string value = "", bool multiline = false)
    {
        // A posed answer, for a harness that cannot type into a modal — see HarnessAnswer.
        // Capture runs only, and only while MAILBOX_ANSWER still has an entry for this dialog.
        if (HarnessAnswer.Next(title) is { } posed)
        {
            return HarnessAnswer.IsCancel(posed) ? null : posed;
        }

        string? answer = null;

        var caption = new TextBlock { Text = label };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        // Several lines for a signature or anything else worth a paragraph; one for a name.
        // The unconstrained values are 0 and infinity: NaN is a valid Width or Height (it means
        // "auto") but not a valid MinHeight or MaxHeight, and setting it throws before the
        // window exists — so a single-line prompt would never open.
        var input = new TextBox
        {
            Text = value,
            Width = multiline ? 360 : 280,
            AcceptsReturn = multiline,
            MinHeight = multiline ? 120 : 0,
            MaxHeight = multiline ? 240 : double.PositiveInfinity,
            TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap,
        };

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => window.Close();

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            answer = input.Text ?? string.Empty;
            window.Close();
        };

        var body = new StackPanel
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

        DialogChrome.Apply(window, body);

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
