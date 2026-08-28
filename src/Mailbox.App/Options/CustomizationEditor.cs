using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Options;

/// <summary>One row in the gallery on the left: a command, or the separator entry.</summary>
public sealed record GalleryEntry(string Label, string Icon, CommandId? Command)
{
    public bool IsSeparator => Command is null;

    public static GalleryEntry Separator { get; } = new("<Separator>", string.Empty, null);

    public static GalleryEntry For(MailboxCommand command)
        => new(command.Label, command.Icon, command.Id);
}

/// <summary>Which commands the gallery offers.</summary>
public enum GallerySource
{
    /// <summary>The short curated list the reference opens on.</summary>
    Popular,

    /// <summary>Everything the shipped layout does not place — where the additions live.</summary>
    NotPlaced,

    /// <summary>The whole catalogue, alphabetical.</summary>
    All,
}

/// <summary>
/// The two-pane customization editor: a gallery of commands on the left, what is placed on the
/// right, and the buttons that move things between them.
/// </summary>
/// <remarks>
/// Customize Ribbon and the Quick Access Toolbar page are the same editor over different
/// targets — the reference draws them identically down to the button positions, and the only
/// real difference is that one target is a tree and the other a list. The scaffold is here; the
/// right-hand pane and the footer under it are what a subclass supplies.
/// </remarks>
public abstract class CustomizationEditor : UserControl
{
    /// <summary>Measured from the capture: the two panes are 286 and 287 wide.</summary>
    private const double ReorderColumnWidth = 24;

    private readonly CommandCatalog _catalog;
    private readonly ComboBox _source = new();
    private readonly ListBox _gallery = new();

    private Button _add = null!;
    private Button _remove = null!;
    private Button _up = null!;
    private Button _down = null!;

    protected CustomizationEditor(CommandCatalog catalog)
    {
        _catalog = catalog;
    }

    protected CommandCatalog Catalog => _catalog;

    /// <summary>The gallery entry to place, or null when nothing is selected.</summary>
    protected GalleryEntry? Selected => _gallery.SelectedItem as GalleryEntry;

    /// <summary>Whether the gallery offers the separator entry. Only the toolbar takes one.</summary>
    protected virtual bool OffersSeparator => false;

    /// <summary>Raised whenever an edit lands, so the host can rebuild what it renders.</summary>
    public event EventHandler? Edited;

    protected void RaiseEdited() => Edited?.Invoke(this, EventArgs.Empty);

    // ---- What a subclass supplies ----------------------------------------------------------

    /// <summary>The label above the right-hand pane. "Customize the Single Line Ribbon:".</summary>
    protected abstract string TargetHeading { get; }

    /// <summary>The right-hand pane itself.</summary>
    protected abstract Control BuildTarget();

    /// <summary>
    /// What sits above it. The heading alone unless the pane has something to choose from, in
    /// which case a subclass adds the picker under it, as the gallery has.
    /// </summary>
    protected virtual Control BuildTargetHeader()
        => new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { Heading(TargetHeading, info: true) },
        };

    /// <summary>Under the gallery. The toolbar page puts its three settings here.</summary>
    protected virtual Control? BuildGalleryFooter() => null;

    /// <summary>Under the right-hand pane, above the Customizations row.</summary>
    protected virtual Control? BuildTargetFooter() => null;

    protected abstract void OnAdd(GalleryEntry entry);

    protected abstract void OnRemove();

    protected abstract void OnMove(int delta);

    /// <summary>Puts everything back the way it shipped.</summary>
    protected abstract void OnReset(bool selectedTabOnly);

    protected abstract void OnImport(string path);

    protected abstract void OnExport(string path);

    /// <summary>Re-evaluates which of the four move buttons can do anything.</summary>
    protected void RefreshButtons()
    {
        _add.IsEnabled = Selected is not null && CanAdd;
        _remove.IsEnabled = CanRemove;
        _up.IsEnabled = CanMove(-1);
        _down.IsEnabled = CanMove(1);
    }

    protected abstract bool CanAdd { get; }

    protected abstract bool CanRemove { get; }

    protected abstract bool CanMove(int delta);

    /// <summary>True where "Reset only this tab" means something. The toolbar has no tabs.</summary>
    protected virtual bool HasPerTabReset => false;

    // ---- The scaffold ----------------------------------------------------------------------

    /// <summary>
    /// Builds the editor. Called by the subclass once its own state is ready, rather than from
    /// this constructor — the target pane is built during this, and a subclass field assigned
    /// after a base constructor runs would still be null when it was needed.
    /// </summary>
    protected void Build()
    {
        // The four movement buttons exist before anything is placed, because building the
        // target pane fills it, and filling it asks which of them can currently do anything.
        CreateMoveButtons();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };

        Place(grid, GalleryHeading(), 0, 0);
        Place(grid, BuildTargetHeader(), 0, 2);

        Place(grid, BuildGallery(), 1, 0);
        Place(grid, BuildMoveButtons(), 1, 1);
        Place(grid, BuildTarget(), 1, 2);
        Place(grid, BuildReorderButtons(), 1, 3);

        Place(grid, BuildGalleryFooter() ?? new Panel(), 2, 0);
        Place(grid, BuildCustomizations(), 2, 2);

        Content = grid;
        RefreshGallery();
        RefreshButtons();
    }

    private static void Place(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private Control GalleryHeading()
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(Heading("Choose commands from:", info: true));

        _source.ItemsSource = new List<string>
        {
            "Popular Commands", "Commands Not in the Ribbon", "All Commands",
        };
        _source.SelectedIndex = 0;
        _source.HorizontalAlignment = HorizontalAlignment.Stretch;
        _source.SelectionChanged += (_, _) => RefreshGallery();
        stack.Children.Add(_source);

        return stack;
    }

    private Control BuildGallery()
    {
        _gallery.ItemTemplate = new FuncDataTemplate<GalleryEntry>((entry, _) => entry is null ? new Control() : GalleryRow(entry));
        _gallery.SelectionChanged += (_, _) => RefreshButtons();
        _gallery.DoubleTapped += (_, _) =>
        {
            if (Selected is { } entry && CanAdd) Add(entry);
        };

        Plain(_gallery);
        return Box(_gallery, right: 10);
    }

    /// <summary>
    /// Strips a list of its own chrome so the box around it is the only frame.
    /// </summary>
    /// <remarks>
    /// The control theme gives a ListBox a fill and a border of its own, which drew a second
    /// frame inside the pane and a horizontal scrollbar the reference does not have — a long
    /// command name is clipped there, not scrolled to.
    /// </remarks>
    protected static void Plain(ListBox list)
    {
        list.Background = null;
        list.BorderThickness = default;
        list.Padding = default;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
    }

    private Control GalleryRow(GalleryEntry entry)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(2, 1),
        };

        if (entry.IsSeparator)
        {
            var rule = new TextBlock { Text = entry.Label, VerticalAlignment = VerticalAlignment.Center };
            Bind(rule, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            row.Children.Add(rule);
            return row;
        }

        row.Children.Add(Glyph(entry.Icon));

        var label = new TextBlock { Text = entry.Label, VerticalAlignment = VerticalAlignment.Center };
        Bind(label, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        row.Children.Add(label);

        return row;
    }

    protected Control Glyph(string icon, double size = 13)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(icon, 16),
            FontFamily = IconFont.Family,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 16,
        };
        Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
        return glyph;
    }

    private void RefreshGallery()
    {
        var source = _source.SelectedIndex switch
        {
            1 => GallerySource.NotPlaced,
            2 => GallerySource.All,
            _ => GallerySource.Popular,
        };

        var entries = new List<GalleryEntry>();
        if (OffersSeparator) entries.Add(GalleryEntry.Separator);
        entries.AddRange(Commands(source).Select(GalleryEntry.For));

        _gallery.ItemsSource = entries;
        RefreshButtons();
    }

    /// <summary>
    /// What each source offers.
    /// </summary>
    /// <remarks>
    /// "Commands Not in the Ribbon" is the interesting one: it is where every addition beyond
    /// parity lives — Snooze, View Source, the tracker report — present in the catalogue since
    /// the first run and reachable here without the shipped ribbon ever having shown them.
    /// </remarks>
    private IEnumerable<MailboxCommand> Commands(GallerySource source)
    {
        var placed = DefaultRibbonLayouts.Mail.PlacedCommands.ToHashSet();

        var commands = source switch
        {
            GallerySource.Popular => DefaultRibbonLayouts.QuickAccessCandidates
                .Select(id => _catalog.TryGet(id, out var command) ? command : null)
                .Where(command => command is not null)
                .Select(command => command!),

            GallerySource.NotPlaced => _catalog.All
                .Where(command => !placed.Contains(command.Id))
                .OrderBy(command => command.Label, StringComparer.CurrentCultureIgnoreCase),

            _ => _catalog.All
                .OrderBy(command => command.Label, StringComparer.CurrentCultureIgnoreCase),
        };

        // Compose-window commands act on a document that is not open here, so offering them
        // would place buttons that can never do anything.
        return commands.Where(command => (command.Scope & MailboxModule.Mail.AsScope()) != 0);
    }

    private void CreateMoveButtons()
    {
        _add = MiddleButton("Add >>");
        _add.Click += (_, _) =>
        {
            if (Selected is { } entry) Add(entry);
        };

        _remove = MiddleButton("<< Remove");
        _remove.Click += (_, _) =>
        {
            OnRemove();
            RaiseEdited();
            RefreshButtons();
        };

        _up = ReorderButton("chevron-up", "Move Up", -1);
        _down = ReorderButton("chevron-down", "Move Down", 1);
    }

    private Control BuildMoveButtons()
    {
        var stack = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        stack.Children.Add(_add);
        stack.Children.Add(_remove);
        return stack;
    }

    private void Add(GalleryEntry entry)
    {
        OnAdd(entry);
        RaiseEdited();
        RefreshButtons();
    }

    private Control BuildReorderButtons()
    {
        var stack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        stack.Children.Add(_up);
        stack.Children.Add(_down);
        return stack;
    }

    private Button ReorderButton(string icon, string tip, int delta)
    {
        var glyph = Glyph(icon, 11);
        Bind(glyph, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var button = new Button
        {
            Content = glyph,
            Width = ReorderColumnWidth,
            Height = 22,
            Padding = default,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            [ToolTip.TipProperty] = tip,
        };
        Bind(button, BorderBrushProperty, "dialog.border.brush");
        Bind(button, BackgroundProperty, "dialog.surface.brush");

        button.Click += (_, _) =>
        {
            OnMove(delta);
            RaiseEdited();
            RefreshButtons();
        };

        return button;
    }

    /// <summary>The Reset and Import/Export pair the reference puts under both panes.</summary>
    private Control BuildCustomizations()
    {
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };

        if (BuildTargetFooter() is { } footer) stack.Children.Add(footer);

        var resetRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var label = new TextBlock
        {
            Text = "Customizations:",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(label, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        resetRow.Children.Add(label);
        resetRow.Children.Add(ResetButton());
        stack.Children.Add(resetRow);

        var exportRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        exportRow.Children.Add(ImportExportButton());
        stack.Children.Add(exportRow);

        return stack;
    }

    private Control ResetButton()
    {
        var button = MenuButton("Reset");
        var flyout = new MenuFlyout();

        if (HasPerTabReset)
        {
            var one = new MenuItem { Header = "Reset only selected Ribbon tab" };
            one.Click += (_, _) =>
            {
                OnReset(selectedTabOnly: true);
                RaiseEdited();
                RefreshButtons();
            };
            flyout.Items.Add(one);
        }

        var all = new MenuItem { Header = "Reset all customizations" };
        all.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window window) return;

            // Reset throws away work that cannot be got back, so it asks first. The reference
            // asks too, and this is the one button on the page that is not undoable.
            var confirmed = await Views.Confirm.AskAsync(
                window,
                "Reset customizations",
                "This removes every ribbon and toolbar customization and puts both back the way "
                + "they shipped. It cannot be undone.",
                "Reset");

            if (!confirmed) return;

            OnReset(selectedTabOnly: false);
            RaiseEdited();
            RefreshButtons();
        };
        flyout.Items.Add(all);

        button.Flyout = flyout;
        return button;
    }

    private Control ImportExportButton()
    {
        var button = MenuButton("Import/Export");
        var flyout = new MenuFlyout();

        var import = new MenuItem { Header = "Import customization file" };
        import.Click += async (_, _) => await ImportAsync();
        flyout.Items.Add(import);

        var export = new MenuItem { Header = "Export all customizations" };
        export.Click += async (_, _) => await ExportAsync();
        flyout.Items.Add(export);

        button.Flyout = flyout;
        return button;
    }

    private async Task ImportAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Import customization file",
            AllowMultiple = false,
            FileTypeFilter = [CustomizationFiles],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        OnImport(path);
        RaiseEdited();
        RefreshButtons();
    }

    private async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Export all customizations",
            SuggestedFileName = "mailbox-customizations.json",
            DefaultExtension = "json",
            FileTypeChoices = [CustomizationFiles],
        });

        if (file?.TryGetLocalPath() is not { } path) return;
        OnExport(path);
    }

    private static FilePickerFileType CustomizationFiles { get; } =
        new("Mailbox customizations")
        {
            Patterns = ["*.json"],
            MimeTypes = ["application/json"],
        };

    // ---- Building blocks -------------------------------------------------------------------

    protected Control Heading(string text, bool info = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };

        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Bind(label, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        row.Children.Add(label);

        if (info)
        {
            var glyph = new TextBlock
            {
                Text = IconGlyphs.GetOrEmpty("info", 16),
                FontFamily = IconFont.Family,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(glyph, TextBlock.ForegroundProperty, "accent.rest.brush");
            row.Children.Add(glyph);
        }

        return row;
    }

    /// <summary>The bordered, filled box both panes sit in.</summary>
    protected Control Box(Control child, double right = 0)
    {
        var border = new Border
        {
            Child = child,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, right, 0),
        };
        Bind(border, Border.BorderBrushProperty, "dialog.border.brush");
        Bind(border, Border.BackgroundProperty, "dialog.surface.brush");
        return border;
    }

    protected Button MiddleButton(string label)
    {
        var button = DialogButton(label);
        button.Width = 80;
        return button;
    }

    /// <summary>A button that drops a menu, drawn with the chevron the reference gives it.</summary>
    protected Button MenuButton(string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Bind(text, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        content.Children.Add(text);

        var chevron = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
            FontFamily = IconFont.Family,
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(chevron, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        content.Children.Add(chevron);

        var button = new Button
        {
            Content = content,
            Height = 24,
            Padding = new Thickness(10, 0),
            BorderThickness = new Thickness(1),
        };
        Bind(button, BorderBrushProperty, "dialog.border.brush");
        Bind(button, BackgroundProperty, "dialog.surface.brush");
        return button;
    }

    protected Button DialogButton(string label)
    {
        var text = new TextBlock
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(text, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var button = new Button
        {
            Content = text,
            Height = 24,
            MinWidth = 74,
            Padding = new Thickness(10, 0),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        Bind(button, BorderBrushProperty, "dialog.border.brush");
        Bind(button, BackgroundProperty, "dialog.surface.brush");
        return button;
    }

    protected static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
