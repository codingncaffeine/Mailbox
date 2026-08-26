using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Theming.Icons;

namespace Mailbox.Controls.Ribbon;

/// <summary>Raised when a ribbon control is activated.</summary>
public sealed class RibbonCommandEventArgs(CommandId command, bool fromChevron = false) : EventArgs
{
    public CommandId Command { get; } = command;

    /// <summary>
    /// True when the chevron half of a split button was pressed rather than its action half.
    /// </summary>
    /// <remarks>
    /// The two halves usually carry different commands, so most hosts never look at this; it is
    /// here for the one that wants to open a menu from the chevron and act from the other half
    /// while both name the same command.
    /// </remarks>
    public bool FromChevron { get; } = fromChevron;
}

/// <summary>
/// Carries the ribbon body that should float over the content while the ribbon is collapsed to
/// its tab strip, or null when it should stop floating.
/// </summary>
/// <remarks>
/// The body cannot simply be shown in place: collapsing to the tab strip means the ribbon's row
/// is the strip's height, and putting a body back in it would push the content down, which is
/// the opposite of what the mode is for. So the host places it on an overlay instead — an
/// ordinary control in the window rather than a popup, because a popup is a separate surface
/// and would not appear in a capture.
/// </remarks>
public sealed class RibbonFloatEventArgs(Control? body) : EventArgs
{
    public Control? Body { get; } = body;
}

/// <summary>
/// Renders a <see cref="RibbonLayout"/> as an Office ribbon.
/// </summary>
/// <remarks>
/// Built in code rather than XAML on purpose: the layout is data that a user can rearrange at
/// runtime, so the visual tree has to be produced from that document rather than authored
/// statically. Everything paints from theme tokens through dynamic resources, so a theme swap
/// needs no rebuild.
/// </remarks>
public sealed class RibbonView : ContentControl
{
    /// <summary>
    /// The shortcut a tooltip shows for a command, when the application keeps a key map of its
    /// own; null falls back to the command's shipped gesture.
    /// </summary>
    public static Func<Mailbox.Core.Commands.MailboxCommand, string?>? GestureLookup { get; set; }

    private readonly CommandCatalog _catalog;

    // What Alt traversal adorns. Rebuilt with the visual tree, because the controls a KeyTip
    // points at are thrown away and remade whenever the tab or the display mode changes.
    private readonly List<(RibbonTab Tab, Control Control)> _tabControls = [];
    private readonly Dictionary<CommandId, List<Control>> _itemControls = [];

    /// <summary>
    /// Commands the layout itself marks unusable, whatever the host's enablement says.
    /// </summary>
    /// <remarks>
    /// <see cref="RibbonItem.IsDisabled"/> had no reader, so the one entry that sets it — Read
    /// Aloud on the classic View tab — drew as an ordinary button and answered a press with a
    /// developer string. A layout that says a control is greyed is the strongest statement about
    /// it there is, so it wins over the host.
    /// </remarks>
    private readonly HashSet<CommandId> _disabled = [];

    // Groups that have degraded to a popup button, and the button they degraded to. Their
    // commands have no control on the bar, so this is the only thing Alt can adorn for them.
    private readonly List<(RibbonGroup Group, Button Button)> _collapsedGroups = [];

    private RibbonLayout _layout;
    private string _activeTabId;
    private Button? _displayOptions;

    public RibbonView(CommandCatalog catalog, RibbonLayout layout)
    {
        _catalog = catalog;
        _layout = layout;
        // File is a Backstage trigger and a contextual tab is not on screen yet, so the first
        // ordinary tab is what starts selected.
        _activeTabId = layout.Tabs.FirstOrDefault(t => !t.IsBackstage && !t.IsContextual)?.Id
            ?? string.Empty;
        Rebuild();
    }

    /// <summary>
    /// The document being rendered. Setting it redraws the ribbon.
    /// </summary>
    /// <remarks>
    /// This is the whole of how a customization reaches the screen: the editor produces a
    /// layout and hands it over, and the ribbon never learns what an edit is. A tab that has
    /// gone — unticked in the editor — takes the selection with it, so the first tab that is
    /// still there becomes the active one rather than leaving the strip pointing at nothing.
    /// </remarks>
    public RibbonLayout Layout
    {
        get => _layout;
        set
        {
            _layout = value;

            if (value.FindTab(_activeTabId) is null)
            {
                _activeTabId = value.Tabs
                    .FirstOrDefault(t => !t.IsBackstage && !t.IsContextual)?.Id ?? string.Empty;
            }

            Rebuild();
        }
    }

    /// <summary>
    /// Drops the Ribbon Display Options menu open so the fidelity harness can photograph it.
    /// A menu that no capture ever opens is a menu whose colours go unchecked — which is how
    /// the light themes shipped with dark-on-dark items, legible only while hovered.
    /// </summary>
    public void OpenDisplayOptions() => _displayOptions?.Flyout?.ShowAt(_displayOptions);

    /// <summary>
    /// The bar's "…" and its menu, for the harness: a flyout cannot be photographed, so what it
    /// would list is read back instead.
    /// </summary>
    private Button? _barOverflow;

    /// <summary>Opens the bar's "…" menu and returns what it holds, in order.</summary>
    public IReadOnlyList<string> OpenOverflowMenu()
    {
        if (_barOverflow?.Flyout is not MenuFlyout flyout) return [];

        flyout.ShowAt(_barOverflow);

        return [.. flyout.Items
            .OfType<object>()
            .Select(item => item switch
            {
                MenuItem menu => menu.Header as string ?? "?",
                Separator => "—",
                _ => item.GetType().Name,
            })];
    }

    public event EventHandler<RibbonCommandEventArgs>? CommandInvoked;

    /// <summary>Raised when the File tab is clicked. The shell opens the Backstage.</summary>
    public event EventHandler? BackstageRequested;

    /// <summary>
    /// Raised by Show/Hide Quick Access Toolbar in the display-options menu.
    /// </summary>
    /// <remarks>
    /// This menu is the only way back once the toolbar is hidden — the chevron that hid it goes
    /// with it. A hide with no matching show is a control that breaks itself.
    /// </remarks>
    public event EventHandler? QuickAccessVisibilityToggled;

    /// <summary>
    /// Full-screen mode was chosen — or chosen again, which comes out of it.
    /// </summary>
    /// <remarks>
    /// Raised rather than done here because it is the window's state, not the bar's: the ribbon
    /// collapses, but so does the caption the host draws, and only the host knows it has one.
    /// </remarks>
    public event EventHandler? FullScreenToggled;

    /// <summary>Whether the host says it is full screen, so the menu can tick the entry.</summary>
    public bool IsFullScreen { get; set; }

    /// <summary>Whether the host is showing the toolbar, so the menu can say the right thing.</summary>
    public bool IsQuickAccessVisible
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = true;

    /// <summary>
    /// Which ribbon the reference application is showing. Simplified is the reference application default, so it is the
    /// default here too — the tall classic ribbon is the alternative, not the baseline.
    /// </summary>
    public RibbonDisplayMode DisplayMode
    {
        get;
        set
        {
            if (field == value) return;

            // Remembered so that revealing a collapsed ribbon brings back the layout it was
            // collapsed from, rather than always the default one.
            if (value != RibbonDisplayMode.Collapsed) _expandedMode = value;

            field = value;
            CloseFloatingBody();
            Rebuild();
            DisplayModeChanged?.Invoke(this, EventArgs.Empty);
        }
    } = RibbonDisplayMode.Simplified;

    /// <summary>
    /// The layout the ribbon shows, or would show if it were not collapsed to its tabs — never
    /// <see cref="RibbonDisplayMode.Collapsed"/>. With <see cref="DisplayMode"/> this is the
    /// whole of the state a host remembers across launches: the menu's two choices, layout and
    /// show, and a collapsed ribbon comes back in the layout it was collapsed from.
    /// </summary>
    public RibbonDisplayMode ExpandedMode => _expandedMode;

    /// <summary>
    /// Raised after <see cref="DisplayMode"/> changes — from the chevron's menu or by code — so
    /// the host can remember the choice. A host that sets the opening mode first and subscribes
    /// second hears only the reader's changes, which is what it wants to write down.
    /// </summary>
    public event EventHandler? DisplayModeChanged;

    private RibbonDisplayMode _expandedMode = RibbonDisplayMode.Simplified;
    private bool _isFloating;

    /// <summary>
    /// Raised while collapsed to the tab strip, with the body to float over the content or null
    /// to stop floating. See <see cref="RibbonFloatEventArgs"/> for why the host places it.
    /// </summary>
    public event EventHandler<RibbonFloatEventArgs>? FloatingBodyChanged;

    /// <summary>
    /// Unrolls a collapsed ribbon over the content, as clicking its tab does. For the harness,
    /// which cannot click.
    /// </summary>
    public void RevealCollapsedRibbon()
    {
        if (DisplayMode != RibbonDisplayMode.Collapsed) return;
        if (_layout.FindTab(_activeTabId) is not { } tab) return;

        OpenFloatingBody(tab);
    }

    /// <summary>Dismisses the floating body. The host calls this on a click elsewhere.</summary>
    public void CloseFloatingBody()
    {
        if (!_isFloating) return;
        _isFloating = false;
        FloatingBodyChanged?.Invoke(this, new RibbonFloatEventArgs(null));
    }

    private void OpenFloatingBody(RibbonTab tab)
    {
        Control? body = _expandedMode == RibbonDisplayMode.Classic && tab.Groups.Count > 0
            ? BuildBody(tab)
            : BuildSimplifiedRow(tab);

        _isFloating = true;
        FloatingBodyChanged?.Invoke(this, new RibbonFloatEventArgs(body));
    }

    // ----------------------------------------------------------------------------------
    // Contextual tabs
    // ----------------------------------------------------------------------------------

    private readonly HashSet<string> _contextualGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Shows or hides a named set of contextual tabs — the unit Office appears and disappears
    /// them in, rather than one tab at a time.
    /// </summary>
    public void SetContextualGroupVisible(string group, bool visible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        var changed = visible ? _contextualGroups.Add(group) : _contextualGroups.Remove(group);
        if (!changed) return;

        // A set going away can leave the ribbon selecting a tab that is no longer in the strip.
        if (ContextualTabs.FallbackFor(_layout.Tabs, _contextualGroups, _activeTabId) is { } fallback)
        {
            _activeTabId = fallback.Id;
        }

        CloseFloatingBody();
        Rebuild();
    }

    public bool IsContextualGroupVisible(string group) => _contextualGroups.Contains(group);

    /// <summary>The tabs currently in the strip: every ordinary one, plus any active set.</summary>
    private IEnumerable<RibbonTab> VisibleTabs =>
        ContextualTabs.Visible(_layout.Tabs, _contextualGroups);

    public string ActiveTabId
    {
        get => _activeTabId;
        set
        {
            if (string.Equals(_activeTabId, value, StringComparison.OrdinalIgnoreCase)) return;
            _activeTabId = value;
            Rebuild();
            ActiveTabChanged?.Invoke(this, _activeTabId);
        }
    }

    /// <summary>
    /// Which tab is showing has changed.
    /// </summary>
    /// <remarks>
    /// A host that puts something different under the bar per tab needs to know — the appointment
    /// window's Scheduling Assistant and Tracking are the form's own workspace replaced, not more
    /// buttons above it.
    /// </remarks>
    public event EventHandler<string>? ActiveTabChanged;

    // ----------------------------------------------------------------------------------
    // Composition
    // ----------------------------------------------------------------------------------

    private void Rebuild()
    {
        _tabControls.Clear();
        _itemControls.Clear();
        CollectDisabled();
        _splitLighters.Clear();
        _menuOpeners.Clear();
        _collapsedGroups.Clear();
        _labelWidth = null;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };

        var strip = BuildTabStrip();
        Grid.SetRow(strip, 0);
        root.Children.Add(strip);

        if (_layout.FindTab(_activeTabId) is { } tab)
        {
            Control? body = DisplayMode switch
            {
                RibbonDisplayMode.Simplified => BuildSimplifiedRow(tab),
                RibbonDisplayMode.Classic when tab.Groups.Count > 0 => BuildBody(tab),
                _ => null,
            };

            if (body is not null)
            {
                Grid.SetRow(body, 1);
                root.Children.Add(body);
            }
        }

        Content = root;
    }

    private Control BuildTabStrip()
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = RibbonMetrics.TabStripHeight,
        };

        foreach (var tab in VisibleTabs)
        {
            strip.Children.Add(BuildTabButton(tab));
        }

        // "Tell me what you want to do" sits after the last tab on the compose window. The
        // shell's captures show none, so it is a property of the layout rather than of the bar.
        if (_layout.TellMe is { Length: > 0 } prompt)
        {
            var hint = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var bulb = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty("lightbulb", 16),
                FontFamily = IconFont.Family,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(bulb, TextBlock.ForegroundProperty, "ribbon.tab.text.brush");
            hint.Children.Add(bulb);

            var label = new TextBlock { Text = prompt, VerticalAlignment = VerticalAlignment.Center };
            Bind(label, TextBlock.ForegroundProperty, "ribbon.tab.text.brush");
            Bind(label, TextBlock.FontSizeProperty, "type.ui.size.value");
            hint.Children.Add(label);

            strip.Children.Add(hint);
        }

        var host = new Border { Height = RibbonMetrics.TabStripHeight, Child = strip };
        Bind(host, Border.BackgroundProperty, "ribbon.tabstrip.background.brush");
        return host;
    }

    private Control BuildTabButton(RibbonTab tab)
    {
        var selected = !tab.IsBackstage
            && string.Equals(tab.Id, _activeTabId, StringComparison.OrdinalIgnoreCase);

        var label = new TextBlock
        {
            Text = tab.Label,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // A contextual tab is tinted rather than banded. Current builds of the reference dropped
        // the coloured header strip that older Office drew above a contextual set, and a band
        // here would change the tab strip's measured height besides.
        Bind(label, TextBlock.ForegroundProperty,
            tab.IsContextual && !selected
                ? "accent.rest.brush"
                : selected ? "ribbon.tab.text.selected.brush" : "ribbon.tab.text.brush");
        Bind(label, TextBlock.FontSizeProperty, "type.ui.size.value");

        // The active tab is marked by a rule under its label, not by a filled pill: measured
        // 2px tall, the exact width of the text, sitting 3px clear of the strip's lower edge.
        // Every tab reserves the rule's space whether or not it is drawn, so that all the
        // labels share one baseline instead of the active one riding higher than its
        // neighbours.
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(RibbonMetrics.TabPaddingH, 0),
            BorderThickness = default,
            CornerRadius = default,
            MinWidth = 0,
            Height = RibbonMetrics.TabStripHeight,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow),
        };

        Bind(button, BackgroundProperty,
            selected ? "ribbon.tab.selected.brush" : "ribbon.tab.rest.brush");

        button.Click += (_, _) =>
        {
            if (tab.IsBackstage)
            {
                BackstageRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (DisplayMode != RibbonDisplayMode.Collapsed)
            {
                ActiveTabId = tab.Id;
                return;
            }

            // Collapsed: a tab click floats the body over the content, and clicking the same
            // tab again puts it away — which is the only way to dismiss it from the keyboard-
            // free path, and what the reference does.
            var sameTab = string.Equals(_activeTabId, tab.Id, StringComparison.OrdinalIgnoreCase);
            if (sameTab && _isFloating)
            {
                CloseFloatingBody();
                return;
            }

            _activeTabId = tab.Id;
            CloseFloatingBody();
            Rebuild();
            OpenFloatingBody(tab);
        };

        // The rule marking the active tab is a sibling of the button, not its content. Inside
        // the button it would be positioned against the template's content box, which is inset
        // by an amount the template owns and silently swallows the clearance below the rule.
        // Out here the measurements hold: 2px tall, 3px clear of the strip's lower edge, and
        // inset by the button's own padding so it spans exactly the label.
        var underline = new Border
        {
            Height = RibbonMetrics.TabUnderlineThickness,
            Margin = new Thickness(
                RibbonMetrics.TabPaddingH, RibbonMetrics.TabUnderlineTop,
                RibbonMetrics.TabPaddingH, 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        if (selected) Bind(underline, Border.BackgroundProperty, "ribbon.tab.underline.brush");

        // Top-aligned, not merely fixed-height: the strip panel can arrange taller than the
        // strip itself, and a fixed-height child with the default alignment centres in that
        // slack, which drops the rule below where it was measured.
        var host = new Grid
        {
            Height = RibbonMetrics.TabStripHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { button, underline },
        };

        _tabControls.Add((tab, host));
        return host;
    }

    // ----------------------------------------------------------------------------------
    // Alt traversal
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// The first level: one badge per tab. Picking one selects it and descends into its
    /// controls, except File, which opens the Backstage and ends the traversal.
    /// </summary>
    public IReadOnlyList<KeyTipTarget> TabKeyTips()
        => _tabControls
            .Where(entry => entry.Tab.KeyTip is not null)
            .Select(entry => new KeyTipTarget
            {
                Tip = entry.Tab.KeyTip!,
                Target = entry.Control,
                Activate = entry.Tab.IsBackstage
                    ? () => BackstageRequested?.Invoke(this, EventArgs.Empty)
                    : () => ActiveTabId = entry.Tab.Id,
                Children = entry.Tab.IsBackstage ? null : ActiveTabKeyTips,
            })
            .ToList();

    /// <summary>
    /// The second level: every command currently drawn, wherever it is drawn.
    /// </summary>
    /// <remarks>
    /// Read off the controls that were built rather than off the layout document, so it needs no
    /// knowledge of whether the ribbon is Simplified or Classic, or of which collapse variant a
    /// group settled on. A command with more than one control — every group is built at three
    /// sizes — contributes the one actually on screen.
    /// </remarks>
    public IReadOnlyList<KeyTipTarget> ActiveTabKeyTips()
    {
        var targets = new List<KeyTipTarget>();

        foreach (var (id, controls) in _itemControls)
        {
            if (!_catalog.TryGet(id, out var command) || command.KeyTip is null) continue;
            if (controls.Find(c => c.IsEffectivelyVisible) is not { } control) continue;

            targets.Add(new KeyTipTarget
            {
                Tip = command.KeyTip,
                Target = control,
                Activate = () => CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(id)),
            });
        }

        targets.AddRange(CollapsedGroupKeyTips());
        return targets;
    }

    /// <summary>
    /// The third level: a collapsed group's own badge, and its commands behind it.
    /// </summary>
    /// <remarks>
    /// A group that has degraded to a popup button draws none of its commands, so they
    /// contribute no control for a badge to adorn and drop out of the traversal — which is a
    /// narrow window quietly taking commands off the keyboard, and the one thing Alt traversal
    /// exists to prevent.
    /// <para>
    /// The group's button gets the badge instead. Activating it opens the flyout, and the
    /// commands inside become the next level, adorned where they are actually drawn: the popup
    /// is a separate surface with an adorner layer of its own, so the badges follow the controls
    /// into it without any of this knowing that is where they went.
    /// </para>
    /// </remarks>
    private IEnumerable<KeyTipTarget> CollapsedGroupKeyTips()
    {
        foreach (var (group, button) in _collapsedGroups)
        {
            if (group.KeyTip is not { Length: > 0 } tip) continue;
            if (!button.IsEffectivelyVisible) continue;

            var owner = group;
            var opener = button;

            yield return new KeyTipTarget
            {
                Tip = tip,
                Target = button,
                Activate = () => opener.Flyout?.ShowAt(opener),

                // Read after the flyout has opened, so the controls it built are on screen and
                // have bounds. Which is also why this is deferred rather than a list: the
                // group's items do not exist until the popup is opened.
                Children = () => GroupKeyTips(owner),
            };
        }
    }

    /// <summary>The commands of one group, wherever their controls have ended up.</summary>
    private IReadOnlyList<KeyTipTarget> GroupKeyTips(RibbonGroup group)
    {
        var targets = new List<KeyTipTarget>();

        foreach (var item in group.Items)
        {
            if (item.IsSentinel) continue;
            if (!_catalog.TryGet(item.Command, out var command) || command.KeyTip is null) continue;
            if (!_itemControls.TryGetValue(item.Command, out var controls)) continue;
            if (controls.Find(c => c.IsEffectivelyVisible) is not { } control) continue;

            var id = item.Command;

            targets.Add(new KeyTipTarget
            {
                Tip = command.KeyTip,
                Target = control,
                Activate = () => CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(id)),
            });
        }

        return targets;
    }

    /// <summary>
    /// The Simplified ribbon: one row of icon-and-label commands with vertical rules between
    /// clusters, an overflow menu after the last of them, and the display-mode chevron in the
    /// panel's bottom-right corner.
    /// </summary>
    private Control BuildSimplifiedRow(RibbonTab tab)
    {
        var items = _layout.SimplifiedRows.TryGetValue(tab.Id, out var row) ? row : [];

        // The panel that gives way the way the reference's bar does — labels first, from the
        // right, then whole controls into the "…" — rather than a stack that clips. See the
        // panel for the rules; the primary command is the first labelled one, and it keeps its
        // label longest.
        var strip = new SimplifiedRowPanel { VerticalAlignment = VerticalAlignment.Center };
        var primaryClaimed = false;

        // Walked cluster by cluster rather than item by item, because a cluster's "…" lists what
        // that cluster leaves out and so has to know which cluster it ends.
        var cluster = new List<RibbonItem>();
        var clusterIndex = 0;
        var start = 0;

        for (var i = 0; i <= items.Count; i++)
        {
            if (i < items.Count && items[i].Kind != RibbonItemKind.Separator)
            {
                cluster.Add(items[i]);
                continue;
            }

            foreach (var item in items.Skip(start).Take(i - start))
            {
                if (item.Kind == RibbonItemKind.Overflow)
                {
                    strip.Add(new SimplifiedEntry(
                        BuildClusterOverflow(tab, cluster), null, null, RibbonItem.NormalLabelRank, clusterIndex, false));
                    continue;
                }

                if (_catalog.TryGet(item.Command, out var command))
                {
                    var control = BuildSimplifiedButton(command, item, out var label);

                    // The first labelled entry is the bar's primary command, and its label goes
                    // last of all — above whatever rank the layout asked for.
                    var primary = label is not null && !primaryClaimed;
                    if (primary) primaryClaimed = true;

                    strip.Add(new SimplifiedEntry(
                        control, label, command.Id,
                        primary ? RibbonItem.PrimaryLabelRank : item.LabelRank,
                        clusterIndex, false));
                }
            }

            if (i < items.Count)
            {
                strip.Add(new SimplifiedEntry(BuildInlineSeparator(), null, null, RibbonItem.NormalLabelRank, clusterIndex, true));
            }

            cluster = [];
            clusterIndex++;
            start = i + 1;
        }

        // The "…" follows the last cluster, behind a rule, the way the reference's does: right
        // after the content when the bar has slack, at the right end when the bar is full. Its
        // menu is built when it opens, so it lists what the bar pushed off at this width on top
        // of what the row never had.
        var tail = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var tailRule = BuildInlineSeparator();
        tail.Children.Add(tailRule);

        var overflow = BuildGlyphButton("more", "More commands", 16, () => { });
        var overflowMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        overflowMenu.Opening += (_, _) => FillOverflowMenu(overflowMenu, tab, strip.Overflowed);
        overflow.Flyout = overflowMenu;
        _barOverflow = overflow;
        tail.Children.Add(overflow);

        // The panel takes what the column gives it and decides what to show; nothing scrolls
        // and nothing is clipped, which is the point of it.
        strip.HorizontalAlignment = HorizontalAlignment.Left;
        var flow = new RowWithTailPanel(strip, tail, tailRule)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(RibbonMetrics.SimplifiedRowInset, 0, RibbonMetrics.DisplayOptionsGap, 0),
        };

        return BuildPanel(flow, RibbonMetrics.SimplifiedHeight, clip: false);
    }

    /// <summary>
    /// The rounded ribbon panel around either layout's content, with the Ribbon Display Options
    /// chevron in its bottom-right corner.
    /// </summary>
    /// <remarks>
    /// The chevron is what this panel is for. It used to be part of the Simplified row alone, so
    /// choosing Classic Ribbon from its menu built a body with no chevron and no way back — the
    /// menu that switches layouts had gone with the layout it was on. Both bodies come through
    /// here now, and the collapsed ribbon's floating body with them, so the menu is at the corner
    /// of whatever ribbon is showing.
    /// <para>
    /// The panel is rounded and inset, so the chrome shows at its corners; a bottom border would
    /// cut across that curve, and the drop shadow already separates it from the workspace below.
    /// </para>
    /// </remarks>
    private Control BuildPanel(Control content, double height, bool clip)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        Grid.SetColumn(content, 0);
        grid.Children.Add(content);

        var chevron = BuildDisplayOptionsChevron();
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        // The shadow is cast by a border of its own, behind the one holding the commands, and
        // the two are the same shape and size.
        //
        // Not decoration: a BoxShadow makes its border render through an intermediate surface,
        // and above a render scale of about 1.25 that surface comes back empty — the panel's own
        // background and shadow draw and every child inside it disappears. On a display at 150%
        // or 200% that is the whole ribbon gone, which is what a sweep at
        // MAILBOX_CAPTURE_SCALE=1.5 found. Casting the shadow from a childless border keeps the
        // measured shadow and leaves the commands on a surface that renders at every scale.
        // The height and the insets stay on the wrapper, and both borders simply fill it, so the
        // panel is the same rectangle in the same place it always was.
        // No background on the caster. A shadow is drawn outside the border's shape, so the
        // caster needs the shape and not the fill — and two filled rounded rectangles stacked
        // would blend their antialiased corners into something a pixel or two off what one
        // border draws, which is exactly what the pixel gate is there to notice.
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(RibbonMetrics.BodyCornerRadius),
            BoxShadow = BoxShadows.Parse("0 1 3 0 #94000000"),
            Background = Brushes.Transparent,
        };

        var host = new Border
        {
            Child = grid,
            ClipToBounds = clip,
            CornerRadius = new CornerRadius(RibbonMetrics.BodyCornerRadius),
        };
        Bind(host, Border.BackgroundProperty, "ribbon.background.brush");

        return new Panel
        {
            Height = height,
            Margin = new Thickness(0, 0, RibbonMetrics.BodyRightInset, RibbonMetrics.BodyBottomGap),
            Children = { shadow, host },
        };
    }

    /// <summary>
    /// The chevron that opens the Ribbon Display Options menu, boxed so that flush in the panel's
    /// bottom-right corner its ink lands where the reference's does — the centre 14px in from the
    /// panel's right edge and 13px up from its bottom, measured, in both layouts. The mark itself
    /// is <see cref="ChevronMark"/>, drawn to the reference's pixels.
    /// </summary>
    private Button BuildDisplayOptionsChevron()
    {
        // At (9,10) in the 28×26 box, the mark's centre pixel is the fourteenth column from the
        // box's right edge and the thirteenth row from its bottom.
        var mark = new ChevronMark
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(9, 10, 0, 0),
        };
        Bind(mark, ChevronMark.StrokeProperty, "text.primary.brush");

        var chevron = new Button
        {
            Content = mark,
            Width = RibbonMetrics.DisplayOptionsWidth,
            Height = RibbonMetrics.DisplayOptionsHeight,
            Padding = default,
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            BorderThickness = default,
            Classes = { RibbonButtonClass },
            Flyout = BuildDisplayOptionsMenu(),
        };
        ToolTip.SetTip(chevron, Screentip("Ribbon Display Options", "Choose how much of the ribbon is shown."));

        _displayOptions = chevron;
        return chevron;
    }

    /// <summary>
    /// Decides whether a command is currently usable. Set by the host.
    /// </summary>
    /// <remarks>
    /// The reference greys most of the formatting run until there is something to format — an
    /// empty message shows a pale left half of the bar, and it darkens as soon as you type. That
    /// is enablement, not a colour choice, and reading it as a colour is how a ribbon ends up
    /// looking right in a screenshot and wrong in use.
    /// </remarks>
    public Func<CommandId, bool>? CommandEnabled
    {
        get;
        set
        {
            field = value;
            RefreshEnablement();
        }
    }

    /// <summary>
    /// Decides whether a command is currently on. Set by the host.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="CommandEnabled"/>, and a different question: whether a
    /// command <em>can</em> be run, and whether what it turns on is on. The reference draws the
    /// second as a box round the button — Work Offline while offline, Add to Favorites on a
    /// folder that is one, the arrangement the list is under — and a tick in a check box for the
    /// switches it draws as one.
    /// </remarks>
    public Func<CommandId, bool>? CommandChecked
    {
        get;
        set
        {
            field = value;
            RefreshChecked();
        }
    }

    /// <summary>
    /// Re-evaluates every drawn control against <see cref="CommandChecked"/>.
    /// </summary>
    /// <remarks>
    /// Walks the controls as <see cref="RefreshEnablement"/> does, and for the same reason: this
    /// answers a selection change, and rebuilding the bar on one would be absurd.
    /// </remarks>
    public void RefreshChecked()
    {
        var checked_ = CommandChecked;
        if (checked_ is null) return;

        foreach (var (id, controls) in _itemControls)
        {
            var on = checked_(id);
            foreach (var control in controls)
            {
                if (control is CheckBox box)
                {
                    // Set without running the handler: this is the state arriving, not a press.
                    _settingCheck = true;
                    box.IsChecked = on;
                    _settingCheck = false;
                    continue;
                }

                if (on) control.Classes.Add(CheckedClass);
                else control.Classes.Remove(CheckedClass);
            }
        }
    }

    /// <summary>True while a tick is being set from the host's state rather than by a reader.</summary>
    private bool _settingCheck;

    /// <summary>The class a button wears while what it turns on is on.</summary>
    public const string CheckedClass = "checked";

    /// <summary>The class an entry inside a gallery box wears, which is boxed differently.</summary>
    public const string GalleryEntryClass = "galleryentry";

    /// <summary>
    /// Re-evaluates every drawn control against <see cref="CommandEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Walks the controls rather than rebuilding: this runs on every keystroke in the compose
    /// body, and rebuilding the ribbon that often would be absurd.
    /// </remarks>
    public void RefreshEnablement()
    {
        var enabled = CommandEnabled;
        if (enabled is null && _disabled.Count == 0) return;

        foreach (var (id, controls) in _itemControls)
        {
            var usable = !_disabled.Contains(id) && (enabled?.Invoke(id) ?? true);
            foreach (var control in controls) control.IsEnabled = usable;
        }
    }

    /// <summary>The corner arrow that opens a cluster's dialog, drawn low as the reference does.</summary>
    private Control BuildSimplifiedLauncher(MailboxCommand command)
    {
        var glyph = new TextBlock
        {
            Text = "⇲",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
        };
        Bind(glyph, TextBlock.ForegroundProperty, "text.secondary.brush");

        var button = new Button
        {
            Content = glyph,
            Padding = new Thickness(2, 0),
            Height = RibbonMetrics.SimplifiedHeight - 12,
            VerticalAlignment = VerticalAlignment.Bottom,
            VerticalContentAlignment = VerticalAlignment.Bottom,
            BorderThickness = default,
            Classes = { RibbonButtonClass },
        };
        ToolTip.SetTip(button, Screentip(command));

        // The name a screen reader speaks. A tooltip is not one — the two travel different
        // channels — so every command button states its label here as well (§16's pass).
        Avalonia.Automation.AutomationProperties.SetName(button, command.Label);
        button.Click += (_, _) =>
            CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));

        Record(command.Id, button);
        return button;
    }

    /// <summary>The "…" ending a cluster, listing what that cluster has no room for.</summary>
    private Control BuildClusterOverflow(RibbonTab tab, IReadOnlyList<RibbonItem> cluster)
    {
        var button = BuildGlyphButton("more", "More commands", 14, () => { });
        button.Padding = new Thickness(RibbonMetrics.SimplifiedGlyphPadding, 0);
        button.Flyout = BuildClusterOverflowMenu(tab, cluster);
        return button;
    }

    /// <summary>
    /// What a cluster leaves out: the commands its classic groups place that its Simplified run
    /// does not.
    /// </summary>
    private MenuFlyout BuildClusterOverflowMenu(RibbonTab tab, IReadOnlyList<RibbonItem> cluster)
    {
        var shown = cluster
            .Where(i => i.Kind is not (RibbonItemKind.Separator or RibbonItemKind.Overflow
                or RibbonItemKind.DialogLauncher))
            .Select(i => i.Command)
            .ToHashSet();

        var hidden = tab.Groups
            .SelectMany(g => g.Items)
            .Where(i => i.Kind != RibbonItemKind.Separator && !shown.Contains(i.Command))
            .Select(i => i.Command)
            .Distinct()
            .Where(id => _catalog.TryGet(id, out _))
            .ToList();

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        if (hidden.Count == 0)
        {
            flyout.ItemsSource = new[]
            {
                new MenuItem { Header = "Nothing further in this group", IsEnabled = false },
            };
            return flyout;
        }

        flyout.ItemsSource = hidden
            .Select(id => _catalog.Get(id))
            .Select(command =>
            {
                var entry = new MenuItem { Header = command.Label };
                entry.Click += (_, _) =>
                    CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));
                return entry;
            })
            .ToList();

        return flyout;
    }

    /// <summary>
    /// The class every button on the ribbon wears.
    /// </summary>
    /// <remarks>
    /// These buttons leave their rest state to the stylesheet rather than setting it here,
    /// because a local value beats every style setter — a <c>Background</c> assigned in code is
    /// exactly why no button on this bar had a hover state at all. The class is what the
    /// hover, pressed and open rules hang off.
    /// </remarks>
    public const string RibbonButtonClass = "ribbonbutton";

    /// <summary>Forces the hover state on a command's control, for the harness, which cannot point.</summary>
    /// <remarks>
    /// A split button is a box round two buttons, and its lit state is a class on the box rather
    /// than a pseudo-class on a button — so posing it means asking the box, not the control that
    /// happens to be on top.
    /// </remarks>
    public void ForceHover(CommandId id)
    {
        if (ControlFor(id) is not { } control) return;

        if (_splitLighters.TryGetValue(control, out var light))
        {
            light(true);
            if (control is Border { Child: Panel row } && row.Children.FirstOrDefault() is Button action)
            {
                ((IPseudoClasses)action.Classes).Add(":pointerover");
            }

            return;
        }

        ((IPseudoClasses)control.Classes).Add(":pointerover");
    }

    /// <summary>How each split button's box is lit, so the harness can pose what a pointer does.</summary>
    private readonly Dictionary<Control, Action<bool>> _splitLighters = [];

    /// <summary>What Alt+Down does over each control that has a menu behind it.</summary>
    private readonly Dictionary<Control, Action> _menuOpeners = [];

    /// <summary>
    /// Opens the menu of whatever ribbon control has the focus, and says whether there was one.
    /// </summary>
    /// <remarks>
    /// Alt+Down is the reference's "open split buttons": with the keyboard on a split button it
    /// drops the chevron's menu rather than doing what the other half does, and on a drop-down it
    /// is simply the button. Every other Alt+Down in the application belongs to whoever else wants
    /// it — the caller asks this first and carries on when the answer is no.
    /// </remarks>
    public bool OpenFocusedMenu()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;

        // The focus sits on one half of a split button, so the walk upwards is what finds the
        // control the menu was registered against.
        for (var node = focused; node is not null; node = node.GetVisualParent())
        {
            if (node is Control control && _menuOpeners.TryGetValue(control, out var open))
            {
                open();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The control a command is drawn as on the bar right now, for a menu that has to hang off
    /// its own button.
    /// </summary>
    /// <remarks>
    /// One command has several controls — a group is built at all three collapse variants — so the
    /// one that matters is the one actually on screen.
    /// </remarks>
    public Control? ControlFor(CommandId id)
        => _itemControls.TryGetValue(id, out var built)
            ? built.FirstOrDefault(c => c.IsEffectivelyVisible) ?? built.FirstOrDefault()
            : null;

    /// <summary>
    /// Opens a menu under the button that asked for it: its bottom edge, its left edge, and the
    /// button held in its open state while the menu is up.
    /// </summary>
    /// <remarks>
    /// The reference lines a dropdown's menu up with the button it came from and draws a box
    /// round that button while it is open, which is how a menu says which button it belongs to.
    /// Showing at the pointer instead put the menu wherever the cursor was — and in a capture
    /// run, where there is no pointer at all, in the corner of the window.
    /// </remarks>
    public void OpenMenuUnder(CommandId id, MenuFlyout menu, Control? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(menu);

        var anchor = ControlFor(id) ?? fallback ?? this;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

        var boxed = Box(anchor);
        menu.Closed += OnClosed;
        menu.ShowAt(anchor);

        void OnClosed(object? sender, EventArgs e)
        {
            boxed?.Invoke();
            menu.Closed -= OnClosed;
        }
    }

    /// <summary>
    /// Draws the open box round a button and hands back what puts it away.
    /// </summary>
    /// <remarks>
    /// Set here rather than through a style class, because a local value beats every style setter
    /// and these buttons set their own rest state locally — a <c>.menuopen</c> rule would simply
    /// never win. The padding gives back what the border takes so the label does not step sideways
    /// as the menu opens.
    /// </remarks>
    private Action? Box(Control anchor)
    {
        if (anchor is not Button button) return null;

        var background = button.Background;
        var borderBrush = button.BorderBrush;
        var thickness = button.BorderThickness;
        var padding = button.Padding;

        if (this.TryFindResource("ribbon.button.open.brush", out var fill) && fill is IBrush face) button.Background = face;
        if (this.TryFindResource("ribbon.button.open.border.brush", out var edge) && edge is IBrush line) button.BorderBrush = line;
        button.BorderThickness = new Thickness(1);
        button.Padding = new Thickness(
            Math.Max(0, padding.Left - 1), Math.Max(0, padding.Top - 1),
            Math.Max(0, padding.Right - 1), Math.Max(0, padding.Bottom - 1));

        return () =>
        {
            button.Background = background;
            button.BorderBrush = borderBrush;
            button.BorderThickness = thickness;
            button.Padding = padding;
        };
    }

    /// <summary>Everything the layout draws greyed, gathered before anything is built.</summary>
    private void CollectDisabled()
    {
        _disabled.Clear();

        foreach (var item in Layout.Tabs.SelectMany(tab => tab.Groups).SelectMany(group => group.Items))
        {
            if (item.IsDisabled) _disabled.Add(item.Command);
        }

        foreach (var item in Layout.SimplifiedRows.Values.SelectMany(row => row))
        {
            if (item.IsDisabled) _disabled.Add(item.Command);
        }
    }

    /// <summary>One command can have several controls; Alt traversal takes whichever is shown.</summary>
    private void Record(CommandId id, Control control)
    {
        if (_disabled.Contains(id)) control.IsEnabled = false;

        if (!_itemControls.TryGetValue(id, out var built))
        {
            built = [];
            _itemControls[id] = built;
        }
        built.Add(control);
    }

    private Control BuildSimplifiedButton(MailboxCommand command, RibbonItem item)
        => BuildSimplifiedButton(command, item, out _);

    /// <param name="label">
    /// The label the row's panel may take away when the bar is short of room, or null for a
    /// control that has none to give — an icon-only button, a field, a launcher.
    /// </param>
    private Control BuildSimplifiedButton(MailboxCommand command, RibbonItem item, out TextBlock? label)
    {
        label = null;

        if (item.Kind is RibbonItemKind.TextBox or RibbonItemKind.ComboBox
            or RibbonItemKind.BoxedButton)
        {
            return BuildSimplifiedField(command, item);
        }
        if (item.Kind == RibbonItemKind.DialogLauncher) return BuildSimplifiedLauncher(command);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(BuildIcon(
            command, RibbonMetrics.SimplifiedIconSize, 20, RibbonMetrics.SimplifiedIconFontSize));

        // Icon-only is the reference's default for a formatting run — Bold, Italic, Underline
        // and the indent and list buttons carry no text at all, and labelling them turns one
        // cluster into half the bar. Honouring ShowLabel is what makes those rows fit.
        if (item.ShowLabel)
        {
            label = new TextBlock
            {
                Text = command.Label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");
            Bind(label, TextBlock.FontSizeProperty, "type.ui.size.value");
            row.Children.Add(label);
        }

        var padding = new Thickness(item.ShowLabel ? 8 : RibbonMetrics.SimplifiedGlyphPadding, 0);

        // A split button is two hit areas, so it is two buttons; a drop-down is one, so the
        // chevron rides inside it.
        if (item.Kind == RibbonItemKind.SplitButton)
        {
            return WrapAsSplitButton(row, command, item, padding, RibbonMetrics.SimplifiedButtonHeight);
        }

        if (item.Kind == RibbonItemKind.DropDown) row.Children.Add(Chevron());

        var built = WrapAsButton(row, command, padding, 0, RibbonMetrics.SimplifiedButtonHeight);
        if (item.Kind == RibbonItemKind.DropDown)
        {
            _menuOpeners[built] = () => CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));
        }

        return built;
    }

    /// <summary>
    /// The two-line screentip every control on the ribbon carries: the command's name with its
    /// shortcut in brackets, in bold, over a sentence saying what it does.
    /// </summary>
    /// <remarks>
    /// The reference gives every button one of these — "New Item (Ctrl+N)" over "Create a new
    /// item." — not a one-line title, and the shortcut in the heading is how most people ever
    /// learn one. The chord comes from the key map rather than the command's shipped default, so
    /// a rebound key shows the key it was rebound to.
    /// </remarks>
    private Control Screentip(MailboxCommand command)
    {
        var gesture = GestureLookup?.Invoke(command) ?? command.DefaultGesture;

        var heading = new TextBlock
        {
            Text = command.Label + (gesture is { Length: > 0 } chord ? $" ({chord})" : string.Empty),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        Bind(heading, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

        var tip = new StackPanel { Spacing = 2, MaxWidth = 260, Children = { heading } };

        if (command.Description is { Length: > 0 } description)
        {
            var body = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap };
            Bind(body, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
            tip.Children.Add(body);
        }

        return tip;
    }

    /// <summary>The same two-line shape for a control that stands for no single command.</summary>
    private Control Screentip(string heading, string description)
    {
        var title = new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Bind(title, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

        var body = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap };
        Bind(body, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

        return new StackPanel { Spacing = 2, MaxWidth = 260, Children = { title, body } };
    }

    /// <summary>The small arrow that says a button opens something.</summary>
    private TextBlock Chevron()
    {
        var chevron = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
            FontFamily = IconFont.Family,
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 2, 0, 0),
        };
        Bind(chevron, TextBlock.ForegroundProperty, "text.secondary.brush");
        return chevron;
    }

    /// <summary>
    /// A fixed-width field on the bar — the Font and Font Size boxes.
    /// </summary>
    /// <remarks>
    /// Drawn rather than templated from a <c>ComboBox</c>, for the same reason the zoom slider
    /// is: the reference's is a plain bordered box of an exact width with a small chevron, and a
    /// stock combo brings its own padding and minimum size that cannot be measured back down.
    /// It becomes a real picker when the editor in Phase 5 gives it something to pick.
    /// </remarks>
    private Control BuildSimplifiedField(MailboxCommand command, RibbonItem item)
    {
        // Three shapes share one box: a plain input, a picker, and a command drawn inside a
        // box. They differ in what sits in the box, not in the box.
        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        if (item.Kind == RibbonItemKind.BoxedButton)
        {
            inner.Children.Add(BuildIcon(
                command, RibbonMetrics.SimplifiedIconSize, 20, RibbonMetrics.SimplifiedIconFontSize));
        }

        var text = new TextBlock
        {
            Text = item.Kind == RibbonItemKind.BoxedButton ? command.Label : item.Text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty,
            item.Kind == RibbonItemKind.TextBox ? "text.secondary.brush" : "text.primary.brush");
        Bind(text, TextBlock.FontSizeProperty, "type.ui.size.value");
        inner.Children.Add(text);

        var content = new Panel { Children = { inner } };

        // A plain input has no chevron; it accepts rather than picks.
        if (item.Kind != RibbonItemKind.TextBox)
        {
            var chevron = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
                FontFamily = IconFont.Family,
                FontSize = 8,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Bind(chevron, TextBlock.ForegroundProperty, "text.secondary.brush");
            content.Children.Add(chevron);
        }

        var box = new Border
        {
            Width = item.Width ?? RibbonMetrics.FieldWidth,
            Height = RibbonMetrics.FieldHeight,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
        Bind(box, Border.BorderBrushProperty, "border.strong.brush");
        Bind(box, Border.BackgroundProperty, "surface.raised.brush");

        // A picker that shows its label carries its icon and name in front of the box, as the
        // appointment window's Show As and Reminder do: "▤ Show As: [ Busy ⌄ ]". The colon is
        // the renderer's, not the command's — a labelled control is how Office writes one, and
        // the same command placed as a plain button must not gain punctuation from it.
        Control face = box;
        if (item.ShowLabel && item.Kind == RibbonItemKind.ComboBox)
        {
            var caption = new TextBlock
            {
                Text = command.Label + ":",
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(caption, TextBlock.ForegroundProperty, "text.primary.brush");
            Bind(caption, TextBlock.FontSizeProperty, "type.ui.size.value");

            face = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    BuildIcon(command, RibbonMetrics.SimplifiedIconSize, 20, RibbonMetrics.SimplifiedIconFontSize),
                    caption,
                    box,
                },
            };
        }

        var button = new Button
        {
            Content = face,
            Padding = new Thickness(RibbonMetrics.FieldPadding, 0),
            BorderThickness = default,
            Classes = { RibbonButtonClass },
        };
        ToolTip.SetTip(button, Screentip(command));

        // The name a screen reader speaks. A tooltip is not one — the two travel different
        // channels — so every command button states its label here as well (§16's pass).
        Avalonia.Automation.AutomationProperties.SetName(button, command.Label);
        button.Click += (_, _) => CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));

        Record(command.Id, button);
        return button;
    }

    /// <summary>
    /// The "…" menu after the row: what the bar pushed off at this width, then what the row
    /// never had.
    /// </summary>
    /// <remarks>
    /// Filled when it opens, because the first part changes with every resize. What was pushed
    /// off comes first and in bar order, so a reader who saw a button disappear finds it where
    /// they would look; a rule separates it from the rest.
    /// </remarks>
    /// <summary>
    /// Fills the bar's "…" from <see cref="OverflowMenu.Plan"/> — what the bar pushed off at
    /// this width, then the rest of the tab under its groups. The plan decides what belongs
    /// there; this only draws it.
    /// </summary>
    private void FillOverflowMenu(MenuFlyout flyout, RibbonTab tab, IReadOnlyList<CommandId> pushedOff)
    {
        flyout.Items.Clear();

        var barItems = _layout.SimplifiedRows.TryGetValue(tab.Id, out var row) ? row : [];
        var plan = OverflowMenu.Plan(
            tab, barItems, pushedOff, [.. _catalog.BeyondDefaultLayout], id => _catalog.TryGet(id, out _));

        foreach (var entry in plan)
        {
            if (entry.IsRule)
            {
                flyout.Items.Add(new Separator());
            }
            else if (entry.IsSubmenu)
            {
                var submenu = new MenuItem { Header = entry.Label };
                foreach (var id in entry.Children) submenu.Items.Add(MenuItemFor(_catalog.Get(id)));
                flyout.Items.Add(submenu);
            }
            else if (entry.Command is { } command)
            {
                flyout.Items.Add(MenuItemFor(_catalog.Get(command)));
            }
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = OverflowMenu.EmptyLabel, IsEnabled = false });
        }
    }

    private MenuItem MenuItemFor(MailboxCommand command)
    {
        var item = new MenuItem { Header = command.Label };
        item.Click += (_, _) => CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));
        return item;
    }

    /// <summary>
    /// The Ribbon Display Options menu behind the chevron: a Ribbon Layout section choosing
    /// Classic or Simplified, then a Show Ribbon section. Ticks mark the active choice.
    /// </summary>
    private MenuFlyout BuildDisplayOptionsMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        flyout.Items.Add(SectionHeader("Ribbon Layout"));
        flyout.Items.Add(ModeItem("Classic Ribbon", RibbonDisplayMode.Classic));
        flyout.Items.Add(ModeItem("Simplified Ribbon", RibbonDisplayMode.Simplified));

        flyout.Items.Add(SectionHeader("Show Ribbon"));

        var fullScreen = new MenuItem
        {
            Header = "Full-screen mode",
            Icon = IsFullScreen ? Tick() : null,
        };
        fullScreen.Click += (_, _) => FullScreenToggled?.Invoke(this, EventArgs.Empty);
        flyout.Items.Add(fullScreen);
        flyout.Items.Add(ModeItem("Show tabs only", RibbonDisplayMode.Collapsed));

        // Back to the layout the ribbon was collapsed from, not to the default one: a reader
        // who collapsed the classic ribbon and asks for it back is asking for the classic ribbon.
        var always = new MenuItem
        {
            Header = "Always show Ribbon",
            Icon = DisplayMode != RibbonDisplayMode.Collapsed ? Tick() : null,
        };
        always.Click += (_, _) => DisplayMode = _expandedMode;
        flyout.Items.Add(always);

        var quickAccess = new MenuItem
        {
            Header = IsQuickAccessVisible
                ? "Hide Quick Access Toolbar"
                : "Show Quick Access Toolbar",
        };
        quickAccess.Click += (_, _) =>
            QuickAccessVisibilityToggled?.Invoke(this, EventArgs.Empty);
        flyout.Items.Add(quickAccess);

        return flyout;
    }

    private MenuItem ModeItem(string header, RibbonDisplayMode mode)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = DisplayMode == mode ? Tick() : null,
        };
        item.Click += (_, _) => DisplayMode = mode;
        return item;
    }

    private static Control Tick() => new TextBlock
    {
        Text = IconGlyphs.GetOrEmpty("mark-complete", 16),
        FontFamily = IconFont.Family,
        FontSize = 12,
    };

    private static Control SectionHeader(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 6, 10, 3),
        };
        Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");

        // A header rather than a command: not clickable, not highlighted on hover.
        return new MenuItem { Header = label, IsEnabled = false };
    }

    private Control BuildInlineSeparator()
    {
        // Height is set rather than derived from a margin. The rule sits in a strip only as tall
        // as its buttons — 30px — so insetting from that gave a 16px rule where the reference
        // has 32, and it is taller than the buttons it divides.
        var rule = new Border
        {
            Width = 1,
            Height = RibbonMetrics.InlineSeparatorHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(RibbonMetrics.InlineSeparatorMargin, 0),
        };
        Bind(rule, Border.BackgroundProperty, "ribbon.group.separator.brush");
        return rule;
    }

    private Button BuildGlyphButton(string icon, string tip, double size, Action onClick, string? description = null)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 16),
            FontFamily = IconFont.Family,
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "text.secondary.brush");

        var button = new Button
        {
            Content = glyph,
            Padding = new Thickness(8, 0),
            Height = RibbonMetrics.SimplifiedButtonHeight,
            BorderThickness = default,
            Classes = { RibbonButtonClass },
        };
        ToolTip.SetTip(button, Screentip(tip, description ?? "Show what the bar has no room for."));
        button.Click += (_, _) => onClick();
        return button;
    }

    private Control BuildBody(RibbonTab tab)
    {
        // Every group is built at all three sizes up front, because the panel chooses between
        // them during measure and cannot rebuild a tree from in there. RibbonGroupsPanel
        // explains why that constraint is real rather than fussiness.
        var slots = tab.Groups
            .Select(group => new RibbonGroupSlot(
                group.Id,
                group.CollapsePriority,
                BuildGroup(group, RibbonGroupVariant.Normal),
                BuildGroup(group, RibbonGroupVariant.Compact),
                BuildGroup(group, RibbonGroupVariant.Popup)))
            .ToList();

        var groups = new RibbonGroupsPanel(slots, BuildGroupSeparator)
        {
            Height = RibbonMetrics.BodyHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // Same rounded, inset panel as the Simplified row — the classic ribbon is taller, not
        // shaped differently — with the display-options chevron in the same corner. Clipped
        // because the panel is what used to sit in a ScrollViewer: without it, a group that will
        // not fit paints straight through the rounded corner.
        return BuildPanel(groups, RibbonMetrics.BodyHeight, clip: true);
    }

    private Control BuildGroupSeparator()
    {
        // Through the label row, as the reference's runs: 5 rows clear at the top and 6 at the
        // bottom of the 100, measured.
        var rule = new Border
        {
            Width = 1,
            Margin = new Thickness(0, RibbonMetrics.SeparatorTop, 0, RibbonMetrics.SeparatorBottom),
        };
        Bind(rule, Border.BackgroundProperty, "ribbon.group.separator.brush");
        return rule;
    }

    private Control BuildGroup(RibbonGroup group, RibbonGroupVariant variant)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions($"*,{RibbonMetrics.GroupLabelHeight}"),
            MinWidth = RibbonMetrics.GroupMinWidth,
            Margin = new Thickness(RibbonMetrics.GroupPaddingH, 0),
        };

        // The label strip stays whichever variant is drawn, so a collapsed group keeps its name
        // on the same baseline as its uncollapsed neighbours instead of riding higher than them.
        Control items = variant switch
        {
            RibbonGroupVariant.Popup => BuildCollapsedGroupButton(group),
            _ when group.IsGallery => BuildGallery(group),
            _ => BuildGroupItems(group, compact: variant == RibbonGroupVariant.Compact),
        };
        Grid.SetRow(items, 0);
        grid.Children.Add(items);

        // A collapsed group is only as wide as its button, so a launcher arrow in the footer
        // ends up hard against the label and reads as punctuation — "Tags," rather than "Tags".
        // It moves into the flyout, where the group is full width and it has somewhere to sit.
        var footer = BuildGroupFooter(group, withLauncher: variant != RibbonGroupVariant.Popup);
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);

        return grid;
    }

    /// <summary>
    /// A group reduced to one button — its leading command's icon over a chevron — opening the
    /// whole group as a flyout. The group's name stays in the footer beneath, so the button
    /// carries no label of its own.
    /// </summary>
    private Control BuildCollapsedGroupButton(RibbonGroup group)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };

        stack.Children.Add(BuildIcon(CollapsedGroupIcon(group), RibbonMetrics.LargeIconSize, 32));

        var chevron = new TextBlock
        {
            Text = "⌄",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -4, 0, 0),
        };
        Bind(chevron, TextBlock.ForegroundProperty, "text.secondary.brush");
        Bind(chevron, TextBlock.FontSizeProperty, "type.ui.size.small.value");
        stack.Children.Add(chevron);

        var button = new Button
        {
            Content = stack,
            Padding = new Thickness(4, 4, 4, 2),
            MinWidth = RibbonMetrics.LargeButtonMinWidth,
            Height = RibbonMetrics.ItemAreaHeight,
            BorderThickness = default,
            CornerRadius = new CornerRadius(RibbonMetrics.ButtonCornerRadius),
            Classes = { RibbonButtonClass },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, Screentip(GroupLabel(group), $"The {GroupLabel(group)} commands, which this window is too narrow to show."));

        // Built when it is first opened rather than with the button. Most collapsed groups are
        // never opened, and this tree is the same size as the one the group would have drawn.
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => flyout.Content ??= BuildCollapsedGroupContent(group);
        button.Flyout = flyout;

        // Remembered so Alt can reach what the group is now hiding.
        _collapsedGroups.Add((group, button));

        return button;
    }

    /// <summary>
    /// The group as it would have drawn had it fitted — items, label and dialog launcher — so a
    /// collapsed group loses no command, only its place on the bar.
    /// </summary>
    private Control BuildCollapsedGroupContent(RibbonGroup group)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions($"*,{RibbonMetrics.GroupLabelHeight}"),
            Height = RibbonMetrics.BodyHeight,
        };

        var items = group.IsGallery ? BuildGallery(group) : BuildGroupItems(group);
        Grid.SetRow(items, 0);
        grid.Children.Add(items);

        var footer = BuildGroupFooter(group);
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);

        var body = new Border
        {
            Padding = new Thickness(RibbonMetrics.GroupPaddingH, 2),
            Child = grid,
        };
        Bind(body, Border.BackgroundProperty, "ribbon.background.brush");
        return body;
    }

    /// <summary>
    /// The group's leading command's icon. The reference application gives each group its own
    /// artwork; ours has none in the layout document, and the first command is what that artwork
    /// almost always depicts.
    /// </summary>
    private string CollapsedGroupIcon(RibbonGroup group)
    {
        foreach (var item in group.Items)
        {
            if (item.Kind == RibbonItemKind.Separator) continue;
            if (_catalog.TryGet(item.Command, out var command)) return command.Icon;
        }

        return "more";
    }

    private static string GroupLabel(RibbonGroup group) => group.Label;

    /// <summary>
    /// Lays a group's items out the way Office does: large buttons sit side by side, and runs
    /// of small buttons pack into columns of three.
    /// </summary>
    /// <param name="compact">
    /// Demotes the group's large buttons to small ones, which packs them three to a column and
    /// is the middle rung of the collapse ladder in <see cref="RibbonCollapsePolicy"/>.
    /// </param>
    private Control BuildGroupItems(RibbonGroup group, bool compact = false)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
        };

        StackPanel? smallColumn = null;

        for (var index = 0; index < group.Items.Count; index++)
        {
            var item = group.Items[index];

            if (item.Kind == RibbonItemKind.Separator)
            {
                smallColumn = null;
                row.Children.Add(BuildGroupSeparator());
                continue;
            }

            // A run of gallery entries is one box, however many entries it holds.
            if (item.InGallery)
            {
                var run = new List<RibbonItem>();
                while (index < group.Items.Count && group.Items[index].InGallery) run.Add(group.Items[index++]);
                index--;

                smallColumn = null;
                row.Children.Add(BuildGalleryCluster(group, run));
                continue;
            }

            if (!_catalog.TryGet(item.Command, out var command)) continue;

            // A tick belongs in the small stack rather than beside it: the reference's Messages
            // group is one narrow column with Show as Conversations over Conversation Settings,
            // and a tick that started its own column made the group twice as wide.
            if (item.Kind == RibbonItemKind.CheckBox)
            {
                smallColumn = Column(smallColumn, row);
                smallColumn.Children.Add(BuildCheckBox(command, item));
                continue;
            }

            if (!compact && item.Size == RibbonItemSize.Large)
            {
                smallColumn = null;
                row.Children.Add(BuildLargeButton(command, item));
                continue;
            }

            smallColumn = Column(smallColumn, row);
            smallColumn.Children.Add(BuildSmallButton(command, item, RibbonMetrics.SmallButtonHeight));
        }

        return row;
    }

    /// <summary>
    /// The column a small control goes in: the one being filled, or a new one beside it once
    /// that is three deep.
    /// </summary>
    private static StackPanel Column(StackPanel? filling, Panel row)
    {
        if (filling is not null && filling.Children.Count < RibbonMetrics.SmallButtonsPerColumn)
        {
            return filling;
        }

        var column = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, RibbonMetrics.SmallStackTop, 0, 0),
        };
        row.Children.Add(column);
        return column;
    }

    private Button GalleryArrow(string glyph, string tip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(glyph, 16),
            FontFamily = IconFont.Family,
            FontSize = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "text.secondary.brush");

        var button = new Button { Content = text, Classes = { "flat" }, Padding = new Thickness(2, 0) };
        ToolTip.SetTip(button, Screentip(tip, "Scroll the gallery."));
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Every command in the gallery, for when scrolling to one is the slower way.</summary>
    private MenuFlyout BuildGalleryMenu(RibbonGroup group)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.ItemsSource = group.Items
            .Where(i => i.Kind != RibbonItemKind.Separator && _catalog.TryGet(i.Command, out _))
            .Select(i => _catalog.Get(i.Command))
            .Select(command =>
            {
                var item = new MenuItem { Header = command.Label };
                item.Click += (_, _) =>
                    CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));
                return item;
            })
            .ToList();
        return flyout;
    }

    /// <summary>
    /// A gallery: the group's entries stacked inside a bordered, differently-shaded box with a
    /// scroll chevron down its right edge. the reference's Quick Steps is the canonical example.
    /// </summary>
    private Control BuildGallery(RibbonGroup group)
    {
        var entries = new StackPanel { Orientation = Orientation.Vertical };

        foreach (var item in group.Items.Where(i => i.Kind != RibbonItemKind.Separator))
        {
            if (_catalog.TryGet(item.Command, out var command))
            {
                entries.Children.Add(BuildSmallButton(command, item, RibbonMetrics.GallerySlotHeight));
            }
        }

        // The entries scroll inside the box; the chevrons drive that scroller and the third
        // glyph opens the whole gallery as a menu. Drawn as glyphs they did nothing at all.
        // Three entries fill the box exactly, as the reference's Quick Steps do.
        var viewer = new ScrollViewer
        {
            Content = entries,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = RibbonMetrics.GalleryInteriorHeight,
        };

        var scroll = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0),
        };

        scroll.Children.Add(GalleryArrow("chevron-up", "Scroll up", () => viewer.LineUp()));
        scroll.Children.Add(GalleryArrow("chevron-down", "Scroll down", () => viewer.LineDown()));

        var all = GalleryArrow("more", $"All {group.Label} commands", () => { });
        all.Flyout = BuildGalleryMenu(group);
        scroll.Children.Add(all);

        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(viewer, 0);
        inner.Children.Add(viewer);
        Grid.SetColumn(scroll, 1);
        inner.Children.Add(scroll);

        // On the body's 6th row with a 1px line, so its entries' text lands on rows 15, 39 and
        // 63 as the reference's does — measured, and why there is no vertical padding.
        var box = new Border
        {
            Child = inner,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2, 0),
            Margin = new Thickness(2, RibbonMetrics.GalleryTop, 2, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        // The gallery's own pair, not the content surface's: a gallery is chrome, and in Dark
        // Gray the content pane is dark where the panel it sits in is light.
        Bind(box, Border.BorderBrushProperty, "ribbon.gallery.border.brush");
        Bind(box, Border.BackgroundProperty, "ribbon.gallery.background.brush");
        return box;
    }

    private Control BuildGroupFooter(RibbonGroup group, bool withLauncher = true)
    {
        // Top-aligned in the label row, which puts the label's baseline on the body's 93rd row
        // — where the reference's is, measured. Centred it sat two rows lower.
        var label = new TextBlock
        {
            Text = GroupLabel(group),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Bind(label, TextBlock.ForegroundProperty, "ribbon.group.label.brush");
        Bind(label, TextBlock.FontSizeProperty, "type.ui.size.small.value");

        if (group.DialogLauncher is null || !withLauncher) return label;

        // The reference puts a small arrow in the group's bottom-right corner that opens the
        // group's full options dialog. A button, not a glyph: it was drawn as text and so did
        // nothing and gave no hover, which is exactly what makes a control look broken.
        var launcher = new Button
        {
            Content = "⌄",
            Classes = { "flat" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        };
        Bind(launcher, TemplatedControl.ForegroundProperty, "ribbon.group.label.brush");
        Bind(launcher, TemplatedControl.FontSizeProperty, "type.ui.size.small.value");
        ToolTip.SetTip(launcher, Screentip($"{GroupLabel(group)} options", $"Open the full options for {GroupLabel(group)}."));

        var opens = group.DialogLauncher.Value;
        launcher.Click += (_, _) =>
            CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(opens));

        var panel = new Grid();
        panel.Children.Add(label);
        panel.Children.Add(launcher);
        return panel;
    }

    private Control BuildLargeButton(MailboxCommand command, RibbonItem item)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
        };

        stack.Children.Add(BuildIcon(command, RibbonMetrics.LargeIconSize, 32));

        // Two lines at the reference's break, or one — decided here from measured widths rather
        // than by wrapping inside a fixed width, which broke a long word in the middle and put a
        // three-word label on three lines. The button is as wide as the wider line.
        var label = new TextBlock
        {
            Text = string.Join('\n', LargeButtonLabel.Lines(command.Label, LabelWidth)),
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            LineHeight = RibbonMetrics.LargeLabelLineHeight,
        };
        Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");
        Bind(label, TextBlock.FontSizeProperty, "type.ui.size.small.value");
        stack.Children.Add(label);

        if (item.Kind is RibbonItemKind.DropDown or RibbonItemKind.SplitButton)
        {
            var chevron = new TextBlock
            {
                Text = "⌄",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 0),
            };
            Bind(chevron, TextBlock.ForegroundProperty, "text.secondary.brush");
            Bind(chevron, TextBlock.FontSizeProperty, "type.ui.size.small.value");
            stack.Children.Add(chevron);
        }

        // Top-aligned, so a one-line label's icon sits where a two-line label's does — the
        // reference's Delete and New Email icons start on the same row.
        var button = WrapAsButton(stack, command, new Thickness(4, RibbonMetrics.LargeButtonPaddingTop, 4, 2),
            RibbonMetrics.LargeButtonMinWidth, RibbonMetrics.ItemAreaHeight);
        button.VerticalContentAlignment = VerticalAlignment.Top;
        RecordMenu(button, command, item);
        return button;
    }

    /// <summary>
    /// The width a large button's label line will measure, in the ribbon's own label font, for
    /// choosing where the label breaks. Resolved at each rebuild rather than once, because the
    /// theme's resources are what name the font and the size, and a static would ask before they
    /// are there. Falls back to the default face where there is no application to ask.
    /// </summary>
    private Func<string, double> LabelWidth => _labelWidth ??= MakeLabelWidth();
    private Func<string, double>? _labelWidth;

    private static Func<string, double> MakeLabelWidth()
    {
        var app = Application.Current;
        var family = app is not null && app.TryFindResource("ui.fontfamily", out var f) && f is FontFamily found
            ? found
            : FontFamily.Default;
        var size = app is not null && app.TryFindResource("type.ui.size.small.value", out var v) && v is double d
            ? d
            : 12;
        var typeface = new Typeface(family);

        return text => new FormattedText(
            text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, null).Width;
    }

    private Control BuildSmallButton(MailboxCommand command, RibbonItem item, double height)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(BuildIcon(command, RibbonMetrics.SmallIconSize, 16));

        // The reference's icon-only stacks — Ignore, Clean Up and Junk in the Delete group — are
        // icon-only because ShowLabel says so. Drawing the label anyway made every such stack
        // three times wider than the capture.
        if (item.ShowLabel)
        {
            var label = new TextBlock
            {
                Text = command.Label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");
            Bind(label, TextBlock.FontSizeProperty, "type.ui.size.small.value");
            row.Children.Add(label);
        }

        if (item.Kind is RibbonItemKind.DropDown or RibbonItemKind.SplitButton)
        {
            var chevron = new TextBlock
            {
                Text = "⌄",
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(chevron, TextBlock.ForegroundProperty, "text.secondary.brush");
            Bind(chevron, TextBlock.FontSizeProperty, "type.ui.size.small.value");
            row.Children.Add(chevron);
        }

        var button = WrapAsButton(row, command, new Thickness(4, 0),
            RibbonMetrics.SmallButtonMinWidth, height);
        RecordMenu(button, command, item);
        return button;
    }

    /// <summary>
    /// Notes what Alt+Down does on a classic-bar button that has a menu behind it.
    /// </summary>
    /// <remarks>
    /// A split button on this bar is one hit area with a chevron drawn under the label rather than
    /// the simplified bar's two halves, so the key opens the menu the chevron stands for — which
    /// for a plain drop-down is the button's own command.
    /// </remarks>
    private void RecordMenu(Control button, MailboxCommand command, RibbonItem item)
    {
        if (item.Kind is not (RibbonItemKind.DropDown or RibbonItemKind.SplitButton)) return;

        _menuOpeners[button] = () => CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(
            item.Kind == RibbonItemKind.SplitButton ? item.ChevronCommand ?? command.Id : command.Id,
            fromChevron: item.Kind == RibbonItemKind.SplitButton));
    }

    /// <param name="fontSize">
    /// The em size to draw at. Defaults to the box — the artwork at its own size — because a
    /// fraction of the box made every icon a thumbnail of itself: the reference's large icons
    /// carry 26–28 rows of ink in their 32px box and its small ones 14 in 16, measured off the
    /// classic capture, where ours carried 18 and 9 at 0.72 of the box. The Simplified bar sets
    /// it explicitly: its glyphs are measured at 17px of ink in an 18px box, and the box stays at
    /// its measured width so the button pitch does not move while the glyph fills it.
    /// </param>
    /// <summary>
    /// A command's icon: its drawing when it has one, otherwise its glyph in its own tint.
    /// </summary>
    /// <remarks>
    /// The reference's ribbon icons are polychrome artwork. Ours are a monochrome font, so a
    /// command may name the token that tints its glyph — Reply and Reply All are magenta,
    /// Forward is blue — and the two whose meaning <em>is</em> their colours, Categorize and
    /// Follow Up, are drawn instead (<see cref="RibbonArtwork"/>).
    /// </remarks>
    private Control BuildIcon(
        MailboxCommand command, double boxSize, int artworkSize, double? fontSize = null)
    {
        if (command.IconArtwork is { Length: > 0 } drawing)
        {
            return new Border
            {
                Width = boxSize,
                Height = boxSize,
                Child = new RibbonArtwork(drawing, boxSize),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        return BuildIcon(command.Icon, boxSize, artworkSize, command.NeutralIcon, fontSize,
            command.IconTint);
    }

    private Control BuildIcon(
        string iconName, double boxSize, int artworkSize, bool neutral = false,
        double? fontSize = null, string? tint = null)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(iconName, artworkSize),
            FontFamily = IconFont.Family,
            FontSize = fontSize ?? boxSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        // The ribbon's icons are outlines, not accents. The reference draws almost all of them
        // in one dark line colour and reserves colour for the few that mean something by it —
        // an accent-blue ribbon was the single biggest thing making ours read as another
        // application. NeutralIcon is a shade darker again: the formatting run's near-black.
        Bind(glyph, TextBlock.ForegroundProperty,
            tint is { Length: > 0 } ? tint + ".brush"
            : neutral ? "text.primary.brush"
            : "ribbon.icon.outline.brush");

        return new Border
        {
            Width = boxSize,
            Height = boxSize,
            Child = glyph,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>
    /// A split button: one box, two hit areas, a divider between them.
    /// </summary>
    /// <remarks>
    /// The reference outlines a split button under the pointer and lights only the half being
    /// pointed at — which is what tells a reader the two halves do different things, because they
    /// do: New Email writes a message and its chevron opens New Items. Both halves are real
    /// buttons; the box round them carries the line, and the halves carry their own fill.
    /// <para>
    /// The border is there at rest as well, in a transparent brush, so nothing moves by a pixel
    /// when the pointer arrives.
    /// </para>
    /// </remarks>
    private Control WrapAsSplitButton(
        Control content, MailboxCommand command, RibbonItem item, Thickness padding, double height)
    {
        var inner = height - 2;

        var action = new Button
        {
            Content = content,
            Padding = padding,
            Height = inner,
            BorderThickness = default,
            CornerRadius = new CornerRadius(RibbonMetrics.ButtonCornerRadius, 0, 0, RibbonMetrics.ButtonCornerRadius),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { RibbonButtonClass },
        };

        var chevron = new Button
        {
            Content = Chevron(),
            Padding = new Thickness(RibbonMetrics.SplitChevronPadding, 0),
            Height = inner,
            BorderThickness = default,
            CornerRadius = new CornerRadius(0, RibbonMetrics.ButtonCornerRadius, RibbonMetrics.ButtonCornerRadius, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { RibbonButtonClass },
        };

        var divider = new Border { Width = 1, Margin = new Thickness(0, 3), Classes = { "ribbonsplitdivider" } };

        var box = new Border
        {
            Classes = { "ribbonsplit" },
            Height = height,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { action, divider, chevron },
            },
        };

        // The line and the divider belong to the whole control, so they answer to the pointer
        // being anywhere over it rather than to either half.
        box.PointerEntered += (_, _) => Lit(true);
        box.PointerExited += (_, _) => Lit(false);

        void Lit(bool on)
        {
            if (on)
            {
                box.Classes.Add("hovered");
                divider.Classes.Add("hovered");
            }
            else
            {
                box.Classes.Remove("hovered");
                divider.Classes.Remove("hovered");
            }
        }

        var screentip = Screentip(command);
        ToolTip.SetTip(action, screentip);
        ToolTip.SetTip(chevron, Screentip(
            item.ChevronCommand is { } other && _catalog.TryGet(other, out var second) ? second : command));

        action.Click += (_, _) => CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));
        chevron.Click += (_, _) => OpenMenu();

        void OpenMenu() => CommandInvoked?.Invoke(
            this, new RibbonCommandEventArgs(item.ChevronCommand ?? command.Id, fromChevron: true));

        _splitLighters[box] = Lit;
        foreach (var part in (Control[])[box, action, chevron]) _menuOpeners[part] = OpenMenu;
        Record(command.Id, box);
        if (CommandEnabled is { } enabled) box.IsEnabled = enabled(command.Id);

        return box;
    }

    private Button WrapAsButton(
        Control content, MailboxCommand command, Thickness padding, double minWidth, double height)
    {
        var button = new Button
        {
            Content = content,
            // A 1px line that is transparent until the command is on, with the padding short
            // by the same pixel: a button that grew when it was ticked would shove its
            // neighbours along the bar, and the reference's boxed buttons sit exactly where
            // their unboxed neighbours do.
            Padding = new Thickness(
                Math.Max(0, padding.Left - 1), Math.Max(0, padding.Top - 1),
                Math.Max(0, padding.Right - 1), Math.Max(0, padding.Bottom - 1)),
            MinWidth = minWidth,
            Height = height,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RibbonMetrics.ButtonCornerRadius),
            Classes = { RibbonButtonClass },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        ToolTip.SetTip(button, Screentip(command));

        // The name a screen reader speaks. A tooltip is not one — the two travel different
        // channels — so every command button states its label here as well (§16's pass).
        Avalonia.Automation.AutomationProperties.SetName(button, command.Label);

        button.Click += (_, _) => CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));

        // One command can have several controls — a group is built at all three collapse
        // variants — so this is a list, and Alt traversal takes whichever is on screen.
        Record(command.Id, button);

        if (CommandEnabled is { } enabled) button.IsEnabled = enabled(command.Id);
        if (CommandChecked is { } on && on(command.Id)) button.Classes.Add(CheckedClass);

        return button;
    }

    /// <summary>
    /// A tick and a label on the bar itself, for a command that is a state rather than an action.
    /// </summary>
    /// <remarks>
    /// The reference draws Show as Conversations this way, and the box is the point: a reader
    /// checking whether conversations are on looks for a tick, not for a fill behind a word.
    /// </remarks>
    private Control BuildCheckBox(MailboxCommand command, RibbonItem item)
    {
        var box = new CheckBox
        {
            Content = command.Label,
            MinHeight = RibbonMetrics.SmallButtonHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
        };
        Bind(box, TemplatedControl.FontSizeProperty, "type.ui.size.small.value");
        Bind(box, TemplatedControl.ForegroundProperty, "text.primary.brush");

        ToolTip.SetTip(box, Screentip(command));
        Avalonia.Automation.AutomationProperties.SetName(box, command.Label);

        // A press runs the command, which is what changes the state; the state then arrives
        // back through RefreshChecked. Setting the tick here as well would fight it.
        box.IsCheckedChanged += (_, _) =>
        {
            if (_settingCheck) return;
            CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));
        };

        Record(command.Id, box);
        if (CommandEnabled is { } enabled) box.IsEnabled = enabled(command.Id);
        if (CommandChecked is { } on)
        {
            _settingCheck = true;
            box.IsChecked = on(command.Id);
            _settingCheck = false;
        }

        _ = item;
        return box;
    }

    /// <summary>
    /// A run of gallery entries inside a group: one bordered box, filled left to right across
    /// <see cref="RibbonGroup.GalleryColumns"/> columns, with the More chevron down its right
    /// edge that the reference gives every gallery.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="BuildGallery"/>, which makes the <em>whole</em> group a gallery —
    /// Quick Steps, a scrolling list of three. This one is a cluster among other items: the View
    /// tab's Arrangement has Message Preview to the left of the box and three small buttons to
    /// the right of it.
    /// </remarks>
    private Control BuildGalleryCluster(RibbonGroup group, IReadOnlyList<RibbonItem> entries)
    {
        var columns = Math.Max(1, group.GalleryColumns);
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var placed = 0;
        foreach (var item in entries)
        {
            if (!_catalog.TryGet(item.Command, out var command)) continue;

            var row = placed / columns;
            if (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var button = BuildSmallButton(command, item, RibbonMetrics.GallerySlotHeight);
            button.Classes.Add(GalleryEntryClass);
            Grid.SetRow(button, row);
            Grid.SetColumn(button, placed % columns);
            grid.Children.Add(button);
            placed++;
        }

        var divider = new Border { Width = 1, Margin = new Thickness(2, 1) };
        Bind(divider, Border.BackgroundProperty, "ribbon.gallery.border.brush");

        var more = GalleryArrow("chevron-down", $"All {group.Label} commands", () =>
        {
            if (group.GalleryMore is { } opens) CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(opens));
        });
        if (group.GalleryMore is null) more.Flyout = BuildGalleryMenu(group);
        more.VerticalAlignment = VerticalAlignment.Center;

        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { grid, divider, more },
        };

        var box = new Border
        {
            Child = inner,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(1, 0),
            Margin = new Thickness(2, RibbonMetrics.GalleryTop, 2, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Bind(box, Border.BorderBrushProperty, "ribbon.gallery.border.brush");
        Bind(box, Border.BackgroundProperty, "ribbon.gallery.background.brush");
        return box;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string resourceKey)
        => target[!property] = new DynamicResourceExtension(resourceKey);
}
