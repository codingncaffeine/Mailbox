using Avalonia;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Mailbox.Core.Commands;

namespace Mailbox.Controls.Ribbon;

/// <summary>
/// One entry on the Simplified bar, and what the panel may do to it when the bar is too narrow.
/// </summary>
/// <param name="Control">The control on the bar.</param>
/// <param name="Label">Its text, when it has some the bar may take away; null otherwise.</param>
/// <param name="Command">What it stands for, so an entry pushed off the bar can be listed.</param>
/// <param name="LabelRank">
/// The order labels are given up in: the lowest rank on the bar goes first, and within a rank
/// the rightmost goes first. The reference does not simply shed right to left — at 1447 it has
/// dropped Reply, Reply All, Forward, Categorize and Follow Up while Unread/Read and
/// Send/Receive All Folders, which are further right, still read as words. New Email is the
/// highest rank and keeps its label longest.
/// </param>
/// <param name="Cluster">Which cluster it belongs to; a cluster's rule goes with its last entry.</param>
/// <param name="IsRule">True for the vertical rule between clusters.</param>
public sealed record SimplifiedEntry(
    Control Control,
    TextBlock? Label,
    CommandId? Command,
    int LabelRank,
    int Cluster,
    bool IsRule);

/// <summary>
/// The Simplified ribbon's row, laid out the way the reference lays it out when it is short of
/// room.
/// </summary>
/// <remarks>
/// A stack in a scroller was what this was, and a stack in a scroller clips: at 1280 wide the
/// bar read "Send/Rece …", at 1000 "Search Peop", which is not a ribbon narrowing but a picture
/// of one being cut. The reference does two things instead, in this order, and this panel does
/// the same:
/// <list type="number">
///   <item><b>Labels go first, from the right.</b> "Show labels as space permits" is the
///   reference's default: a labelled button becomes its icon, right to left, and the primary
///   command keeps its label longest. No text is ever truncated — a label is whole or gone.</item>
///   <item><b>Then whole controls go, from the right,</b> into the "…" menu at the bar's end. A
///   cluster's rule goes with its last control. Jensen Harris's rule for the classic ribbon holds
///   here too: nothing appears or disappears mid-control, and what is pushed off is still one
///   click away.</item>
/// </list>
/// The decisions are arithmetic over widths measured while everything is showing, so a second
/// layout pass — which changing a visibility causes — reaches the same answer and the layout
/// settles rather than oscillating. The host reads <see cref="Overflowed"/> when the "…" menu
/// opens, so the menu lists what the bar could not, on top of what it never had.
/// </remarks>
public sealed class SimplifiedRowPanel : Panel
{
    private readonly List<SimplifiedEntry> _entries = [];

    /// <summary>The gap a label occupies beyond its icon: the label itself and the spacing before it.</summary>
    private readonly Dictionary<Control, double> _labelWidth = [];

    /// <summary>What each control measures with its label showing, remembered while it is.</summary>
    private readonly Dictionary<Control, double> _fullWidth = [];

    private readonly List<CommandId> _overflowed = [];

    /// <summary>Read once: this sits inside a layout pass, which runs on every resize.</summary>
    private static readonly bool Trace =
        Environment.GetEnvironmentVariable("MAILBOX_RIBBON_TRACE") == "1";

    /// <summary>The commands the bar could not fit, left to right, for the "…" menu.</summary>
    public IReadOnlyList<CommandId> Overflowed => _overflowed;

    /// <summary>Raised when what fits has changed, so a menu already open can be rebuilt.</summary>
    public event EventHandler? OverflowChanged;

    /// <summary>Adds an entry, in bar order.</summary>
    public void Add(SimplifiedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
        Children.Add(entry.Control);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Everything at its fullest, so the widths remembered are the fullest ones. A control
        // whose label is hidden measures narrower and would poison the cache.
        foreach (var entry in _entries)
        {
            entry.Control.IsVisible = true;
            if (entry.Label is not null) entry.Label.IsVisible = true;
        }

        foreach (var entry in _entries)
        {
            entry.Control.Measure(Size.Infinity);
            _fullWidth[entry.Control] = entry.Control.DesiredSize.Width;

            if (entry.Label is not null)
            {
                entry.Label.Measure(Size.Infinity);

                // The label and the spacing before it, which is what a dropped label gives back.
                // The spacing is the button's own; asking the button is more honest than a
                // constant, but the constant is what every one of these is built with.
                _labelWidth[entry.Control] = entry.Label.DesiredSize.Width + LabelSpacing;
            }
        }

        var available = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;

        var specs = _entries.Select(e => new SimplifiedFit(
            _fullWidth.GetValueOrDefault(e.Control),
            e.Label is null ? 0 : _labelWidth.GetValueOrDefault(e.Control),
            e.LabelRank,
            e.IsRule)).ToArray();

        var (labelled, shown) = Fit(specs, available);

        var wasOverflowed = _overflowed.ToList();
        _overflowed.Clear();

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            entry.Control.IsVisible = shown[i];
            if (shown[i] && entry.Label is not null) Show(entry.Label, labelled[i], entry.Control);
        }

        // The decision was arithmetic over cached widths; the truth is what the controls
        // re-measure at now their labels are set. A few pixels of drift between the two let the
        // last control spill past the bar's rounded edge — visibly, at 800 — so the actual sum
        // is walked and anything past the width is pushed off too. Deterministic, because it
        // only ever hides more, never less.
        var width = 0.0;
        var height = 0.0;

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (!shown[i]) continue;

            entry.Control.Measure(Size.Infinity);
            var actual = entry.Control.DesiredSize.Width;

            if (!entry.IsRule && width + actual > available)
            {
                shown[i] = false;
                entry.Control.IsVisible = false;
                continue;
            }

            // MAILBOX_RIBBON_TRACE=1 says what every entry measured and whether it kept its
            // label. A bar that is too wide is arithmetic, and this is how the arithmetic is
            // read back — it is what found hidden labels still taking their room.
            if (Trace)
            {
                Mailbox.Core.Diagnostics.Log.Info(
                    $"Ribbon: {entry.Command?.Value ?? (entry.IsRule ? "rule" : "entry")} "
                    + $"full={_fullWidth.GetValueOrDefault(entry.Control):0} "
                    + $"label={(entry.Label is null ? 0 : _labelWidth.GetValueOrDefault(entry.Control)):0} "
                    + $"labelled={labelled[i]} measured={actual:0}");
            }

            width += actual;
            height = Math.Max(height, entry.Control.DesiredSize.Height);
        }

        // Rules stranded by the corrective pass, swept the same way Fit sweeps its own.
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (!_entries[i].IsRule || !shown[i]) continue;

            var anythingAfter = false;
            for (var j = i + 1; j < _entries.Count; j++)
            {
                if (shown[j] && !_entries[j].IsRule) { anythingAfter = true; break; }
            }

            if (!anythingAfter)
            {
                shown[i] = false;
                _entries[i].Control.IsVisible = false;
                width -= _entries[i].Control.DesiredSize.Width;
            }
        }

        for (var i = 0; i < _entries.Count; i++)
        {
            if (!shown[i] && _entries[i].Command is { } pushedOff) _overflowed.Add(pushedOff);
        }

        if (!wasOverflowed.SequenceEqual(_overflowed)) OverflowChanged?.Invoke(this, EventArgs.Empty);

        return new Size(Math.Min(width, available), height);
    }

    /// <summary>
    /// Shows or hides a label and makes every control between it and its button measure again.
    /// </summary>
    /// <remarks>
    /// Hiding a child invalidates that child, not the panel holding it, and a panel whose
    /// measure is still thought valid hands back the width it had when the child was showing.
    /// So a hidden label went on taking its room: the bar looked narrowed and was not, and
    /// pushed controls into the "…" that the reference still fits. The walk stops at the
    /// entry's own control, which this panel measures itself a moment later.
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

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;

        foreach (var entry in _entries)
        {
            if (!entry.Control.IsVisible) continue;

            var size = entry.Control.DesiredSize;
            entry.Control.Arrange(new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width;
        }

        return new Size(x, finalSize.Height);
    }

    /// <summary>What one entry costs the fitter: its full width, the width its label alone adds,
    /// whether its label is spared longest, and whether it is a cluster rule.</summary>
    public readonly record struct SimplifiedFit(
        double FullWidth, double LabelWidth, int LabelRank, bool IsRule);

    /// <summary>
    /// What to show and what to label at a width, as pure arithmetic over the entries' widths.
    /// </summary>
    /// <remarks>
    /// Free of any control, so the reference's two rules can be tested against widths rather
    /// than against a window that has to be sized and photographed — the same discipline
    /// <c>RibbonCollapsePolicy</c> keeps for the classic bar. The rules, in order:
    /// <list type="number">
    ///   <item>Labels off by ascending <c>LabelRank</c>, and from the right within a rank.</item>
    ///   <item>Then that one's label too, before any whole control goes.</item>
    ///   <item>Then whole controls off, from the right, a cluster's rule going once nothing is
    ///   left to its right.</item>
    /// </list>
    /// It settles because it is a function of the width alone: the same width always gives the
    /// same answer, however many layout passes reach it.
    /// </remarks>
    public static (bool[] Labelled, bool[] Shown) Fit(IReadOnlyList<SimplifiedFit> entries, double available)
    {
        var count = entries.Count;
        var labelled = new bool[count];
        var shown = new bool[count];

        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            labelled[i] = entries[i].LabelWidth > 0;
            shown[i] = true;
            total += entries[i].FullWidth;
        }

        if (total <= available) return (labelled, shown);

        // Labels first, a whole rank at a time from the lowest, and every label before any whole
        // control — which is the reference's order: a bar of icons with everything still on it
        // rather than a shorter bar of words. A rank goes together rather than one label at a
        // time because half a cluster labelled is what nothing in the reference ever looks
        // like: at 1447 all five Respond and Tags words are gone at once while Unread/Read,
        // further right, still reads.
        foreach (var rank in entries.Where(e => e.LabelWidth > 0).Select(e => e.LabelRank).Distinct().Order())
        {
            if (total <= available) break;

            for (var i = count - 1; i >= 0; i--)
            {
                if (!labelled[i] || entries[i].LabelRank != rank) continue;
                labelled[i] = false;
                total -= entries[i].LabelWidth;
            }
        }

        // Then whole controls, from the right. A rule goes once nothing real is left to its right.
        for (var i = count - 1; i >= 0 && total > available; i--)
        {
            if (entries[i].IsRule)
            {
                if (!AnythingAfter(entries, shown, i))
                {
                    shown[i] = false;
                    total -= entries[i].FullWidth;
                }

                continue;
            }

            shown[i] = false;
            total -= entries[i].FullWidth - (labelled[i] ? 0 : entries[i].LabelWidth);
        }

        // A rule left stranded at the end — its cluster gone entirely — is a line hanging off
        // the bar. Sweep those whether or not the width demanded it.
        for (var i = count - 1; i >= 0; i--)
        {
            if (entries[i].IsRule && shown[i] && !AnythingAfter(entries, shown, i)) shown[i] = false;
        }

        return (labelled, shown);
    }

    /// <summary>Whether any non-rule entry after <paramref name="index"/> is still shown.</summary>
    private static bool AnythingAfter(IReadOnlyList<SimplifiedFit> entries, bool[] shown, int index)
    {
        for (var i = index + 1; i < entries.Count; i++)
        {
            if (shown[i] && !entries[i].IsRule) return true;
        }

        return false;
    }

    /// <summary>The spacing a labelled button puts between its icon and its label.</summary>
    internal const double LabelSpacing = 6;
}
