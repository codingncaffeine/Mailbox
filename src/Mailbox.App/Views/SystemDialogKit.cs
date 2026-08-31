using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// The parts a system dialog is built from: labels, the desktop's push button, the flat
/// toolbar button with its coloured icon, and the white banner that names a page.
/// </summary>
/// <remarks>
/// Kept together so the dialogs that wear the system palette — Account Settings and the
/// small dialogs it opens — build from one vocabulary. Every measurement is off the Account
/// Settings capture, and every colour is a <c>systemdialog.*</c> token through the styles in
/// <c>SystemDialog.axaml</c>; nothing here names a colour.
/// </remarks>
internal static class SystemDialogKit
{
    internal static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>A line of the dialog's text: 12px, black, bold when asked.</summary>
    internal static TextBlock Label(string text, bool bold = false, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        return block;
    }

    /// <summary>
    /// A paragraph of the dialog's text. The desktop sets its static text on 13px lines,
    /// tighter than the font's own leading; measured off the Data Files capture.
    /// </summary>
    internal static TextBlock Paragraph(string text)
    {
        var block = Label(text, wrap: true);
        block.LineHeight = 13;
        return block;
    }

    /// <summary>The desktop's push button: 21px tall, 73 wide at least, in a rounded 1px line.</summary>
    internal static Button PushButton(string text, Action onClick, double? width = null)
    {
        var button = PushButton(text, width);
        button.Click += (_, _) => onClick();
        return button;
    }

    internal static Button PushButton(string text, Func<Task> onClick, double? width = null)
    {
        var button = PushButton(text, width);
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private static Button PushButton(string text, double? width)
    {
        var button = new Button { Content = text, Classes = { "sysbutton" } };

        // A button given the reference's exact width has its label centred in it rather than
        // padded out to it; the padding is what would clip a label a font renders wider.
        if (width is { } w)
        {
            button.Width = w;
            button.Padding = new Thickness(2, 3, 2, 0);
        }

        return button;
    }

    /// <summary>
    /// A toolbar button: a 16px coloured icon, a 4px gap, and its word, flat until hovered.
    /// The icon greys to a silhouette with the button.
    /// </summary>
    internal static Button ToolButton(string icon, string text, Func<Task> onClick)
    {
        var button = ToolButton(icon, text);
        button.Click += async (_, _) => await onClick();
        return button;
    }

    internal static Button ToolButton(string icon, string text, Action onClick)
    {
        var button = ToolButton(icon, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button ToolButton(string icon, string text)
    {
        // No icon named is a button the reference draws as a word alone — Run Rules Now and
        // Options on the rules toolbar. An empty ClassicIcon would still take an icon's width and
        // push the end of the bar off the dialog.
        var glyph = icon.Length > 0 ? new ClassicIcon(icon) { VerticalAlignment = VerticalAlignment.Center } : null;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null) content.Children.Add(glyph);

        // The move arrows carry no word: an icon alone in a button the width of the icon and
        // its padding, which is how the reference spaces them.
        if (text.Length > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(glyph is null ? 0 : 4, 0, 0, 1),
            });
        }

        var button = new Button
        {
            Classes = { "systool" },
            Content = content,
        };

        // The icon follows the button's enabled state rather than being told: a button whose
        // icon stayed coloured after it went grey is what an owner notices first.
        if (glyph is not null)
        {
            glyph[!ClassicIcon.IsDisabledProperty] = new Avalonia.Data.Binding("IsEnabled")
            {
                Source = button,
                Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, bool>(enabled => !enabled),
            };
        }

        // The name a screen reader speaks: the word on the button, or for the wordless move
        // arrows the icon's own key, said as a word.
        Avalonia.Automation.AutomationProperties.SetName(button,
            text.Length > 0 ? text : char.ToUpperInvariant(icon[0]) + icon[1..].Replace('-', ' '));

        // A label wants the button's text at rest and the disabled ink otherwise; the stylesheet
        // handles both, but only when the label carries no ink of its own.
        return button;
    }

    /// <summary>
    /// The upright rule a toolbar puts between two groups of buttons.
    /// </summary>
    /// <remarks>
    /// The reference draws one after the move arrows, which is what makes the arrows read as
    /// belonging to the list rather than to Run Rules Now beside them. A <see cref="Border"/>
    /// rather than a character, so it lines up with the buttons instead of sitting on their
    /// baseline.
    /// </remarks>
    internal static Border ToolSeparator()
    {
        var rule = new Border
        {
            Width = 1,
            Height = 16,
            Margin = new Thickness(5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(rule, Border.BackgroundProperty, "systemdialog.border.brush");
        return rule;
    }

    /// <summary>A row of toolbar buttons, 6px in from the page's left edge; its icons stand 15px under the page's top edge.</summary>
    internal static StackPanel Toolbar(params Control[] buttons)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 11, 0, 0),
            Height = 22,
        };
        foreach (var button in buttons) bar.Children.Add(button);
        return bar;
    }

    /// <summary>
    /// The white band under the caption naming the page: a bold heading, and a sentence under
    /// it set 18px further in, over a grey rule. 62px tall, measured.
    /// </summary>
    internal static Border Banner(TextBlock heading, TextBlock description)
    {
        // A 12px line's cap top stands 3px below the block's top; the heading's cap top is
        // 13px into the band and the sentence's 29, measured.
        heading.FontWeight = FontWeight.Bold;
        heading.Margin = new Thickness(24, 10, 12, 0);
        heading.VerticalAlignment = VerticalAlignment.Top;

        description.Margin = new Thickness(42, 26, 12, 0);
        description.VerticalAlignment = VerticalAlignment.Top;
        description.TextWrapping = TextWrapping.NoWrap;

        var band = new Border
        {
            Height = 62,
            Child = new Panel { Children = { heading, description } },
        };
        Bind(band, Border.BackgroundProperty, "systemdialog.banner.brush");
        return band;
    }

    /// <summary>The rule under the banner.</summary>
    internal static Border BannerRule()
    {
        var rule = new Border { Height = 1 };
        Bind(rule, Border.BackgroundProperty, "systemdialog.banner.rule.brush");
        return rule;
    }

    /// <summary>The desktop's edit box: white in a hairline, 20px tall.</summary>
    internal static TextBox Field(string watermark = "")
        => new() { Classes = { "sysfield" }, PlaceholderText = watermark };

    /// <summary>
    /// The desktop's group box: a hairline rectangle with its name sitting on the top edge.
    /// </summary>
    /// <remarks>
    /// Drawn rather than borrowed, as the tabs and the report list are: Avalonia has no group
    /// box, and the reference's dialogs are built out of them — the RSS Feed Options dialog is
    /// four of them stacked.
    /// </remarks>
    internal static Control GroupBox(string label, Control content, double top = 0)
    {
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Margin = new Thickness(0, 7, 0, 0),
            Padding = new Thickness(9, 12, 9, 9),
            Child = content,
        };
        Bind(frame, Border.BorderBrushProperty, "systemdialog.border.brush");

        // The name sits on the line, with the dialog's own ground behind it so the line does not
        // run through the text — which is how Win32 has drawn a group box since 1995.
        var name = Label(label);
        name.Margin = new Thickness(8, 0, 0, 0);
        name.Padding = new Thickness(3, 0, 3, 0);
        name.HorizontalAlignment = HorizontalAlignment.Left;
        name.VerticalAlignment = VerticalAlignment.Top;

        // Top, and only as tall as the word. Left to stretch, this opaque rectangle runs the
        // whole height of the box and paints out the first inch of every row inside it — which
        // is what it did: "Feed Name:" read "me:", and a paragraph began mid-word. It is there to
        // break one line, not to be a column.
        var backdrop = new Border
        {
            Child = name,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Bind(backdrop, Border.BackgroundProperty, "systemdialog.background.brush");

        return new Panel
        {
            Margin = new Thickness(0, top, 0, 0),
            Children = { frame, backdrop },
        };
    }

    /// <summary>The desktop's tick box, on a system dialog's page.</summary>
    internal static CheckBox Tick(string label, bool isChecked = false)
    {
        var box = new CheckBox { Content = label, IsChecked = isChecked };
        Bind(box, CheckBox.ForegroundProperty, "systemdialog.foreground.brush");
        return box;
    }
}
