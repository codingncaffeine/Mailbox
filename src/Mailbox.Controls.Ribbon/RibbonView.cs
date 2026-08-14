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
    private readonly CommandCatalog _catalog;

    // What Alt traversal adorns. Rebuilt with the visual tree, because the controls a KeyTip
    // points at are thrown away and remade whenever the tab or the display mode changes.
    private readonly List<(RibbonTab Tab, Control Control)> _tabControls = [];
    private readonly Dictionary<CommandId, List<Control>> _itemControls = [];

    private RibbonLayout _layout;
    private string _activeTabId;
    private Button? _displayOptions;

    public RibbonView(CommandCatalog catalog, RibbonLayout layout)
    {
        _catalog = catalog;
        _layout = layout;
        // File is a Backstage trigger, so the first ordinary tab is what starts selected.
        _activeTabId = layout.Tabs.FirstOrDefault(t => !t.IsBackstage)?.Id ?? string.Empty;
        Rebuild();
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
            field = value;
            Rebuild();
        }
    } = RibbonDisplayMode.Simplified;

    /// <summary>Cycles Simplified → Classic → Collapsed, as the chevron at the bar's end does.</summary>
    public void CycleDisplayMode()
        => DisplayMode = DisplayMode switch
        {
            RibbonDisplayMode.Simplified => RibbonDisplayMode.Classic,
            RibbonDisplayMode.Classic => RibbonDisplayMode.Collapsed,
            _ => RibbonDisplayMode.Simplified,
        };

    public RibbonLayout Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            if (_layout.FindTab(_activeTabId) is null)
            {
                _activeTabId = _layout.Tabs.Count > 0 ? _layout.Tabs[0].Id : string.Empty;
            }
            Rebuild();
        }
    }

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

        foreach (var tab in _layout.Tabs)
        {
            strip.Children.Add(BuildTabButton(tab));
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
        Bind(label, TextBlock.ForegroundProperty,
            selected ? "ribbon.tab.text.selected.brush" : "ribbon.tab.text.brush");
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
            if (tab.IsBackstage) BackstageRequested?.Invoke(this, EventArgs.Empty);
            else ActiveTabId = tab.Id;
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

        return targets;
    }

    /// <summary>
    /// The Simplified ribbon: one row of icon-and-label commands with vertical rules between
    /// clusters, an overflow menu, and the display-mode chevron pinned to the right.
    /// </summary>
    private Control BuildSimplifiedRow(RibbonTab tab)
    {
        var items = _layout.SimplifiedRows.TryGetValue(tab.Id, out var row) ? row : [];

        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var item in items)
        {
            if (item.Kind == RibbonItemKind.Separator)
            {
                strip.Children.Add(BuildInlineSeparator());
                continue;
            }

            if (_catalog.TryGet(item.Command, out var command))
            {
                strip.Children.Add(BuildSimplifiedButton(command, item));
            }
        }

        // Overflow, then the chevron that switches Simplified / Classic / Collapsed.
        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var overflow = BuildGlyphButton("more", "More commands", 16, () => { });
        overflow.Flyout = BuildOverflowMenu(tab);
        trailing.Children.Add(overflow);

        var chevron = BuildGlyphButton("chevron-down", "Ribbon Display Options", 14, () => { });
        chevron.HorizontalAlignment = HorizontalAlignment.Right;
        chevron.Flyout = BuildDisplayOptionsMenu();
        _displayOptions = chevron;
        trailing.Children.Add(chevron);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var scroller = new ScrollViewer
        {
            Content = strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(scroller, 0);
        grid.Children.Add(scroller);

        Grid.SetColumn(trailing, 1);
        grid.Children.Add(trailing);

        // The panel is rounded and inset, so the chrome shows at its corners; a bottom border
        // would cut across that curve, and the drop shadow already separates it from the
        // workspace below.
        var host = new Border
        {
            Height = RibbonMetrics.SimplifiedHeight,
            Padding = new Thickness(6, 0),
            Child = grid,
            CornerRadius = new CornerRadius(RibbonMetrics.BodyCornerRadius),
            BoxShadow = BoxShadows.Parse("0 1 3 0 #94000000"),
            Margin = new Thickness(0, 0, RibbonMetrics.BodyRightInset, RibbonMetrics.BodyBottomGap),
        };
        Bind(host, Border.BackgroundProperty, "ribbon.background.brush");
        return host;
    }

    private Control BuildSimplifiedButton(MailboxCommand command, RibbonItem item)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(BuildIcon(command.Icon, RibbonMetrics.SimplifiedIconSize, 20));

        var label = new TextBlock
        {
            Text = command.Label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");
        Bind(label, TextBlock.FontSizeProperty, "type.ui.size.value");
        row.Children.Add(label);

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

        return WrapAsButton(row, command, new Thickness(8, 0),
            0, RibbonMetrics.SimplifiedButtonHeight);
    }

    /// <summary>
    /// The Ribbon Display Options menu behind the chevron: a Ribbon Layout section choosing
    /// Classic or Simplified, then a Show Ribbon section. Ticks mark the active choice.
    /// </summary>
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

    private MenuFlyout BuildDisplayOptionsMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        flyout.Items.Add(SectionHeader("Ribbon Layout"));
        flyout.Items.Add(ModeItem("Classic Ribbon", RibbonDisplayMode.Classic));
        flyout.Items.Add(ModeItem("Simplified Ribbon", RibbonDisplayMode.Simplified));

        flyout.Items.Add(SectionHeader("Show Ribbon"));
        flyout.Items.Add(new MenuItem { Header = "Full-screen mode" });
        flyout.Items.Add(ModeItem("Show tabs only", RibbonDisplayMode.Collapsed));

        var always = new MenuItem
        {
            Header = "Always show Ribbon",
            Icon = DisplayMode != RibbonDisplayMode.Collapsed ? Tick() : null,
        };
        always.Click += (_, _) => DisplayMode = RibbonDisplayMode.Simplified;
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
        var rule = new Border
        {
            Width = 1,
            Margin = new Thickness(5, 7),
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
        // shaped differently. Clipped because the panel is what used to sit in a ScrollViewer:
        // without it, a group that will not fit paints straight through the rounded corner.
        var host = new Border
        {
            Height = RibbonMetrics.BodyHeight,
            Child = groups,
            ClipToBounds = true,
            CornerRadius = new CornerRadius(RibbonMetrics.BodyCornerRadius),
            BoxShadow = BoxShadows.Parse("0 1 3 0 #94000000"),
            Margin = new Thickness(0, 0, RibbonMetrics.BodyRightInset, RibbonMetrics.BodyBottomGap),
        };
        Bind(host, Border.BackgroundProperty, "ribbon.background.brush");
        return host;
    }

    private Control BuildGroupSeparator()
    {
        var rule = new Border
        {
            Width = 1,
            Margin = new Thickness(0, RibbonMetrics.SeparatorMargin,
                                   0, RibbonMetrics.GroupLabelHeight + RibbonMetrics.SeparatorMargin),
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

        stack.Children.Add(BuildIcon(CollapsedGroupIcon(group), RibbonMetrics.LargeIconSize, 24));

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

    private static string GroupLabel(RibbonGroup group)
        => group.Label.Replace("&amp;", "&", StringComparison.Ordinal);

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
                };
                row.Children.Add(smallColumn);
            }

            smallColumn.Children.Add(BuildSmallButton(command, item));
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
                entries.Children.Add(BuildSmallButton(command, item));
            }
        }

        // The entries scroll inside the box; the chevrons drive that scroller and the third
        // glyph opens the whole gallery as a menu. Drawn as glyphs they did nothing at all.
        var viewer = new ScrollViewer
        {
            Content = entries,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = RibbonMetrics.BodyHeight - 28,
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

        var box = new Border
        {
            Child = inner,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2),
            Margin = new Thickness(2, 2, 2, 4),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Bind(box, Border.BorderBrushProperty, "border.subtle.brush");
        Bind(box, Border.BackgroundProperty, "surface.sunken.brush");
        return box;
    }

    private Control BuildGroupFooter(RibbonGroup group, bool withLauncher = true)
    {
        var label = new TextBlock
        {
            Text = GroupLabel(group),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
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

        stack.Children.Add(BuildIcon(command.Icon, RibbonMetrics.LargeIconSize, 24));

        var label = new TextBlock
        {
            Text = command.Label,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = RibbonMetrics.LargeButtonMaxWidth - 8,
            LineHeight = 13,
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

        return WrapAsButton(stack, command, new Thickness(4, 4, 4, 2),
            RibbonMetrics.LargeButtonMinWidth, RibbonMetrics.ItemAreaHeight);
    }

    private Control BuildSmallButton(MailboxCommand command, RibbonItem item)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(BuildIcon(command.Icon, RibbonMetrics.SmallIconSize, 16));

        var label = new TextBlock
        {
            Text = command.Label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "text.primary.brush");
        Bind(label, TextBlock.FontSizeProperty, "type.ui.size.small.value");
        row.Children.Add(label);

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
            RibbonMetrics.SmallButtonMinWidth, RibbonMetrics.SmallButtonHeight);
    }

    private Control BuildIcon(string iconName, double boxSize, int artworkSize)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(iconName, artworkSize),
            FontFamily = IconFont.Family,
            FontSize = boxSize * 0.72,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");

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
            Text = command.Label + (command.DefaultGesture is { } g ? $"  ({g})" : string.Empty),
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
        if (!_itemControls.TryGetValue(command.Id, out var built))
        {
            built = [];
            _itemControls[command.Id] = built;
        }
        built.Add(button);

        return button;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string resourceKey)
        => target[!property] = new DynamicResourceExtension(resourceKey);
}
