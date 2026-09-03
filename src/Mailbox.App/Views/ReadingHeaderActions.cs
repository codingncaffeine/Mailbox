using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mailbox.App.ViewModels;

namespace Mailbox.App.Views;

/// <summary>
/// Reply, Reply All and Forward at the top right of the reading pane, giving way into their own
/// "…" as the pane narrows.
/// </summary>
/// <remarks>
/// <b>The rule the ribbon follows, in the one place that was not following it.</b> Everything that
/// cannot fit goes into the "…" at the right of whatever is holding it — the bar does it, the tab
/// strip does its own version of it — and this header did not: it was a two-column grid whose left
/// column kept a least width and whose right column took whatever the buttons wanted, so past a
/// certain width neither gave way and the sender was drawn <em>underneath</em> the buttons, with
/// the subject cut mid-word. Reproducible at a 760-wide window, and at any width with the To-Do
/// Bar docked beside the pane.
/// <para>
/// The order is the bar's order: labels first, from the right, so a button becomes its glyph
/// before any button leaves; then whole buttons, from the right, into the "…". Reply keeps its
/// label longest because it is the one the reader wants — the same reason the bar's primary
/// command keeps its own.
/// </para>
/// <para>
/// A panel rather than a grid column because the arithmetic needs a width to do it against, and a
/// grid measures an <c>Auto</c> column's child with infinity: nothing put in one can ever discover
/// that it does not fit. This is docked, so what it is offered is what the pane actually has.
/// </para>
/// </remarks>
public sealed class ReadingHeaderActions : Panel
{
    /// <summary>One action: the button, and the label inside it that goes first.</summary>
    private sealed record Entry(Button Button, TextBlock Label);

    private readonly List<Entry> _entries = [];
    private Button? _overflow;
    private MenuFlyout? _menu;

    /// <summary>
    /// What the sender keeps whatever the buttons want. The header exists to say who wrote the
    /// message, so the buttons are fitted into what is left of the pane after this rather than
    /// into the whole of it.
    /// </summary>
    /// <remarks>
    /// The number the markup used to put on the sender column as a <c>MinWidth</c>, which is
    /// where it stopped working: a minimum on one column does not make the other give way, it
    /// only decides which of the two is drawn on top of the other.
    /// </remarks>
    public const double SenderLeastWidth = 90;

    /// <summary>Builds the row from the shell's own list, and the "…" that catches what falls off.</summary>
    public void Fill(IReadOnlyList<QuickAccessButton> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        Children.Clear();
        _entries.Clear();

        foreach (var action in actions)
        {
            if (action.IsSeparator) continue;

            var glyph = new TextBlock
            {
                Text = action.Glyph,
                FontFamily = action.IconFamily,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("accent.rest.brush"),
            };

            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = action.Label };
            label.Classes.Add("small");

            var button = new Button
            {
                Padding = new Thickness(7, 4),
                Command = action.Invoke,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Children = { glyph, label },
                },
            };
            ToolTip.SetTip(button, action.Tip);
            AutomationProperties.SetName(button, action.Label);

            _entries.Add(new Entry(button, label));
            Children.Add(button);
        }

        _menu = new MenuFlyout();
        _overflow = new Button
        {
            Padding = new Thickness(7, 4),
            IsVisible = false,
            Flyout = _menu,
            Content = new TextBlock
            {
                Text = "…",
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(_overflow, "More respond commands");
        AutomationProperties.SetName(_overflow, "More respond commands");
        Children.Add(_overflow);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_entries.Count == 0) return default;

        // Everything at its fullest, so the widths remembered are the fullest ones — a button
        // measured with its label already hidden would cache the narrow width and never grow.
        foreach (var entry in _entries)
        {
            entry.Label.IsVisible = true;
            entry.Button.IsVisible = true;
        }

        if (_overflow is not null) _overflow.IsVisible = false;

        foreach (var entry in _entries) entry.Button.Measure(Size.Infinity);
        _overflow?.Measure(Size.Infinity);

        var full = _entries.Select(e => e.Button.DesiredSize.Width).ToArray();
        var labels = _entries.Select(e => e.Label.DesiredSize.Width + Spacing).ToArray();

        var budget = double.IsInfinity(availableSize.Width)
            ? double.MaxValue
            : Math.Max(0, availableSize.Width - SenderLeastWidth);

        var (labelled, shown) = Fit(full, labels, _overflow?.DesiredSize.Width ?? 0, budget);
        var overflowed = shown.Any(s => !s);

        for (var i = 0; i < _entries.Count; i++)
        {
            Show(_entries[i].Label, labelled[i], _entries[i].Button);
            _entries[i].Button.IsVisible = shown[i];
        }

        if (_overflow is not null) _overflow.IsVisible = overflowed;
        FillMenu(shown);

        var width = 0.0;
        var height = 0.0;

        foreach (var entry in _entries)
        {
            if (!entry.Button.IsVisible) continue;
            entry.Button.Measure(Size.Infinity);
            width += entry.Button.DesiredSize.Width + Spacing;
            height = Math.Max(height, entry.Button.DesiredSize.Height);
        }

        if (_overflow is { IsVisible: true })
        {
            _overflow.Measure(Size.Infinity);
            width += _overflow.DesiredSize.Width;
            height = Math.Max(height, _overflow.DesiredSize.Height);
        }
        else if (width > 0)
        {
            width -= Spacing;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;

        foreach (var entry in _entries)
        {
            if (!entry.Button.IsVisible) continue;
            var w = entry.Button.DesiredSize.Width;
            entry.Button.Arrange(new Rect(x, 0, w, entry.Button.DesiredSize.Height));
            x += w + Spacing;
        }

        if (_overflow is { IsVisible: true })
        {
            _overflow.Arrange(new Rect(
                x, 0, _overflow.DesiredSize.Width, _overflow.DesiredSize.Height));
        }

        return finalSize;
    }

    /// <summary>
    /// Shows or hides a label and makes every control between it and its button measure again.
    /// </summary>
    /// <remarks>
    /// Hiding a child invalidates that child, not the panel holding it, and a panel whose measure
    /// is still thought valid hands back the width it had when the child was showing. So a hidden
    /// label went on taking its room: the three buttons drew as glyphs and still sat at the pitch
    /// of their labelled selves, spread across the header with the sender squeezed out from under
    /// them — the same trap <c>SimplifiedRowPanel</c> records, in the same shape.
    /// </remarks>
    private static void Show(Control label, bool visible, Control until)
    {
        if (label.IsVisible == visible) return;

        label.IsVisible = visible;

        for (var parent = label.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
        {
            if (parent is Layoutable layoutable) layoutable.InvalidateMeasure();
            if (ReferenceEquals(parent, until)) break;
        }
    }

    /// <summary>
    /// What the row keeps at a width: which buttons still write their names, and which are still
    /// on the row at all.
    /// </summary>
    /// <remarks>
    /// Arithmetic over widths rather than a pass over controls, so the order can be checked
    /// without a window — the same separation <c>SimplifiedRowPanel.Fit</c> and
    /// <c>TabStripPanel.Fit</c> have, and <c>ReadingHeaderFitTests</c> is where it is pinned.
    /// <para>
    /// <paramref name="budget"/> is what the buttons may have, which is the pane less whatever
    /// the sender keeps: fitting them into the whole pane is what drew them over it.
    /// </para>
    /// </remarks>
    public static (bool[] Labelled, bool[] Shown) Fit(
        IReadOnlyList<double> full, IReadOnlyList<double> labels, double overflowWidth, double budget)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(labels);

        var shown = new bool[full.Count];
        Array.Fill(shown, true);
        var labelled = new bool[full.Count];
        Array.Fill(labelled, true);

        // Labels first, from the right: a button becomes its glyph before any button leaves, so
        // three reachable glyphs beat two names and a menu.
        for (var i = full.Count - 1; i >= 0 && Total(full, labels, shown, labelled) > budget; i--)
        {
            labelled[i] = false;
        }

        // Then whole buttons, from the right, into the "…". The menu costs its width only once
        // something is in it — charging for it up front dropped a button at a width where losing
        // the last name was enough, which is a command put behind a menu for nothing.
        while (true)
        {
            var anyOverflowed = Array.IndexOf(shown, false) >= 0;
            if (Total(full, labels, shown, labelled) + (anyOverflowed ? overflowWidth : 0) <= budget)
            {
                break;
            }

            var last = Array.LastIndexOf(shown, true);
            if (last < 0) break;

            shown[last] = false;
        }

        return (labelled, shown);
    }

    /// <summary>What the row costs with these labels showing and these buttons on it.</summary>
    private static double Total(
        IReadOnlyList<double> full, IReadOnlyList<double> labels, bool[] shown, bool[] labelled)
    {
        var total = 0.0;

        for (var i = 0; i < full.Count; i++)
        {
            if (!shown[i]) continue;
            total += full[i] - (labelled[i] ? 0 : labels[i]) + Spacing;
        }

        return total > 0 ? total - Spacing : 0;
    }

    /// <summary>
    /// The "…" lists what left the row, in the row's own order — so a reader who has just watched
    /// Forward go finds it at the bottom of the menu rather than sorted somewhere else.
    /// </summary>
    private void FillMenu(bool[] shown)
    {
        if (_menu is null) return;

        _menu.Items.Clear();

        for (var i = 0; i < _entries.Count; i++)
        {
            if (shown[i]) continue;

            var entry = _entries[i];
            _menu.Items.Add(new MenuItem
            {
                Header = AutomationProperties.GetName(entry.Button),
                Command = entry.Button.Command,
            });
        }
    }

    /// <summary>The gap between two of these buttons, as the markup drew it.</summary>
    private const double Spacing = 2;
}
