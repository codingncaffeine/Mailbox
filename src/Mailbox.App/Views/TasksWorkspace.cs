using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Controls.Tasks;
using Mailbox.Scheduling;
using Mailbox.Store;
using Mailbox.Store.Pim;

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
    private readonly CollectionNavPane _navPane;

    private TaskViewKind _kind = TaskViewKind.Todo;

    public TasksWorkspace(
        PimRepository repository,
        DateOnly today,
        Func<IReadOnlyList<(string Address, MailRepository Mail)>>? mailboxes = null)
    {
        Avalonia.Automation.AutomationProperties.SetName(_list, "Task list");
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

        // With the mail behind it the list holds the flagged messages too, which is what the
        // reference's own To-Do List is: tasks and mail together.
        _book = new TaskBook(repository, mailboxes);
        Today = today;

        // Bound rather than read: a resource read in a constructor is read before the
        // control has a scope to read it from, and the pane would sit flush to the edge.
        this[!MarginProperty] = new DynamicResourceExtension("workspace.inset.rightmargin");
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        ClipToBounds = true;
        this[!BackgroundProperty] = new DynamicResourceExtension("list.background.brush");

        // The reference's My Tasks pair rather than a list of ticked collections: To-Do List
        // first — tasks and flagged mail together — then the task folders themselves, each the
        // one place that shows a reader their tasks alone.
        _navPane = new CollectionNavPane(repository, CollectionKind.Tasks, "My Tasks", selectFirst: "To-Do List");
        _navPane.VisibilityChanged += (_, _) => Reload();
        _navPane.SelectionChanged += (_, _) => Reload();

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
    /// <summary>
    /// Puts the keyboard on the task list, so the arrow keys reach it without a Tab or a click
    /// first — the rail button that switched to the module keeps the focus otherwise.
    /// </summary>
    public bool FocusSurface() => _list.Focus();

    public string Status => Search.Length == 0 ? $"Items: {_list.Count}" : $"Items: {_list.Count} found";

    public IReadOnlyList<TaskRow> Rows => _list.Rows;

    /// <summary>The View tab's Reverse Sort, acting on this module's own rows.</summary>
    public bool Reversed
    {
        get;
        set { field = value; Reload(); }
    }

    /// <summary>The drawn list, which the harness presses.</summary>
    internal TaskListView List => _list;

    /// <summary>The folders the pane offers, top to bottom, which a pose reads back.</summary>
    public IReadOnlyList<string> PaneNames => _navPane.Listed();

    /// <summary>Opens a folder by name — the To-Do List, or a task list — as a click on the pane does.</summary>
    public bool OpenFolderByName(string name)
    {
        if (string.Equals(name, "To-Do List", StringComparison.OrdinalIgnoreCase))
        {
            _navPane.Select(null);
            return true;
        }

        if (_book.Lists().FirstOrDefault(l => l.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)) is not { } list) return false;
        _navPane.Select(list.Id);
        return true;
    }

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

    /// <summary>
    /// What Instant Search is looking for in this module, or empty for everything.
    /// </summary>
    /// <remarks>
    /// The words are matched against the store's own index (`pim_fts`), which is what makes this
    /// the same search the other modules run rather than five different ideas of a match; the
    /// flagged mail beside the tasks is matched on its subject, that being what a message's row
    /// on this list is.
    /// </remarks>
    public string Search
    {
        get;
        set
        {
            var wanted = value?.Trim() ?? string.Empty;
            if (field == wanted) return;
            field = wanted;
            Reload();
        }
    } = string.Empty;

    /// <summary>The folder the pane has open: null is the To-Do List, otherwise a task list's id.</summary>
    public long? OpenFolder => _navPane.SelectedCollectionId;

    /// <summary>What the open folder is called, which the status line and a pose read back.</summary>
    public string OpenFolderName => _navPane.SelectedCollectionId is { } id
        ? _book.Lists().FirstOrDefault(l => l.Id == id)?.DisplayName ?? "Tasks"
        : "To-Do List";

    public void Reload()
    {
        // The flagged mail and contacts belong to the To-Do List alone: a Tasks folder is the
        // reference's own tasks-only view, and borrowed rows on it were the defect.
        var todoList = _navPane.SelectedCollectionId is null;
        var rows = _book.Rows(
            Today,
            includeCompleted: _kind != TaskViewKind.Todo,
            collectionIds: todoList ? null : [_navPane.SelectedCollectionId!.Value],
            includeFlagged: todoList);
        if (Search.Length > 0)
        {
            var found = _repository.Search(Search).Select(i => i.Id).ToHashSet();
            rows = [.. rows.Where(r => (!r.IsMessage && found.Contains(r.ItemId))
                                       || r.Summary.Contains(Search, StringComparison.OrdinalIgnoreCase))];
        }

        // Detailed is the same rows under every column a task has, which is the whole of what
        // makes it a third view rather than the Simple List again — and a table is sorted by its
        // column rather than banded, so what is finished stays on the date it was due.
        // After the view's own ordering, so Detailed's date sort is what gets reversed.
        var shown = _kind == TaskViewKind.Detailed ? TaskBook.ByDueDate(rows) : rows;
        if (Reversed) shown = [.. shown.Reverse()];

        _list.ShowColumns = _kind == TaskViewKind.Detailed;
        _list.Rows = shown;
        _list.ArrangedBy = _kind == TaskViewKind.Todo ? "Flag: Due Date" : "Due Date";
        Selected = _list.Selected;
        _navPane.Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects the nth row, as a click does, for a harness run.</summary>
    public string PoseSelect(int at)
    {
        if (at < 0 || at >= Rows.Count) return $"only {Rows.Count} row(s) are showing";

        _list.Selected = Rows[at];
        Selected = Rows[at];
        Changed?.Invoke(this, EventArgs.Empty);
        return $"“{Rows[at].Summary}”";
    }

    /// <summary>Selects the row whose summary carries the words, for a harness run.</summary>
    public string PoseSelect(string named)
    {
        for (var at = 0; at < Rows.Count; at++)
        {
            if (Rows[at].Summary.Contains(named, StringComparison.OrdinalIgnoreCase)) return PoseSelect(at);
        }

        return $"nothing on the list matches “{named}” ({Rows.Count} row(s))";
    }

    /// <summary>The list a new task goes on: the default one, made if there is none.</summary>
    public Collection DefaultList()
    {
        var lists = _book.Lists();
        return lists.FirstOrDefault(l => l.IsDefault)
               ?? lists.FirstOrDefault()
               ?? _repository.AddCollection(CollectionKind.Tasks, "Tasks", "#0078D4", string.Empty);
    }
}
