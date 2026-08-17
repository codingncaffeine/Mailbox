using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Controls.Tasks;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>Which view the Current View group has chosen.</summary>
public enum TaskViewKind
{
    /// <summary>Everything outstanding, banded by when it is due.</summary>
    Todo,

    /// <summary>Every task, finished ones included.</summary>
    Simple,

    /// <summary>Every task with every column it has.</summary>
    Detailed,
}

/// <summary>
/// The Tasks module in the window: the lists down the side, and the to-do list beside them.
/// </summary>
/// <remarks>
/// The same shape the Calendar module has — a navigation pane the shell's own toggle hides, and
/// a drawn view filling the rest — because the reference's modules are the same window with
/// different contents, and building each one its own way is how they stop looking alike.
/// </remarks>
public sealed class TasksWorkspace : Border
{
    private readonly PimRepository _repository;
    private readonly TaskBook _book;
    private readonly TaskListView _list = new();
    private readonly StackPanel _listNames = new();
    private readonly Border _navPane;

    private TaskViewKind _kind = TaskViewKind.Todo;

    public TasksWorkspace(PimRepository repository, DateOnly today)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _book = new TaskBook(repository);
        Today = today;

        // Bound rather than read: a resource read in a constructor is read before the
        // control has a scope to read it from, and the pane would sit flush to the edge.
        this[!MarginProperty] = new DynamicResourceExtension("workspace.inset.rightmargin");
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        ClipToBounds = true;
        this[!BackgroundProperty] = new DynamicResourceExtension("list.background.brush");

        _navPane = BuildNavPane();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(_navPane);
        Grid.SetColumn(_list, 1);
        grid.Children.Add(_list);
        Child = grid;

        _list.TaskSelected += (_, row) => { Selected = row; Changed?.Invoke(this, EventArgs.Empty); };
        _list.TaskActivated += (_, row) => TaskOpened?.Invoke(this, row);
        _list.TaskToggled += (_, row) => TaskToggled?.Invoke(this, row);
        _list.TaskTyped += (_, text) => TaskTyped?.Invoke(this, text);

        Reload();
    }

    /// <summary>Today, as the module believes it — pinned by the harness, live otherwise.</summary>
    public DateOnly Today { get; }

    public TaskViewKind Kind => _kind;

    public TaskRow? Selected { get; private set; }

    /// <summary>Whether the navigation pane is showing, which the shell's own toggle drives.</summary>
    public bool IsNavVisible
    {
        get => _navPane.IsVisible;
        set => _navPane.IsVisible = value;
    }

    /// <summary>What the status bar says: the reference counts what the view is showing.</summary>
    public string Status => $"Items: {_list.Count}";

    public IReadOnlyList<TaskRow> Rows => _list.Rows;

    /// <summary>The drawn list, which the harness presses.</summary>
    internal TaskListView List => _list;

    public event EventHandler? Changed;

    public event EventHandler<TaskRow>? TaskOpened;

    /// <summary>A tick box pressed: this task should now be done, or not done.</summary>
    public event EventHandler<TaskRow>? TaskToggled;

    /// <summary>Something typed into the row at the top of the list, which makes a task.</summary>
    public event EventHandler<string>? TaskTyped;

    public void SetView(TaskViewKind kind)
    {
        if (_kind == kind) return;
        _kind = kind;
        Reload();
    }

    /// <summary>Reads the store again — after a write, or when a list is shown or hidden.</summary>
    public void Reload()
    {
        _list.Rows = _book.Rows(Today, includeCompleted: _kind != TaskViewKind.Todo);
        _list.ArrangedBy = _kind == TaskViewKind.Todo ? "Flag: Due Date" : "Due Date";
        Selected = _list.Selected;
        RefreshListNames();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The list a new task goes on: the default one, made if there is none.</summary>
    public Collection DefaultList()
    {
        var lists = _book.Lists();
        return lists.FirstOrDefault(l => l.IsDefault)
               ?? lists.FirstOrDefault()
               ?? _repository.AddCollection(CollectionKind.Tasks, "Tasks", "#0078D4", string.Empty);
    }

    // ---- The navigation pane -----------------------------------------------------------------

    private Border BuildNavPane()
    {
        var pane = new Border { Width = Resource<double>("nav.width.value") is { } w and > 0 ? w : 235 };
        pane[!BackgroundProperty] = new DynamicResourceExtension("nav.background.brush");

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
        collapse.Click += (_, _) => IsNavVisible = false;
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

        var headerText = new TextBlock { Text = "My Tasks", FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        headerText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
        header.Children.Add(headerText);
        stack.Children.Add(header);

        _listNames.Margin = new Thickness(5, 0, 4, 0);
        stack.Children.Add(_listNames);

        pane.Child = stack;
        return pane;
    }

    /// <summary>
    /// One row per task list, drawn as the calendar pane draws its calendars: the shown ones
    /// filled and in bold, with a tick beside each only once there are two to choose between.
    /// </summary>
    private void RefreshListNames()
    {
        _listNames.Children.Clear();
        var lists = _book.Lists();

        foreach (var list in lists)
        {
            var row = new Border { Height = 24, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            if (list.IsVisible) row[!BackgroundProperty] = new DynamicResourceExtension("nav.item.selected.brush");

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (lists.Count > 1)
            {
                line.Children.Add(new CheckBox
                {
                    IsChecked = list.IsVisible,
                    Margin = new Thickness(22, 0, 0, 0),
                    MinWidth = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                });
            }

            var name = new TextBlock
            {
                Text = list.DisplayName,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(lists.Count > 1 ? 0 : 43, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("nav.item.text.brush");
            line.Children.Add(name);

            row.Child = line;
            var id = list.Id;
            var visible = list.IsVisible;
            row.PointerPressed += (_, _) =>
            {
                // Only when there is another list to fall back on: hiding the only one leaves a
                // module with nothing in it and no way back.
                if (lists.Count <= 1) return;
                _repository.SetCollectionVisible(id, !visible);
                Reload();
            };

            _listNames.Children.Add(row);
        }
    }

    private T? Resource<T>(string key)
        => this.TryFindResource(key, out var value) && value is T typed ? typed : default;
}
