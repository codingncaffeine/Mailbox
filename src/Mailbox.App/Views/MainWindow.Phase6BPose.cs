using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Calendar;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The doors time, invitations and the calendar's edges needed before any of them could be
/// checked: what clock the grid is on, what the second column of hours reads, what a calendar
/// subscription does when it is refreshed, and where an export goes.
/// </summary>
/// <remarks>
/// Each of these exists because the surface answers the wrong question to a camera.
/// <list type="bullet">
/// <item><description>An appointment written in another zone is a picture of a coloured
/// rectangle: the claim is that the rectangle is at the hour <em>this</em> clock reads at that
/// instant, and only the two numbers together say so. The same picture on the two days a year a
/// zone moves its clocks is the only evidence that survives the change, and a rectangle cannot
/// carry it.</description></item>
/// <item><description>A subscription refreshing is three HTTP requests and a store write. The
/// window shows a calendar either way.</description></item>
/// <item><description>An export hands over to the desktop's file picker, which a headless run
/// has no way to answer — so "it saved nothing" and "it did nothing" were the same
/// picture.</description></item>
/// </list>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this lane's doors. Called once from the shell's constructor.</summary>
    private void WirePhase6BDoors()
    {
        // Runs the real calendar half of a send/receive — the DAV engine, subscriptions and the
        // conflict prompt — without the mail half, which a seeded store has no server for and
        // which spends thirty seconds failing to find one.
        if (Environment.GetEnvironmentVariable("MAILBOX_CALENDAR_SYNC") is { Length: > 0 } sync)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => _ = PoseCalendarSyncAsync(sync),
                DispatcherPriority.Background);
        }

        // Ticking a calendar off the overlay, which is a press on a row in the navigation pane
        // and so is not reachable by any other pose.
        if (Environment.GetEnvironmentVariable("MAILBOX_CALENDAR_SHOW") is { Length: > 0 } shown)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => PressCalendarRows(shown),
                DispatcherPriority.Background);
        }

        // Last of all, at Background: a report taken before MAILBOX_RUN has pressed anything, or
        // before a sync has landed, describes the calendar as it was rather than as the pose left
        // it — which is the shape of evidence that proves the pose never ran.
        if (Environment.GetEnvironmentVariable("MAILBOX_CALENDAR_REPORT") is { Length: > 0 } report)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => Dispatcher.UIThread.Post(() => PoseCalendarReport(report), DispatcherPriority.Background),
                DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Where a Save As picker should write when the harness is driving:
    /// <c>MAILBOX_EXPORT=ics:/tmp/out.ics|vcf:/tmp/out.vcf</c>.
    /// </summary>
    /// <remarks>
    /// The plan records "a file-picker read-back" as a missing door, and this is it for the two
    /// exports whose contents can be checked byte for byte afterwards. Only under
    /// <c>MAILBOX_CAPTURE</c>: a reader running the application normally must always be asked
    /// where a file goes, whatever is in their environment.
    /// </remarks>
    internal static string? HarnessSavePath(string kind)
    {
        if (!WindowCapture.IsRequested) return null;
        if (Environment.GetEnvironmentVariable("MAILBOX_EXPORT") is not { Length: > 0 } spec) return null;

        foreach (var part in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = part.IndexOf(':');
            if (split < 1) continue;
            if (!part[..split].Trim().Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;

            var path = part[(split + 1)..].Trim();
            if (path.Length == 0) continue;
            Log.Info($"Harness: export — {kind} goes to {path} instead of a picker.");
            return path;
        }

        return null;
    }

    // ---- The overlay's own control -----------------------------------------------------------

    /// <summary>
    /// Presses the navigation pane's row for each named calendar, which is what puts one into the
    /// overlay or takes it out again: <c>MAILBOX_CALENDAR_SHOW=Team</c>, or several in order,
    /// which is how the "the last one cannot be hidden" guard is reached at all.
    /// </summary>
    private void PressCalendarRows(string spec)
    {
        foreach (var name in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            PressCalendarRow(name);
        }
    }

    /// <remarks>
    /// Through the row's own <c>PointerPressed</c>, because that is where the handler is — the
    /// tick beside the name is drawn with hit-testing off and does nothing when it is clicked, so
    /// a pose that set its <c>IsChecked</c> would prove a checkbox rather than the pane. The row
    /// is found by the name it shows, since that is all a reader has to aim at either.
    /// </remarks>
    private void PressCalendarRow(string name)
    {
        if (DataContext is not ShellViewModel shell) return;
        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);
        calendar.UpdateLayout();

        // Up from the name rather than down from the pane: the pane is itself a Border holding
        // every row, so looking downwards for "a Border containing this name" finds the pane
        // first — and the pane has no handler, so the press did nothing and the pose read like a
        // row that ignores being clicked.
        var wanted = name.Trim();
        var row = calendar.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBlock>()
            .Where(t => string.Equals(t.Text, wanted, StringComparison.OrdinalIgnoreCase) && t.FontSize > 14)
            .Select(t => t.GetVisualAncestors().OfType<Avalonia.Controls.Border>().FirstOrDefault())
            .FirstOrDefault(b => b is not null);

        if (row is null)
        {
            Log.Warn($"Harness: calendar show — no row named “{wanted}” in the pane. It lists: "
                     + string.Join(", ", App.Pim.Collections(CollectionKind.Events).Select(c => c.DisplayName)) + ".");
            return;
        }

        var pointer = new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, isPrimary: true);
        var properties = new Avalonia.Input.PointerPointProperties(
            Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);

        row.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
            row, pointer, row, new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), 0,
            properties, Avalonia.Input.KeyModifiers.None));

        var after = App.Pim.Collections(CollectionKind.Events)
            .FirstOrDefault(c => string.Equals(c.DisplayName, wanted, StringComparison.OrdinalIgnoreCase));

        Log.Info($"Harness: calendar show — pressed “{wanted}”; it is now "
                 + $"{(after?.IsVisible == true ? "shown" : "hidden")}, and the grid holds "
                 + $"{calendar.Entries.Count} item(s) from "
                 + $"{string.Join(", ", calendar.Entries.Select(e => e.CollectionName).Distinct().Order())}.");
    }

    // ---- The calendar's clocks ---------------------------------------------------------------

    private void PoseCalendarReport(string what)
    {
        if (DataContext is not ShellViewModel shell) return;
        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);
        calendar.UpdateLayout();

        var wanted = what.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();
        var all = wanted.Count == 0 || wanted.Contains("all") || wanted.Contains("1") || wanted.Contains("true");

        // A write made by another door — an import, a sync — reaches the store without going
        // through the calendar's own save, so the grid is still holding what it read on the way
        // in. `reload` is asked for rather than assumed: a report that always reloaded could
        // never tell a view that refreshes itself from one that does not.
        if (wanted.Contains("reload")) calendar.Reload();

        if (all || wanted.Contains("zones")) ReportZones(calendar);
        if (all || wanted.Contains("ruler")) ReportRuler(calendar);
        if (all || wanted.Contains("times")) ReportTimes(calendar);
        if (all || wanted.Contains("collections")) ReportCollections();
        if (all || wanted.Contains("attendees")) ReportAttendees();
    }

    /// <summary>What clock the grid is on, and what the second column of hours is.</summary>
    private void ReportZones(CalendarWorkspace calendar)
    {
        var options = App.CalendarOptions;
        var noon = calendar.Anchor.ToDateTime(new TimeOnly(12, 0));

        Log.Info($"Harness: calendar clock — view {options.TimeZone.Id} "
                 + $"offset {Offset(options.TimeZone, noon)} "
                 + $"label “{(options.TimeZoneLabel.Length > 0 ? options.TimeZoneLabel : TimeZoneChoicesShort(options.TimeZone, noon))}”; "
                 + $"second {(options.SecondTimeZone is { } second ? second.Id : "none")}"
                 + $"{(options.SecondTimeZone is { } s2 ? $" offset {Offset(s2, noon)}" : string.Empty)} "
                 + $"label “{options.SecondTimeZoneLabel}”, shown {options.ShowSecondTimeZone}.");

        if (calendar.TimeGrid is { } grid)
        {
            Log.Info($"Harness: calendar ruler — {TimeGridView.RulerSpanFor(grid.SecondZone is not null):0}px wide, "
                     + $"{(grid.SecondZone is null ? "one column" : "two columns")}, "
                     + $"grid zone {grid.ViewZone.Id}, second {(grid.SecondZone?.Id ?? "none")}.");
        }
    }

    /// <summary>
    /// Every hour the ruler writes, in both columns, through the method the paint itself calls.
    /// </summary>
    private static void ReportRuler(CalendarWorkspace calendar)
    {
        if (calendar.TimeGrid is not { } grid)
        {
            Log.Info($"Harness: calendar ruler — the {calendar.Kind} view has no hour ruler.");
            return;
        }

        for (var hour = 0; hour < 24; hour++)
        {
            var here = grid.HourAt(hour * 60, zone: null);
            var second = grid.SecondZone is { } zone ? grid.HourAt(hour * 60, zone) : (TimeOnly?)null;
            Log.Info($"Harness: calendar ruler row — {here:HH:mm}"
                     + $"{(second is { } elsewhere ? $" | {elsewhere:HH:mm}" : string.Empty)}.");
        }
    }

    /// <summary>
    /// Every entry the view is holding, as written and as drawn.
    /// </summary>
    /// <remarks>
    /// Three times per line, because a zone bug shows up as two of them agreeing and the third
    /// not: what the appointment says (its own wall clock and the zone it was written in), the
    /// instant that comes to, and what the grid's clock reads at that instant — which is where
    /// the chip goes. A line whose stored and drawn times match on a machine in a different zone
    /// is the bug, not the pass.
    /// </remarks>
    private static void ReportTimes(CalendarWorkspace calendar)
    {
        foreach (var entry in calendar.Entries)
        {
            var e = entry.Occurrence.Event;
            Log.Info($"Harness: calendar time — “{entry.Summary}” on {entry.CollectionName}: "
                     + $"written {e.Start.Wall:yyyy-MM-dd HH:mm}–{e.End.Wall:HH:mm} {e.Start.TzId ?? "floating"}, "
                     + $"utc {entry.StartUtc:yyyy-MM-dd HH:mm}Z–{entry.EndUtc:HH:mm}Z, "
                     + $"drawn {entry.StartWall:yyyy-MM-dd HH:mm}–{entry.EndWall:HH:mm} in {entry.Zone.Id}, "
                     + $"{(entry.AllDay ? "all-day" : "timed")}, {entry.Busy.ToString().ToLowerInvariant()}, "
                     + $"status {(e.Status.Length > 0 ? e.Status : "none")}, "
                     + $"chip {(entry.Colour is { } colour ? colour.ToString() : "the theme's default")}.");
        }
    }

    /// <summary>Every calendar the store holds, and which of them the grid is drawing.</summary>
    private static void ReportCollections()
    {
        foreach (var collection in App.Pim.Collections(CollectionKind.Events))
        {
            Log.Info($"Harness: calendar collection — “{collection.DisplayName}” ({collection.Id}) "
                     + $"colour {collection.Color}, {(collection.IsVisible ? "shown" : "hidden")}, "
                     + $"{(collection.IsReadOnly ? "read-only" : "writable")}, "
                     + $"{(collection.DavUrl is { Length: > 0 } url ? url : "no address")}, "
                     + $"{App.Pim.Items(collection.Id).Count} row(s), "
                     + $"checked {collection.LastCheckedUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "never"}.");
        }
    }

    /// <summary>
    /// Who was asked to each meeting the store holds, and what they have said.
    /// </summary>
    /// <remarks>
    /// The organizer's half of iMIP is entirely this table: an answer arriving by mail is only
    /// worth anything if it lands here, and the Tracking tab reads it. Nothing in a capture of
    /// the mail list says whether it did.
    /// </remarks>
    private static void ReportAttendees()
    {
        foreach (var collection in App.Pim.Collections(CollectionKind.Events))
        {
            foreach (var item in App.Pim.Items(collection.Id))
            {
                var e = PimEventCodec.FromItem(item);
                if (e.Attendees.Count == 0) continue;

                Log.Info($"Harness: calendar meeting — “{e.Summary}” uid {e.Uid} seq {e.Sequence} "
                         + $"organizer {(e.Organizer.Length > 0 ? e.Organizer : "none")}: "
                         + string.Join("; ", e.Attendees.Select(a => $"{a.Address} {a.PartStat}")) + ".");
            }
        }
    }

    private static string Offset(TimeZoneInfo zone, DateTime at)
    {
        // Written by hand rather than with a format string: TimeSpan has no negative section, so
        // the obvious "+hh:mm;-hh:mm" throws rather than writing a zone west of Greenwich.
        var offset = zone.GetUtcOffset(at);
        return (offset < TimeSpan.Zero ? "-" : "+")
               + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    private static string TimeZoneChoicesShort(TimeZoneInfo zone, DateTime at)
        => Mailbox.Core.Settings.TimeZoneChoices.ShortLabel(zone, new DateTimeOffset(at, zone.GetUtcOffset(at)));

    // ---- Subscriptions, publishing and the conflict prompt ------------------------------------

    /// <summary>
    /// Runs the calendar half of a send/receive, and says what every calendar with an address
    /// behind it held before and after.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_CALENDAR_SYNC=1</c> refreshes once; <c>=twice</c> refreshes again straight
    /// after, which is the only way to tell a subscription that re-reads its document from one
    /// that re-downloads it — the second pass should send a conditional request and be told
    /// nothing has changed.
    /// </remarks>
    private async Task PoseCalendarSyncAsync(string spec)
    {
        if (DataContext is not ShellViewModel shell) return;

        var passes = spec.Trim().Equals("twice", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

        for (var pass = 1; pass <= passes; pass++)
        {
            foreach (var collection in App.Pim.Collections(CollectionKind.Events))
            {
                if (collection.DavUrl is not { Length: > 0 } url) continue;
                Log.Info($"Harness: calendar sync {pass} — before: “{collection.DisplayName}” at {url}, "
                         + $"{App.Pim.Items(collection.Id).Count} row(s), ctag {collection.Ctag ?? "none"}.");
            }

            try
            {
                await SyncCalendarsAsync(shell, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warn("Harness: the calendar sync pose failed.", ex);
            }

            foreach (var collection in App.Pim.Collections(CollectionKind.Events))
            {
                if (collection.DavUrl is not { Length: > 0 } url) continue;
                Log.Info($"Harness: calendar sync {pass} — after: “{collection.DisplayName}” at {url}, "
                         + $"{App.Pim.Items(collection.Id).Count} row(s), ctag {collection.Ctag ?? "none"}, "
                         + $"checked {collection.LastCheckedUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "never"}.");

                foreach (var item in App.Pim.Items(collection.Id))
                {
                    Log.Info($"Harness: calendar sync {pass} — “{collection.DisplayName}” holds “{item.Summary}” "
                             + $"uid {item.Uid} at {item.StartsLocal ?? "no start"}.");
                }
            }

            Log.Info($"Harness: calendar sync {pass} — status “{shell.StatusRight}”.");
        }
    }
}
