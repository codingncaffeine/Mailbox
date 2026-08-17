using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mailbox.Contacts;
using Mailbox.Controls.Common;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.People;

/// <summary>
/// The five arrangements the Current View group offers, in the reference's own order.
/// </summary>
/// <remarks>
/// People is the one with a capture and the one the module opens in; the other four are authored
/// from what each is for — two grids of cards and two tables — and say so where they are drawn.
/// </remarks>
public enum ContactArrangement
{
    People,
    BusinessCard,
    Card,
    Phone,
    List,
}

/// <summary>
/// The People module's list: everybody in the shown address books, filed in order, with the
/// alphabet index down its left-hand side.
/// </summary>
/// <remarks>
/// Measured off the reference's own module, and every number here is read off it rather than
/// reasoned about: the index letters are 23px apart in a 39px column with their ink 18 in, the
/// empty state is two centred lines 13px apart, and a row is 56 tall closed by a hairline with a
/// 36px disc 8 in and its name 53 in. A row has no fill of its own — the reference draws its
/// contacts on the pane and bands only the one that is picked.
/// <para>
/// The index is what the Options page calls "Show an additional index", and it is not decoration:
/// it is how the reference reaches the Ws in an address book of two thousand people without a
/// scrollbar drag.
/// </para>
/// </remarks>
public sealed class ContactListView : DrawnSurface
{
    /// <summary>
    /// The index column's width, and where its letters sit inside it.
    /// </summary>
    /// <remarks>
    /// Measured off the reference's list: its pane starts at 293 and its rows at 332, with the
    /// letters centred on 311 — so a 39px column with its ink 18 in.
    /// </remarks>
    private const double IndexWidth = 39;
    private const double IndexInk = 18;

    /// <summary>Measured: the letters are 23px apart, the first baseline 25px down.</summary>
    private const double IndexStep = 23;
    private const double IndexFirstBaseline = 25;
    private const double IndexTextSize = 11;

    /// <summary>
    /// Measured off the reference's own list: a row is 56 tall closed by a hairline, its disc is
    /// 36 across and 8 in, and the name starts 53 in with its baseline 33 down.
    /// </summary>
    /// <remarks>
    /// The row band stops short of the pane's right edge by <see cref="Gutter"/>, which is where
    /// the reference reserves its scrollbar, and the first row starts <see cref="RowsTop"/> below
    /// the top — the same inset the index's first letter and the empty state are drawn at.
    /// </remarks>
    private const double RowHeight = 56;
    private const double AvatarSize = 36;
    private const double AvatarLeft = 8;
    private const double NameLeft = 53;
    private const double NameBaseline = 33;
    private const double NameTextSize = 15;
    private const double RowsTop = 26;
    private const double Gutter = 22;

    /// <summary>
    /// Authored: the card views' tile, and the table views' rows.
    /// </summary>
    /// <remarks>
    /// The reference has five arrangements and a capture of one, so the other four are built from
    /// what each is for: a card is a tile with a coloured edge, and a table row is the People
    /// view's own row without the disc to make room for.
    /// </remarks>
    private const double CardWidth = 232;
    private const double CardHeight = 72;
    private const double CardGap = 8;
    private const double TableRowHeight = 24;
    private const double TableHeaderHeight = 26;

    /// <summary>Measured: two centred lines, 13px apart, the first 22px below the top.</summary>
    private const double EmptyTextSize = 12;
    private const double EmptyFirstBaseline = 22;
    private const double EmptyLineHeight = 13;

    private readonly List<(Rect Box, ContactRow Row)> _rowHits = [];
    private readonly List<(Rect Box, char Letter)> _indexHits = [];

    private IReadOnlyList<ContactRow> _rows = [];
    private ContactRow? _selected;
    private ContactRow? _hover;
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

    /// <summary>
    /// Whether this list is inside a desktop popup, and so takes the popup's own palette.
    /// </summary>
    /// <remarks>
    /// The rule the calendar peek states and this obeys: a pane inside the window follows the
    /// theme, and a popup off the rail keeps the desktop's light colours in every theme. Without
    /// it the People peek drew a dark list inside a light grey popup — the theme's palette in a
    /// place the theme does not reach.
    /// </remarks>
    public bool OnPopup
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>The token for a part of the list, in whichever palette it is drawing in.</summary>
    private Color Ink(string themed, string popup) => Colour(OnPopup ? popup : themed);

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

    /// <summary>Which of the Current View group's five arrangements is showing.</summary>
    public ContactArrangement Arrangement
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            _scroll = 0;
            InvalidateVisual();
        }
    }

    public event EventHandler<ContactRow>? ContactSelected;
    public event EventHandler<ContactRow>? ContactActivated;

    /// <summary>A right-click on somebody: the row is picked, and its menu is asked for.</summary>
    public event EventHandler<ContactRow>? ContactMenuRequested;

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

        Fill(context, new Rect(0, 0, width, height), Ink(TokenKeys.List.Background, TokenKeys.Peek.PopBackground));

        var left = _showIndex ? IndexWidth : 0;
        if (_showIndex) DrawIndex(context, height);

        if (_rows.Count == 0)
        {
            DrawEmpty(context, new Rect(left, 0, width - left, height));
            return;
        }

        var area = new Rect(left, 0, width - left, height);
        switch (Arrangement)
        {
            case ContactArrangement.BusinessCard:
            case ContactArrangement.Card:
                DrawCards(context, area, Arrangement == ContactArrangement.BusinessCard);
                break;

            case ContactArrangement.Phone:
            case ContactArrangement.List:
                DrawTable(context, area, Arrangement == ContactArrangement.List);
                break;

            default:
                DrawRows(context, area);
                break;
        }
    }

    /// <summary>
    /// Business Card and Card: the contacts as tiles rather than a list.
    /// </summary>
    /// <remarks>
    /// Authored — the reference's own capture shows the People view and nothing else — from what
    /// its two card views are: a grid of small cards, the business one carrying the disc and the
    /// plain one the words alone. What is borrowed is measured: the disc, the row's own type
    /// sizes and the hairline between rows.
    /// </remarks>
    private void DrawCards(DrawingContext context, Rect area, bool withDisc)
    {
        var ink = Ink(TokenKeys.List.HeaderText, TokenKeys.Peek.PopText);
        var onBand = Ink(TokenKeys.List.ReadText, TokenKeys.Peek.PopText);
        var quiet = Ink(TokenKeys.List.PreviewText, TokenKeys.Peek.PopTextDim);
        var line = Ink(TokenKeys.List.Separator, TokenKeys.Peek.PopFrame);
        var selected = Ink(TokenKeys.List.RowSelected, TokenKeys.Peek.PopHover);

        var columns = Math.Max(1, (int)((area.Width - CardGap) / (CardWidth + CardGap)));
        var x = area.X + CardGap;
        var y = area.Y + CardGap;

        for (var i = _scroll; i < _rows.Count; i++)
        {
            if (y + CardHeight > area.Bottom) break;

            var row = _rows[i];
            var box = new Rect(x, y, CardWidth, CardHeight);
            var chosen = _selected is { } picked && picked.Id == row.Id;

            Fill(context, box, chosen ? selected : Ink(TokenKeys.List.RowBackground, TokenKeys.Peek.PopBackground));
            Fill(context, new Rect(box.X, box.Y, 3, box.Height), Colour(TokenKeys.People.Avatar));

            var textLeft = box.X + 12;
            if (withDisc)
            {
                DrawAvatar(context, row, new Rect(box.X + 10, box.Y + 10, 28, 28));
                textLeft = box.X + 46;
            }

            var contact = row.Contact;
            var room = box.Right - textLeft - 8;
            var ready = chosen ? onBand : ink;

            DrawAt(context, Ink(Ellipsize(contact.Named(), room, 13), 13, ready, SemiBoldFace), textLeft, box.Y + 22);

            var second = string.Join(", ", new[] { contact.JobTitle, contact.Company }.Where(p => p.Length > 0));
            if (second.Length > 0) DrawAt(context, Ink(Ellipsize(second, room, 11), 11, chosen ? quiet : ready), textLeft, box.Y + 38);

            var third = contact.PrimaryEmail is { Length: > 0 } mail ? mail : contact.Phones.FirstOrDefault()?.Number ?? string.Empty;
            if (third.Length > 0) DrawAt(context, Ink(Ellipsize(third, room, 11), 11, chosen ? quiet : ready), textLeft, box.Y + 54);

            Fill(context, new Rect(box.X, box.Bottom - 1, box.Width, 1), line);
            _rowHits.Add((box, row));

            x += CardWidth + CardGap;
            if (x + CardWidth <= area.Right) continue;

            x = area.X + CardGap;
            y += CardHeight + CardGap;
        }
    }

    /// <summary>
    /// Phone and List: the contacts as a table, the second grouped by company.
    /// </summary>
    /// <remarks>
    /// Authored, for the same reason the cards are: the reference has these views and no capture
    /// of them. The columns are the ones each view is named for — Phone shows the numbers, List
    /// shows who works where — and the row is the People view's own 56 halved, a table row
    /// carrying one line rather than a disc.
    /// </remarks>
    private void DrawTable(DrawingContext context, Rect area, bool grouped)
    {
        var ink = Ink(TokenKeys.List.HeaderText, TokenKeys.Peek.PopText);
        var onBand = Ink(TokenKeys.List.ReadText, TokenKeys.Peek.PopText);
        var quiet = Ink(TokenKeys.List.PreviewText, TokenKeys.Peek.PopTextDim);
        var line = Ink(TokenKeys.List.Separator, TokenKeys.Peek.PopFrame);
        var selected = Ink(TokenKeys.List.RowSelected, TokenKeys.Peek.PopHover);

        (string Heading, double Width)[] columns = grouped
            ? [("Full Name", 0), ("Job title", 150), ("Business Phone", 140), ("E-mail", 200)]
            : [("Full Name", 0), ("Company", 160), ("Business Phone", 140), ("Mobile Phone", 140)];

        // The header, then the rows under it.
        var y = area.Y;
        Fill(context, new Rect(area.X, y, area.Width, TableHeaderHeight), Colour(TokenKeys.List.HeaderBackground));
        foreach (var (heading, cell) in Slice(columns, area))
        {
            DrawAt(context, Ink(heading, 11, ink), cell.X, y + 17);
        }

        Fill(context, new Rect(area.X, y + TableHeaderHeight - 1, area.Width, 1), line);
        y += TableHeaderHeight;

        var company = string.Empty;
        for (var i = _scroll; i < _rows.Count && y < area.Bottom; i++)
        {
            var row = _rows[i];
            var contact = row.Contact;

            if (grouped && !string.Equals(company, contact.Company, StringComparison.OrdinalIgnoreCase))
            {
                company = contact.Company;
                var band = new Rect(area.X, y, area.Width, TableRowHeight);
                Fill(context, band, Colour(TokenKeys.List.GroupHeaderBackground));
                DrawAt(
                    context,
                    Ink(company.Length > 0 ? company : "(no company)", 12, Colour(TokenKeys.List.GroupHeaderText), SemiBoldFace),
                    area.X + 8,
                    y + 16);
                y += TableRowHeight;
                if (y >= area.Bottom) break;
            }

            var box = new Rect(area.X, y, area.Width, TableRowHeight);
            var chosen = _selected is { } picked && picked.Id == row.Id;
            if (chosen) Fill(context, box, selected);

            var ready = chosen ? onBand : ink;
            foreach (var (heading, cell) in Slice(columns, area))
            {
                var text = heading switch
                {
                    "Full Name" => contact.Named(),
                    "Company" => contact.Company,
                    "Job title" => contact.JobTitle,
                    "Business Phone" => contact.Phones.FirstOrDefault(p => p.Kind == PhoneKind.Business)?.Number ?? string.Empty,
                    "Mobile Phone" => contact.Phones.FirstOrDefault(p => p.Kind == PhoneKind.Mobile)?.Number ?? string.Empty,
                    _ => contact.PrimaryEmail,
                };

                if (text.Length == 0) continue;
                DrawAt(context, Ink(Ellipsize(text, cell.Width - 8, 12), 12, heading == "Full Name" ? ready : chosen ? quiet : ready), cell.X, y + 16);
            }

            Fill(context, new Rect(box.X, box.Bottom - 1, box.Width, 1), line);
            _rowHits.Add((box, row));
            y += TableRowHeight;
        }
    }

    /// <summary>Where each column of a table starts, the first taking what the others leave.</summary>
    private static IEnumerable<(string Heading, Rect Box)> Slice((string Heading, double Width)[] columns, Rect area)
    {
        var fixedWidth = columns.Sum(c => c.Width);
        var first = Math.Max(120, area.Width - fixedWidth - 16);
        var x = area.X + 8;

        foreach (var (heading, given) in columns)
        {
            var cell = given > 0 ? given : first;
            yield return (heading, new Rect(x, area.Y, cell, 0));
            x += cell;
        }
    }

    /// <summary>
    /// The alphabet down the side: <c>123</c> for everything that does not file under a letter,
    /// then a to z.
    /// </summary>
    /// <remarks>
    /// Lower case, and every letter drawn alike — which is what the reference does, and not what
    /// this drew before: a letter nobody files under was dimmed, on the reasoning that a ruler
    /// should say where its marks are. The reference's own index with one contact in it draws all
    /// twenty-seven the same, so it does not.
    /// <para>
    /// The ink is the pane's rather than a row's: the index sits on the list's own background,
    /// which is a dark panel in Dark Gray and a white one in the light themes.
    /// </para>
    /// </remarks>
    private void DrawIndex(DrawingContext context, double height)
    {
        var ink = Ink(TokenKeys.List.HeaderText, TokenKeys.Peek.PopText);

        var baseline = IndexFirstBaseline;
        foreach (var letter in Letters())
        {
            if (baseline > height) break;

            var text = letter == '#' ? "123" : char.ToLowerInvariant(letter).ToString(Culture);
            var run = Ink(text, IndexTextSize, ink);
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
        // On the pane, so the pane's ink — the same the index and an unbanded row take.
        var ink = Ink(TokenKeys.List.HeaderText, TokenKeys.Peek.PopText);
        string[] lines = ["We didn't find anything to show here.", "Double-click here to create a new Contact."];

        var baseline = area.Y + EmptyFirstBaseline;
        foreach (var line in lines)
        {
            var run = Ink(line, EmptyTextSize, ink);
            DrawAt(context, run, area.X + Math.Max(0, (area.Width - run.Width) / 2), baseline);
            baseline += EmptyLineHeight;
        }
    }

    /// <remarks>
    /// A row has no fill of its own: the reference draws its contacts straight on the list's pane
    /// and bands only the one that is picked. So the ink follows the ground — the pane's ink
    /// (<c>list.header.text</c>, which is what reads on it in every theme) for an ordinary row,
    /// and the content ink for the selected band, which is a light panel even in Dark Gray.
    /// </remarks>
    private void DrawRows(DrawingContext context, Rect area)
    {
        var onPane = Ink(TokenKeys.List.HeaderText, TokenKeys.Peek.PopText);
        var onBand = Ink(TokenKeys.List.ReadText, TokenKeys.Peek.PopText);
        var subtle = Ink(TokenKeys.List.PreviewText, TokenKeys.Peek.PopTextDim);
        var selected = Ink(TokenKeys.List.RowSelected, TokenKeys.Peek.PopHover);
        var hover = Ink(TokenKeys.List.RowHover, TokenKeys.Peek.PopHover);
        var line = Ink(TokenKeys.List.Separator, TokenKeys.Peek.PopFrame);

        var y = area.Y + RowsTop;
        var width = Math.Max(40, area.Width - Gutter);

        for (var i = _scroll; i < _rows.Count && y < area.Bottom; i++)
        {
            var row = _rows[i];
            var box = new Rect(area.X, y, width, RowHeight);
            var chosen = _selected is { } picked && picked.Id == row.Id;

            if (chosen) Fill(context, box, selected);
            else if (_hover is { } over && over.Id == row.Id) Fill(context, box, hover);

            var ink = chosen ? onBand : onPane;

            DrawAvatar(context, row, new Rect(box.X + AvatarLeft, box.Y + ((RowHeight - AvatarSize) / 2), AvatarSize, AvatarSize));

            var name = row.Contact.FiledAs(Order);
            var room = box.Width - NameLeft - 8;
            DrawAt(context, Ink(Ellipsize(name, room, NameTextSize), NameTextSize, ink), box.X + NameLeft, box.Y + NameBaseline);

            // A group says so beside its name: a distribution list and a person are different
            // things to press Enter on.
            if (row.Contact.IsGroup)
            {
                var mark = Ink("group", IndexTextSize, chosen ? subtle : onPane);
                DrawAt(context, mark, box.Right - mark.Width - 10, box.Y + NameBaseline);
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
        var circle = Colour(TokenKeys.People.Avatar);
        context.DrawEllipse(Brush(circle), null, box.Center, box.Width / 2, box.Height / 2);

        var initials = InitialsOf(row.Contact);
        if (initials.Length == 0) return;

        // Measured: the initials are white on the disc and about a third of its height.
        var run = Ink(initials, Math.Round(box.Height * 0.36), Colour(TokenKeys.People.AvatarText), SemiBoldFace);
        DrawAt(context, run, box.Center.X - (run.Width / 2), box.Center.Y + (run.Height / 3));
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

    // ---- Input ---------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);

        // The right button picks the row under it and asks for its menu — which is where the
        // reference puts Add to Favourites, in so many words: "right-click a person anywhere to
        // add them to your favourites".
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            foreach (var (box, row) in _rowHits)
            {
                if (!box.Contains(point)) continue;
                Selected = row;
                ContactSelected?.Invoke(this, row);
                ContactMenuRequested?.Invoke(this, row);
                e.Handled = true;
                return;
            }

            return;
        }

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

    /// <summary>The row under the pointer, which is lit as every other list of ours lights one.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        ContactRow? over = null;
        foreach (var (box, row) in _rowHits)
        {
            if (!box.Contains(point)) continue;
            over = row;
            break;
        }

        if (over?.Id == _hover?.Id) return;
        _hover = over;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
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
