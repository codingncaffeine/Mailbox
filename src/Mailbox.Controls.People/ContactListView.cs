using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Contacts;
using Mailbox.Controls.Common;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.People;

/// <summary>
/// The People module's list: everybody in the shown address books, filed in order, with the
/// alphabet index down its left-hand side.
/// </summary>
/// <remarks>
/// Measured off the one People capture there is, which is of an empty address book with the peek
/// pane over its left-hand half: the index letters are 23px apart with the first baseline 25px
/// below the list's top and their ink 17px in from its edge, and the empty-state text is two
/// centred lines 13px apart. **The rows themselves have no capture** — the reference's own
/// address book in that screenshot is empty — so a row's 30px height, its 20px avatar and where
/// its name starts are authored from the reference's shape, and a capture of a full list would
/// settle them.
/// <para>
/// The index is what the Options page calls "Show an additional index", and it is not decoration:
/// it is how the reference reaches the Ws in an address book of two thousand people without a
/// scrollbar drag.
/// </para>
/// </remarks>
public sealed class ContactListView : DrawnSurface
{
    /// <summary>The index column's width, and where its letters sit inside it.</summary>
    private const double IndexWidth = 26;
    private const double IndexInk = 17;

    /// <summary>Measured: the letters are 23px apart, the first baseline 25px down.</summary>
    private const double IndexStep = 23;
    private const double IndexFirstBaseline = 25;
    private const double IndexTextSize = 11;

    /// <summary>Authored: no capture shows a row, this being an empty address book.</summary>
    private const double RowHeight = 30;
    private const double AvatarSize = 20;
    private const double AvatarLeft = 8;
    private const double NameLeft = 36;
    private const double NameTextSize = 15;

    /// <summary>Measured: two centred lines, 13px apart, the first 22px below the top.</summary>
    private const double EmptyTextSize = 12;
    private const double EmptyFirstBaseline = 22;
    private const double EmptyLineHeight = 13;

    private readonly Dictionary<string, Color> _colours = [];
    private readonly List<(Rect Box, ContactRow Row)> _rowHits = [];
    private readonly List<(Rect Box, char Letter)> _indexHits = [];

    private IReadOnlyList<ContactRow> _rows = [];
    private ContactRow? _selected;
    private int _scroll;
    private bool _showIndex = true;

    public ContactListView()
    {
        Focusable = true;
    }

    /// <summary>The contacts on show, already filed in order.</summary>
    public IReadOnlyList<ContactRow> Rows
    {
        get => _rows;
        set
        {
            _rows = value ?? [];
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _rows.Count - 1));
            if (_selected is { } chosen && !_rows.Any(r => r.Id == chosen.Id)) _selected = null;
            InvalidateVisual();
        }
    }

    public ContactRow? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            InvalidateVisual();
        }
    }

    /// <summary>Whether the alphabet runs down the side — the People page's own switch.</summary>
    public bool ShowIndex
    {
        get => _showIndex;
        set
        {
            if (_showIndex == value) return;
            _showIndex = value;
            InvalidateVisual();
        }
    }

    /// <summary>The first row drawn, which is what the wheel and the index move.</summary>
    public int Scroll
    {
        get => _scroll;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, _rows.Count - 1));
            if (_scroll == clamped) return;
            _scroll = clamped;
            InvalidateVisual();
        }
    }

    /// <summary>How a contact files, which the Options page's order decides.</summary>
    public FileAsOrder Order { get; set; } = FileAsOrder.LastFirst;

    public event EventHandler<ContactRow>? ContactSelected;
    public event EventHandler<ContactRow>? ContactActivated;

    /// <summary>A double click on nothing: the reference's own "double-click here" invitation.</summary>
    public event EventHandler? EmptySpaceActivated;

    /// <summary>What the status bar counts.</summary>
    public int Count => _rows.Count;

    // ---- Render --------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        _rowHits.Clear();
        _indexHits.Clear();

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 40 || height < 20) return;

        Fill(context, new Rect(0, 0, width, height), Colour(TokenKeys.List.Background));

        var left = _showIndex ? IndexWidth : 0;
        if (_showIndex) DrawIndex(context, height);

        if (_rows.Count == 0)
        {
            DrawEmpty(context, new Rect(left, 0, width - left, height));
            return;
        }

        DrawRows(context, new Rect(left, 0, width - left, height));
    }

    /// <summary>
    /// The alphabet down the side: <c>123</c> for everything that does not start with a letter,
    /// then A to Z, with a letter nobody files under drawn faintly rather than left out — the
    /// index is a ruler, and a ruler with missing marks is not one.
    /// </summary>
    private void DrawIndex(DrawingContext context, double height)
    {
        var present = _rows.Select(r => r.Contact.IndexLetter(Order)).ToHashSet();
        var ink = Colour(TokenKeys.List.ReadText);
        var faint = Colour(TokenKeys.Text.Disabled);

        var baseline = IndexFirstBaseline;
        foreach (var letter in Letters())
        {
            if (baseline > height) break;

            var text = letter == '#' ? "123" : letter.ToString(Culture);
            var run = Ink(text, IndexTextSize, present.Contains(letter) ? ink : faint);
            DrawAt(context, run, IndexInk - (run.Width / 2), baseline);
            _indexHits.Add((new Rect(0, baseline - IndexStep + 6, IndexWidth, IndexStep), letter));
            baseline += IndexStep;
        }
    }

    private static IEnumerable<char> Letters()
    {
        yield return '#';
        for (var c = 'A'; c <= 'Z'; c++) yield return c;
    }

    /// <summary>
    /// What the reference says when there is nobody to show, in its own words and its own place:
    /// two centred lines at the top of the list rather than in the middle of it.
    /// </summary>
    private void DrawEmpty(DrawingContext context, Rect area)
    {
        var ink = Colour(TokenKeys.List.ReadText);
        string[] lines = ["We didn't find anything to show here.", "Double-click here to create a new Contact."];

        var baseline = area.Y + EmptyFirstBaseline;
        foreach (var line in lines)
        {
            var run = Ink(line, EmptyTextSize, ink);
            DrawAt(context, run, area.X + Math.Max(0, (area.Width - run.Width) / 2), baseline);
            baseline += EmptyLineHeight;
        }
    }

    private void DrawRows(DrawingContext context, Rect area)
    {
        var ink = Colour(TokenKeys.List.ReadText);
        var subtle = Colour(TokenKeys.List.PreviewText);
        var selected = Colour(TokenKeys.List.RowSelected);
        var line = Colour(TokenKeys.Border.Subtle);

        var y = area.Y;
        for (var i = _scroll; i < _rows.Count && y < area.Bottom; i++)
        {
            var row = _rows[i];
            var box = new Rect(area.X, y, area.Width, RowHeight);

            if (_selected is { } chosen && chosen.Id == row.Id) Fill(context, box, selected);

            DrawAvatar(context, row, new Rect(box.X + AvatarLeft, box.Y + ((RowHeight - AvatarSize) / 2), AvatarSize, AvatarSize));

            var name = row.Contact.FiledAs(Order);
            var room = box.Width - NameLeft - 8;
            DrawAt(context, Ink(Ellipsize(name, room, NameTextSize), NameTextSize, ink), box.X + NameLeft, box.Y + 20);

            // A group says so beside its name: a distribution list and a person are different
            // things to press Enter on.
            if (row.Contact.IsGroup)
            {
                var mark = Ink("group", IndexTextSize, subtle);
                DrawAt(context, mark, box.Right - mark.Width - 10, box.Y + 20);
            }

            Fill(context, new Rect(box.X, Math.Round(box.Bottom) - 1, box.Width, 1), line);
            _rowHits.Add((box, row));
            y += RowHeight;
        }
    }

    /// <summary>
    /// The circle beside a name: the photograph where there is one, and the initials where there
    /// is not — which is what the reference draws, and what makes a list of names scannable.
    /// </summary>
    private void DrawAvatar(DrawingContext context, ContactRow row, Rect box)
    {
        var circle = Colour(TokenKeys.Accent.Subtle);
        context.DrawEllipse(Brush(circle), null, box.Center, box.Width / 2, box.Height / 2);

        var initials = InitialsOf(row.Contact);
        if (initials.Length == 0) return;

        var run = Ink(initials, 10, Colour(TokenKeys.Text.Primary), SemiBoldFace);
        DrawAt(context, run, box.Center.X - (run.Width / 2), box.Center.Y + 4);
    }

    /// <summary>The two letters a photograph-less contact is drawn with.</summary>
    public static string InitialsOf(Contact contact)
    {
        var first = contact.FirstName.Trim();
        var last = contact.LastName.Trim();
        if (first.Length > 0 && last.Length > 0) return $"{char.ToUpperInvariant(first[0])}{char.ToUpperInvariant(last[0])}";

        var named = contact.Named().Trim();
        if (named.Length == 0) return string.Empty;

        var parts = named.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1
            ? $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}"
            : char.ToUpperInvariant(named[0]).ToString();
    }

    /// <summary>
    /// A token's colour, resolved once and kept until the theme moves — a lookup per row per
    /// frame is what the calendar's palette exists to avoid.
    /// </summary>
    private Color Colour(string key)
    {
        if (_colours.TryGetValue(key, out var cached)) return cached;
        var colour = this.TryFindResource(key + ".color", out var found) && found is Color resolved ? resolved : Colors.Magenta;
        _colours[key] = colour;
        return colour;
    }

    protected override void OnPaletteChanged() => _colours.Clear();

    // ---- Input ---------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);

        foreach (var (box, letter) in _indexHits)
        {
            if (!box.Contains(point)) continue;
            GoTo(letter);
            e.Handled = true;
            return;
        }

        foreach (var (box, row) in _rowHits)
        {
            if (!box.Contains(point)) continue;
            Selected = row;
            ContactSelected?.Invoke(this, row);
            if (e.ClickCount >= 2) ContactActivated?.Invoke(this, row);
            e.Handled = true;
            return;
        }

        // The reference's own invitation: double-clicking the empty list makes a contact.
        if (e.ClickCount >= 2) EmptySpaceActivated?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>Scrolls to the first contact filed under a letter, as pressing the index does.</summary>
    public void GoTo(char letter)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Contact.IndexLetter(Order) != letter) continue;
            Scroll = i;
            return;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Scroll -= (int)Math.Round(e.Delta.Y * 3);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rows.Count == 0) return;

        var at = _selected is { } chosen ? _rows.ToList().FindIndex(r => r.Id == chosen.Id) : -1;
        var moved = e.Key switch
        {
            Key.Down => at + 1,
            Key.Up => at - 1,
            Key.Home => 0,
            Key.End => _rows.Count - 1,
            Key.PageDown => at + Rows(),
            Key.PageUp => at - Rows(),
            _ => int.MinValue,
        };

        if (moved == int.MinValue)
        {
            if (e.Key is Key.Enter && _selected is { } open) ContactActivated?.Invoke(this, open);
            return;
        }

        var next = Math.Clamp(moved, 0, _rows.Count - 1);
        Selected = _rows[next];
        ContactSelected?.Invoke(this, _rows[next]);

        // Keep the selection in view, which is what makes Down at the bottom of the list scroll.
        if (next < _scroll) Scroll = next;
        else if (next >= _scroll + Rows()) Scroll = next - Rows() + 1;

        e.Handled = true;

        int Rows() => Math.Max(1, (int)(Bounds.Height / RowHeight));
    }
}
