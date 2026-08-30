using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Mailbox.Controls.Journal;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// Two doors onto the Journal module: where the timeline actually put everything, and what its
/// entry form does to a value on the way past.
/// </summary>
/// <remarks>
/// The module's existing pose says which arrangement is showing and lists what the journal holds.
/// Neither is the question. A timeline is a claim about <i>position</i> — this entry hangs under
/// that hour, this box is as wide as the time it stands for, today's column is the shaded one —
/// and a drawn view has no children to walk, so every one of those claims was previously settled
/// by looking at a picture. <c>MAILBOX_JOURNAL_LAYOUT</c> hands over the same numbers
/// <c>Render</c> draws with: the columns and their bands, the lane and the box of every entry,
/// and where the entry's own start moment falls for comparison.
/// <para>
/// <c>MAILBOX_JOURNAL_FORM</c> is the second half. Every seed in this project writes through the
/// repository, so no journal entry had ever been through the form that edits one: the duration
/// list had never been chosen from, and the timer — the one thing this module exists for — had
/// never been pressed by anything. The steps run against the real controls, the timer counts
/// against a clock the pose moves so an elapsed duration is the same number twice, and what the
/// store holds afterwards is read back and written out.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Wires the form door onto a journal entry window the shell has just built. Called wherever
    /// one is constructed, before it is shown.
    /// </summary>
    internal static void WireJournalFormDoor(JournalEntryWindow window)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_JOURNAL_FORM") is not { Length: > 0 } steps) return;

        window.PoseTimerClock();
        window.Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                Log.Info($"Harness: journal form opened — {window.FormState()}.");

                foreach (var step in steps.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    // "probe" is not a control, so it is answered here rather than by the form.
                    if (string.Equals(step, "probe", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Info($"Harness: journal form — {window.FormState()}.");
                        continue;
                    }

                    Log.Info($"Harness: journal form step “{step}” — {window.Pose(step)}.");
                }

                Log.Info(window.IsVisible
                    ? $"Harness: journal form still open — {window.FormState()}."
                    : $"Harness: journal form closed — {(window.Deleted ? "Delete" : window.Result is null ? "no result" : "saved")}.");
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Writes where the Journal module put everything: the columns of the timeline, the lane and
    /// box of every entry hung on it, or the lines of whichever list is showing instead.
    /// </summary>
    private void DumpJournalLayout(JournalWorkspace journal)
    {
        var view = journal.View;
        UpdateLayout();

        Log.Info($"Harness: journal view {view.Bounds.Width:0}x{view.Bounds.Height:0}, "
                 + $"{view.Arrangement} at {view.Scale}, anchor {view.Anchor:yyyy-MM-dd}, today {view.Today:yyyy-MM-dd}, "
                 + $"week starts {view.FirstDayOfWeek}, showing {view.Count} of {view.Rows.Count}.");

        if (JournalBook.IsTimeline(view.Arrangement) && !view.IsSearch)
        {
            DumpTimeline(view);
            return;
        }

        foreach (var (key, label, left, width, sort) in view.HeaderLaid())
        {
            Log.Info($"Harness: journal header “{(label.Length > 0 ? label : key)}” — x {left:0}..{left + width:0} "
                     + $"({width:0} wide){(sort.Length > 0 ? $", sorted {sort}" : string.Empty)}.");
        }

        foreach (var (label, row, box) in view.DrawnLines())
        {
            Log.Info(row is null
                ? $"Harness: journal heading “{label}” — y {box.Y:0}..{box.Bottom:0} ({box.Height:0} tall)."
                : $"Harness: journal row “{row.Subject}” — y {box.Y:0}..{box.Bottom:0} ({box.Height:0} tall), "
                  + $"{row.EntryType}, {row.StartText(CultureInfo.InvariantCulture)}"
                  + $", duration “{(row.DurationText(CultureInfo.InvariantCulture) is { Length: > 0 } d ? d : "—")}”"
                  + $", contacts “{(row.Contacts.Length > 0 ? row.Contacts : "—")}”"
                  + $", company “{(row.Company.Length > 0 ? row.Company : "—")}”"
                  + $", categories [{string.Join(" | ", row.Categories)}].");
        }
    }

    /// <summary>The axis, the bands, and what is hung where.</summary>
    private static void DumpTimeline(JournalView view)
    {
        Log.Info($"Harness: journal heading “{view.SpanText()}” — span row {JournalView.SpanRowHeight:0}px "
                 + $"over a day row {JournalView.DayRowHeight:0}px, {JournalView.HeadingHeight:0}px in all; "
                 + $"{view.ColumnCount} columns inset {JournalView.TimelineInset:0}px each side.");

        foreach (var (label, month, box) in view.ScaleBands())
        {
            Log.Info($"Harness: journal month band “{label}” for {month:yyyy-MM} — x {box.X:0.0}..{box.Right:0.0} ({box.Width:0.0} wide).");
        }

        foreach (var (label, collapsed, lanes, entries) in view.BandsLaid())
        {
            Log.Info($"Harness: journal band “{label}” — {entries} entr{(entries == 1 ? "y" : "ies")} in {lanes} lane(s)"
                     + (collapsed ? ", folded." : "."));
        }

        foreach (var (index, label, day, left, width, isToday) in view.Columns())
        {
            Log.Info($"Harness: journal column {index} — “{label}” for {day:yyyy-MM-dd}, "
                     + $"x {left:0.0}..{left + width:0.0} ({width:0.0} wide)"
                     + (isToday ? "  ← today, shaded whole." : "."));
        }

        var drawn = view.DrawnRows().Select(d => d.Row.ItemId).ToHashSet();

        foreach (var (row, left, width, lane) in view.Laid())
        {
            var box = view.BoxOf(row.ItemId);
            var starts = view.XOf(row.Start);
            var ends = row.Duration is { } span && span > TimeSpan.Zero ? view.XOf(row.Start + span) : starts;

            Log.Info($"Harness: journal entry “{row.Subject}” — lane {lane}, "
                     + $"x {left:0.0}..{left + width:0.0} ({width:0.0} wide); "
                     + $"its {(row.DurationText(CultureInfo.InvariantCulture) is { Length: > 0 } d ? d : "no duration")} "
                     + $"is {ends - starts:0.0}px, so the box is {width - (ends - starts):0.0}px wider than the time it stands for; "
                     + (box is { } drawnAt
                         ? $"drawn at y {drawnAt.Y:0.0}..{drawnAt.Bottom:0.0}."
                         : $"NOT DRAWN — lane {lane} falls outside the view."));
        }

        if (view.Laid().Count != drawn.Count)
        {
            Log.Info($"Harness: {view.Laid().Count - drawn.Count} of {view.Laid().Count} entries in this span were not drawn.");
        }
    }

    /// <summary>
    /// What the message Forward built is actually carrying: the body's words, and the item
    /// itself on the attachment strip. <c>MAILBOX_COMPOSE_QUEUE</c> presses Send only on
    /// windows the compose poses open themselves, so nothing else could read this one at all.
    /// </summary>
    private static void DumpForwardedEntry()
    {
        var compose = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows.OfType<ComposeWindow>().LastOrDefault();

        if (compose is null)
        {
            Log.Info("Harness: Forward opened no compose window.");
            return;
        }

        var body = compose.BodyText.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ⏎ ", StringComparison.Ordinal).Trim();

        var strip = compose.AttachmentStrip;
        Log.Info($"Harness: forwarded as “{compose.Title}” — {body.Length} characters of body: “{body}”; "
                 + $"attachments {(strip.IsVisible ? $"[{string.Join(" | ", strip.Files)}]" : "none")}.");
    }

    /// <summary>
    /// What the store holds for the journal, read back after a form pose has written to it.
    /// </summary>
    private void DumpJournalStore(string why)
    {
        foreach (var list in App.Pim.Collections(CollectionKind.Journal))
        {
            foreach (var item in App.Pim.Items(list.Id))
            {
                var entry = PimJournalCodec.FromItem(item);
                if (entry.IsNote) continue;

                Log.Info($"Harness: store ({why}) — “{entry.Summary}” in {list.DisplayName}: type “{entry.EntryType}”, "
                         + $"starts {entry.When?.Wall:yyyy-MM-dd HH:mm}, "
                         + $"duration {(entry.Duration is { } d ? JournalCodec.DurationText(d, CultureInfo.InvariantCulture) + $" ({d})" : "none")}, "
                         + $"contacts [{string.Join(" | ", entry.Contacts)}], "
                         + $"company “{(entry.Company.Length > 0 ? entry.Company : "—")}”, "
                         + $"{(entry.IsPrivate ? "private, " : string.Empty)}"
                         + $"categories [{string.Join(" | ", entry.Categories)}], "
                         + $"{item.SyncState}.");
            }
        }
    }
}
