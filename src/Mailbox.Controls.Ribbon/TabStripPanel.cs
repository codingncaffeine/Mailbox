using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mailbox.Controls.Ribbon;

/// <summary>What the tab strip does at one width.</summary>
/// <param name="Cap">The width every tab is held to, or infinity while they all fit as they are.</param>
/// <param name="Squeezing">Whether any tab had to come in — which is when the rules are drawn.</param>
/// <param name="Scrolling">Whether the chevron is needed on top of that.</param>
/// <param name="KeepTrailing">Whether there is still room for what follows the last tab.</param>
public readonly record struct TabStripFit(
    double Cap, bool Squeezing, bool Scrolling, bool KeepTrailing);

/// <summary>
/// The tab strip, laid out the way the reference lays it out when the window is short of room.
/// </summary>
/// <remarks>
/// A horizontal stack was what this was, and a stack does not narrow: past a certain width the
/// later tabs simply left the window, and File, Home and Send / Receive were all somebody could
/// reach — there was no gesture that got them back. The reference does two things instead, in
/// this order, and this panel does the same:
/// <list type="number">
///   <item><b>The tabs squeeze, and their labels are cut.</b> Every tab is capped at one shared
///   width — the same for all of them, so the row stays even — and the cap comes down only as far
///   as it must. A label wider than its cap is <b>clipped</b>, not ellipsised: the reference reads
///   "Hom", "Messa", "Optior", never "Hom…". A rule is drawn between each pair while
///   this is happening, because clipped labels with nothing between them run together.</item>
///   <item><b>Then the strip scrolls.</b> When even the least cap will not fit them, a small
///   chevron appears at the strip's right and the tabs page under it. Nothing is ever unreachable:
///   that is the whole point of the chevron, and it is why the tabs are not simply dropped.</item>
/// </list>
/// Measured off the reference squeezed as far as it goes: its shell at 347 across, with the
/// rules between tabs landing on x = 145, 183, 221, 259 — a cell of exactly 38 — and its message
/// window at 499, which clips its tabs but is still wide enough to want no chevron. Both
/// states are reproduced here, and the wide capture is what says the rules are absent until the
/// squeezing starts.
/// </remarks>
public sealed class TabStripPanel : Panel
{
    private readonly List<Control> _tabs = [];

    /// <summary>
    /// The rules between tabs — one fewer than there are tabs, drawn only while squeezing.
    /// </summary>
    /// <remarks>
    /// Children the panel places rather than something it paints: <c>Panel.Render</c> is sealed in
    /// Avalonia, and a panel that owns its own arrangement can put a one-pixel border exactly
    /// where it just put the boundary anyway.
    /// </remarks>
    private readonly List<Border> _rules = [];

    private Control? _trailing;
    private Control? _scrollLeft;
    private Control? _scrollRight;

    /// <summary>How far the tabs are scrolled left, once even the least cap will not fit.</summary>
    private double _offset;

    /// <summary>Whether the last measure had to squeeze, which is when the rules are drawn.</summary>
    private bool _squeezing;

    /// <summary>The rule between two squeezed tabs.</summary>
    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<TabStripPanel, IBrush?>(nameof(SeparatorBrush));

    public IBrush? SeparatorBrush
    {
        get => GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    /// <summary>Adds a tab, in strip order, and the rule that precedes it.</summary>
    public void AddTab(Control tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (_tabs.Count > 0)
        {
            var rule = new Border
            {
                Width = RibbonMetrics.TabSeparatorThickness,
                IsHitTestVisible = false,
                IsVisible = false,
                [!Border.BackgroundProperty] = this[!SeparatorBrushProperty],
            };

            _rules.Add(rule);
            Children.Add(rule);
        }

        _tabs.Add(tab);
        Children.Add(tab);
    }

    /// <summary>
    /// What follows the last tab and is not one — the compose window's "Tell me what you want to
    /// do". It keeps its width: it is not a tab, and squeezing it would be squeezing a sentence.
    /// </summary>
    public void SetTrailing(Control? trailing)
    {
        if (_trailing is not null) Children.Remove(_trailing);
        _trailing = trailing;
        if (trailing is not null) Children.Add(trailing);
    }

    /// <summary>
    /// The two chevrons, made by the host so they carry its dress. Added to the panel here and
    /// shown only while the strip is scrolling.
    /// </summary>
    public void SetScrollButtons(Control left, Control right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        _scrollLeft = left;
        _scrollRight = right;
        Children.Add(left);
        Children.Add(right);
        left.IsVisible = false;
        right.IsVisible = false;
    }

    /// <summary>Pages the strip one tab's worth in either direction.</summary>
    public void Scroll(int direction)
    {
        _offset = Math.Max(0, _offset + (direction * RibbonMetrics.TabSqueezedWidth * 2));
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>Puts the strip back to its start — after a rebuild, so a tab is not lost off it.</summary>
    public void ResetScroll() => _offset = 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var available = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;

        // Everything at its fullest first, so the natural widths are the fullest ones — a tab
        // measured while capped would cache the cap and never grow back.
        foreach (var tab in _tabs)
        {
            SetCap(tab, double.PositiveInfinity);
            tab.Measure(Size.Infinity);
        }

        var natural = _tabs.Select(t => t.DesiredSize.Width).ToArray();

        var trailing = 0.0;
        if (_trailing is not null)
        {
            _trailing.Measure(Size.Infinity);
            trailing = _trailing.DesiredSize.Width;
        }

        var height = _tabs.Count == 0 ? 0 : _tabs.Max(t => t.DesiredSize.Height);
        if (_trailing is not null) height = Math.Max(height, _trailing.DesiredSize.Height);

        var (cap, squeezing, scrolling, keepTrailing) = Fit(natural, trailing, available);
        _squeezing = squeezing;

        foreach (var tab in _tabs)
        {
            SetCap(tab, cap);
            tab.Measure(new Size(double.IsPositiveInfinity(cap) ? double.PositiveInfinity : cap, availableSize.Height));
        }

        // Arranged explicitly below, but a control arranged without having been measured has no
        // desired size and Avalonia treats that as a layout it never saw.
        foreach (var rule in _rules) rule.Measure(availableSize);

        Show(_scrollRight, scrolling);
        Show(_scrollLeft, scrolling && _offset > 0);

        // A sentence sitting where the chevron has to be is a sentence drawn over it, so the hint
        // goes when what it costs is the difference between squeezing and scrolling. Fit decides
        // that, and this only carries it out.
        if (_trailing is not null)
        {
            _trailing.IsVisible = keepTrailing;
            if (!keepTrailing) trailing = 0;
        }

        if (scrolling)
        {
            _scrollRight?.Measure(Size.Infinity);
            _scrollLeft?.Measure(Size.Infinity);
        }
        else
        {
            _offset = 0;
        }

        var width = _tabs.Sum(t => t.DesiredSize.Width) + trailing
            + (scrolling ? RibbonMetrics.TabScrollButtonWidth : 0);

        return new Size(Math.Min(width, available), Math.Max(height, RibbonMetrics.TabStripHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var scrolling = _scrollRight?.IsVisible == true;
        var right = finalSize.Width - (scrolling ? RibbonMetrics.TabScrollButtonWidth : 0);

        var x = -_offset;

        // Between each pair, and not before the first or after the last: the reference draws four
        // rules for its five tabs.
        var ruleTop = RibbonMetrics.TabSeparatorInsetV;
        var ruleHeight = Math.Max(0, finalSize.Height - (RibbonMetrics.TabSeparatorInsetV * 2));

        for (var i = 0; i < _tabs.Count; i++)
        {
            if (i > 0 && i - 1 < _rules.Count)
            {
                var rule = _rules[i - 1];
                var visible = _squeezing && x > 0 && x < right;
                rule.IsVisible = visible;

                if (visible)
                {
                    rule.Arrange(new Rect(
                        x, ruleTop, RibbonMetrics.TabSeparatorThickness, ruleHeight));
                }
            }

            var w = _tabs[i].DesiredSize.Width;
            _tabs[i].Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }

        if (_trailing is { IsVisible: true })
        {
            _trailing.Arrange(new Rect(x, 0, _trailing.DesiredSize.Width, finalSize.Height));
        }

        if (scrolling)
        {
            var top = (finalSize.Height - RibbonMetrics.TabScrollButtonHeight) / 2;

            _scrollRight?.Arrange(new Rect(
                right, top, RibbonMetrics.TabScrollButtonWidth, RibbonMetrics.TabScrollButtonHeight));

            // At the left edge and over the first tab, which is what it is scrolling out of view.
            _scrollLeft?.Arrange(new Rect(
                0, top, RibbonMetrics.TabScrollButtonWidth, RibbonMetrics.TabScrollButtonHeight));
        }

        return finalSize;
    }

    /// <summary>
    /// What the strip does at a width: the cap every tab is held to, whether that is a squeeze,
    /// and whether it has to scroll on top of it.
    /// </summary>
    /// <remarks>
    /// Arithmetic over widths rather than a pass over controls, so what the strip decides can be
    /// checked without a window — the same reason <c>SimplifiedRowPanel.Fit</c> is separable, and
    /// <c>TabStripFitTests</c> is where the reference's own two states are pinned.
    /// </remarks>
    public static TabStripFit Fit(IReadOnlyList<double> natural, double trailing, double available)
    {
        ArgumentNullException.ThrowIfNull(natural);

        // With the hint, which is the ordinary case: it keeps its width, so it comes off the room
        // the tabs are fitted into.
        var withHint = At(natural, available - trailing);
        if (!withHint.Scrolling) return withHint with { KeepTrailing = true };

        // Without it. A sentence beside the tabs is worth less than the tabs being whole, so the
        // hint is what goes when dropping it is the difference between squeezing and scrolling —
        // and it is only asked for once, so this settles rather than alternating.
        var without = At(natural, available);
        return without with { KeepTrailing = false };
    }

    private static TabStripFit At(IReadOnlyList<double> natural, double room)
    {
        var cap = CapFor(natural, room);
        var squeezing = !double.IsPositiveInfinity(cap);

        // The chevron takes room of its own, so whether it is needed changes what is left to fit
        // in — worked out once against the room the chevron leaves rather than iterated, which is
        // what would oscillate.
        var scrolling = squeezing
            && Total(natural, RibbonMetrics.TabSqueezedWidth)
                > room - RibbonMetrics.TabScrollButtonWidth;

        if (scrolling) cap = RibbonMetrics.TabSqueezedWidth;

        return new TabStripFit(cap, squeezing, scrolling, true);
    }

    /// <summary>
    /// The one width every tab is capped at, or infinity when they all fit as they are.
    /// </summary>
    /// <remarks>
    /// One shared cap rather than a share each: the reference's squeezed strip reads "Hom | Send |
    /// View | Help" on cells of exactly 38 apiece, so a short label does not keep more room than a
    /// long one gets. A tab narrower than the cap keeps its own width — capping is a ceiling, not
    /// a size — which is why the sum below is of the minimum of the two.
    /// </remarks>
    private static double CapFor(IReadOnlyList<double> natural, double room)
    {
        if (natural.Count == 0 || Total(natural, double.PositiveInfinity) <= room)
        {
            return double.PositiveInfinity;
        }

        // Widest first: the cap can only ever land on one of the widths already present, or below
        // the least of them, so walking them down finds it exactly without a search.
        foreach (var candidate in natural.OrderByDescending(w => w))
        {
            if (Total(natural, candidate) <= room) return candidate;
        }

        // Narrower than the narrowest tab: share what there is, floored at the least the reference
        // squeezes to, and let the caller scroll if that still does not fit.
        var even = room / natural.Count;
        return Math.Max(RibbonMetrics.TabSqueezedWidth, even);
    }

    private static double Total(IReadOnlyList<double> natural, double cap)
        => natural.Sum(w => Math.Min(w, cap));

    /// <summary>
    /// Caps one tab. The width goes on the tab itself so the button inside it is cut rather than
    /// ellipsised — a clip, which is what the reference shows — and the padding comes in with it,
    /// measured 11 at rest and 6 once squeezing.
    /// </summary>
    private static void SetCap(Control tab, double cap)
    {
        var squeezed = !double.IsPositiveInfinity(cap);

        if (squeezed)
        {
            tab.Width = cap;
            tab.ClipToBounds = true;
        }
        else
        {
            tab.Width = double.NaN;
            tab.ClipToBounds = false;
        }

        if (tab is Panel host)
        {
            foreach (var child in host.Children)
            {
                if (child is Button button)
                {
                    button.Padding = new Thickness(
                        squeezed ? RibbonMetrics.TabSqueezedPaddingH : RibbonMetrics.TabPaddingH, 0);
                }
                else if (child is Border underline)
                {
                    // The rule under the active tab spans its label, so it comes in with the
                    // padding — otherwise it keeps the width of a label that is no longer there.
                    var pad = squeezed ? RibbonMetrics.TabSqueezedPaddingH : RibbonMetrics.TabPaddingH;
                    underline.Margin = new Thickness(pad, RibbonMetrics.TabUnderlineTop, pad, 0);
                }
            }
        }
    }

    private static void Show(Control? control, bool visible)
    {
        if (control is not null) control.IsVisible = visible;
    }
}
