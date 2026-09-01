using System.Globalization;
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
    /// <summary>How wide the list is when the pane is beside it, as a share of the two.</summary>
    /// <remarks>
    /// The reference gives the reading pane the larger half of what is left after the navigation
    /// pane, which is what stops a to-do row being twelve hundred pixels of white with its flag at
    /// the far end. A splitter moves it and this is only where it starts.
    /// </remarks>
    private const double ListShare = 1;
    private const double PaneShare = 1.35;

    private readonly PimRepository _repository;
    private readonly TaskBook _book;
    private readonly TaskListView _list = new();
    private readonly CollectionNavPane _navPane;

    /// <summary>The reading pane's contents, rebuilt for whatever row is selected.</summary>
    private readonly StackPanel _reading = new() { Margin = new Thickness(22, 18, 22, 18), Spacing = 2 };
    private readonly ScrollViewer _readingScroll;
    private readonly Border _readingPane;

    /// <summary>The list and the pane, and whichever way round they are.</summary>
    private readonly Grid _split = new();

    private TaskViewKind _kind = TaskViewKind.Todo;
    private bool _paneVisible = true;
    private bool _paneAtBottom;

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

        _readingScroll = new ScrollViewer
        {
            Content = _reading,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        _readingPane = new Border { Child = _readingScroll };
        _readingPane[!BackgroundProperty] = new DynamicResourceExtension("reading.background.brush");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(_navPane);
        Grid.SetColumn(_split, 1);
        grid.Children.Add(_split);
        Child = grid;
        BuildSplit();

        _list.TaskSelected += (_, row) => { Selected = row; Show(row); Changed?.Invoke(this, EventArgs.Empty); };
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

    /// <summary>
    /// Puts the keyboard on the task list, so the arrow keys reach it without a Tab or a click
    /// first — the rail button that switched to the module keeps the focus otherwise.
    /// </summary>
    public bool FocusSurface() => _list.Focus();

    /// <summary>What the status bar says: the reference counts what the view is showing.</summary>
    public string Status => Search.Length == 0 ? $"Items: {_list.Count}" : $"Items: {_list.Count} found";

    public IReadOnlyList<TaskRow> Rows => _list.Rows;

    /// <summary>The View tab's Reverse Sort, acting on this module's own rows.</summary>
    public bool Reversed
    {
        get;
        set { field = value; Reload(); }
    }

    /// <summary>
    /// What the list is grouped by, which the View tab's Arrangement gallery sets.
    /// </summary>
    /// <remarks>
    /// Due date is the default and was for a long time the only thing this could be — the bands
    /// were an enum, so there was nowhere for a category or a task list to go.
    /// </remarks>
    public TaskArrangement Arrangement
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Reload();
        }
    } = TaskArrangement.DueDate;

    /// <summary>The drawn list, which the harness presses.</summary>
    internal TaskListView List => _list;

    /// <summary>The folders the pane offers, top to bottom, which a pose reads back.</summary>
    public IReadOnlyList<string> PaneNames => _navPane.Listed();

    // ---- The reading pane ----------------------------------------------------------------------
    //
    // The module had no pane at all: a row ran the whole width of the window with its flag a
    // thousand pixels from its subject, and selecting one showed nothing anywhere. Both halves of
    // that are this pane — the list narrows because something else is beside it, and what is
    // selected is finally somewhere to be read.

    /// <summary>Whether the pane is shown, which View › Layout › Reading Pane drives.</summary>
    public bool ReadingPaneVisible
    {
        get => _paneVisible;
        set
        {
            if (_paneVisible == value) return;
            _paneVisible = value;
            BuildSplit();
        }
    }

    /// <summary>Under the list rather than beside it, as the same menu offers for mail.</summary>
    public bool ReadingPaneAtBottom
    {
        get => _paneAtBottom;
        set
        {
            if (_paneAtBottom == value) return;
            _paneAtBottom = value;
            BuildSplit();
        }
    }

    /// <summary>The pane's own sentences, top to bottom, which a pose reads back.</summary>
    public IReadOnlyList<string> ReadingLines =>
        [.. _reading.Children.OfType<Control>().SelectMany(Lines)];

    private static IEnumerable<string> Lines(Control control) => control switch
    {
        TextBlock { Text: { Length: > 0 } text } => [text],
        Panel panel => panel.Children.OfType<Control>().SelectMany(Lines),
        _ => [],
    };

    /// <summary>
    /// Lays the list and the pane out, whichever way round they go — and takes the pane out of the
    /// tree entirely when it is off, rather than leaving a zero-wide column nothing can be dragged
    /// out of.
    /// </summary>
    private void BuildSplit()
    {
        _split.Children.Clear();
        _split.ColumnDefinitions.Clear();
        _split.RowDefinitions.Clear();
        Grid.SetColumn(_list, 0);
        Grid.SetRow(_list, 0);

        if (!_paneVisible)
        {
            _split.Children.Add(_list);
            return;
        }

        var splitter = new GridSplitter { Background = Avalonia.Media.Brushes.Transparent };
        var line = new Border();
        line[!BackgroundProperty] = new DynamicResourceExtension("border.subtle.brush");

        if (_paneAtBottom)
        {
            // Under the list the shares turn over: the list is as wide as the window there, so it
            // is showing its single-line layout and the room is better spent on rows than on a
            // pane with a dozen short lines in it.
            _split.RowDefinitions = new RowDefinitions(
                $"{PaneShare.ToString(CultureInfo.InvariantCulture)}*,Auto,{ListShare.ToString(CultureInfo.InvariantCulture)}*");
            splitter.Height = 4;
            splitter.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            line.Height = 1;
            line.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            Grid.SetRow(splitter, 1);
            Grid.SetRow(line, 1);
            Grid.SetRow(_readingPane, 2);
        }
        else
        {
            _split.ColumnDefinitions = new ColumnDefinitions(
                $"{ListShare.ToString(CultureInfo.InvariantCulture)}*,Auto,{PaneShare.ToString(CultureInfo.InvariantCulture)}*");
            splitter.Width = 4;
            splitter.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            line.Width = 1;
            line.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(line, 1);
            Grid.SetColumn(_readingPane, 2);
        }

        _split.Children.Add(_list);
        _split.Children.Add(_readingPane);
        _split.Children.Add(line);
        _split.Children.Add(splitter);
    }

    /// <summary>
    /// Fills the pane for a row: what it is, then the fields it has, then whatever was written in
    /// it. A field with nothing in it is left out rather than drawn empty — the reference's own
    /// pane does the same, and a column of "None" says less than an absence does.
    /// </summary>
    private void Show(TaskRow? row)
    {
        _reading.Children.Clear();
        if (row is null)
        {
            var hint = new TextBlock
            {
                Text = "Select a task to read it here.",
                Margin = new Thickness(0, 40, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                FontSize = 13,
            };
            hint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
            _reading.Children.Add(hint);
            return;
        }

        var task = row.Task;

        var subject = new TextBlock
        {
            Text = row.Summary,
            FontSize = 19,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            TextDecorations = row.IsComplete ? Avalonia.Media.TextDecorations.Strikethrough : null,
        };
        subject[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
            row.IsComplete ? "text.secondary.brush" : "text.primary.brush");
        _reading.Children.Add(subject);

        if (Banner(row) is { Length: > 0 } banner)
        {
            var line = new TextBlock
            {
                Text = banner,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            line[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
                row.IsOverdue ? "list.overdue.text.brush" : "text.secondary.brush");
            _reading.Children.Add(line);
        }

        // A borrowed row is somebody else's item on this list, and saying so is what stops the
        // pane reading as though the task itself were a message.
        if (row.Message is { } message)
        {
            _reading.Children.Add(Field("Flagged message", message.From.Length > 0 ? $"from {message.From}" : "in your mail"));
        }
        else if (row.Contact is { } contact)
        {
            _reading.Children.Add(Field("Flagged contact", contact.Name));
        }

        _reading.Children.Add(Rule());

        if (task.Start is { } start) _reading.Children.Add(Field("Start date", start.Wall.ToString("ddd dd/MM/yyyy", Culture)));
        if (task.Due is { } due) _reading.Children.Add(Field("Due date", due.Wall.ToString("ddd dd/MM/yyyy", Culture)));
        _reading.Children.Add(Field("Status", StatusText(task)));
        if (task.PercentComplete > 0) _reading.Children.Add(Field("% Complete", $"{task.PercentComplete}%"));
        if (task.Urgency != TaskUrgency.Normal) _reading.Children.Add(Field("Priority", task.Urgency.ToString()));
        if (task.ReminderMinutes is { } minutes) _reading.Children.Add(Field("Reminder", Reminder(minutes)));
        if (task.Owner.Length > 0) _reading.Children.Add(Field("Owner", task.Owner));
        if (task.Categories.Count > 0) _reading.Children.Add(Field("Categories", string.Join(", ", task.Categories)));
        if (task.IsPrivate) _reading.Children.Add(Field("Private", "Kept to yourself when this list is shared"));

        if (task.Description.Trim().Length == 0) return;

        _reading.Children.Add(Rule());
        var body = new TextBlock
        {
            Text = task.Description.Trim(),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 13.5,
        };
        body[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");
        _reading.Children.Add(body);
    }

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>
    /// The one line the reference colours, and only when there is something to say: this is late,
    /// or this is done. An ordinary task's date is in the fields below and saying it twice says
    /// less than saying it once.
    /// </summary>
    private static string? Banner(TaskRow row)
    {
        if (row.Task.CompletedUtc is { } done) return $"Completed on {done.ToLocalTime():ddd dd/MM/yyyy}.";
        if (row.IsComplete) return "Completed.";
        return row.IsOverdue && row.Task.Due is { } late ? $"Due {late.Wall:ddd dd/MM/yyyy}. This is overdue." : null;
    }

    private static string StatusText(TaskItem task) => task.Progress switch
    {
        TaskProgress.Completed => "Completed",
        TaskProgress.InProgress => "In Progress",
        TaskProgress.Waiting => "Waiting on someone else",
        TaskProgress.Deferred => "Deferred",
        _ => task.PercentComplete >= 100 ? "Completed" : "Not Started",
    };

    private static string Reminder(int minutes) => minutes switch
    {
        0 => "At the time it is due",
        < 60 => $"{minutes} minutes before",
        < 60 * 24 => $"{minutes / 60} hour{(minutes / 60 == 1 ? string.Empty : "s")} before",
        _ => $"{minutes / (60 * 24)} day{(minutes / (60 * 24) == 1 ? string.Empty : "s")} before",
    };

    private Control Field(string label, string value)
    {
        var name = new TextBlock { Text = label, Width = 108, FontSize = 12.5 };
        name[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");

        var said = new TextBlock { Text = value, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12.5 };
        said[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 3, 0, 0) };
        row.Children.Add(name);
        Grid.SetColumn(said, 1);
        row.Children.Add(said);
        return row;
    }

    private Control Rule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 6) };
        rule[!BackgroundProperty] = new DynamicResourceExtension("border.subtle.brush");
        return rule;
    }

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

        // Stamped with the band each row is drawn under, and put in band order. The rows arrive
        // already sorted by due date and then by name, and the arrangement is stable within a
        // band, so what changes is which headings appear and in what order — not the order of
        // the tasks under any one of them.
        shown = TaskArrangements.Arrange(shown, Arrangement, Today, ListName);

        if (Reversed) shown = [.. shown.Reverse()];

        _list.ShowColumns = _kind == TaskViewKind.Detailed;
        _list.Rows = shown;

        // What the column header writes, and what the ribbon's gallery boxes. The To-Do List
        // says "Flag: " because its rows are flagged mail as much as tasks, which is the
        // reference's own wording for the same list.
        _list.ArrangedBy = _kind == TaskViewKind.Todo && Arrangement == TaskArrangement.DueDate
            ? "Flag: Due Date"
            : TaskArrangements.Label(Arrangement);
        Selected = _list.Selected;

        // The pane follows the list. A row that a reload has taken away — ticked off in the
        // To-Do view, filtered out by a search — leaves the pane showing an item that is no
        // longer on the list, which reads as a selection that never cleared.
        Show(Selected is { } chosen && shown.Any(r => r.Key == chosen.Key) ? Selected : null);
        _navPane.Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What a task list is called, for the Folder arrangement.
    /// </summary>
    /// <remarks>
    /// Looked up through the book rather than the pane: the pane shows the lists that are ticked
    /// and a row can be on one that has just been unticked mid-reload, which would leave its band
    /// unnamed.
    /// </remarks>
    private string ListName(long collectionId)
        => _book.Lists().FirstOrDefault(l => l.Id == collectionId)?.DisplayName ?? string.Empty;

    /// <summary>Selects the nth row, as a click does, for a harness run.</summary>
    public string PoseSelect(int at)
    {
        if (at < 0 || at >= Rows.Count) return $"only {Rows.Count} row(s) are showing";

        _list.Selected = Rows[at];
        Selected = Rows[at];
        Show(Selected);
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
