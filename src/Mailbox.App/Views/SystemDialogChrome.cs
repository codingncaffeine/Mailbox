using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// The frame of a system dialog: the desktop's light caption over the desktop's light dialog
/// ground, whatever the theme.
/// </summary>
/// <remarks>
/// The reference draws Account Settings and the dialogs it opens with the operating system's
/// own controls rather than its own, and the operating system's dialog palette does not follow
/// the Office theme — those windows are the same light grey under Dark Gray as under Colorful.
/// So this is <see cref="DialogChrome"/> with the caption's proportions and every colour taken
/// from the <c>systemdialog.*</c> tokens instead of the <c>dialog.*</c> six.
/// <para>
/// Still drawn by the application rather than handed to the desktop's decorator: the rule is
/// that every window carries the application's own caption buttons, and a system dialog is
/// no exception — it merely wears the palette the reference's does.
/// </para>
/// </remarks>
internal static class SystemDialogChrome
{
    /// <summary>Measured off the Account Settings capture: the desktop's caption is 30px.</summary>
    internal const double TitleBarHeight = 30;

    /// <summary>
    /// Wraps <paramref name="content"/> in the caption and the rounded, clipping surface, and
    /// sets it as the window's content. <see cref="Window.Title"/> must be set first.
    /// </summary>
    internal static void Apply(Window window, Control content)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(content);

        WindowFrame.Apply(window);

        var bar = TitleBar(window);

        var root = new Grid { RowDefinitions = new RowDefinitions($"{TitleBarHeight},*") };
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var shell = WindowFrame.Rounded(root, "systemdialog.background.brush");

        // Classed so the stylesheet can give the dialog's text and buttons the system palette
        // without each dialog naming it control by control.
        shell.Classes.Add("systemdialogroot");

        window.Content = shell;
        WindowFrame.Drags(window, bar);
    }

    private static Control TitleBar(Window window)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        // The title's cap top stands 10px below the caption's top and 9px in, measured.
        var title = new TextBlock
        {
            Text = window.Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 1),
        };
        Bind(title, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var buttons = new CaptionButtons(window, dialog: true, system: true);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        var band = new Border { Child = grid };
        Bind(band, Border.BackgroundProperty, "systemdialog.titlebar.brush");
        return band;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
