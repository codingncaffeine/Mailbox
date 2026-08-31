using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Controls.Common;

namespace Mailbox.App.Views;

/// <summary>A column of a <see cref="ClassicListView"/>: its heading and its width.</summary>
public sealed record ClassicColumn(string Header, double Width);

/// <summary>
/// One row: its cells in column order, whether it carries the marker icon, and — where the list
/// is a list of things that can be switched off — whether its tick box is on.
/// </summary>
/// <param name="Checked">
/// Null for a list with no tick boxes, which is every one but the rules. A list that has them
/// draws one on every row, so a rule with no tick would read as a rule that cannot be switched
/// off rather than as one that is on.
/// </param>
public sealed record ClassicRow(
    IReadOnlyList<string> Cells, bool Marked = false, object? Tag = null, bool? Checked = null);

/// <summary>
/// The report-style list of a system dialog: column headings over rows, with full-row selection.
/// </summary>
/// <remarks>
/// Drawn to the Account Settings capture: a white box in a 1px frame; a 25px heading row with a
/// faint line between the columns; 17px rows whose text stands 6px into its column; the first
/// column leaving room for a 16px marker icon before its text; a selected row filled from the
/// text's start to the last column's end, in a quiet grey while the list is not focused and in
/// the desktop's pale blue while it is. Double-clicking a row activates it, which is how the
/// reference opens an account from the list.
/// </remarks>
public sealed class ClassicListView : Border
{
    private const double HeaderHeight = 25;
    private const double RowHeight = 17;
    private const double TextInset = 6;
    private const double MarkerInset = 4;
    private const double MarkerWidth = 16;

    private readonly Header _header;
    private readonly Body _body;
    private readonly ScrollViewer _scroller;
    private IReadOnlyList<ClassicColumn> _columns = [];
    private IReadOnlyList<ClassicRow> _rows = [];
    private int _selected = -1;

    public ClassicListView()
    {
        BorderThickness = new Thickness(1);
        Bind(this, BackgroundProperty, "systemdialog.list.background.brush");
        Bind(this, BorderBrushProperty, "systemdialog.list.border.brush");

        _header = new Header(this) { Height = HeaderHeight };
        _body = new Body(this);
        _scroller = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            // Reserved rather than shown when needed: the reference keeps the gutter down every
            // one of these lists whether or not it has anything to scroll, so the rows below the
            // header do not change width the moment a list outgrows its box.
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible,
        };

        var stack = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        stack.Children.Add(_header);
        stack.Children.Add(_scroller);
        Child = stack;

        // The body is the focusable control and so the node a screen reader lands on; the name
        // a dialog gives the list rides down to it, so "Accounts list" is heard there and not
        // on a silent border two levels up.
        _body.Bind(AutomationProperties.NameProperty, this.GetObservable(AutomationProperties.NameProperty));
    }

    /// <summary>Raised when the selected row changes, including to nothing.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when a row is double-clicked or Enter is pressed on it.</summary>
    public event EventHandler? ItemActivated;

    /// <summary>A row's tick box was clicked; the argument is the row's index.</summary>
    public event EventHandler<int>? RowToggled;

    public IReadOnlyList<ClassicColumn> Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            _header.InvalidateVisual();
            _body.InvalidateVisual();
        }
    }

    public IReadOnlyList<ClassicRow> Rows => _rows;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var clamped = value < 0 || value >= _rows.Count ? -1 : value;
            if (clamped == _selected) return;
            _selected = clamped;
            _body.InvalidateVisual();
            _body.SaySelectionChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ClassicRow? SelectedRow => _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null;

    /// <summary>Replaces the rows, keeping the selection on the same tag where it still exists.</summary>
    public void SetRows(IReadOnlyList<ClassicRow> rows)
    {
        var keep = SelectedRow?.Tag;
        _rows = rows;
        _body.InvalidateMeasure();
        _body.InvalidateVisual();
        _body.SayRowsChanged();

        var index = keep is null ? -1 : rows.ToList().FindIndex(r => Equals(r.Tag, keep));
        _selected = -2; // so the setter always raises, even for the same index over new rows
        SelectedIndex = index >= 0 ? index : rows.Count > 0 ? 0 : -1;
    }

    /// <summary>Gives the list the keyboard focus, which turns the selection blue.</summary>
    public void FocusList() => _body.Focus();

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    private static FormattedText Text(string s, Typeface face, IBrush ink, double maxWidth, bool bold = false)
    {
        var text = new FormattedText(
            s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            bold ? new Typeface(face.FontFamily, weight: FontWeight.Bold) : face, 12, ink)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return text;
    }

    /// <summary>The heading row: column names over a white ground, faint lines between them.</summary>
    private sealed class Header : Control
    {
        private readonly ClassicListView _owner;

        public static readonly StyledProperty<IBrush?> LineProperty =
            AvaloniaProperty.Register<Header, IBrush?>(nameof(Line));
        public static readonly StyledProperty<IBrush?> InkProperty =
            AvaloniaProperty.Register<Header, IBrush?>(nameof(Ink));
        public static readonly StyledProperty<FontFamily?> FontProperty =
            AvaloniaProperty.Register<Header, FontFamily?>(nameof(Font));

        static Header() => AffectsRender<Header>(LineProperty, InkProperty, FontProperty);

        public Header(ClassicListView owner)
        {
            _owner = owner;
            this[!LineProperty] = new DynamicResourceExtension("systemdialog.border.brush");
            this[!InkProperty] = new DynamicResourceExtension("systemdialog.foreground.brush");
            this[!FontProperty] = new DynamicResourceExtension("ui.fontfamily");
        }

        public IBrush? Line => GetValue(LineProperty);
        public IBrush? Ink => GetValue(InkProperty);
        public FontFamily? Font => GetValue(FontProperty);

        public override void Render(DrawingContext context)
        {
            // Both are bound to systemdialog.* tokens; magenta is what a theme that has not
            // defined one gets, so the gap shows rather than being papered over.
            var line = Line ?? Brushes.Magenta;
            var ink = Ink ?? Brushes.Magenta;
            var face = new Typeface(Font ?? FontFamily.Default);
            var x = 0.0;

            foreach (var column in _owner._columns)
            {
                // A 12px line's cap top stands 3px below its origin; the heading's is 9px down.
                context.DrawText(Text(column.Header, face, ink, column.Width - TextInset - 2), new Point(x + TextInset, 6));
                x += column.Width;
                // The line between headings starts a pixel down from the top, as the reference's does.
                context.FillRectangle(line, new Rect(x - 1, 1, 1, Bounds.Height - 1));
            }
        }
    }

    /// <summary>The rows, and everything the pointer and keyboard do to them.</summary>
    private sealed class Body : Control, ISpokenRows
    {
        private readonly ClassicListView _owner;
        private int _hot = -1;

        // ---- The list, spoken for --------------------------------------------------------

        public int SpokenCount => _owner._rows.Count;

        /// <summary>The cells in column order, and the marks that are otherwise silent ink.</summary>
        public string SpokenRow(int index)
        {
            var row = _owner._rows[index];
            var said = string.Join(", ", row.Cells.Where(c => c.Length > 0));
            return row.Marked ? said + ", the default" : said;
        }

        public int SpokenSelectedIndex => _owner._selected;

        public void SpokenSelect(int index) => _owner.SelectedIndex = index;

        public Rect? SpokenRowBounds(int index)
            => new Rect(0, index * RowHeight, Math.Max(1, _owner._columns.Sum(c => c.Width)), RowHeight);

        public bool? SpokenRowToggled(int index) => _owner._rows[index].Checked;

        public void SpokenToggle(int index) => _owner.RowToggled?.Invoke(_owner, index);

        public event EventHandler? SpokenRowsChanged;

        public event EventHandler? SpokenSelectionChanged;

        internal void SayRowsChanged() => SpokenRowsChanged?.Invoke(this, EventArgs.Empty);

        internal void SaySelectionChanged() => SpokenSelectionChanged?.Invoke(this, EventArgs.Empty);

        protected override AutomationPeer OnCreateAutomationPeer() => new SpokenRowsPeer(this);

        public static readonly StyledProperty<IBrush?> InkProperty =
            AvaloniaProperty.Register<Body, IBrush?>(nameof(Ink));
        public static readonly StyledProperty<IBrush?> SelectionProperty =
            AvaloniaProperty.Register<Body, IBrush?>(nameof(Selection));
        public static readonly StyledProperty<IBrush?> SelectionFocusedProperty =
            AvaloniaProperty.Register<Body, IBrush?>(nameof(SelectionFocused));
        public static readonly StyledProperty<IBrush?> HoverProperty =
            AvaloniaProperty.Register<Body, IBrush?>(nameof(Hover));
        public static readonly StyledProperty<FontFamily?> FontProperty =
            AvaloniaProperty.Register<Body, FontFamily?>(nameof(Font));

        static Body() => AffectsRender<Body>(InkProperty, SelectionProperty, SelectionFocusedProperty, HoverProperty, FontProperty);

        public Body(ClassicListView owner)
        {
            _owner = owner;
            Focusable = true;
            this[!InkProperty] = new DynamicResourceExtension("systemdialog.foreground.brush");
            this[!SelectionProperty] = new DynamicResourceExtension("systemdialog.selection.brush");
            this[!SelectionFocusedProperty] = new DynamicResourceExtension("systemdialog.selection.focused.brush");
            this[!HoverProperty] = new DynamicResourceExtension("systemdialog.hover.brush");
            this[!FontProperty] = new DynamicResourceExtension("ui.fontfamily");
            GotFocus += (_, _) => InvalidateVisual();
            LostFocus += (_, _) => InvalidateVisual();
        }

        public IBrush? Ink => GetValue(InkProperty);
        public IBrush? Selection => GetValue(SelectionProperty);
        public IBrush? SelectionFocused => GetValue(SelectionFocusedProperty);
        public IBrush? Hover => GetValue(HoverProperty);
        public FontFamily? Font => GetValue(FontProperty);

        protected override Size MeasureOverride(Size availableSize)
            => new(availableSize.Width, _owner._rows.Count * RowHeight);

        public override void Render(DrawingContext context)
        {
            var ink = Ink ?? Brushes.Magenta;
            var paper = _owner.Background ?? Brushes.Magenta;
            var face = new Typeface(Font ?? FontFamily.Default);
            var textStart = MarkerInset + MarkerWidth + 1;
            var last = _owner._columns.Sum(c => c.Width);

            for (var i = 0; i < _owner._rows.Count; i++)
            {
                var row = _owner._rows[i];
                var top = i * RowHeight;

                if (i == _owner._selected)
                {
                    var fill = IsFocused ? SelectionFocused : Selection;
                    context.FillRectangle(fill ?? Brushes.Magenta, new Rect(textStart, top, last - textStart, RowHeight));
                }
                else if (i == _hot)
                {
                    context.FillRectangle(Hover ?? Brushes.Magenta, new Rect(textStart, top, last - textStart, RowHeight));
                }

                // The marker: the same disc-and-tick as Set as Default, drawn straight into the
                // row in the row's own ink and ground.
                if (row.Marked)
                {
                    using (context.PushTransform(Matrix.CreateTranslation(MarkerInset, top + 0.5)))
                    {
                        ClassicIcon.Draw(context, "default", ClassicIcon.Palette.Mono(ink, paper));
                    }
                }
                else if (row.Checked is { } ticked)
                {
                    using (context.PushTransform(Matrix.CreateTranslation(MarkerInset, top + 0.5)))
                    {
                        ClassicIcon.Draw(context, ticked ? "tick" : "untick", ClassicIcon.Palette.Mono(ink, paper));
                    }
                }

                var x = 0.0;
                for (var c = 0; c < _owner._columns.Count && c < row.Cells.Count; c++)
                {
                    var column = _owner._columns[c];
                    var left = c == 0 ? textStart : x + TextInset;
                    var width = x + column.Width - left - 2;
                    context.DrawText(Text(row.Cells[c], face, ink, width), new Point(left, top + 2));
                    x += column.Width;
                }
            }
        }

        private int RowAt(Point p)
        {
            var index = (int)Math.Floor(p.Y / RowHeight);
            return index >= 0 && index < _owner._rows.Count ? index : -1;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus();
            var at = e.GetPosition(this);
            var hit = RowAt(at);
            _owner.SelectedIndex = hit;

            // A click in the tick box toggles it rather than opening the row, which is what the
            // desktop's own list does and what anybody switching a rule off expects.
            if (hit >= 0 && _owner._rows[hit].Checked is not null
                && at.X >= MarkerInset && at.X <= MarkerInset + MarkerWidth)
            {
                _owner.RowToggled?.Invoke(_owner, hit);
                e.Handled = true;
                return;
            }

            if (hit >= 0 && e.ClickCount == 2) _owner.ItemActivated?.Invoke(_owner, EventArgs.Empty);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var hit = RowAt(e.GetPosition(this));
            if (hit == _hot) return;
            _hot = hit;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_hot < 0) return;
            _hot = -1;
            InvalidateVisual();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var count = _owner._rows.Count;
            if (count == 0) return;
            switch (e.Key)
            {
                case Key.Up: _owner.SelectedIndex = Math.Max(0, _owner._selected - 1); e.Handled = true; break;
                case Key.Down: _owner.SelectedIndex = Math.Min(count - 1, _owner._selected + 1); e.Handled = true; break;
                case Key.Home: _owner.SelectedIndex = 0; e.Handled = true; break;
                case Key.End: _owner.SelectedIndex = count - 1; e.Handled = true; break;
                case Key.Enter when _owner._selected >= 0:
                    _owner.ItemActivated?.Invoke(_owner, EventArgs.Empty);
                    e.Handled = true;
                    break;
            }
        }
    }
}
