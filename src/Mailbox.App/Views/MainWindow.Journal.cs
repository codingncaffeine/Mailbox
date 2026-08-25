using System.Globalization;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Journal;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The Journal module in the shell: switching to it, the workspace it puts in the window, and the
/// commands its ribbon presses.
/// </summary>
/// <remarks>
/// A partial of the shell, as the other four modules' halves are. It shares its collections with
/// Notes — one component, one table, two readings — so a write from either refreshes both.
/// </remarks>
public partial class MainWindow
{
    private JournalWorkspace? _journalModule;

    /// <summary>The Journal ribbon: the shipped layout with the reader's edits over it.</summary>
    private static RibbonLayout JournalRibbon() => App.RibbonEdits.Apply(App.Plugins.InjectRibbon(JournalRibbonLayout.Build()));

    private JournalWorkspace EnsureJournal(ShellViewModel shell)
    {
        if (_journalModule is not null) return _journalModule;

        var workspace = new JournalWorkspace(App.Pim, CalendarToday, App.CalendarOptions.FirstDayOfWeek)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.Changed += (_, _) => shell.ModuleStatusLeft = workspace.Status;
        workspace.EntryOpened += (_, row) => _ = OpenJournalEntryAsync(shell, row);

        _journalModule = workspace;
        return workspace;
    }

    /// <summary>
    /// The Journal module's commands. Returns false for anything it does not own, so the shell's
    /// own list carries on.
    /// </summary>
    private bool RunJournalCommand(ShellViewModel shell, CommandId id)
    {
        if (shell.Module != MailboxModule.Journal) return false;
        var journal = EnsureJournal(shell);

        switch (id.Value)
        {
            case "journal.new":
                _ = NewJournalEntryAsync(shell);
                return true;

            case "journal.open" when journal.Selected is { } open:
                _ = OpenJournalEntryAsync(shell, open);
                return true;

            case "journal.delete" when journal.Selected is { } gone:
                DeleteJournalEntry(shell, gone);
                return true;

            case "journal.forward" when journal.Selected is { } sent:
                NewMessage(new Mailbox.Core.Compose.MailtoLink([], [], [], sent.Subject, sent.Entry.Description));
                shell.StatusRight = $"“{sent.Subject}” ready to send.";
                return true;

            case "journal.today":
                journal.GoToday();
                return true;

            case "journal.back":
                journal.Step(-1);
                shell.StatusRight = journal.SpanText;
                return true;

            case "journal.next":
                journal.Step(1);
                shell.StatusRight = journal.SpanText;
                return true;

            case "journal.scale.day":
                journal.SetScale(TimelineScale.Day);
                return true;

            case "journal.scale.week":
                journal.SetScale(TimelineScale.Week);
                return true;

            case "journal.scale.month":
                journal.SetScale(TimelineScale.Month);
                return true;

            case "journal.view.timeline":
                journal.SetView(JournalArrangement.Timeline);
                shell.ModuleStatusLeft = journal.Status;
                return true;

            case "journal.view.entries":
                journal.SetView(JournalArrangement.EntryList);
                shell.ModuleStatusLeft = journal.Status;
                return true;

            case "journal.view.calls":
                journal.SetView(JournalArrangement.PhoneCalls);
                shell.ModuleStatusLeft = journal.Status;
                return true;

            case "journal.view.week":
                journal.SetView(JournalArrangement.LastSevenDays);
                shell.ModuleStatusLeft = journal.Status;
                return true;

            case "journal.new.items":
                ShowNewItemsMenu();
                return true;

            case "journal.categorize":
                CategorizeJournalEntry(shell);
                return true;

            default:
                return false;
        }
    }

    // ---- Writing -------------------------------------------------------------------------------

    /// <summary>Writes an entry into a journal and refreshes what is showing it.</summary>
    internal PimItem SaveJournalEntry(JournalEntry entry, PimItem? existing = null, long? collectionId = null)
    {
        var journal = collectionId ?? EnsureJournal((ShellViewModel)DataContext!).DefaultJournal().Id;
        var row = PimJournalCodec.ToItem(entry, journal, existing);
        var written = existing is null ? App.Pim.AddItem(row) : Store(row);

        App.PimSync.QueuePut(written);
        _journalModule?.Reload();
        _noteModule?.Reload();
        return written;

        PimItem Store(PimItem item)
        {
            App.Pim.UpdateItem(item);
            return item;
        }
    }

    private void DeleteJournalEntry(ShellViewModel shell, JournalRow row)
    {
        if (App.Pim.Item(row.ItemId) is not { } item) return;

        App.PimSync.Remove(item);
        _journalModule?.Reload();

        shell.StatusRight = $"“{row.Subject}” deleted.";
        Log.Info($"Journal entry {item.Id} deleted.");
    }

    private async Task NewJournalEntryAsync(ShellViewModel shell)
    {
        var journal = EnsureJournal(shell);
        var window = new JournalEntryWindow(new JournalEntry
        {
            Uid = JournalEntry.NewUid(),

            // A journal entry is a record of something done, so it starts as a phone call rather
            // than as a note: a note is what the other module makes.
            EntryType = JournalBook.PhoneCall,
            When = EventTime.At(Now(), TimeZoneInfo.Local.Id),
            LastModified = DateTimeOffset.UtcNow,
        });

        await window.ShowDialog(this);
        if (window.Result is not { } made) return;

        SaveJournalEntry(made, collectionId: journal.DefaultJournal().Id);
        shell.StatusRight = $"“{made.Summary}” recorded.";
    }

    private async Task OpenJournalEntryAsync(ShellViewModel shell, JournalRow row)
    {
        if (App.Pim.Item(row.ItemId) is not { } item) return;

        var window = new JournalEntryWindow(PimJournalCodec.FromItem(item));
        await window.ShowDialog(this);

        if (window.Deleted)
        {
            DeleteJournalEntry(shell, row);
            return;
        }

        if (window.Result is not { } edited) return;
        SaveJournalEntry(edited, item, item.CollectionId);
        shell.StatusRight = $"“{edited.Summary}” saved.";
    }

    /// <summary>
    /// The Journal harness poses: the module opened on a chosen arrangement and scale, and what
    /// the timeline holds read back — a drawn view cannot be inspected any other way.
    /// </summary>
    private void PoseJournal(ShellViewModel shell)
    {
        var journal = EnsureJournal(shell);

        if (Environment.GetEnvironmentVariable("MAILBOX_JOURNAL_SCALE")?.Trim().ToLowerInvariant() is { Length: > 0 } scale)
        {
            journal.SetScale(scale switch
            {
                "day" => TimelineScale.Day,
                "month" => TimelineScale.Month,
                _ => TimelineScale.Week,
            });
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_JOURNAL_VIEW")?.Trim().ToLowerInvariant() is { Length: > 0 } view)
        {
            journal.SetView(view switch
            {
                "entries" or "entrylist" or "list" => JournalArrangement.EntryList,
                "calls" or "phone" => JournalArrangement.PhoneCalls,
                "week" or "last7" or "lastsevendays" => JournalArrangement.LastSevenDays,
                _ => JournalArrangement.Timeline,
            });
        }

        Log.Info($"Harness: journal showing {journal.Arrangement} at {journal.Scale} — {journal.SpanText}, {journal.Status}.");
        foreach (var row in journal.Rows)
        {
            Log.Info($"Harness: entry “{row.Subject}” — {row.EntryType}, {row.StartText(CultureInfo.InvariantCulture)}"
                + (row.DurationText(CultureInfo.InvariantCulture) is { Length: > 0 } d ? $", {d}" : string.Empty)
                + (row.Contacts.Length > 0 ? $", with {row.Contacts}" : string.Empty) + ".");
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_JOURNAL_PRESS") is { Length: > 0 } press)
        {
            PressJournal(shell, journal, press.Trim());
        }
    }

    /// <summary>
    /// Presses one thing in the journal: <c>open:part of a subject</c> opens that entry,
    /// <c>select:…</c> picks it, and <c>new</c> opens the entry window. The store is read back
    /// afterwards, which is the claim.
    /// </summary>
    private void PressJournal(ShellViewModel shell, JournalWorkspace journal, string spec)
    {
        // The window's own layout, not the view's: a view of zero width hits nothing.
        UpdateLayout();

        if (spec.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            CaptureNextWindow();
            _ = NewJournalEntryAsync(shell);
            return;
        }

        var wanted = spec.Contains(':', StringComparison.Ordinal) ? spec[(spec.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim() : spec;
        var row = journal.Rows.FirstOrDefault(r => r.Subject.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            Log.Info($"Harness: no entry matching “{wanted}” is in the journal.");
            return;
        }

        if (spec.StartsWith("open:", StringComparison.OrdinalIgnoreCase))
        {
            CaptureNextWindow();
            _ = OpenJournalEntryAsync(shell, row);
            return;
        }

        if (journal.View.BoxOf(row.ItemId) is not { } box)
        {
            Log.Info($"Harness: “{row.Subject}” was not drawn — it may be outside the span on show.");
            return;
        }

        Press(journal.View, box.Center);
        Log.Info($"Harness: the journal's selection is now “{journal.Selected?.Subject ?? "—"}”.");
    }
}
