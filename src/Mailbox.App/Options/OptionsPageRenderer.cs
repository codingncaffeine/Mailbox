using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Theming.Icons;

using Mailbox.Core.Settings;

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
    private readonly Dictionary<Control, string> _keys = [];
    private readonly SettingsStore _settings;

    /// <summary>
    /// Rows read and write through the store, so a page rebuilt after switching away comes back
    /// showing what was chosen. Before this every control read a static default and discarded
    /// whatever it was told the moment the page was left.
    /// </summary>
    public OptionsPageRenderer(SettingsStore settings) => _settings = settings;

    /// <summary>Raised when a row's button is pressed, with the button's label.</summary>
    public event EventHandler<string>? ActionInvoked;

    /// <summary>
    /// Slot hosts created during the last <see cref="Render"/>, by slot id. Returned rather
    /// than looked up by name: a code-built tree has no XAML name scope, so FindControl throws.
    /// </summary>
    public IReadOnlyDictionary<string, ContentControl> Slots => _slots;

    /// <summary>
    /// The settings key behind each control that has one, for the harness to read a press back by.
    /// </summary>
    /// <remarks>
    /// A row's key is worked out here and nowhere else — its own, or its label when it has not
    /// declared one — so anything that wants to say what a press wrote has to ask rather than
    /// guess. §20 turns on rows knowing which key each one is waiting for.
    /// </remarks>
    public IReadOnlyDictionary<Control, string> Keys => _keys;

    /// <summary>
    /// Registers a control a slot built itself, so a press on it reads back like any other row's.
    /// </summary>
    /// <remarks>
    /// A slot that draws a real setting — the reminder sound's tick box, which the reference puts
    /// on one line with a field and a Browse… — is invisible to the harness otherwise, and
    /// <c>MAILBOX_OPTIONS_PRESS</c> would report "the row carries no key" for a row that carries
    /// one. That is worse than no answer: it reads as §20's hundred-odd waiting rows rather than
    /// as a wired one.
    /// </remarks>
    public void Remember(Control control, string key) => _keys[control] = key;

    public Control Render(OptionsPage page)
    {
        _slots.Clear();
        _keys.Clear();
        var stack = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 10, 0) };
        stack.Children.Add(PageHeader(page.Icon, page.Description));

        foreach (var section in page.Sections)
        {
            stack.Children.Add(SectionHeading(section.Heading));

            var rows = section.Rows.Select(RenderRow).ToList();
            if (section.Icon is { Length: > 0 } icon) stack.Children.Add(WithSectionIcon(icon, rows));
            else foreach (var row in rows) stack.Children.Add(row);
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
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
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

    /// <summary>
    /// The key a row persists under: its own if it declares one, otherwise its label. Rows
    /// without a label carry no value and are not stored.
    /// </summary>
    private static string? KeyFor(OptionRow row, string label)
        => row.Key ?? (string.IsNullOrWhiteSpace(label) ? null : label);

    private Control Check(CheckRow row)
    {
        var key = KeyFor(row, row.Label);
        var box = new CheckBox
        {
            IsChecked = key is null ? row.IsChecked : _settings.GetBool(key, row.IsChecked),
            Content = row.Label,
        };
        if (key is not null)
        {
            box.IsCheckedChanged += (_, _) => _settings.Set(key, box.IsChecked == true);
            _keys[box] = key;
        }

        Bind(box, Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty,
            row.IsDisabled ? "text.disabled.brush" : "dialog.foreground.brush");
        return row.HasInfo ? WithInfo(box) : box;
    }

    private Control Radio(RadioRow row)
    {
        // Radios persist under their group, holding the chosen label — an index would shift
        // the moment an option is inserted above it.
        var key = row.Key ?? row.Group;
        var button = new RadioButton
        {
            GroupName = row.Group,
            IsChecked = _settings.Has(key)
                ? _settings.GetString(key) == row.Label
                : row.IsChecked,
            Content = row.Label,
        };
        button.IsCheckedChanged += (_, _) =>
        {
            if (button.IsChecked == true) _settings.Set(key, row.Label);
        };
        _keys[button] = key;
        Bind(button, Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty,
            "dialog.foreground.brush");
        return row.HasInfo ? WithInfo(button) : button;
    }

    private Control Combo(ComboRow row)
    {
        var key = KeyFor(row, row.Label);
        var combo = new ComboBox
        {
            ItemsSource = row.Items.ToList(),
            MinWidth = row.Width,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // A row that names what its entries stand for keeps the text; every other keeps the
        // index, which is what a list this application wrote can be trusted to mean.
        if (key is not null && row.Values is { Count: > 0 } values)
        {
            var stored = _settings.GetString(key, string.Empty);
            var found = values.ToList().IndexOf(stored);
            combo.SelectedIndex = found >= 0 ? found : Math.Clamp(row.Selected, 0, values.Count - 1);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0 && combo.SelectedIndex < values.Count)
                {
                    _settings.Set(key, values[combo.SelectedIndex]);
                }
            };

            _keys[combo] = key;
            return Labelled(row.Label, combo, row.LabelWidth, row.HasInfo);
        }

        combo.SelectedIndex = key is null ? row.Selected : (int)_settings.GetNumber(key, row.Selected);
        if (key is not null)
        {
            combo.SelectionChanged += (_, _) => _settings.Set(key, combo.SelectedIndex);
            _keys[combo] = key;
        }

        return Labelled(row.Label, combo, row.LabelWidth, row.HasInfo);
    }

    private Control Text(TextRow row)
    {
        var key = KeyFor(row, row.Label);
        var box = new TextBox
        {
            Text = key is null ? row.Value : _settings.GetString(key, row.Value),
            Width = row.Width,
            PlaceholderText = row.Placeholder,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (key is not null)
        {
            box.LostFocus += (_, _) => _settings.Set(key, box.Text ?? string.Empty);
            _keys[box] = key;
        }

        return Labelled(row.Label, box, row.LabelWidth, row.HasInfo);
    }

    private Control Spinner(SpinnerRow row)
    {
        var key = KeyFor(row, row.Label);
        var spinner = new NumericUpDown
        {
            Value = (decimal)(key is null ? row.Value : _settings.GetNumber(key, row.Value)),
            Minimum = row.Minimum,
            Maximum = row.Maximum,
            Increment = 1,
            Width = 78,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (key is not null)
        {
            spinner.ValueChanged += (_, _) =>
                _settings.Set(key, (double)(spinner.Value ?? row.Value));
            _keys[spinner] = key;
        }

        return Labelled(row.Label, spinner, row.LabelWidth, row.HasInfo);
    }

    private Control Note(NoteRow row)
    {
        var text = new TextBlock { Text = row.Text, TextWrapping = TextWrapping.Wrap };
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return text;
    }

    private Control SubHeading(SubHeadingRow row)
    {
        var text = new TextBlock
        {
            Text = row.Text,
            Margin = new Thickness(0, 4, 0, 2),
        };
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
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
        Bind(description, TextBlock.ForegroundProperty, "dialog.foreground.brush");
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
            Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
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

        // The label sits on the button, not on the dialog, and those are different colours
        // wherever the two disagree — which in Dark Gray is white ink on a light grey button.
        Bind(text, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

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
            "dialog.border.brush");
        Bind(button, Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty,
            "dialog.surface.brush");
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
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        row.Children.Add(text);

        return row;
    }

    /// <summary>
    /// A group's rows with the reference's glyph standing to their left.
    /// </summary>
    /// <remarks>
    /// One icon for the whole group rather than one per row, which is how the reference draws
    /// them — it sits against the top of the run, not centred on it, so a group of five rows and
    /// a group of two put their icon in the same place.
    /// </remarks>
    private Control WithSectionIcon(string icon, IReadOnlyList<Control> rows)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 24),
            FontFamily = IconFont.Family,
            FontSize = 19,
            Margin = new Thickness(0, 2, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        // The accent, as the ActionRow glyphs on the same pages take: the ribbon's own outline
        // ink is a dark grey and all but disappears against the Options page behind it.
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");

        var lines = new StackPanel();
        foreach (var row in rows) lines.Children.Add(row);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(glyph);
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);
        return grid;
    }

    private Control SectionHeading(string text)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 12, 0, 6),
        };

        var label = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold };
        Bind(label, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(rule, Border.BackgroundProperty, "dialog.border.brush");
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);

        return grid;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
