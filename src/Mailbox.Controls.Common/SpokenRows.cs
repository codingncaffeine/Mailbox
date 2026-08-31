using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Mailbox.Controls.Common;

/// <summary>
/// What a drawn list tells assistive technology: how many rows it holds, the sentence each row
/// should be heard as, which one is current, and the doors for a reader to select or toggle one.
/// </summary>
/// <remarks>
/// The drawn views each keep a selection model and a keyboard model of their own; this is that
/// model turned outward. A view implements it and returns a <see cref="SpokenRowsPeer"/> from
/// <c>OnCreateAutomationPeer</c>, and the rows appear to a screen reader as selectable list
/// items — named, stated, and pressable — without a control per row existing anywhere.
/// </remarks>
public interface ISpokenRows
{
    /// <summary>How many rows there are to speak.</summary>
    int SpokenCount { get; }

    /// <summary>The row as a screen reader should say it.</summary>
    string SpokenRow(int index);

    /// <summary>The current row, -1 for none.</summary>
    int SpokenSelectedIndex { get; }

    /// <summary>Selects a row the way a click would — raising the view's own events.</summary>
    void SpokenSelect(int index);

    /// <summary>
    /// The row's box in the view's own coordinates, or null when the view cannot place it
    /// (filtered out of the current arrangement, or in a layout that does not draw it).
    /// A row that is merely scrolled out of sight keeps its box; the peer works out
    /// visibility from where the box lands.
    /// </summary>
    Rect? SpokenRowBounds(int index);

    /// <summary>The row's tick, or null where the row has none. Rows without ticks are the rule.</summary>
    bool? SpokenRowToggled(int index) => null;

    /// <summary>Presses the row's tick.</summary>
    void SpokenToggle(int index)
    {
    }

    /// <summary>The rows were replaced or their count moved.</summary>
    event EventHandler? SpokenRowsChanged;

    /// <summary>The current row moved, including to nothing.</summary>
    event EventHandler? SpokenSelectionChanged;
}

/// <summary>
/// The automation peer of a drawn list: a List whose children are one peer per row, kept in
/// step with the view's own model and raising the two notifications a screen reader keys on —
/// children changed when the rows are replaced, selection changed when the current row moves.
/// </summary>
public class SpokenRowsPeer : ControlAutomationPeer, ISelectionProvider
{
    private readonly List<SpokenRowPeer> _rows = [];
    private readonly AutomationControlType _role;
    private readonly AutomationControlType _rowRole;

    public SpokenRowsPeer(Control owner,
        AutomationControlType role = AutomationControlType.List,
        AutomationControlType rowRole = AutomationControlType.ListItem) : base(owner)
    {
        _role = role;
        _rowRole = rowRole;
        var view = (ISpokenRows)owner;
        view.SpokenRowsChanged += (_, _) => InvalidateChildren();
        // The container's selection property is the one change the accessibility bridge turns
        // into a selection-changed signal; per-row selected-state raises reach nothing.
        view.SpokenSelectionChanged += (_, _) =>
            RaisePropertyChangedEvent(SelectionPatternIdentifiers.SelectionProperty, null, null);
    }

    internal ISpokenRows View => (ISpokenRows)Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => _role;

    internal AutomationControlType RowRole => _rowRole;

    protected override IReadOnlyList<AutomationPeer> GetChildrenCore()
    {
        var count = View.SpokenCount;
        while (_rows.Count > count) _rows.RemoveAt(_rows.Count - 1);
        while (_rows.Count < count) _rows.Add(new SpokenRowPeer(this, _rows.Count));
        return [.. _rows];
    }

    /// <summary>The row's peer, synced first so a fresh selection can be answered.</summary>
    internal SpokenRowPeer? Row(int index)
    {
        if (index < 0) return null;
        var rows = GetChildrenCore();
        return index < rows.Count ? (SpokenRowPeer)rows[index] : null;
    }

    public IReadOnlyList<AutomationPeer> GetSelection()
        => Row(View.SpokenSelectedIndex) is { } row ? [row] : [];

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => false;
}

/// <summary>
/// One drawn row, spoken for. Not backed by a control — the whole point — so everything a peer
/// usually reads off its owner is answered from the view's model instead.
/// </summary>
public sealed class SpokenRowPeer(SpokenRowsPeer list, int index)
    : UnrealizedElementAutomationPeer, ISelectionItemProvider, IToggleProvider
{
    private ISpokenRows View => list.View;

    protected override AutomationPeer GetParentCore() => list;

    protected override AutomationControlType GetAutomationControlTypeCore() => list.RowRole;

    protected override string GetNameCore() => View.SpokenRow(index);

    protected override string GetClassNameCore() => "SpokenRow";

    protected override string? GetAutomationIdCore() => null;

    protected override string? GetAcceleratorKeyCore() => null;

    protected override string? GetAccessKeyCore() => null;

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsEnabledCore() => list.Owner.IsEnabled;

    /// <summary>
    /// The row's box in the window's coordinates, which is the space the bridge converts to the
    /// screen from. The view's transformed bounds carry both the transform and the accumulated
    /// clip, so scrolling is in the transform and visibility falls out of the clip.
    /// </summary>
    protected override Rect GetBoundingRectangleCore()
    {
        if (View.SpokenRowBounds(index) is not { } local) return default;
        return list.Owner.GetTransformedBounds() is { } placed
            ? local.TransformToAABB(placed.Transform)
            : default;
    }

    protected override bool IsOffscreenCore()
    {
        if (View.SpokenRowBounds(index) is not { } local) return true;
        // Against the accumulated clip rather than the view's own bounds: a drawn body inside
        // a ScrollViewer is as tall as all its rows, so its own box never rules anything out,
        // while the clip is exactly the viewport the reader can see.
        if (list.Owner.GetTransformedBounds() is not { } placed) return true;
        return !placed.Clip.Intersects(local.TransformToAABB(placed.Transform));
    }

    // Like a list box's items: a reader can ask the row to take the keyboard, and asking
    // selects it, because selection is what the keyboard model here moves.
    protected override bool IsKeyboardFocusableCore() => list.Owner.Focusable;

    protected override bool HasKeyboardFocusCore()
        => list.Owner.IsFocused && View.SpokenSelectedIndex == index;

    protected override void SetFocusCore()
    {
        list.Owner.Focus();
        View.SpokenSelect(index);
    }

    // ---- Selection, offered to the reader ----------------------------------------------------

    public bool IsSelected => View.SpokenSelectedIndex == index;

    public ISelectionProvider? SelectionContainer => list;

    public void Select() => View.SpokenSelect(index);

    public void AddToSelection() => View.SpokenSelect(index);

    public void RemoveFromSelection()
    {
        if (IsSelected) View.SpokenSelect(-1);
    }

    // ---- The tick, where the row has one -----------------------------------------------------

    /// <summary>Only rows that draw a tick offer the toggle pattern; the rest answer null.</summary>
    protected override object? GetProviderCore(Type providerType)
        => providerType == typeof(IToggleProvider) && View.SpokenRowToggled(index) is null
            ? null
            : base.GetProviderCore(providerType);

    public ToggleState ToggleState => View.SpokenRowToggled(index) == true ? ToggleState.On : ToggleState.Off;

    public void Toggle() => View.SpokenToggle(index);
}
