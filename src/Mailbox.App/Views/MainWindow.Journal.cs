using System.Globalization;
using Avalonia.Threading;
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

        workspace.Changed += (_, _) =>
        {
            shell.ModuleStatusLeft = workspace.Status;

            // A module's own selection decides what its ribbon can do, the same way the message
            // list's does.
            RefreshCommandEnablement();
        };
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
                ForwardJournalEntry(shell, sent);
                return true;

            case "journal.today":
                journal.GoToday();

                // Says which span it landed on, as Back and Forward do. Without this the status
                // bar kept whichever span the last Back had named, so Today moved the timeline
                // and left a line beside it stating the week it had just left.
                shell.StatusRight = journal.SpanText;
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
                journal.SetView(JournalArrangement.ByType);
                shell.ModuleStatusLeft = journal.Status;
                return true;

            case "journal.view.bycontact":
                journal.SetView(JournalArrangement.ByContact);
                shell.ModuleStatusLeft = journal.Status;
                return true;

            case "journal.view.bycategory":
                journal.SetView(JournalArrangement.ByCategory);
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
        var written = Persisted("The journal entry", () => existing is null ? App.Pim.AddItem(row) : Store(row));

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

    /// <summary>
    /// Forward: a message with the journal entry attached as an iCalendar file, the way an
    /// appointment forwards.
    /// </summary>
    /// <remarks>
    /// The item travels whole — the VJOURNAL carries the type, the start, the duration, the
    /// contacts and the company, which a subject-and-notes mailto dropped — and the body says the
    /// same things in words for a reader whose client will not open the attachment.
    /// </remarks>
    private void ForwardJournalEntry(ShellViewModel shell, JournalRow row)
    {
        var entry = row.Entry;
        var payload = JournalCodec.SerializeCalendar([entry]);

        var attachment = new MimeKit.TextPart("calendar") { Text = payload };
        attachment.ContentType.Parameters["charset"] = "utf-8";
        attachment.ContentType.Name = SafeName(row.Subject, "journal-entry") + ".ics";
        attachment.ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment)
        {
            FileName = attachment.ContentType.Name,
        };

        var described = string.Join("\n", new[]
        {
            row.Subject,
            $"Entry type: {row.EntryType}",
            $"Start: {row.StartText(CultureInfo.CurrentCulture)}",
            row.DurationText(CultureInfo.CurrentCulture) is { Length: > 0 } duration ? $"Duration: {duration}" : null,
            row.Contacts.Length > 0 ? $"Contacts: {row.Contacts}" : null,
            row.Company.Length > 0 ? $"Company: {row.Company}" : null,
            entry.Description.Length > 0 ? "\n" + entry.Description : null,
        }.Where(line => line is not null));

        var draft = new Mailbox.Rendering.ReplyDraft
        {
            Subject = "FW: " + row.Subject,
            QuotedText = described,
            Attachments = [new Mailbox.Rendering.CarriedPart(attachment.ContentType.Name, "text/calendar", attachment)],
        };

        NewMessage(draft, Mailbox.Rendering.ReplyKind.Forward);
        shell.StatusRight = $"“{row.Subject}” is attached to a new message.";
        Log.Info($"Journal: forwarding “{row.Subject}” as {attachment.ContentType.Name}.");
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
            LastModified = Mailbox.Core.PosedClock.UtcNow,
        });

        WireJournalFormDoor(window);
        await window.ShowDialog(this);
        if (window.Result is not { } made) return;

        SaveJournalEntry(made, collectionId: journal.DefaultJournal().Id);
        shell.StatusRight = $"“{made.Summary}” recorded.";
    }

    private async Task OpenJournalEntryAsync(ShellViewModel shell, JournalRow row)
    {
        if (App.Pim.Item(row.ItemId) is not { } item) return;

        var window = new JournalEntryWindow(PimJournalCodec.FromItem(item));
        WireJournalFormDoor(window);
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
                "bycontact" or "contact" => JournalArrangement.ByContact,
                "bycategory" or "category" => JournalArrangement.ByCategory,
                _ => JournalArrangement.ByType,
            });
        }

        // MAILBOX_SELECT names which entry, as it does in every module: a command pressed
        // afterwards acts on the selection, and a run cannot click the timeline.
        if (Environment.GetEnvironmentVariable("MAILBOX_SELECT") is { Length: > 0 } wanted)
        {
            Log.Info($"Harness: journal selection — {journal.PoseSelect(wanted)}.");
        }

        Log.Info($"Harness: journal showing {journal.Arrangement} at {journal.Scale} — {journal.SpanText}, {journal.Status}.");
        Log.Info($"Harness: journal pane lists [{string.Join(" | ", journal.PaneNames)}].");
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

        // Where everything ended up, and what the store holds once a form pose has finished with
        // it. Both below the Background the poses above run at, so each measures the arrangement
        // they made rather than the one they were about to replace.
        if (Environment.GetEnvironmentVariable("MAILBOX_JOURNAL_LAYOUT") is { Length: > 0 })
        {
            Dispatcher.UIThread.Post(() => DumpJournalLayout(journal), DispatcherPriority.ApplicationIdle);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_JOURNAL_STORE") is { Length: > 0 } why)
        {
            Dispatcher.UIThread.Post(() => DumpJournalStore(why.Trim()), DispatcherPriority.ApplicationIdle);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_JOURNAL_FORWARD") is { Length: > 0 })
        {
            Dispatcher.UIThread.Post(DumpForwardedEntry, DispatcherPriority.ApplicationIdle);
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

        if (spec.StartsWith("band:", StringComparison.OrdinalIgnoreCase))
        {
            var folded = journal.View.ToggleBand(wanted);
            Log.Info($"Harness: the “{wanted}” band is now {(folded ? "folded" : "open")}.");
            return;
        }

        // Presses the upper scale's month band through the real pointer path, so the drop-down
        // it opens is measured rather than assumed.
        if (spec.StartsWith("month:", StringComparison.OrdinalIgnoreCase))
        {
            var band = journal.View.ScaleBands().FirstOrDefault(b => b.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (band.Label is null or "")
            {
                Log.Info($"Harness: no month band matching “{wanted}” is on the scale.");
                return;
            }

            Press(journal.View, band.Box.Center);
            Log.Info($"Harness: pressed the “{band.Label}” band.");
            return;
        }

        if (spec.StartsWith("sort:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = wanted.Split(',', StringSplitOptions.TrimEntries);
            journal.View.SortBy(parts[0], parts.Length > 1 && parts[1].StartsWith("desc", StringComparison.OrdinalIgnoreCase));
            Log.Info($"Harness: the journal table is sorted by “{parts[0]}”.");
            return;
        }

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
