using System.Globalization;
using Avalonia.Controls;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Notes;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The Notes module in the shell: switching to it, the workspace it puts in the window, and the
/// commands its ribbon presses.
/// </summary>
/// <remarks>
/// A partial of the shell for the reason the calendar's, People's and Tasks' halves are: it needs
/// the window's ribbon, its dialogs and its status line.
/// </remarks>
public partial class MainWindow
{
    private NotesWorkspace? _noteModule;

    /// <summary>The Notes ribbon: the shipped layout with the reader's edits over it.</summary>
    private static RibbonLayout NotesRibbon() => App.RibbonEdits.Apply(App.Plugins.InjectRibbon(NotesRibbonLayout.Build()));

    private NotesWorkspace EnsureNotes(ShellViewModel shell)
    {
        if (_noteModule is not null) return _noteModule;

        var workspace = new NotesWorkspace(App.Pim, CalendarToday)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.Changed += (_, _) =>
        {
            shell.ModuleStatusLeft = workspace.Status;

            // A module's own selection decides what its ribbon can do, the same way the message
            // list's does.
            RefreshCommandEnablement();
        };
        workspace.NoteOpened += (_, row) => _ = OpenNoteAsync(shell, row);
        workspace.NewNoteRequested += (_, _) => _ = NewNoteAsync(shell);

        _noteModule = workspace;
        return workspace;
    }

    /// <summary>
    /// The Notes module's commands. Returns false for anything it does not own, so the shell's
    /// own list carries on.
    /// </summary>
    private bool RunNoteCommand(ShellViewModel shell, CommandId id)
    {
        if (shell.Module != MailboxModule.Notes) return false;
        var notes = EnsureNotes(shell);

        switch (id.Value)
        {
            case "notes.new":
                _ = NewNoteAsync(shell);
                return true;

            case "notes.open" when notes.Selected is { } open:
                _ = OpenNoteAsync(shell, open);
                return true;

            case "notes.delete" when notes.Selected is { } gone:
                DeleteNote(shell, gone);
                return true;

            case "notes.view.icons":
                notes.SetView(NoteArrangement.Icons);
                shell.ModuleStatusLeft = notes.Status;
                return true;

            case "notes.view.list":
                notes.SetView(NoteArrangement.List);
                shell.ModuleStatusLeft = notes.Status;
                return true;

            case "notes.view.week":
                notes.SetView(NoteArrangement.LastSevenDays);
                shell.ModuleStatusLeft = notes.Status;
                return true;

            case "notes.forward" when notes.Selected is { } sent:
                ForwardNote(shell, sent);
                return true;

            // The colour of a note is the colour of the category on it, so this is also how a
            // note is recoloured.
            case "notes.categorize":
                CategorizeNote(shell);
                return true;

            case "notes.new.items":
                ShowNewItemsMenu();
                return true;

            case "notes.moveto":
                MoveNote(shell);
                return true;

            default:
                return false;
        }
    }

    // ---- Writing -------------------------------------------------------------------------------

    /// <summary>
    /// Writes a note into a folder and refreshes what is showing it.
    /// </summary>
    /// <remarks>
    /// The same shape <see cref="SaveTask"/> has: the change is queued rather than sent, so an
    /// edit made with the network down is a longer queue and not a lost edit.
    /// </remarks>
    internal PimItem SaveNote(JournalEntry note, PimItem? existing = null, long? collectionId = null)
    {
        var folder = collectionId ?? EnsureNotes((ShellViewModel)DataContext!).DefaultFolder().Id;
        var row = PimJournalCodec.ToItem(note, folder, existing);

        // Guarded, because a write that will not go must not look like one that did: see
        // MainWindow.Persisted.
        var written = Persisted("The note", () => existing is null ? App.Pim.AddItem(row) : Store(row));

        App.PimSync.QueuePut(written);
        _noteModule?.Reload();
        _journalModule?.Reload();
        return written;

        PimItem Store(PimItem item)
        {
            App.Pim.UpdateItem(item);
            return item;
        }
    }

    /// <summary>
    /// Move: the folders this module shares with Journal, in a menu under the button.
    /// </summary>
    /// <remarks>
    /// A note and a journal entry are one component in one kind of collection, so the folders on
    /// offer are every one of them — which is also why the entry is on the Notes bar and the move
    /// refreshes both modules.
    /// <para>
    /// The move itself is <see cref="PimSyncService.Move"/>, which is a delete there and a create
    /// here rather than a change of column, because that is what a move is to a server.
    /// </para>
    /// </remarks>
    private void MoveNote(ShellViewModel shell)
    {
        var notes = EnsureNotes(shell);
        if (notes.Selected is not { } row || App.Pim.Item(row.ItemId) is not { } item)
        {
            shell.StatusRight = "Select a note first.";
            return;
        }

        var folders = App.Pim.Collections(CollectionKind.Journal).Where(f => f.Id != item.CollectionId).ToList();
        if (folders.Count == 0)
        {
            shell.StatusRight = "There is nowhere else to keep a note: this is the only folder.";
            return;
        }

        // A menu is a surface no capture can show, so the harness names the folder instead and
        // the store is read back — the same bargain the Categorize menu makes.
        if (Environment.GetEnvironmentVariable("MAILBOX_MOVE")?.Trim() is { Length: > 0 } posed)
        {
            if (folders.FirstOrDefault(f => f.DisplayName.Contains(posed, StringComparison.OrdinalIgnoreCase)) is not { } wanted)
            {
                Log.Info($"Harness: no folder matching “{posed}” to move “{row.Title}” to.");
                return;
            }

            MoveNoteTo(shell, item, wanted);
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var folder in folders)
        {
            var entry = new MenuItem { Header = folder.DisplayName };
            var chosen = folder;
            entry.Click += (_, _) => MoveNoteTo(shell, item, chosen);
            flyout.Items.Add(entry);
        }

        MenuProbe.Show("the move-note menu", flyout, _ribbon ?? (Control)this, atPointer: true);
    }

    private void MoveNoteTo(ShellViewModel shell, PimItem item, Collection folder)
    {
        var moved = App.PimSync.Move(item, folder.Id);
        _noteModule?.Reload();
        _journalModule?.Reload();

        shell.StatusRight = $"“{item.Summary}” moved to {folder.DisplayName}.";
        Log.Info($"Note {item.Id} moved to {folder.DisplayName} as {moved.Id}; "
            + $"the old row is {(App.Pim.Item(item.Id) is { } old ? old.SyncState.ToString() : "gone")}.");
    }

    private void DeleteNote(ShellViewModel shell, NoteRow row)
    {
        if (App.Pim.Item(row.ItemId) is not { } item) return;

        // Remove, not Delete: on a server-backed folder the row is kept, marked and queued, so a
        // delete made offline still reaches the server.
        App.PimSync.Remove(item);
        _noteModule?.Reload();

        shell.StatusRight = $"“{row.Title}” deleted.";
        Log.Info($"Note {item.Id} deleted.");
    }

    /// <summary>
    /// Forward: the note's text becomes a message, which is what the reference sends — the note
    /// itself is not an attachment anybody else could read.
    /// </summary>
    private void ForwardNote(ShellViewModel shell, NoteRow row)
    {
        NewMessage(new Mailbox.Core.Compose.MailtoLink([], [], [], row.Title, row.Entry.Description));
        shell.StatusRight = $"“{row.Title}” ready to send.";
    }

    /// <summary>Opens the note window on a new note, and writes it when it is closed.</summary>
    private async Task NewNoteAsync(ShellViewModel shell)
    {
        var notes = EnsureNotes(shell);
        var window = new NoteWindow(new JournalEntry
        {
            Uid = JournalEntry.NewUid(),
            When = EventTime.At(Now(), TimeZoneInfo.Local.Id),
            LastModified = Mailbox.Core.PosedClock.UtcNow,
        });

        WirePhase9AForm(window);
        await window.ShowDialog(this);
        if (window.Deleted) return;
        if (window.Result is not { } made) return;

        // An empty note is not a note: the reference throws away one closed without a word in it.
        if (made.Description.Trim().Length == 0)
        {
            shell.StatusRight = "The note was empty, so nothing was kept.";
            return;
        }

        SaveNote(made, collectionId: notes.DefaultFolder().Id);
        shell.StatusRight = $"“{made.Titled()}” added.";

        if (window.Forwarded)
        {
            NewMessage(new Mailbox.Core.Compose.MailtoLink([], [], [], made.Titled(), made.Description));
            shell.StatusRight = $"“{made.Titled()}” ready to send.";
        }
    }

    /// <summary>Opens a note that is already on the wall.</summary>
    /// <remarks>
    /// A note read and closed is not a note edited. Closing is the only save this window has, so
    /// it collects what it holds whatever happened and stamps a new modified time on it — and
    /// without the comparison below, opening a note to read it rewrote the row, moved its modified
    /// time and queued a PUT, which on a folder shared with another client is this application
    /// offering to overwrite somebody else's change with the text it had just read.
    /// </remarks>
    private async Task OpenNoteAsync(ShellViewModel shell, NoteRow row)
    {
        if (App.Pim.Item(row.ItemId) is not { } item) return;

        var before = PimJournalCodec.FromItem(item);
        var window = new NoteWindow(before);
        WirePhase9AForm(window);
        await window.ShowDialog(this);

        // The icon menu's Delete is a close too, so it is answered before the save-on-close.
        if (window.Deleted)
        {
            DeleteNote(shell, row);
            return;
        }

        if (window.Result is not { } edited) return;
        if (!Unchanged(before, edited))
        {
            SaveNote(edited, item, item.CollectionId);
            shell.StatusRight = $"“{edited.Titled()}” saved.";
        }

        // Forward composes from what the window held when it closed, saved or not.
        if (window.Forwarded)
        {
            NewMessage(new Mailbox.Core.Compose.MailtoLink([], [], [], edited.Titled(), edited.Description));
            shell.StatusRight = $"“{edited.Titled()}” ready to send.";
        }
    }

    /// <summary>
    /// Whether the window gave back the note it was given. The title is compared as well as the
    /// writing, so a note whose stored title has drifted from its own first line is still put
    /// right by being opened.
    /// </summary>
    private static bool Unchanged(JournalEntry before, JournalEntry after)
        => string.Equals(before.Description, after.Description, StringComparison.Ordinal)
           && string.Equals(before.Summary, after.Summary, StringComparison.Ordinal)
           && before.Categories.SequenceEqual(after.Categories, StringComparer.Ordinal);

    /// <summary>The clock the module writes with, which a pinned day moves so a capture repeats.</summary>
    private static DateTime Now()
        => CalendarNow ?? CalendarToday.ToDateTime(new TimeOnly(9, 0));

    /// <summary>
    /// The Notes harness poses: the module opened on a chosen arrangement, and what the wall
    /// holds read back — a drawn view cannot be inspected any other way.
    /// </summary>
    private void PoseNotes(ShellViewModel shell)
    {
        var notes = EnsureNotes(shell);

        if (Environment.GetEnvironmentVariable("MAILBOX_NOTE_VIEW")?.Trim().ToLowerInvariant() is { Length: > 0 } view)
        {
            notes.SetView(view switch
            {
                "list" or "notes-list" => NoteArrangement.List,
                "week" or "last7" or "lastsevendays" => NoteArrangement.LastSevenDays,
                _ => NoteArrangement.Icons,
            });
        }

        Log.Info($"Harness: notes showing {notes.Arrangement}, {notes.Status}.");
        Log.Info($"Harness: notes pane lists [{string.Join(" | ", notes.PaneNames)}].");
        foreach (var row in notes.Rows)
        {
            Log.Info($"Harness: note “{row.Title}” — {row.MadeText(CalendarToday, CultureInfo.InvariantCulture)}"
                + (row.Categories.Count > 0 ? $", {string.Join("/", row.Categories)}" : string.Empty) + ".");
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_NOTE_PRESS") is { Length: > 0 } press)
        {
            PressNote(shell, notes, press.Trim());
        }

        WirePhase9ADoors(shell);
    }

    /// <summary>
    /// Presses one thing on the wall: <c>select:part of a title</c> picks that note,
    /// <c>activate:…</c> double-clicks it, <c>hover:…</c> puts the pointer over it, <c>open:…</c>
    /// opens it without the gesture, and <c>new</c> double-clicks the empty wall, which is the
    /// reference's own way of making one. The store is read back afterwards, which is the claim.
    /// </summary>
    /// <remarks>
    /// <c>new</c> used to call the method the gesture calls, and said in this comment that it
    /// double-clicked the wall. It does now: the branch in <see cref="NotesView"/> that answers a
    /// second click on empty space — the only way a module with nothing in it gets its first note
    /// — had never been reached by anything.
    /// </remarks>
    private void PressNote(ShellViewModel shell, NotesWorkspace notes, string spec)
    {
        // The window's own layout, not the view's: a control lays out inside its parent, and the
        // module has only just been put in one — a view of zero width hits nothing.
        UpdateLayout();

        if (spec.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            CaptureNextWindow();
            DoubleClick(notes.View, notes.View.EmptySpot());
            Log.Info("Harness: the empty wall was double-clicked.");
            return;
        }

        var wanted = spec.Contains(':', StringComparison.Ordinal) ? spec[(spec.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim() : spec;

        // dblat:x,y — two clicks at a point in the view's own coordinates rather than at a note.
        // The wall answers a second click on anything that is not a note, and "anything" includes
        // parts of the view that are not the wall, so where the gesture lands has to be sayable.
        if (spec.StartsWith("dblat:", StringComparison.OrdinalIgnoreCase))
        {
            var pair = wanted.Split(',', StringSplitOptions.TrimEntries);
            if (pair.Length != 2
                || !double.TryParse(pair[0], CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(pair[1], CultureInfo.InvariantCulture, out var y))
            {
                Log.Info($"Harness: “{wanted}” is not a point — say dblat:x,y.");
                return;
            }

            CaptureNextWindow();
            DoubleClick(notes.View, new Avalonia.Point(x, y));
            Log.Info($"Harness: the view was double-clicked at {x:0},{y:0}.");
            return;
        }

        var row = notes.Rows.FirstOrDefault(r => r.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            Log.Info($"Harness: no note matching “{wanted}” is on the wall.");
            return;
        }

        if (spec.StartsWith("open:", StringComparison.OrdinalIgnoreCase))
        {
            CaptureNextWindow();
            _ = OpenNoteAsync(shell, row);
            return;
        }

        // Pressed where the view really drew it rather than called directly.
        if (notes.View.BoxOf(row.ItemId) is not { } box)
        {
            Log.Info($"Harness: “{row.Title}” was not drawn — the wall may not have laid out.");
            return;
        }

        if (spec.StartsWith("hover:", StringComparison.OrdinalIgnoreCase))
        {
            Hover(notes.View, box.Center);
            Log.Info($"Harness: the pointer is over “{row.Title}” at {box.Center.X:0},{box.Center.Y:0}.");
            return;
        }

        if (spec.StartsWith("activate:", StringComparison.OrdinalIgnoreCase))
        {
            CaptureNextWindow();
            DoubleClick(notes.View, box.Center);
            Log.Info($"Harness: “{row.Title}” was double-clicked; the wall's selection is now "
                + $"“{notes.Selected?.Title ?? "—"}”.");
            return;
        }

        Press(notes.View, box.Center);
        Log.Info($"Harness: the wall's selection is now “{notes.Selected?.Title ?? "—"}”.");
    }
}
