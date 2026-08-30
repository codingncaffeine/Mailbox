using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Store.Pim;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The navigation pane the PIM modules share: a heading, and a row per collection with the shown
/// ones filled in.
/// </summary>
/// <remarks>
/// One pane rather than one per module, which is what the third copy of it asked for: Tasks,
/// Notes and Journal draw the same thing over three different kinds of collection, and only the
/// heading and the kind differ. The calendar's own pane stays where it is — it carries the date
/// navigator above its list, which nothing else does.
/// </remarks>
internal sealed class CollectionNavPane : Border
{
    private readonly PimRepository _repository;
    private readonly CollectionKind _kind;
    private readonly Func<Collection, bool>? _belongs;
    private readonly string? _selectFirst;
    private readonly StackPanel _names = new();

    /// <summary>In the select shape: the chosen collection, or null for the synthetic first row.</summary>
    public long? SelectedCollectionId { get; private set; }

    /// <summary>The select shape's own event: which folder is now open.</summary>
    public event EventHandler? SelectionChanged;

    /// <param name="belongs">
    /// Which of the kind's collections are this module's to list — Notes and Journal share a
    /// kind, and each pane should offer only the folders that can put a row in its own view.
    /// Null lists them all.
    /// </param>
    /// <param name="selectFirst">
    /// Puts the pane in the reference's folder-select shape instead of the calendar's tick
    /// shape: a synthetic first row with this label — the Tasks module's To-Do List — then one
    /// row per collection, a click choosing which folder is open rather than toggling ticks.
    /// </param>
    public CollectionNavPane(
        PimRepository repository, CollectionKind kind, string heading,
        Func<Collection, bool>? belongs = null, string? selectFirst = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _kind = kind;
        _belongs = belongs;
        _selectFirst = selectFirst;

        Width = this.TryFindResource("nav.width.value", out var width) && width is double w and > 0 ? w : 235;
        this[!BackgroundProperty] = new DynamicResourceExtension("nav.background.brush");

        var stack = new StackPanel();

        var collapse = new Button
        {
            Classes = { "flat" },
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 4, 0),
            FontFamily = IconFont.Family,
            FontSize = 12,
            Content = IconGlyphs.GetOrEmpty("collapse-left", 16),
        };
        ToolTip.SetTip(collapse, "Collapse the Folder Pane");
        collapse.Click += (_, _) => IsVisible = false;
        stack.Children.Add(collapse);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Height = 24,
            Margin = new Thickness(9, 4, 0, 0),
        };

        var chevron = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty("chevron-down", 16),
            FontFamily = IconFont.Family,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        header.Children.Add(chevron);

        var headerText = new TextBlock { Text = heading, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        headerText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        header.Children.Add(headerText);
        stack.Children.Add(header);

        _names.Margin = new Thickness(5, 0, 4, 0);
        stack.Children.Add(_names);

        Child = stack;
        Refresh();
    }

    /// <summary>A collection was shown or hidden, so whatever is drawing them should read again.</summary>
    public event EventHandler? VisibilityChanged;

    /// <summary>Opens a folder of the select shape, as a click on its row does.</summary>
    public void Select(long? id)
    {
        if (SelectedCollectionId == id) return;
        SelectedCollectionId = id;
        Refresh();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One row of the select shape: its name, highlighted while it is the open folder.</summary>
    private void AddSelectRow(string label, long? id)
    {
        var row = new Border { Height = 24, Cursor = new Cursor(StandardCursorType.Hand) };
        if (SelectedCollectionId == id) row[!BackgroundProperty] = new DynamicResourceExtension("nav.item.selected.brush");

        var name = new TextBlock
        {
            Text = label,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(43, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        row.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name },
        };

        row.PointerPressed += (_, _) =>
        {
            if (SelectedCollectionId == id) return;
            SelectedCollectionId = id;
            Refresh();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };

        _names.Children.Add(row);
    }

    /// <summary>What the pane is listing, top to bottom — the read-back for a drawn set of rows.</summary>
    public IReadOnlyList<string> Listed()
        => [.. _names.Children.OfType<Border>()
            .Select(row => (row.Child as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty)
            .Where(name => name.Length > 0)];

    /// <summary>Reads the collections again and redraws the rows.</summary>
    public void Refresh()
    {
        _names.Children.Clear();
        IReadOnlyList<Collection> collections = _repository.Collections(_kind);
        if (_belongs is { } belongs) collections = [.. collections.Where(belongs)];

        // The select shape: the synthetic folder first, then the collections, a click opening
        // one rather than ticking it — the reference's My Tasks pair.
        if (_selectFirst is { } first)
        {
            AddSelectRow(first, null);
            foreach (var collection in collections) AddSelectRow(collection.DisplayName, collection.Id);
            return;
        }

        foreach (var collection in collections)
        {
            var row = new Border { Height = 24, Cursor = new Cursor(StandardCursorType.Hand) };
            if (collection.IsVisible) row[!BackgroundProperty] = new DynamicResourceExtension("nav.item.selected.brush");

            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // The tick appears only once there are two to choose between: a single collection
            // has nothing to be chosen against.
            if (collections.Count > 1)
            {
                line.Children.Add(new CheckBox
                {
                    IsChecked = collection.IsVisible,
                    Margin = new Thickness(22, 0, 0, 0),
                    MinWidth = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                });
            }

            var name = new TextBlock
            {
                Text = collection.DisplayName,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(collections.Count > 1 ? 0 : 43, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
            line.Children.Add(name);

            row.Child = line;
            var id = collection.Id;
            var visible = collection.IsVisible;
            row.PointerPressed += (_, _) =>
            {
                // Only when there is another to fall back on: hiding the only one leaves a module
                // with nothing in it and no way back.
                if (collections.Count <= 1) return;
                _repository.SetCollectionVisible(id, !visible);
                VisibilityChanged?.Invoke(this, EventArgs.Empty);
            };

            _names.Children.Add(row);
        }
    }
}
