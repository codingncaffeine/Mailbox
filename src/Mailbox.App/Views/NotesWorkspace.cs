using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Controls.Notes;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The Notes module in the window: the folders down the side, and the wall of notes beside them.
/// </summary>
/// <remarks>
/// The same shape the Calendar, People and Tasks modules have — a navigation pane the shell's own
/// toggle hides, and a drawn view filling the rest.
/// </remarks>
public sealed class NotesWorkspace : Border
{
    private readonly PimRepository _repository;
    private readonly NoteBook _book;
    private readonly NotesView _view = new();
    private readonly CollectionNavPane _navPane;

    private NoteArrangement _arrangement = NoteArrangement.Icons;

    public NotesWorkspace(PimRepository repository, DateOnly today)
    {
        Avalonia.Automation.AutomationProperties.SetName(_view, "Notes");
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _book = new NoteBook(repository);
        Today = today;
        _view.Today = today;

        // Bound rather than read: a resource read in a constructor is read before the control
        // has a scope to read it from, and the pane would sit flush to the edge.
        this[!MarginProperty] = new DynamicResourceExtension("workspace.inset.rightmargin");
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        ClipToBounds = true;
        this[!BackgroundProperty] = new DynamicResourceExtension("list.background.brush");

        // Only the folders that can put a note on the wall: journal entries live next door, and
        // their folder here would be a tick that can never change what the wall shows. A folder
        // holding both — another client's — is honestly on both panes.
        _navPane = new CollectionNavPane(repository, CollectionKind.Journal, "My Notes", HoldsNotes);
        _navPane.VisibilityChanged += (_, _) => Reload();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(_navPane);
        Grid.SetColumn(_view, 1);
        grid.Children.Add(_view);
        Child = grid;

        _view.NoteSelected += (_, row) => { Selected = row; Changed?.Invoke(this, EventArgs.Empty); };
        _view.NoteActivated += (_, row) => NoteOpened?.Invoke(this, row);
        _view.NewNoteRequested += (_, _) => NewNoteRequested?.Invoke(this, EventArgs.Empty);

        Reload();
    }

    /// <summary>Today, as the module believes it — pinned by the harness, live otherwise.</summary>
    public DateOnly Today { get; }

    public NoteArrangement Arrangement => _arrangement;

    public NoteRow? Selected { get; private set; }

    /// <summary>Whether the navigation pane is showing, which the shell's own toggle drives.</summary>
    public bool IsNavVisible
    {
        get => _navPane.IsVisible;
        set => _navPane.IsVisible = value;
    }

    /// <summary>What the status bar says: the reference counts what the view is showing.</summary>
    public string Status => Search.Length == 0 ? $"Items: {_view.Count}" : $"Items: {_view.Count} found";

    public IReadOnlyList<NoteRow> Rows => _view.Rows;

    /// <summary>The drawn view, which the harness presses.</summary>
    internal NotesView View => _view;

    /// <summary>The folders the pane offers, which is this module's own list and nobody else's.</summary>
    public IReadOnlyList<string> PaneNames => _navPane.Listed();

    public event EventHandler? Changed;

    public event EventHandler<NoteRow>? NoteOpened;

    /// <summary>A double click on the wall itself, which is how the reference makes a note.</summary>
    public event EventHandler? NewNoteRequested;

    public void SetView(NoteArrangement arrangement)
    {
        if (_arrangement == arrangement) return;
        _arrangement = arrangement;
        Reload();
    }

    /// <summary>Reads the store again — after a write, or when a folder is shown or hidden.</summary>
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

    /// <summary>The View tab's Reverse Sort, acting on this module's own rows.</summary>
    public bool Reversed
    {
        get;
        set { field = value; Reload(); }
    }

    public void Reload()
    {
        _view.Arrangement = _arrangement;
        var rows = _book.Rows(_arrangement, Today);
        if (Search.Length > 0)
        {
            var found = _repository.Search(Search).Select(i => i.Id).ToHashSet();
            rows = [.. rows.Where(r => found.Contains(r.ItemId))];
        }

        if (Reversed) rows = [.. rows.Reverse()];

        _view.Rows = rows;
        Selected = _view.Selected;
        _navPane.Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects the nth note, as a click does, for a harness run.</summary>
    public string PoseSelect(int at)
    {
        if (at < 0 || at >= Rows.Count) return $"only {Rows.Count} note(s) are showing";

        _view.Selected = Rows[at];
        Selected = Rows[at];
        Changed?.Invoke(this, EventArgs.Empty);
        return $"“{Rows[at].Title}”";
    }

    /// <summary>Selects the note whose title carries the words, for a harness run.</summary>
    public string PoseSelect(string named)
    {
        for (var at = 0; at < Rows.Count; at++)
        {
            if (Rows[at].Title.Contains(named, StringComparison.OrdinalIgnoreCase)) return PoseSelect(at);
        }

        return $"nothing on the wall matches “{named}” ({Rows.Count} note(s))";
    }

    /// <summary>The folder a new note goes in: the one that holds notes, made if there is none.</summary>
    /// <remarks>
    /// Not the collection the store marks default. Notes and journal entries are one component in
    /// one kind of collection, and the store marks the *first* collection of a kind as that kind's
    /// default — so "the default" is whichever of the two modules made its folder first, and on a
    /// store where that was the Journal every note a reader writes is filed on the journal's
    /// folder, on the journal's server collection, and disappears the moment they untick Journal
    /// in a pane headed My Notes. The Journal side of this was the same bug and was fixed the same
    /// way at `73080eb`; the two halves have to agree or they simply swap which one is wrong.
    /// </remarks>
    public Collection DefaultFolder()
    {
        var folders = _book.Lists();

        return folders.FirstOrDefault(Holds)
               ?? folders.FirstOrDefault(f => string.Equals(f.DisplayName, NotesFolder, StringComparison.OrdinalIgnoreCase))
               ?? _repository.AddCollection(CollectionKind.Journal, NotesFolder, "#F2C811", string.Empty);
    }

    /// <summary>What this application calls the notes folder when it makes one.</summary>
    private const string NotesFolder = "Notes";

    /// <summary>Whether a collection already holds notes rather than journal entries.</summary>
    private bool Holds(Collection list) => _book.Rows(NoteArrangement.Icons, Today, [list.Id]).Count > 0;

    /// <summary>
    /// Whether a collection belongs on this module's pane: it holds notes, or it is empty and not
    /// the journal's own folder — an empty folder reads as notes because a note is the component's
    /// default, exactly as an untyped entry is.
    /// </summary>
    private bool HoldsNotes(Collection list)
        => Holds(list)
           || (!string.Equals(list.DisplayName, "Journal", StringComparison.OrdinalIgnoreCase)
               && _repository.Items(list.Id).All(i => i.SyncState == PimSyncState.Deleted));
}
