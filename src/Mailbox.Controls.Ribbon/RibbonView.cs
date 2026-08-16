using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Theming.Icons;

namespace Mailbox.Controls.Ribbon;

/// <summary>Raised when a ribbon control is activated.</summary>
public sealed class RibbonCommandEventArgs(CommandId command) : EventArgs
{
    public CommandId Command { get; } = command;
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
        }
    }

    // ----------------------------------------------------------------------------------
    // Composition
    // ----------------------------------------------------------------------------------

    private void Rebuild()
    {
        _tabControls.Clear();
        _itemControls.Clear();
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
                        BuildClusterOverflow(tab, cluster), null, null, false, clusterIndex, false));
                    continue;
                }

                if (_catalog.TryGet(item.Command, out var command))
                {
                    var control = BuildSimplifiedButton(command, item, out var label);
                    var primary = label is not null && !primaryClaimed;
                    if (primary) primaryClaimed = true;

                    strip.Add(new SimplifiedEntry(control, label, command.Id, primary, clusterIndex, false));
                }
            }

            if (i < items.Count)
            {
                strip.Add(new SimplifiedEntry(BuildInlineSeparator(), null, null, false, clusterIndex, true));
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

        var host = new Border
        {
            Height = height,
            Child = grid,
            ClipToBounds = clip,
            CornerRadius = new CornerRadius(RibbonMetrics.BodyCornerRadius),
            BoxShadow = BoxShadows.Parse("0 1 3 0 #94000000"),
            Margin = new Thickness(0, 0, RibbonMetrics.BodyRightInset, RibbonMetrics.BodyBottomGap),
        };
        Bind(host, Border.BackgroundProperty, "ribbon.background.brush");
        return host;
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
            Background = Brushes.Transparent,
            Flyout = BuildDisplayOptionsMenu(),
        };
        ToolTip.SetTip(chevron, "Ribbon Display Options");

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
    /// Re-evaluates every drawn control against <see cref="CommandEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Walks the controls rather than rebuilding: this runs on every keystroke in the compose
    /// body, and rebuilding the ribbon that often would be absurd.
    /// </remarks>
    public void RefreshEnablement()
    {
        if (CommandEnabled is not { } enabled) return;

        foreach (var (id, controls) in _itemControls)
        {
            var usable = enabled(id);
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
            Background = Brushes.Transparent,
        };
        ToolTip.SetTip(button, command.Label);
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

    /// <summary>One command can have several controls; Alt traversal takes whichever is shown.</summary>
    private void Record(CommandId id, Control control)
    {
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
            command.Icon, RibbonMetrics.SimplifiedIconSize, 20, command.NeutralIcon,
            RibbonMetrics.SimplifiedIconFontSize));

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

        if (item.Kind is RibbonItemKind.DropDown or RibbonItemKind.SplitButton)
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
            row.Children.Add(chevron);
        }

        return WrapAsButton(row, command,
            new Thickness(item.ShowLabel ? 8 : RibbonMetrics.SimplifiedGlyphPadding, 0),
            0, RibbonMetrics.SimplifiedButtonHeight);
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
                command.Icon, RibbonMetrics.SimplifiedIconSize, 20, command.NeutralIcon,
                RibbonMetrics.SimplifiedIconFontSize));
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

        var button = new Button
        {
            Content = box,
            Padding = new Thickness(RibbonMetrics.FieldPadding, 0),
            BorderThickness = default,
            Background = Brushes.Transparent,
        };
        ToolTip.SetTip(button, command.Label);
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
    private void FillOverflowMenu(MenuFlyout flyout, RibbonTab tab, IReadOnlyList<CommandId> pushedOff)
    {
        flyout.Items.Clear();

        foreach (var id in pushedOff)
        {
            if (!_catalog.TryGet(id, out var command)) continue;
            flyout.Items.Add(MenuItemFor(command));
        }

        if (pushedOff.Count > 0) flyout.Items.Add(new Separator());

        var rest = BuildOverflowMenu(tab);
        if (rest.ItemsSource is IEnumerable<object> items)
        {
            foreach (var item in items) flyout.Items.Add(item);
        }
    }

    /// <summary>
    /// The commands this tab owns that its row has no room for, plus everything the default
    /// layout leaves out entirely. Empty is a real answer — a tab whose row already shows
    /// everything gets a disabled note rather than a menu that opens onto nothing.
    /// </summary>
    private MenuFlyout BuildOverflowMenu(RibbonTab tab)
    {
        var shown = (_layout.SimplifiedRows.TryGetValue(tab.Id, out var row) ? row : [])
            .Where(i => i.Kind != RibbonItemKind.Separator)
            .Select(i => i.Command)
            .ToHashSet();

        var hidden = tab.Groups
            .SelectMany(g => g.Items)
            .Where(i => i.Kind != RibbonItemKind.Separator && !shown.Contains(i.Command))
            .Select(i => i.Command)
            .Distinct()
            .Concat(_catalog.BeyondDefaultLayout.Select(c => c.Id))
            .Distinct()
            .Where(id => _catalog.TryGet(id, out _))
            .ToList();

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        if (hidden.Count == 0)
        {
            flyout.ItemsSource = new[]
            {
                new MenuItem { Header = "Nothing further on this tab", IsEnabled = false },
            };
            return flyout;
        }

        flyout.ItemsSource = hidden
            .Select(id => _catalog.Get(id))
            .OrderBy(c => c.Label, StringComparer.CurrentCulture)
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

    /// <summary>A menu entry that runs a command, as every entry on the bar's menus does.</summary>
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
        flyout.Items.Add(new MenuItem { Header = "Full-screen mode" });
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

    private Button BuildGlyphButton(string icon, string tip, double size, Action onClick)
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
            Background = Brushes.Transparent,
        };
        ToolTip.SetTip(button, tip);
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
            CornerRadius = new CornerRadius(2),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, GroupLabel(group));

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

        foreach (var item in group.Items)
        {
            if (item.Kind == RibbonItemKind.Separator)
            {
                smallColumn = null;
                row.Children.Add(BuildGroupSeparator());
                continue;
            }

            if (!_catalog.TryGet(item.Command, out var command)) continue;

            if (!compact && item.Size == RibbonItemSize.Large)
            {
                smallColumn = null;
                row.Children.Add(BuildLargeButton(command, item));
                continue;
            }

            if (smallColumn is null || smallColumn.Children.Count >= RibbonMetrics.SmallButtonsPerColumn)
            {
                smallColumn = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, RibbonMetrics.SmallStackTop, 0, 0),
                };
                row.Children.Add(smallColumn);
            }

            smallColumn.Children.Add(BuildSmallButton(command, item, RibbonMetrics.SmallButtonHeight));
        }

        return row;
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
        ToolTip.SetTip(button, tip);
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
        ToolTip.SetTip(launcher, $"{group.Label} options");

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

        stack.Children.Add(BuildIcon(
            command.Icon, RibbonMetrics.LargeIconSize, 32, command.NeutralIcon));

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

        row.Children.Add(BuildIcon(
            command.Icon, RibbonMetrics.SmallIconSize, 16, command.NeutralIcon));

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

        return WrapAsButton(row, command, new Thickness(4, 0),
            RibbonMetrics.SmallButtonMinWidth, height);
    }

    /// <param name="fontSize">
    /// The em size to draw at. Defaults to the box — the artwork at its own size — because a
    /// fraction of the box made every icon a thumbnail of itself: the reference's large icons
    /// carry 26–28 rows of ink in their 32px box and its small ones 14 in 16, measured off the
    /// classic capture, where ours carried 18 and 9 at 0.72 of the box. The Simplified bar sets
    /// it explicitly: its glyphs are measured at 17px of ink in an 18px box, and the box stays at
    /// its measured width so the button pitch does not move while the glyph fills it.
    /// </param>
    private Control BuildIcon(
        string iconName, double boxSize, int artworkSize, bool neutral = false,
        double? fontSize = null)
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
        Bind(glyph, TextBlock.ForegroundProperty,
            neutral ? "text.primary.brush" : "accent.rest.brush");

        return new Border
        {
            Width = boxSize,
            Height = boxSize,
            Child = glyph,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    private Button WrapAsButton(
        Control content, MailboxCommand command, Thickness padding, double minWidth, double height)
    {
        var button = new Button
        {
            Content = content,
            Padding = padding,
            MinWidth = minWidth,
            Height = height,
            BorderThickness = default,
            CornerRadius = new CornerRadius(2),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        // Two-line screentip: bold heading over a description, as Office does — not a
        // one-line tooltip.
        var tip = new StackPanel { Spacing = 2, MaxWidth = 260 };
        tip.Children.Add(new TextBlock
        {
            Text = command.Label + ((GestureLookup?.Invoke(command) ?? command.DefaultGesture) is { } g ? $"  ({g})" : string.Empty),
            FontWeight = FontWeight.SemiBold,
        });
        tip.Children.Add(new TextBlock
        {
            Text = command.Description,
            TextWrapping = TextWrapping.Wrap,
        });
        ToolTip.SetTip(button, tip);

        button.Click += (_, _) => CommandInvoked?.Invoke(this, new RibbonCommandEventArgs(command.Id));

        // One command can have several controls — a group is built at all three collapse
        // variants — so this is a list, and Alt traversal takes whichever is on screen.
        Record(command.Id, button);

        if (CommandEnabled is { } enabled) button.IsEnabled = enabled(command.Id);

        return button;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string resourceKey)
        => target[!property] = new DynamicResourceExtension(resourceKey);
}
