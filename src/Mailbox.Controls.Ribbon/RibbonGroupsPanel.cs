using Avalonia;
using Avalonia.Controls;
using Mailbox.Core.Ribbon;

namespace Mailbox.Controls.Ribbon;

/// <summary>One group's three renderings, and where it sits in the degrade order.</summary>
internal sealed record RibbonGroupSlot(
    string GroupId,
    int CollapsePriority,
    Control Normal,
    Control Compact,
    Control Popup)
{
    public Control At(RibbonGroupVariant variant) => variant switch
    {
        RibbonGroupVariant.Normal => Normal,
        RibbonGroupVariant.Compact => Compact,
        _ => Popup,
    };
}

/// <summary>
/// Lays the classic ribbon's groups out left to right, degrading them as the window narrows in
/// the order the layout document declares.
/// </summary>
/// <remarks>
/// The decision has to happen during measure, because it depends on the width we are handed.
/// That rules out rebuilding the visual tree to change a group's size: mutating children from
/// inside <see cref="MeasureOverride"/> invalidates the measure that is currently running, and
/// the pass either loops or settles a frame late. So all three renderings of every group are
/// built once and kept as children, and the panel chooses between them by visibility — which is
/// free to do mid-measure.
/// <para>
/// Widths are cached from the first pass, while every child is still visible. Measuring a hidden
/// control reports nothing, so the cache cannot be refreshed later without showing everything
/// again; the ribbon rebuilds this panel on any change that would move the numbers.
/// </para>
/// </remarks>
internal sealed class RibbonGroupsPanel : Panel
{
    private readonly IReadOnlyList<RibbonGroupSlot> _slots;
    private readonly List<Control> _separators = [];

    private RibbonGroupWidth[]? _widths;
    private double _furniture;
    private RibbonGroupVariant[] _chosen = [];

    internal RibbonGroupsPanel(IReadOnlyList<RibbonGroupSlot> slots, Func<Control> separator)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(separator);

        _slots = slots;
        _chosen = new RibbonGroupVariant[slots.Count];

        for (var i = 0; i < slots.Count; i++)
        {
            Children.Add(slots[i].Normal);
            Children.Add(slots[i].Compact);
            Children.Add(slots[i].Popup);

            if (i >= slots.Count - 1) continue;

            var rule = separator();
            _separators.Add(rule);
            Children.Add(rule);
        }
    }

    /// <summary>Which variant each group settled on. For the fidelity harness and the tests.</summary>
    internal IReadOnlyList<RibbonGroupVariant> ChosenVariants => _chosen;

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = double.IsInfinity(availableSize.Height)
            ? RibbonMetrics.BodyHeight
            : availableSize.Height;

        MeasureWidthsOnce(height);

        _chosen = RibbonCollapsePolicy.Choose(_widths!, availableSize.Width, _furniture);

        var width = _furniture;

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var variant = _chosen[i];

            slot.Normal.IsVisible = variant == RibbonGroupVariant.Normal;
            slot.Compact.IsVisible = variant == RibbonGroupVariant.Compact;
            slot.Popup.IsVisible = variant == RibbonGroupVariant.Popup;

            var shown = slot.At(variant);
            shown.Measure(new Size(double.PositiveInfinity, height));
            width += shown.DesiredSize.Width;
        }

        return new Size(width, RibbonMetrics.BodyHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;

        for (var i = 0; i < _slots.Count; i++)
        {
            var shown = _slots[i].At(_chosen[i]);
            var width = shown.DesiredSize.Width;
            shown.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;

            // Separators sit between groups, so there is one fewer of them than there are
            // groups and the last group is not followed by one.
            if (i >= _separators.Count) continue;

            var rule = _separators[i];
            rule.Arrange(new Rect(x, 0, rule.DesiredSize.Width, finalSize.Height));
            x += rule.DesiredSize.Width;
        }

        return finalSize;
    }

    /// <summary>
    /// Measures every variant of every group, once, while they are all still visible.
    /// </summary>
    private void MeasureWidthsOnce(double height)
    {
        if (_widths is not null) return;

        var widths = new RibbonGroupWidth[_slots.Count];
        var unconstrained = new Size(double.PositiveInfinity, height);

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            slot.Normal.Measure(unconstrained);
            slot.Compact.Measure(unconstrained);
            slot.Popup.Measure(unconstrained);

            widths[i] = new RibbonGroupWidth(
                slot.GroupId,
                slot.CollapsePriority,
                slot.Normal.DesiredSize.Width,
                slot.Compact.DesiredSize.Width,
                slot.Popup.DesiredSize.Width);
        }

        _furniture = 0;
        foreach (var rule in _separators)
        {
            rule.Measure(unconstrained);
            _furniture += rule.DesiredSize.Width;
        }

        _widths = widths;
    }
}
