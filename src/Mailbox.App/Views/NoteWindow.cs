using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>
/// The note window: a square of the note's own colour with the writing on it, as the reference
/// opens one.
/// </summary>
/// <remarks>
/// <b>No capture of this window exists</b>, so its proportions are authored from the reference's
/// shape: the coloured face runs to the window's top edge with no caption band — the note's own
/// icon at the top left opens its menu (Save &amp; Close, Delete, Forward, Categorize, Print), the
/// close at the top right is drawn on the colour, the bar between them drags and maximises on a
/// double click, and a grip at the bottom right resizes. Two things about it are the reference's
/// behaviour rather than a choice — <b>there is no Save button</b>, because a note is saved by
/// being closed, and <b>there is no title field</b>, because a note's title is its first line
/// (<see cref="JournalEntry.WithBody"/>).
/// <para>
/// The face is the note's category colour mixed toward <c>notes.ground</c>, which is the same
/// mix the wall's squares are drawn with, so a note opened is the colour it was on the wall. The
/// footer writes the moment the note was last modified, which is the reference's own footer.
/// </para>
/// </remarks>
public sealed class NoteWindow : Window
{
    private readonly JournalEntry _original;
    private readonly TextBox _body = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Padding = new Thickness(10, 8, 10, 8),
    };

    private readonly TextBox _categories = new()
    {
        PlaceholderText = "Categories",
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Width = 150,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private readonly TextBlock _icon = new()
    {
        FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(7, 0, 0, 0),
    };

    private readonly Button _close = new()
    {
        Classes = { "flat" },
        FontSize = 11,
        Padding = new Thickness(8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Background = Brushes.Transparent,
    };

    private Border? _shape;
    private readonly TextBlock _made = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private Control? _grip;

    public NoteWindow(JournalEntry note)
    {
        ArgumentNullException.ThrowIfNull(note);
        _original = note;

        Title = note.Titled();
        Width = 320;
        Height = 300;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _body.Text = note.Description;
        _categories.Text = string.Join(", ", note.Categories);

        // The colour follows what is typed into the Categories line, so a note recoloured is
        // recoloured while it is open rather than on the next reload — and the window's own
        // name follows the first line, so the note a taskbar shows is the note it holds.
        _categories.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Repaint();
        };
        _body.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Title = _original.WithBody(_body.Text ?? string.Empty).Titled();
        };

        WindowFrame.Apply(this);
        _shape = (Border)WindowFrame.Rounded(BuildBody(), "list.background.brush");
        Content = _shape;
        Repaint();

        // Saved by being closed, which is the whole of a note's editing.
        Closing += (_, _) => Result = Collect();
    }

    /// <summary>The note as it was left, which the shell writes when the window closes.</summary>
    public JournalEntry? Result { get; private set; }

    /// <summary>True when the icon menu's Delete was chosen rather than a plain close.</summary>
    public bool Deleted { get; private set; }

    /// <summary>True when the icon menu's Forward was chosen: the shell composes after the save.</summary>
    public bool Forwarded { get; private set; }

    /// <summary>
    /// Everything this window holds and everything it is drawn as, for a harness to read back.
    /// </summary>
    /// <remarks>
    /// A photograph of a note says what it looks like and not what it holds, and the two questions
    /// this window raises are both invisible to one: what the body says after an edit, and what
    /// chrome is round it — a note has no Save button, so the only proof that closing saved is the
    /// text before, the text after and the store afterwards.
    /// </remarks>
    public IReadOnlyList<(string Field, string Value)> FormFields =>
    [
        ("Body", (_body.Text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', '⏎')),
        ("Categories", _categories.Text ?? string.Empty),
        ("Title", Title ?? string.Empty),
        ("Modified", _made.Text ?? string.Empty),
        ("Face", _shape?.Background?.ToString() ?? "none"),
        ("Size", $"{Width}×{Height}"),
        ("Resizable", CanResize ? "yes" : "no"),
        ("Decorations", WindowDecorations.ToString()),
        ("Chrome", $"icon menu at top left, close on the face, grip {(_grip?.IsVisible == true ? "drawn" : "hidden")}"),
    ];

    /// <summary>Sets one field by the name <see cref="FormFields"/> reports it under.</summary>
    /// <returns>False for a name this window has no field for, which is itself an answer.</returns>
    public bool SetFormField(string field, string value)
    {
        switch (field.Trim().ToLowerInvariant())
        {
            case "body": _body.Text = value; return true;
            case "categories": _categories.Text = value; return true;
            default: return false;
        }
    }

    /// <summary>Presses the close drawn on the face, which is the only way a note is saved.</summary>
    public bool PressClose()
    {
        _close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>Chooses one entry of the icon's menu by its label, for a harness run.</summary>
    public string PressMenu(string label)
    {
        var menu = BuildMenu();
        var item = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(i => (i.Header as string ?? string.Empty).Contains(label, StringComparison.OrdinalIgnoreCase));
        if (item is null) return $"no “{label}” on the note's menu";
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        return $"pressed “{item.Header}”";
    }

    private Control BuildBody()
    {
        _made.Text = _original.LastModified.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

        // The top strip the reference draws on the colour: the note icon and its menu at the
        // left, the close at the right, and everything between them dragging the window.
        _icon.Text = Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty("notes", 16);
        _icon.FontFamily = Mailbox.Theming.Icons.IconFont.Family;
        _icon.Cursor = new Cursor(StandardCursorType.Hand);
        _icon.PointerPressed += (_, e) =>
        {
            MenuProbe.Show("note icon menu", BuildMenu(), _icon);
            e.Handled = true;
        };

        _close.Content = Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty("dismiss", 16);
        _close.FontFamily = Mailbox.Theming.Icons.IconFont.Family;
        _close.Click += (_, _) => Close();

        var drag = new Border { Background = Brushes.Transparent };
        WindowFrame.Drags(this, drag);

        var strip = new Grid
        {
            Height = 24,
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        Grid.SetColumn(_icon, 0);
        strip.Children.Add(_icon);
        Grid.SetColumn(drag, 1);
        strip.Children.Add(drag);
        Grid.SetColumn(_close, 2);
        strip.Children.Add(_close);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(10, 0, 4, 4),
        };

        Grid.SetColumn(_made, 0);
        footer.Children.Add(_made);
        Grid.SetColumn(_categories, 1);
        footer.Children.Add(_categories);

        // The reference's resize grip, drawn where every desktop draws one. The window's own
        // edges resize too; the grip is the visible promise.
        _grip = WindowFrame.Grip(this);
        _grip.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_grip, 2);
        footer.Children.Add(_grip);

        return new DockPanel
        {
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Top, Child = strip },
                new Border { [DockPanel.DockProperty] = Dock.Bottom, Child = footer },
                _body,
            },
        };
    }

    /// <summary>The icon's menu: the reference's own five entries. Built full, then shown.</summary>
    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();

        var save = new MenuItem { Header = "Save & Close" };
        save.Click += (_, _) => Close();
        menu.Items.Add(save);

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) =>
        {
            Deleted = true;
            Close();
        };
        menu.Items.Add(delete);

        menu.Items.Add(new Separator());

        var forward = new MenuItem { Header = "Forward" };
        forward.Click += (_, _) =>
        {
            Forwarded = true;
            Close();
        };
        menu.Items.Add(forward);

        var categorize = new MenuItem { Header = "Categorize…" };
        categorize.Click += async (_, _) =>
        {
            var offered = App.Categories.All().Select(c => new PickListDialog.Item(c.Name, c.Name)).ToList();
            var ticked = (_categories.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var chosen = await PickListDialog.PickAsync(this, "Categorize", "Categories:", offered, ticked);
            if (chosen is not null) _categories.Text = string.Join(", ", chosen);
        };
        menu.Items.Add(categorize);

        var print = new MenuItem { Header = "Print" };
        print.Click += (_, _) =>
            PrintPreviewWindow.ForText(App.Themes, Collect().Titled(), _body.Text ?? string.Empty).Show(this);
        menu.Items.Add(print);

        return menu;
    }

    /// <summary>Paints the window in the note's own colour, as the wall paints its square.</summary>
    private void Repaint()
    {
        var categories = Split(_categories.Text);
        var colour = Colour(CategoryTokens.First(categories) ?? TokenKeys.Notes.Default);
        var face = Blend.Toward(colour, Colour(TokenKeys.Notes.Ground), Number(TokenKeys.Notes.Tint, 0.72));

        if (_shape is { } shape) shape.Background = new SolidColorBrush(face);

        var ink = new SolidColorBrush(Colour(TokenKeys.Notes.Text));
        _body.Foreground = ink;
        _body.CaretBrush = ink;
        _categories.Foreground = ink;
        _icon.Foreground = ink;
        _close.Foreground = ink;
        var dim = new SolidColorBrush(Colour(TokenKeys.Notes.TextDim));
        _made.Foreground = dim;
        _grip?.SetValue(TextBlock.ForegroundProperty, dim);
    }

    /// <summary>
    /// A token's colour, or magenta when a theme has not defined it — the same loud fallback
    /// the drawn surfaces use. A plausible note yellow here would hide the missing token.
    /// </summary>
    private Color Colour(string key)
        => this.TryFindResource(key + ".color", out var found) && found is Color colour ? colour : Colors.Magenta;

    private double Number(string key, double fallback)
        => this.TryFindResource(key + ".value", out var found) && found is double value ? value : fallback;

    private static string[] Split(string? text)
        => (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>What the window now says the note is: the body, and the title taken from it.</summary>
    /// <remarks>
    /// Stamped from the application's own clock rather than the machine's, so a pinned day writes
    /// the same moment every run — a note saved by a capture used to carry the afternoon it was
    /// taken, which is the one field that made two runs of the same pose disagree.
    /// </remarks>
    private JournalEntry Collect()
        => (_original with
        {
            Categories = Split(_categories.Text),
            LastModified = Mailbox.Core.PosedClock.UtcNow,
        })
        .WithBody(_body.Text ?? string.Empty);

}
