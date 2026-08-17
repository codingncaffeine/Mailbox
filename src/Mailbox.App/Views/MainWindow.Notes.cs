using System.Globalization;
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
    private static RibbonLayout NotesRibbon() => App.RibbonEdits.Apply(NotesRibbonLayout.Build());

    private NotesWorkspace EnsureNotes(ShellViewModel shell)
    {
        if (_noteModule is not null) return _noteModule;

        var workspace = new NotesWorkspace(App.Pim, CalendarToday)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.Changed += (_, _) => shell.ModuleStatusLeft = workspace.Status;
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

            case "notes.categorize":
                // The colour of a note is the colour of the category on it, and the categories
                // are one set across the modules — which is Phase 14. Until then a note is
                // recoloured on its own window, and saying so beats a menu of nothing.
                shell.StatusRight = "Categories are one set across the modules, which arrives with Phase 14 — a note's own window sets its category meanwhile.";
                return true;

            case "notes.moveto":
                shell.StatusRight = "Moving a note between folders arrives with the folder list this module shares with Journal.";
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
        var written = existing is null ? App.Pim.AddItem(row) : Store(row);

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
            LastModified = DateTimeOffset.UtcNow,
        });

        await window.ShowDialog(this);
        if (window.Result is not { } made) return;

        // An empty note is not a note: the reference throws away one closed without a word in it.
        if (made.Description.Trim().Length == 0)
        {
            shell.StatusRight = "The note was empty, so nothing was kept.";
            return;
        }

        SaveNote(made, collectionId: notes.DefaultFolder().Id);
        shell.StatusRight = $"“{made.Titled()}” added.";
    }

    /// <summary>Opens a note that is already on the wall.</summary>
    private async Task OpenNoteAsync(ShellViewModel shell, NoteRow row)
    {
        if (App.Pim.Item(row.ItemId) is not { } item) return;

        var window = new NoteWindow(PimJournalCodec.FromItem(item));
        await window.ShowDialog(this);

        if (window.Result is not { } edited) return;
        SaveNote(edited, item, item.CollectionId);
        shell.StatusRight = $"“{edited.Titled()}” saved.";
    }

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
        foreach (var row in notes.Rows)
        {
            Log.Info($"Harness: note “{row.Title}” — {row.MadeText(CalendarToday, CultureInfo.InvariantCulture)}"
                + (row.Categories.Count > 0 ? $", {string.Join("/", row.Categories)}" : string.Empty) + ".");
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_NOTE_PRESS") is { Length: > 0 } press)
        {
            PressNote(shell, notes, press.Trim());
        }
    }

    /// <summary>
    /// Presses one thing on the wall: <c>open:part of a title</c> opens that note,
    /// <c>select:…</c> picks it, and <c>new</c> double-clicks the wall itself, which is the
    /// reference's own way of making one. The store is read back afterwards, which is the claim.
    /// </summary>
    private void PressNote(ShellViewModel shell, NotesWorkspace notes, string spec)
    {
        // The window's own layout, not the view's: a control lays out inside its parent, and the
        // module has only just been put in one — a view of zero width hits nothing.
        UpdateLayout();

        if (spec.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            CaptureNextWindow();
            _ = NewNoteAsync(shell);
            return;
        }

        var wanted = spec.Contains(':', StringComparison.Ordinal) ? spec[(spec.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim() : spec;
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

        Press(notes.View, box.Center);
        Log.Info($"Harness: the wall's selection is now “{notes.Selected?.Title ?? "—"}”.");
    }
}
