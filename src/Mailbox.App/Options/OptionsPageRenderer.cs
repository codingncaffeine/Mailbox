using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Options;

/// <summary>
/// Turns an <see cref="OptionsPage"/> description into controls.
/// </summary>
/// <remarks>
/// Every colour comes from a token, so the dialog follows the active theme like the rest of the
/// shell. Nothing here knows what any particular option means — the renderer only knows shapes,
/// which is what lets a new page be pure data.
/// </remarks>
public sealed class OptionsPageRenderer
{
    private const double IndentStep = 16;

    private readonly Dictionary<string, ContentControl> _slots = [];

    /// <summary>Raised when a row's button is pressed, with the button's label.</summary>
    public event EventHandler<string>? ActionInvoked;

    /// <summary>
    /// Slot hosts created during the last <see cref="Render"/>, by slot id. Returned rather
    /// than looked up by name: a code-built tree has no XAML name scope, so FindControl throws.
    /// </summary>
    public IReadOnlyDictionary<string, ContentControl> Slots => _slots;

    public Control Render(OptionsPage page)
    {
        _slots.Clear();
        var stack = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 10, 0) };
        stack.Children.Add(PageHeader(page.Icon, page.Description));

        foreach (var section in page.Sections)
        {
            stack.Children.Add(SectionHeading(section.Heading));
            foreach (var row in section.Rows) stack.Children.Add(RenderRow(row));
        }

        if (!page.IsAuthored) stack.Children.Add(NotYetTranscribed());
        return stack;
    }

    private Control NotYetTranscribed()
    {
        var text = new TextBlock
        {
            Text = "Captured from the reference; not transcribed yet.",
            Margin = new Thickness(0, 8, 0, 0),
        };
        Bind(text, TextBlock.ForegroundProperty, "text.secondary.brush");
        return text;
    }

    private Control RenderRow(OptionRow row)
    {
        var control = row switch
        {
            CheckRow r => Check(r),
            RadioRow r => Radio(r),
            ComboRow r => Combo(r),
            TextRow r => Text(r),
            SpinnerRow r => Spinner(r),
            NoteRow r => Note(r),
            SubHeadingRow r => SubHeading(r),
            ActionRow r => Action(r),
            BrowseRow r => Browse(r),
            SlotRow r => Slot(r),
            _ => new Panel(),
        };

        control.Margin = new Thickness(
            (row.Indent * IndentStep) + (row is ActionRow ? 0 : 14), 2, 0, 2);
        control.IsEnabled = !row.IsDisabled;
        return control;
    }

    // ---- Rows --------------------------------------------------------------------------

    private Control Check(CheckRow row)
    {
        var box = new CheckBox { IsChecked = row.IsChecked, Content = row.Label };
        Bind(box, Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty,
            row.IsDisabled ? "text.disabled.brush" : "text.primary.brush");
        return row.HasInfo ? WithInfo(box) : box;
    }

    private Control Radio(RadioRow row)
    {
        var button = new RadioButton
        {
            GroupName = row.Group,
            IsChecked = row.IsChecked,
            Content = row.Label,
        };
        Bind(button, Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty,
            "text.primary.brush");
        return row.HasInfo ? WithInfo(button) : button;
    }

    private Control Combo(ComboRow row)
    {
        var combo = new ComboBox
        {
            ItemsSource = row.Items.ToList(),
            SelectedIndex = row.Selected,
            MinWidth = row.Width,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return Labelled(row.Label, combo, row.LabelWidth, row.HasInfo);
    }

    private Control Text(TextRow row)
    {
        var box = new TextBox
        {
            Text = row.Value,
            Width = row.Width,
            PlaceholderText = row.Placeholder,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return Labelled(row.Label, box, row.LabelWidth, row.HasInfo);
    }

    private Control Spinner(SpinnerRow row)
    {
        var spinner = new NumericUpDown
        {
            Value = row.Value,
            Minimum = row.Minimum,
            Maximum = row.Maximum,
            Increment = 1,
            Width = 78,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return Labelled(row.Label, spinner, row.LabelWidth, row.HasInfo);
    }

    private Control Note(NoteRow row)
    {
        var text = new TextBlock { Text = row.Text, TextWrapping = TextWrapping.Wrap };
        Bind(text, TextBlock.ForegroundProperty, "text.primary.brush");
        return text;
    }

    private Control SubHeading(SubHeadingRow row)
    {
        var text = new TextBlock
        {
            Text = row.Text,
            Margin = new Thickness(0, 4, 0, 2),
        };
        Bind(text, TextBlock.ForegroundProperty, "text.primary.brush");
        return text;
    }

    private Control Browse(BrowseRow row)
    {
        var field = new TextBox { Text = row.Value, Width = 240, VerticalAlignment = VerticalAlignment.Center };

        var browse = DialogButton("Browse...");
        browse.Margin = new Thickness(6, 0, 0, 0);

        var group = new StackPanel { Orientation = Orientation.Horizontal };
        group.Children.Add(field);
        group.Children.Add(browse);

        return Labelled(row.Label, group, row.LabelWidth, row.HasInfo);
    }

    /// <summary>Icon, description, and a button pushed to the right edge.</summary>
    private Control Action(ActionRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(row.Icon, 24),
            FontFamily = IconFont.Family,
            FontSize = 19,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(glyph);

        var middle = new StackPanel { Spacing = 3 };
        var description = new TextBlock { Text = row.Description, TextWrapping = TextWrapping.Wrap };
        Bind(description, TextBlock.ForegroundProperty, "text.primary.brush");
        middle.Children.Add(description);

        foreach (var child in row.Children ?? [])
        {
            var rendered = RenderRow(child);
            rendered.Margin = new Thickness(child.Indent * IndentStep, 2, 0, 2);
            middle.Children.Add(rendered);
        }

        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        var button = DialogButton(row.ButtonLabel);
        button.VerticalAlignment = VerticalAlignment.Top;
        button.Margin = new Thickness(12, 0, 0, 0);
        button.Click += (_, _) => ActionInvoked?.Invoke(this, row.ButtonLabel);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);

        return grid;
    }

    /// <summary>Empty host the window fills after rendering.</summary>
    private Control Slot(SlotRow row)
    {
        var host = new ContentControl();
        _slots[row.SlotId] = host;
        return host;
    }

    // ---- Building blocks ---------------------------------------------------------------

    private Control Labelled(string label, Control control, double labelWidth, bool info)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        if (!string.IsNullOrEmpty(label))
        {
            var text = new TextBlock
            {
                Text = label,
                Width = labelWidth,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(text, TextBlock.ForegroundProperty, "text.primary.brush");
            row.Children.Add(text);
        }

        row.Children.Add(control);
        if (info) row.Children.Add(InfoGlyph());
        return row;
    }

    private Control WithInfo(Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(control);
        row.Children.Add(InfoGlyph());
        return row;
    }

    private Control InfoGlyph()
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("info", 16),
            FontFamily = IconFont.Family,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
        return glyph;
    }

    public Button DialogButton(string label)
    {
        var text = new TextBlock
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "text.primary.brush");

        var button = new Button
        {
            Content = text,
            MinWidth = 108,
            Height = 24,
            Padding = new Thickness(8, 0),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        Bind(button, Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty,
            "border.strong.brush");
        Bind(button, Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty,
            "surface.sunken.brush");
        return button;
    }

    private Control PageHeader(string icon, string description)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 24),
            FontFamily = IconFont.Family,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
        row.Children.Add(glyph);

        var text = new TextBlock { Text = description, VerticalAlignment = VerticalAlignment.Center };
        Bind(text, TextBlock.ForegroundProperty, "text.primary.brush");
        row.Children.Add(text);

        return row;
    }

    private Control SectionHeading(string text)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 12, 0, 6),
        };

        var label = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold };
        Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(rule, Border.BackgroundProperty, "border.subtle.brush");
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);

        return grid;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
