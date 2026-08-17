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

        _navPane = new CollectionNavPane(repository, CollectionKind.Journal, "My Notes");
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

    /// <summary>The folder a new note goes in: the default one, made if there is none.</summary>
    public Collection DefaultFolder()
    {
        var folders = _book.Lists();
        return folders.FirstOrDefault(f => f.IsDefault)
               ?? folders.FirstOrDefault()
               ?? _repository.AddCollection(CollectionKind.Journal, "Notes", "#F2C811", string.Empty);
    }
}
