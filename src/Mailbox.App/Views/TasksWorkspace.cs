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

        _navPane = new CollectionNavPane(repository, CollectionKind.Tasks, "My Tasks");
        _navPane.VisibilityChanged += (_, _) => Reload();

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
        _navPane.Refresh();
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
}
