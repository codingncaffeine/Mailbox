using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// row: the timeline writes the months it is showing in its own top heading, and Today, Back and
/// Forward are on the ribbon where the module's other commands are.
/// </remarks>
public sealed class JournalWorkspace : Border
{
    private readonly PimRepository _repository;
    private readonly JournalBook _book;
    private readonly JournalView _view = new();
    private readonly CollectionNavPane _navPane;

    private JournalArrangement _arrangement = JournalArrangement.ByType;
    private JournalArrangement _lastTimeline = JournalArrangement.ByType;

    public JournalWorkspace(PimRepository repository, DateOnly today, DayOfWeek firstDayOfWeek = DayOfWeek.Sunday)
    {
        Avalonia.Automation.AutomationProperties.SetName(_view, "Journal timeline");
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

        // Only the folders that can put a row in this module: notes live next door, and a
        // folder of them here would be a tick that can never change what the timeline shows.
        _navPane = new CollectionNavPane(repository, CollectionKind.Journal, "My Journals", HoldsEntries);
        _navPane.VisibilityChanged += (_, _) => Reload();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(_navPane);
        Grid.SetColumn(_view, 1);
        grid.Children.Add(_view);
        Child = grid;

        _view.EntrySelected += (_, row) => { Selected = row; Changed?.Invoke(this, EventArgs.Empty); };
        _view.EntryActivated += (_, row) => EntryOpened?.Invoke(this, row);
        _view.MonthBandPressed += (_, pressed) => OpenMonthMenu(pressed.Month);

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

    /// <summary>The folders the pane offers, which is this module's own list and nobody else's.</summary>
    public IReadOnlyList<string> PaneNames => _navPane.Listed();

    public event EventHandler? Changed;

    public event EventHandler<JournalRow>? EntryOpened;

    public void SetView(JournalArrangement arrangement)
    {
        if (_arrangement == arrangement) return;
        _arrangement = arrangement;
        if (JournalBook.IsTimeline(arrangement)) _lastTimeline = arrangement;
        Reload();
    }

    public void SetScale(TimelineScale scale)
    {
        // Coming back to the timeline is what a scale means, the way pressing Week in the
        // calendar means "show me a week" rather than "remember that I like weeks". Tested
        // before the scale rather than after it: a week is the scale the module opens at, so
        // leaving early when the scale had not changed meant Week did nothing at all from the
        // Entry List — the one arrangement a reader presses it from.
        if (_view.Scale == scale && JournalBook.IsTimeline(_arrangement)) return;

        _view.Scale = scale;
        _arrangement = _lastTimeline;
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

    /// <summary>Moves the timeline to a chosen day, which a month band's drop-down picks.</summary>
    public void GoTo(DateOnly day)
    {
        _view.Anchor = day;
        Reload();
    }

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

    /// <summary>Reads the store again — after a write, or when a journal is shown or hidden.</summary>
    public void Reload()
    {
        // A search's answer is a table of every match, whatever arrangement is chosen: the
        // timeline shows a span, and an answer that depended on which span the reader last
        // moved to said three different things about one store.
        _view.IsSearch = Search.Length > 0;
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

    /// <summary>Selects the entry whose subject carries the words, for a harness run.</summary>
    public string PoseSelect(string named)
    {
        var row = Rows.FirstOrDefault(r => r.Subject.Contains(named, StringComparison.OrdinalIgnoreCase));
        if (row is null) return $"nothing in the journal matches “{named}” ({Rows.Count} row(s))";

        _view.Selected = row;
        Selected = row;
        Changed?.Invoke(this, EventArgs.Empty);
        return $"“{row.Subject}”";
    }

    /// <summary>
    /// The drop-down a month band carries: a date navigator that moves the span. Built full and
    /// then shown, because a flyout filled from its own Opening event is measured empty.
    /// </summary>
    private void OpenMonthMenu(DateOnly month)
    {
        var picker = new Calendar
        {
            DisplayDate = month.ToDateTime(TimeOnly.MinValue),
            SelectedDate = _view.Anchor.ToDateTime(TimeOnly.MinValue),
        };

        var flyout = new Flyout { Content = picker, Placement = PlacementMode.Pointer };

        picker.SelectedDatesChanged += (_, _) =>
        {
            if (picker.SelectedDate is not { } chosen) return;
            flyout.Hide();
            GoTo(DateOnly.FromDateTime(chosen));
        };

        MenuProbe.Show($"journal month band {month:yyyy-MM}", flyout, _view, atPointer: true);
    }

    /// <summary>The journal a new entry goes in: the one that holds them, made if there is none.</summary>
    /// <remarks>
    /// Not the collection marked default, which is what this asked for and what put every entry a
    /// reader ever recorded on the <i>Notes</i> folder. Notes and journal entries share a
    /// collection kind, and the store marks the first collection of a kind as its default — so the
    /// default is whichever of the two modules made its folder first, and the other module then
    /// writes into it. The journal is the collection that holds journal entries; failing that, the
    /// one this application names for it; failing both, a new one, so the two modules never share
    /// a folder.
    /// </remarks>
    public Collection DefaultJournal()
    {
        var journals = _book.Lists();

        return journals.FirstOrDefault(Holds)
               ?? journals.FirstOrDefault(j => string.Equals(j.DisplayName, JournalFolder, StringComparison.OrdinalIgnoreCase))
               ?? _repository.AddCollection(CollectionKind.Journal, JournalFolder, "#8764B8", string.Empty);
    }

    /// <summary>What this application calls the journal's own folder when it makes one.</summary>
    private const string JournalFolder = "Journal";

    /// <summary>Whether a collection already holds journal entries rather than notes.</summary>
    private bool Holds(Collection list) => _book.Rows(JournalArrangement.EntryList, Today, [list.Id]).Count > 0;

    /// <summary>
    /// Whether a collection belongs on this module's pane: it holds entries, or it is the folder
    /// this module writes to. The kind cannot say — notes share it — so what is in the folder does.
    /// </summary>
    private bool HoldsEntries(Collection list)
        => Holds(list) || string.Equals(list.DisplayName, JournalFolder, StringComparison.OrdinalIgnoreCase);
}
