using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Controls.Common;

namespace Mailbox.App.Views;

/// <summary>
/// The tab control of a system dialog: a row of tabs standing on the page they open.
/// </summary>
/// <remarks>
/// Drawn rather than templated, because the look is all hairlines: an unselected tab is a
/// box of the tab fill in a faint 1px line with its top corners barely rounded, standing on the
/// page's top edge; the selected tab is the page's own colour, two pixels taller and two wider
/// each side, and the page's top edge stops at its sides so the two read as one surface. Every
/// measurement is off the Account Settings capture: tabs 18px tall over the page line, text
/// 6px in from the tab's fill, the page in a 1px line with a two-pixel shadow down its right
/// and one along its bottom.
/// </remarks>
public sealed class ClassicTabControl : Grid
{
    /// <summary>The strip's height: two rows for the selected tab's rise, eighteen of tab, one of page edge.</summary>
    private const double StripHeight = 21;

    private readonly Strip _strip;
    private readonly Border _page;
    private readonly List<(string Header, Control Content)> _tabs = [];

    public ClassicTabControl()
    {
        RowDefinitions = new RowDefinitions($"{StripHeight},*");

        _strip = new Strip(this);
        SetRow(_strip, 0);
        Children.Add(_strip);

        // The page's shadow: the tab fill showing two pixels to the right and one below.
        var shadow = new Border { Margin = new Thickness(0, 0, 0, 0) };
        Bind(shadow, Border.BackgroundProperty, "systemdialog.tab.brush");
        SetRow(shadow, 1);
        Children.Add(shadow);

        _page = new Border
        {
            BorderThickness = new Thickness(1, 0, 1, 1),
            Margin = new Thickness(0, 0, 2, 1),
        };
        Bind(_page, Border.BackgroundProperty, "systemdialog.surface.brush");
        Bind(_page, Border.BorderBrushProperty, "systemdialog.border.brush");
        SetRow(_page, 1);
        Children.Add(_page);
    }

    /// <summary>Raised after the selected tab changes, by click, key or code.</summary>
    public event EventHandler? SelectionChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _tabs.Count || value == _selected) return;
            _selected = value;
            _page.Child = _tabs[value].Content;
            _strip.InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            _strip.SaySelectionChanged();
        }
    }

    private int _selected = -1;

    public IReadOnlyList<string> Headers => _tabs.Select(t => t.Header).ToList();

    public void AddTab(string header, Control content)
    {
        _tabs.Add((header, content));
        if (_selected < 0) SelectedIndex = 0;
        _strip.InvalidateMeasure();
        _strip.InvalidateVisual();
        _strip.SayRowsChanged();
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>The row of tabs, and the page's top edge between and beside them.</summary>
    private sealed class Strip : Control, ISpokenRows
    {
        // ---- The tabs, spoken for ----------------------------------------------------------

        public int SpokenCount => _owner._tabs.Count;

        public string SpokenRow(int index) => _owner._tabs[index].Header;

        public int SpokenSelectedIndex => _owner._selected;

        public void SpokenSelect(int index) => _owner.SelectedIndex = index;

        public Rect? SpokenRowBounds(int index)
        {
            var spans = Spans();
            if (index < 0 || index >= spans.Count) return null;
            var (left, right) = spans[index];
            return new Rect(left - Rise, 0, right - left + (2 * Rise), Bounds.Height);
        }

        public event EventHandler? SpokenRowsChanged;

        public event EventHandler? SpokenSelectionChanged;

        internal void SayRowsChanged() => SpokenRowsChanged?.Invoke(this, EventArgs.Empty);

        internal void SaySelectionChanged() => SpokenSelectionChanged?.Invoke(this, EventArgs.Empty);

        protected override AutomationPeer OnCreateAutomationPeer()
            => new SpokenRowsPeer(this, AutomationControlType.Tab, AutomationControlType.TabItem);

        /// <summary>The tabs begin two pixels in, which is where the first one's rise lands when it is selected.</summary>
        private const double Origin = 2;
        private const double Rise = 2;
        private const double TextInset = 6;
        private const double MinTabWidth = 46;

        private readonly ClassicTabControl _owner;
        private int _hot = -1;

        public static readonly StyledProperty<IBrush?> TabFillProperty =
            AvaloniaProperty.Register<Strip, IBrush?>(nameof(TabFill));
        public static readonly StyledProperty<IBrush?> PageFillProperty =
            AvaloniaProperty.Register<Strip, IBrush?>(nameof(PageFill));
        public static readonly StyledProperty<IBrush?> LineProperty =
            AvaloniaProperty.Register<Strip, IBrush?>(nameof(Line));
        public static readonly StyledProperty<IBrush?> InkProperty =
            AvaloniaProperty.Register<Strip, IBrush?>(nameof(Ink));
        public static readonly StyledProperty<IBrush?> HoverProperty =
            AvaloniaProperty.Register<Strip, IBrush?>(nameof(Hover));
        public static readonly StyledProperty<FontFamily?> FontProperty =
            AvaloniaProperty.Register<Strip, FontFamily?>(nameof(Font));

        static Strip()
        {
            AffectsRender<Strip>(TabFillProperty, PageFillProperty, LineProperty, InkProperty, HoverProperty);
            AffectsMeasure<Strip>(FontProperty);
        }

        public Strip(ClassicTabControl owner)
        {
            _owner = owner;
            Focusable = true;
            this[!TabFillProperty] = new DynamicResourceExtension("systemdialog.tab.brush");
            this[!PageFillProperty] = new DynamicResourceExtension("systemdialog.surface.brush");
            this[!LineProperty] = new DynamicResourceExtension("systemdialog.border.brush");
            this[!InkProperty] = new DynamicResourceExtension("systemdialog.foreground.brush");
            this[!HoverProperty] = new DynamicResourceExtension("systemdialog.hover.brush");
            this[!FontProperty] = new DynamicResourceExtension("ui.fontfamily");
        }

        public IBrush? TabFill => GetValue(TabFillProperty);
        public IBrush? PageFill => GetValue(PageFillProperty);
        public IBrush? Line => GetValue(LineProperty);
        public IBrush? Ink => GetValue(InkProperty);
        public IBrush? Hover => GetValue(HoverProperty);
        public FontFamily? Font => GetValue(FontProperty);

        private Typeface Typeface => new(Font ?? FontFamily.Default);

        private FormattedText Text(string header, IBrush? ink) => new(
            header, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, 12, ink);

        /// <summary>Each tab's natural span, border to shared border, from the origin.</summary>
        private List<(double Left, double Right)> Spans()
        {
            var spans = new List<(double, double)>();
            var x = Origin;
            foreach (var (header, _) in _owner._tabs)
            {
                // Measured, never drawn, so it is asked for with no brush at all rather than
                // with a colour this file has no business naming.
                var width = Math.Max(Math.Ceiling(Text(header, null).Width) + 2 * TextInset + 1, MinTabWidth);
                spans.Add((x, x + width));
                x += width;
            }
            return spans;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var spans = Spans();
            var width = spans.Count == 0 ? 0 : spans[^1].Right + Rise + 1;
            return new Size(Math.Min(width, availableSize.Width), StripHeight);
        }

        public override void Render(DrawingContext context)
        {
            var spans = Spans();
            // All four are bound to systemdialog.* tokens; a theme that has not defined one
            // draws magenta rather than something plausible, so the gap is visible.
            var line = Line ?? Brushes.Magenta;
            var tabFill = TabFill ?? Brushes.Magenta;
            var pageFill = PageFill ?? Brushes.Magenta;
            var ink = Ink ?? Brushes.Magenta;
            var pageTop = Bounds.Height - 1;
            var selected = _owner._selected;

            // The page's top edge, the full width, and its shadow's first two pixels beyond
            // the right edge — the selected tab paints over its share of the edge below.
            context.FillRectangle(line, new Rect(0, pageTop, Bounds.Width - 2, 1));
            context.FillRectangle(tabFill, new Rect(Bounds.Width - 2, pageTop, 2, 1));

            for (var i = 0; i < spans.Count; i++)
            {
                if (i == selected) continue;
                var (left, right) = spans[i];
                var fill = i == _hot ? Hover ?? tabFill : tabFill;
                DrawTab(context, new Rect(left, Rise, right - left + 1, pageTop - Rise), fill, line, corner: 2);
                DrawText(context, _owner._tabs[i].Header, left + 1 + TextInset, Rise + 3, ink);
            }

            if (selected >= 0 && selected < spans.Count)
            {
                var (left, right) = spans[selected];
                var l = Math.Max(0, left - Rise);
                var r = right + Rise;
                // One row taller than the strip: it covers the page edge and joins the page.
                DrawTab(context, new Rect(l, 0, r - l + 1, Bounds.Height), pageFill, line, corner: 2);
                context.FillRectangle(pageFill, new Rect(l + 1, pageTop, r - l - 1, 1));
                DrawText(context, _owner._tabs[selected].Header, left + 1 + TextInset, 3, ink);
            }
        }

        /// <summary>A box in a 1px line whose top corners are rounded by a pixel or two.</summary>
        private static void DrawTab(DrawingContext context, Rect rect, IBrush fill, IBrush line, double corner)
        {
            var outer = new RoundedRect(rect, new Vector(corner, corner), new Vector(corner, corner), default, default);
            context.DrawRectangle(line, null, outer);
            var inner = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 1);
            var innerCorner = Math.Max(0, corner - 1);
            context.DrawRectangle(fill, null,
                new RoundedRect(inner, new Vector(innerCorner, innerCorner), new Vector(innerCorner, innerCorner), default, default));
        }

        private void DrawText(DrawingContext context, string header, double x, double y, IBrush ink)
            => context.DrawText(Text(header, ink), new Point(x, y));

        private int HitTab(Point p)
        {
            var spans = Spans();
            for (var i = 0; i < spans.Count; i++)
            {
                var (left, right) = spans[i];
                if (p.X >= left - Rise && p.X <= right + Rise) return i;
            }
            return -1;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var hit = HitTab(e.GetPosition(this));
            if (hit < 0) return;
            Focus();
            _owner.SelectedIndex = hit;
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var hit = HitTab(e.GetPosition(this));
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
            var count = _owner._tabs.Count;
            if (count == 0) return;
            switch (e.Key)
            {
                case Key.Left: _owner.SelectedIndex = (_owner._selected + count - 1) % count; e.Handled = true; break;
                case Key.Right: _owner.SelectedIndex = (_owner._selected + 1) % count; e.Handled = true; break;
                case Key.Home: _owner.SelectedIndex = 0; e.Handled = true; break;
                case Key.End: _owner.SelectedIndex = count - 1; e.Handled = true; break;
            }
        }
    }
}
