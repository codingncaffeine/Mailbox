using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// Gives a dialog the application's own frame instead of the system's.
/// </summary>
/// <remarks>
/// Every window in Mailbox draws its own caption, dialogs included — a themed application with
/// one window wearing the desktop's title bar looks like two applications. The shell and the
/// compose window have done this since Phase 0 through <see cref="WindowFrame"/>; this is the
/// same recipe with a dialog's proportions, in one place so the seven of them cannot drift.
/// <para>
/// A dialog's caption carries a title and a close button. The reference puts a "?" beside it,
/// which is left out here rather than drawn inert: there is no help to open yet.
/// </para>
/// </remarks>
internal static class DialogChrome
{
    /// <summary>Measured off the Options capture: the caption band is 33px.</summary>
    private const double TitleBarHeight = 33;

    /// <summary>
    /// Wraps <paramref name="content"/> in a caption bar and the rounded, clipping surface, and
    /// sets it as the window's content. The window's <see cref="Window.Title"/> is what the bar
    /// shows, so it must be set first. <paramref name="iconName"/> puts the named glyph at the
    /// caption's left — item windows carry their item's icon in the reference, plain dialogs none.
    /// </summary>
    internal static void Apply(Window window, Control content, string? iconName = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(content);

        WindowFrame.Apply(window);

        var bar = TitleBar(window, iconName);

        var root = new Grid { RowDefinitions = new RowDefinitions($"{TitleBarHeight},*") };
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var shell = WindowFrame.Rounded(root, "dialog.background.brush");

        // Classed so the stylesheet can give everything inside a dialog the dialog's colours
        // without each dialog naming them control by control. Anything that sets its own is
        // unaffected: a local value beats a style setter.
        shell.Classes.Add("dialogroot");

        window.Content = shell;
        WindowFrame.Drags(window, bar);
    }

    private static Control TitleBar(Window window, string? iconName)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };

        if (iconName is { Length: > 0 })
        {
            var icon = new TextBlock
            {
                Text = Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty(iconName, 16),
                FontFamily = Mailbox.Theming.Icons.IconFont.Family,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, -6, 0),
            };
            Bind(icon, TextBlock.ForegroundProperty, "dialog.foreground.brush");

            var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            pair.Children.Add(icon);
            Grid.SetColumn(title, 1);
            pair.Children.Add(title);
            Grid.SetColumn(pair, 0);
            grid.Children.Add(pair);
        }

        // Followed rather than copied. A dialog whose caption says something that changes —
        // the Reminders window counts what it is holding — set its Title and kept the words it
        // had been given when the frame was built.
        title.Bind(TextBlock.TextProperty, window.GetObservable(Window.TitleProperty));
        Bind(title, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        if (title.Parent is null)
        {
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);
        }

        var buttons = new CaptionButtons(window, dialog: true);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        // Transparent rather than unset, so the bar is something the pointer can grab: a panel
        // with no fill is not hit-tested, and the drag would only work over the title text.
        var band = new Border { Child = grid, Background = Brushes.Transparent };
        return band;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
