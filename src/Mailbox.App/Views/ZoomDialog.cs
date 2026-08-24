using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Mailbox.App.Views;

/// <summary>
/// The message window's Zoom dialog: the reference's percentages as radios, over the body of
/// the one open message — the shell reads at the status bar's slider instead, which is why this
/// dialog belongs to the window and not to the application.
/// </summary>
public static class ZoomDialog
{
    private static readonly int[] Presets = [50, 75, 100, 125, 150, 200];

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>Asks, and answers with a percentage — or null for Cancel.</summary>
    public static async Task<double?> AskAsync(Window owner, double current)
    {
        double? chosen = null;

        var caption = new TextBlock { Text = "Zoom to:" };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var options = new StackPanel { Spacing = 6 };
        var picked = (int)Math.Round(current);

        foreach (var percent in Presets)
        {
            var radio = new RadioButton
            {
                GroupName = "zoom",
                Content = $"{percent}%",
                IsChecked = percent == picked,
                Tag = percent,
            };
            Bind(radio, RadioButton.ForegroundProperty, "dialog.foreground.brush");
            options.Children.Add(radio);
        }

        // A percentage that is none of the presets — the last choice was custom-ish — keeps the
        // closest preset ticked rather than opening with nothing chosen.
        if (options.Children.OfType<RadioButton>().All(r => r.IsChecked != true))
        {
            var closest = options.Children.OfType<RadioButton>()
                .OrderBy(r => Math.Abs((int)r.Tag! - picked))
                .First();
            closest.IsChecked = true;
        }

        var window = new Window
        {
            Title = "Zoom",
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
            chosen = options.Children.OfType<RadioButton>()
                .FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? 100;
            window.Close();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                caption,
                options,
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

        await window.ShowDialog(owner);
        return chosen;
    }
}
