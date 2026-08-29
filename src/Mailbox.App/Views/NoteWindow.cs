using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>
/// The note window: a square of the note's own colour with the writing on it, as the reference
/// opens one.
/// </summary>
/// <remarks>
/// <b>No capture of this window exists</b>, so its proportions are authored from the reference's
/// shape: a small square, the text filling it, and the date along the bottom. Two things about it
/// are the reference's behaviour rather than a choice — <b>there is no Save button</b>, because a
/// note is saved by being closed, and <b>there is no title field</b>, because a note's title is
/// its first line (<see cref="JournalEntry.WithBody"/>).
/// <para>
/// The face is the note's category colour mixed toward <c>notes.ground</c>, which is the same
/// mix the wall's squares are drawn with, so a note opened is the colour it was on the wall. The
/// Categories line is the stand-in for the reference's own category picker until Phase 14 makes
/// the categories one set across the modules.
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

    private readonly Border _face = new();
    private readonly TextBlock _made = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };

    public NoteWindow(JournalEntry note)
    {
        ArgumentNullException.ThrowIfNull(note);
        _original = note;

        Title = note.Titled();
        Width = 320;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _body.Text = note.Description;
        _categories.Text = string.Join(", ", note.Categories);

        // The colour follows what is typed into the Categories line, so a note recoloured is
        // recoloured while it is open rather than on the next reload.
        _categories.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Repaint();
        };

        DialogChrome.Apply(this, BuildBody());
        Repaint();

        // Saved by being closed, which is the whole of a note's editing.
        Closing += (_, _) => Result = Collect();
    }

    /// <summary>The note as it was left, which the shell writes when the window closes.</summary>
    public JournalEntry? Result { get; private set; }

    /// <summary>
    /// Everything this window holds and everything it is drawn as, for a harness to read back.
    /// </summary>
    /// <remarks>
    /// A photograph of a note says what it looks like and not what it holds, and the two questions
    /// this window raises are both invisible to one: what the body says after an edit, and what
    /// chrome is round it — a note has no Save button, so the only proof that closing saved is the
    /// text before, the text after and the store afterwards.
    /// </remarks>
    internal IReadOnlyList<(string Field, string Value)> FormFields =>
    [
        ("Body", (_body.Text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', '⏎')),
        ("Categories", _categories.Text ?? string.Empty),
        ("Title", Title ?? string.Empty),
        ("Made", _made.Text ?? string.Empty),
        ("Face", _face.Background?.ToString() ?? "none"),
        ("Size", $"{Width}×{Height}"),
        ("Resizable", CanResize ? "yes" : "no"),
        ("Decorations", WindowDecorations.ToString()),
        ("Caption buttons", string.Join(
            ", ",
            this.GetVisualDescendants().OfType<CaptionButtons>()
                .SelectMany(c => c.GetVisualDescendants().OfType<Button>())
                .Select(b => ToolTip.GetTip(b) as string ?? "?"))),
    ];

    /// <summary>Sets one field by the name <see cref="FormFields"/> reports it under.</summary>
    /// <returns>False for a name this window has no field for, which is itself an answer.</returns>
    internal bool SetFormField(string field, string value)
    {
        switch (field.Trim().ToLowerInvariant())
        {
            case "body": _body.Text = value; return true;
            case "categories": _categories.Text = value; return true;
            default: return false;
        }
    }

    /// <summary>Presses the close the caption draws, which is the only way a note is saved.</summary>
    internal bool PressClose()
        => this.GetVisualDescendants().OfType<CaptionButtons>().FirstOrDefault() is { } caption
           && caption.Press("close");

    private Control BuildBody()
    {
        _made.Text = (_original.When?.Wall ?? _original.LastModified.LocalDateTime).ToString("g", CultureInfo.CurrentCulture);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 0, 10, 8),
        };

        Grid.SetColumn(_made, 0);
        footer.Children.Add(_made);
        Grid.SetColumn(_categories, 1);
        footer.Children.Add(_categories);

        _face.Child = new DockPanel
        {
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Bottom, Child = footer },
                _body,
            },
        };

        return _face;
    }

    /// <summary>Paints the window in the note's own colour, as the wall paints its square.</summary>
    private void Repaint()
    {
        var categories = Split(_categories.Text);
        var colour = Colour(CategoryTokens.First(categories) ?? TokenKeys.Notes.Default);
        var face = Blend.Toward(colour, Colour(TokenKeys.Notes.Ground), Number(TokenKeys.Notes.Tint, 0.72));

        _face.Background = new SolidColorBrush(face);

        var ink = new SolidColorBrush(Colour(TokenKeys.Notes.Text));
        _body.Foreground = ink;
        _body.CaretBrush = ink;
        _categories.Foreground = ink;
        _made.Foreground = new SolidColorBrush(Colour(TokenKeys.Notes.TextDim));
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
