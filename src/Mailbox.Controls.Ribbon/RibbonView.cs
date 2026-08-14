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
    private RibbonLayout _layout;
    private string _activeTabId;

    public RibbonView(CommandCatalog catalog, RibbonLayout layout)
    {
        _catalog = catalog;
        _layout = layout;
        // File is a Backstage trigger, so the first ordinary tab is what starts selected.
        _activeTabId = layout.Tabs.FirstOrDefault(t => !t.IsBackstage)?.Id ?? string.Empty;
        Rebuild();
    }

    public event EventHandler<RibbonCommandEventArgs>? CommandInvoked;

    /// <summary>Raised when the File tab is clicked. The shell opens the Backstage.</summary>
    public event EventHandler? BackstageRequested;

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
        return button;
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
        trailing.Children.Add(BuildGlyphButton("more", "More commands", 16, () => { }));

        var chevron = BuildGlyphButton("chevron-down", "Ribbon Display Options", 14, () => { });
        chevron.HorizontalAlignment = HorizontalAlignment.Right;
        chevron.Flyout = BuildDisplayOptionsMenu();
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

        var host = new Border
        {
            Height = RibbonMetrics.SimplifiedHeight,
            Padding = new Thickness(6, 0),
            Child = grid,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        Bind(host, Border.BackgroundProperty, "ribbon.background.brush");
        Bind(host, Border.BorderBrushProperty, "border.subtle.brush");
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

        flyout.Items.Add(new MenuItem { Header = "Hide Quick Access Toolbar" });
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
        var groups = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = RibbonMetrics.BodyHeight,
        };

        for (var i = 0; i < tab.Groups.Count; i++)
        {
            groups.Children.Add(BuildGroup(tab.Groups[i]));

            if (i < tab.Groups.Count - 1)
            {
                groups.Children.Add(BuildGroupSeparator());
            }
        }

        // Hidden, not Auto: a visible scrollbar steals height from the group labels. the reference application
        // never scrolls the ribbon — it collapses the lowest-priority groups to popup buttons
        // instead. That variant sizing is modelled in RibbonGroup.CollapsePriority and is the
        // proper fix; scrolling is the interim behaviour until it is implemented.
        var scroller = new ScrollViewer
        {
            Content = groups,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var host = new Border
        {
            Height = RibbonMetrics.BodyHeight,
            Child = scroller,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        Bind(host, Border.BackgroundProperty, "ribbon.background.brush");
        Bind(host, Border.BorderBrushProperty, "border.subtle.brush");
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

    private Control BuildGroup(RibbonGroup group)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions($"*,{RibbonMetrics.GroupLabelHeight}"),
            MinWidth = RibbonMetrics.GroupMinWidth,
            Margin = new Thickness(RibbonMetrics.GroupPaddingH, 0),
        };

        var items = group.IsGallery ? BuildGallery(group) : BuildGroupItems(group);
        Grid.SetRow(items, 0);
        grid.Children.Add(items);

        var footer = BuildGroupFooter(group);
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);

        return grid;
    }

    /// <summary>
    /// Lays a group's items out the way Office does: large buttons sit side by side, and runs
    /// of small buttons pack into columns of three.
    /// </summary>
    private Control BuildGroupItems(RibbonGroup group)
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

            if (item.Size == RibbonItemSize.Large)
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

        var scroll = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0),
        };
        foreach (var glyph in (string[])["chevron-up", "chevron-down", "more"])
        {
            var arrow = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty(glyph, 16),
                FontFamily = IconFont.Family,
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Bind(arrow, TextBlock.ForegroundProperty, "text.secondary.brush");
            scroll.Children.Add(arrow);
        }

        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(entries, 0);
        inner.Children.Add(entries);
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

    private Control BuildGroupFooter(RibbonGroup group)
    {
        var label = new TextBlock
        {
            Text = group.Label.Replace("&amp;", "&", StringComparison.Ordinal),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "ribbon.group.label.brush");
        Bind(label, TextBlock.FontSizeProperty, "type.ui.size.small.value");

        if (group.DialogLauncher is null) return label;

        // the reference application puts a small arrow in the group's bottom-right corner that opens its
        // full options dialog.
        var launcher = new TextBlock
        {
            Text = "⌄",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        };
        Bind(launcher, TextBlock.ForegroundProperty, "ribbon.group.label.brush");
        Bind(launcher, TextBlock.FontSizeProperty, "type.ui.size.small.value");

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
        return button;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string resourceKey)
        => target[!property] = new DynamicResourceExtension(resourceKey);
}
