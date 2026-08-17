using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Controls.Journal;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The Journal module in the window: the journals down the side, and the timeline beside them.
/// </summary>
/// <remarks>
/// The same shape the other PIM modules have. What it does not have is the calendar's toolbar
/// row: the timeline writes the span it is showing in its own top heading, and Today, Back and
/// Forward are on the ribbon where the module's other commands are.
/// </remarks>
public sealed class JournalWorkspace : Border
{
    private readonly PimRepository _repository;
    private readonly JournalBook _book;
    private readonly JournalView _view = new();
    private readonly CollectionNavPane _navPane;

    private JournalArrangement _arrangement = JournalArrangement.Timeline;

    public JournalWorkspace(PimRepository repository, DateOnly today, DayOfWeek firstDayOfWeek = DayOfWeek.Sunday)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _book = new JournalBook(repository);
        Today = today;

        _view.Today = today;
        _view.Anchor = today;
        _view.FirstDayOfWeek = firstDayOfWeek;

        this[!MarginProperty] = new DynamicResourceExtension("workspace.inset.rightmargin");
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        ClipToBounds = true;
        this[!BackgroundProperty] = new DynamicResourceExtension("list.background.brush");

        _navPane = new CollectionNavPane(repository, CollectionKind.Journal, "My Journals");
        _navPane.VisibilityChanged += (_, _) => Reload();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(_navPane);
        Grid.SetColumn(_view, 1);
        grid.Children.Add(_view);
        Child = grid;

        _view.EntrySelected += (_, row) => { Selected = row; Changed?.Invoke(this, EventArgs.Empty); };
        _view.EntryActivated += (_, row) => EntryOpened?.Invoke(this, row);

        Reload();
    }

    /// <summary>Today, as the module believes it — pinned by the harness, live otherwise.</summary>
    public DateOnly Today { get; }

    public JournalArrangement Arrangement => _arrangement;

    public TimelineScale Scale => _view.Scale;

    /// <summary>Which span the timeline is showing, which Back and Forward move.</summary>
    public DateOnly Anchor => _view.Anchor;

    public JournalRow? Selected { get; private set; }

    public bool IsNavVisible
    {
        get => _navPane.IsVisible;
        set => _navPane.IsVisible = value;
    }

    /// <summary>What the status bar says: the reference counts what the view is showing.</summary>
    public string Status => Search.Length == 0 ? $"Items: {_view.Count}" : $"Items: {_view.Count} found";

    public IReadOnlyList<JournalRow> Rows => _view.Rows;

    /// <summary>What the timeline writes over its columns, which the harness reads back.</summary>
    public string SpanText => _view.SpanText();

    /// <summary>The drawn view, which the harness presses.</summary>
    internal JournalView View => _view;

    public event EventHandler? Changed;

    public event EventHandler<JournalRow>? EntryOpened;

    public void SetView(JournalArrangement arrangement)
    {
        if (_arrangement == arrangement) return;
        _arrangement = arrangement;
        Reload();
    }

    public void SetScale(TimelineScale scale)
    {
        if (_view.Scale == scale) return;
        _view.Scale = scale;

        // Coming back to the timeline is what a scale means, the way pressing Week in the
        // calendar means "show me a week" rather than "remember that I like weeks".
        _arrangement = JournalArrangement.Timeline;
        Reload();
    }

    /// <summary>The span before this one, or the one after it.</summary>
    public void Step(int direction)
    {
        _view.Anchor = _view.Scale switch
        {
            TimelineScale.Day => _view.Anchor.AddDays(direction),
            TimelineScale.Month => _view.Anchor.AddMonths(direction),
            _ => _view.Anchor.AddDays(7 * direction),
        };

        Reload();
    }

    public void GoToday()
    {
        _view.Anchor = Today;
        Reload();
    }

    /// <summary>Reads the store again — after a write, or when a journal is shown or hidden.</summary>
    /// <summary>What Instant Search is looking for here, matched against the store's own index.</summary>
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

    public void Reload()
    {
        _view.Arrangement = _arrangement;
        var rows = _book.Rows(_arrangement, Today);
        if (Search.Length > 0)
        {
            var found = _repository.Search(Search).Select(i => i.Id).ToHashSet();
            rows = [.. rows.Where(r => found.Contains(r.ItemId))];
        }

        _view.Rows = rows;
        Selected = _view.Selected;
        _navPane.Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The journal a new entry goes in: the default one, made if there is none.</summary>
    public Collection DefaultJournal()
    {
        var journals = _book.Lists();
        return journals.FirstOrDefault(j => j.IsDefault)
               ?? journals.FirstOrDefault()
               ?? _repository.AddCollection(CollectionKind.Journal, "Journal", "#8764B8", string.Empty);
    }
}
