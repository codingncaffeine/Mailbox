using Avalonia;
using Avalonia.Controls;

namespace Mailbox.Controls.Ribbon;

/// <summary>
/// A row followed by a tail: the tail is measured first and always kept, the row gets what is
/// left, and the tail sits wherever the row ends rather than at the far edge.
/// </summary>
/// <remarks>
/// This is how the Simplified bar's "…" behaves in the reference. When the bar has slack the
/// "…" comes right after the last cluster, with a rule before it, and the rest of the bar is
/// empty up to the display-options chevron; when the bar is full it ends up at the right because
/// the row does. A star column with the "…" beside it pins it to the right in both cases, which
/// is what this used to do and is not what the reference does. Only the placement differs: the
/// row is still measured against the width the tail leaves it, so what fits is decided exactly
/// as before.
/// <para>
/// The tail's rule is optional and goes when the row is empty — every control pushed off — so a
/// bar that has given everything up shows "…" and not a rule hanging in front of it.
/// </para>
/// </remarks>
public sealed class RowWithTailPanel : Panel
{
    private readonly Control _row;
    private readonly Control _tail;
    private readonly Control? _tailRule;

    public RowWithTailPanel(Control row, Control tail, Control? tailRule = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(tail);

        _row = row;
        _tail = tail;
        _tailRule = tailRule;

        Children.Add(row);
        Children.Add(tail);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The tail at its full size first, so the row is offered the width the tail leaves.
        if (_tailRule is not null) _tailRule.IsVisible = true;
        _tail.Measure(Size.Infinity);
        var tailWidth = _tail.DesiredSize.Width;

        var rowWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - tailWidth);
        _row.Measure(new Size(rowWidth, availableSize.Height));

        if (_tailRule is not null && _row.DesiredSize.Width <= 0)
        {
            _tailRule.IsVisible = false;
            _tail.Measure(Size.Infinity);
            tailWidth = _tail.DesiredSize.Width;
        }

        var height = Math.Max(_row.DesiredSize.Height, _tail.DesiredSize.Height);
        return new Size(_row.DesiredSize.Width + tailWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rowWidth = _row.DesiredSize.Width;
        _row.Arrange(new Rect(0, 0, rowWidth, finalSize.Height));

        var tailWidth = _tail.DesiredSize.Width;
        _tail.Arrange(new Rect(rowWidth, 0, tailWidth, finalSize.Height));

        return new Size(rowWidth + tailWidth, finalSize.Height);
    }
}
