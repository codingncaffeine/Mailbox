using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Mailbox.App.ViewModels;
using Mailbox.Controls.Calendar;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The calendar lane's doors: pressing the grid, and reading back what the grid is showing.
/// </summary>
/// <remarks>
/// Two things were unreachable before these. <b>Nothing could select an appointment</b> — the
/// selection is made by a pointer press on a chip and by nothing else, so Open, Delete, Categorize
/// and Recurrence each answered every pose with "Select an appointment first." and four commands
/// had never been pressed at all. And <b>nothing could say what the grid was showing</b>: the
/// status bar counts items and a capture shows coloured rectangles, neither of which answers "is
/// that Sunday's occurrence of the weekly one, and is it in the right column".
/// <list type="bullet">
/// <item><description><c>MAILBOX_CALENDAR_PRESS</c> — real pointer events at coordinates the view
/// really drew at, so what a pose proves is the hit-testing and the handler behind it rather than
/// an event the pose made up. <c>entry:</c> selects a chip, <c>entry2:</c> opens it,
/// <c>slot:</c>/<c>slot2:</c> press empty time, <c>nav:</c> presses a day in the date
/// navigator.</description></item>
/// <item><description><c>MAILBOX_CALENDAR_PROBE</c> — <c>view</c>, <c>entries</c>, <c>layout</c>,
/// <c>navigator</c>, <c>store</c>. The navigator half reads the store a second time, by a
/// different query, and says whether the bold days agree — bold means "that day has something on
/// it", and the only way to check it is against the store rather than by eye.</description></item>
/// </list>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this lane's doors. Called once from the shell's constructor.</summary>
    private void WirePhase6ADoors()
    {
        var press = Environment.GetEnvironmentVariable("MAILBOX_CALENDAR_PRESS");
        var probe = Environment.GetEnvironmentVariable("MAILBOX_CALENDAR_PROBE");
        if (press is not { Length: > 0 } && probe is not { Length: > 0 }) return;

        Opened += (_, _) =>
        {
            // Loaded rather than Background: MAILBOX_RUN posts its presses at Background from a
            // handler registered earlier, and a selection made after the command it was made for
            // proves nothing. Loaded runs first, and the module pose — also Loaded, and registered
            // earlier still — has already put the view up by the time this lands.
            if (press is { Length: > 0 })
            {
                Dispatcher.UIThread.Post(() => Guarded(() => PoseCalendarPress(press)), DispatcherPriority.Loaded);
            }

            // The probe goes last of all, so it reports the grid as the presses and the commands
            // left it rather than as it opened.
            if (probe is { Length: > 0 })
            {
                Dispatcher.UIThread.Post(
                    () => Dispatcher.UIThread.Post(
                        () => Guarded(() => PoseCalendarProbe(probe)),
                        DispatcherPriority.ApplicationIdle),
                    DispatcherPriority.Background);
            }
        };
    }

    /// <summary>
    /// Runs a door and says so when it throws.
    /// </summary>
    /// <remarks>
    /// A posted action that throws leaves a run with a plausible capture, no error and nothing to
    /// grep, which is a trap this sweep has already been caught by once.
    /// </remarks>
    private static void Guarded(Action door)
    {
        try
        {
            door();
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: a calendar door failed.", ex);
        }
    }

    // ---- Pressing the grid ----------------------------------------------------------------

    /// <summary>
    /// Presses one or more gestures into whichever calendar view is up.
    /// </summary>
    /// <remarks>
    /// Steps are separated by <c>|</c> and taken in order:
    /// <c>entry:&lt;match&gt;</c> and <c>entry2:&lt;match&gt;</c> press once and twice on the chip
    /// whose summary contains the match (<c>match#2</c> takes the second such chip);
    /// <c>slot:&lt;yyyy-MM-dd&gt;[@HH:mm]</c> and <c>slot2:</c> press empty grid, which is where a
    /// new appointment comes from; <c>nav:&lt;yyyy-MM-dd&gt;</c>, <c>nav:prev</c> and
    /// <c>nav:next</c> press the date navigator.
    /// </remarks>
    private void PoseCalendarPress(string spec)
    {
        if (DataContext is not ShellViewModel shell) return;
        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);
        calendar.UpdateLayout();

        foreach (var step in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = step.IndexOf(':', StringComparison.Ordinal);
            var verb = (colon < 0 ? step : step[..colon]).Trim().ToLowerInvariant();
            var argument = colon < 0 ? string.Empty : step[(colon + 1)..].Trim();

            switch (verb)
            {
                case "entry":
                case "entry2":
                    PressEntry(calendar, argument, verb == "entry2" ? 2 : 1);
                    break;

                case "slot":
                case "slot2":
                    PressSlot(calendar, argument, verb == "slot2" ? 2 : 1);
                    break;

                case "nav":
                    PressNavigator(calendar, argument);
                    break;

                default:
                    Log.Info($"Harness: “{step}” is not a calendar press — say entry:, entry2:, slot:, slot2: or nav:.");
                    break;
            }

            // Each press on its own layout pass: a press that reloads the store replaces every
            // entry with a fresh object, and the next step would then aim at a chip that is no
            // longer the one on screen.
            calendar.UpdateLayout();
        }

        Log.Info($"Harness: calendar selection is "
                 + $"{(calendar.SelectedEntry is { } chosen ? Describe(chosen) : "nothing")}.");
    }

    private void PressEntry(CalendarWorkspace calendar, string match, int clicks)
    {
        var wanted = match;
        var nth = 1;
        var hash = match.LastIndexOf('#');
        if (hash > 0 && int.TryParse(match[(hash + 1)..], CultureInfo.InvariantCulture, out var parsed))
        {
            wanted = match[..hash];
            nth = Math.Max(1, parsed);
        }

        var hits = calendar.Entries
            .Where(e => wanted.Length == 0 || e.Summary.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hits.Count < nth)
        {
            Log.Info($"Harness: the calendar is showing {hits.Count} chip(s) reading “{wanted}”, not {nth}.");
            return;
        }

        var entry = hits[nth - 1];
        if (BoxOf(calendar, entry) is not { } box)
        {
            Log.Info($"Harness: “{entry.Summary}” is in the view but was not drawn — nothing to press.");
            return;
        }

        Log.Info($"Harness: pressing “{entry.Summary}” at ({box.Center.X:0},{box.Center.Y:0}), {clicks} click(s).");
        Click(ViewOf(calendar)!, box.Center, clicks);
    }

    private void PressSlot(CalendarWorkspace calendar, string argument, int clicks)
    {
        var parts = argument.Split('@', StringSplitOptions.TrimEntries);
        if (!DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            Log.Info($"Harness: “{parts[0]}” is not a date — say slot:2026-08-13@10:00.");
            return;
        }

        var at = parts.Length > 1 && TimeOnly.TryParse(parts[1], CultureInfo.InvariantCulture, out var time)
            ? time.ToTimeSpan()
            : new TimeSpan(9, 0, 0);

        // Half a row down in a time grid: PointAt answers with the top edge of the row, which a
        // rectangle's own bottom edge also touches — a press exactly there lands in the row above,
        // and reads as an appointment created half an hour early. A reader aims at the middle.
        Point? where = calendar.Month is { } month
            ? month.PointAt(day)
            : calendar.TimeGrid is { } grid
                ? grid.PointAt(day, at) is { } top ? new Point(top.X, top.Y + (grid.SlotHeight / 2)) : null
                : calendar.Schedule?.PointAt(at, calendar.Schedule.DrawnRows.Count > 0 ? calendar.Schedule.DrawnRows[0].CollectionId : 0);

        if (where is not { } point)
        {
            Log.Info($"Harness: {day:yyyy-MM-dd} at {at} is not on show in the {calendar.Kind} view.");
            return;
        }

        Log.Info($"Harness: pressing empty time on {day:yyyy-MM-dd} at {at:hh\\:mm} — ({point.X:0},{point.Y:0}), {clicks} click(s).");
        Click(ViewOf(calendar)!, point, clicks);
    }

    private void PressNavigator(CalendarWorkspace calendar, string argument)
    {
        var navigator = calendar.Navigator;
        navigator.UpdateLayout();

        if (string.Equals(argument, "prev", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "next", StringComparison.OrdinalIgnoreCase))
        {
            var arrow = navigator.ArrowAt(back: string.Equals(argument, "prev", StringComparison.OrdinalIgnoreCase));
            if (arrow is not { } spot)
            {
                Log.Info("Harness: the date navigator has not drawn its arrows.");
                return;
            }

            Log.Info($"Harness: pressing the navigator's {argument} arrow at ({spot.X:0},{spot.Y:0}).");
            Click(navigator, spot, 1);
            return;
        }

        if (!DateOnly.TryParseExact(argument, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            Log.Info($"Harness: “{argument}” is not a date — say nav:2026-09-03, nav:prev or nav:next.");
            return;
        }

        if (navigator.PointAt(day) is not { } target)
        {
            Log.Info($"Harness: the date navigator is not showing {day:yyyy-MM-dd}.");
            return;
        }

        Log.Info($"Harness: pressing {day:yyyy-MM-dd} in the date navigator at ({target.X:0},{target.Y:0}).");
        Click(navigator, target, 1);
    }

    private static Rect? BoxOf(CalendarWorkspace calendar, CalendarEntry entry)
        => calendar.Month is { } month ? month.BoxOf(entry)
            : calendar.TimeGrid is { } grid ? grid.BoxOf(entry)
            : calendar.Schedule?.BoxOf(entry);

    private static Control? ViewOf(CalendarWorkspace calendar)
        => calendar.Month as Control ?? calendar.TimeGrid as Control ?? calendar.Schedule;

    /// <summary>
    /// Press and release at a point in a control, as a pointer would.
    /// </summary>
    /// <remarks>
    /// The same route <see cref="Drag"/> takes and for the same reason: a pointer event states its
    /// position in the window's coordinates, not the control's, so handing one coordinates that
    /// were already the control's lands the press somewhere else entirely.
    /// </remarks>
    private static void Click(Control view, Point at, int clicks)
    {
        var root = TopLevel.GetTopLevel(view) as Visual ?? view;
        var point = view.TranslatePoint(at, root) ?? at;

        var pointer = new Pointer(2, PointerType.Mouse, isPrimary: true);
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);

        view.RaiseEvent(new PointerPressedEventArgs(view, pointer, root, point, 0, down, KeyModifiers.None, clicks));
        view.RaiseEvent(new PointerReleasedEventArgs(view, pointer, root, point, 1, up, KeyModifiers.None, MouseButton.Left));
    }

    // ---- Reading the grid back --------------------------------------------------------------

    private void PoseCalendarProbe(string what)
    {
        if (DataContext is not ShellViewModel shell) return;
        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);
        calendar.UpdateLayout();

        var wanted = what.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();
        var all = wanted.Contains("all");

        if (all || wanted.Contains("view")) ProbeView(calendar);
        if (all || wanted.Contains("entries")) ProbeEntries(calendar);
        if (all || wanted.Contains("layout")) ProbeLayout(calendar);
        if (all || wanted.Contains("navigator")) ProbeNavigator(calendar);
        if (all || wanted.Contains("store")) ProbeStore();
    }

    private void ProbeView(CalendarWorkspace calendar)
    {
        var (first, last) = calendar.VisibleDays();
        Log.Info($"Harness: calendar view — {calendar.Kind}, anchor {calendar.Anchor:yyyy-MM-dd}, "
                 + $"title “{calendar.TitleForHarness}”, showing {first:yyyy-MM-dd}..{last:yyyy-MM-dd} "
                 + $"({last.DayNumber - first.DayNumber + 1} day(s)), {calendar.Entries.Count} item(s), "
                 + $"scale {App.CalendarOptions.TimeScaleMinutes}min, week starts {calendar.FirstDayOfWeek}, "
                 + $"today {calendar.Today:yyyy-MM-dd}.");

        if (calendar.TimeGrid is { } grid)
        {
            Log.Info($"Harness: calendar columns — {string.Join(", ", grid.Days().Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}.");
        }

        if (calendar.Schedule is { } schedule)
        {
            Log.Info($"Harness: calendar rows — {string.Join(", ", schedule.DrawnRows.Select(r => r.Name))}.");
        }
    }

    private static void ProbeEntries(CalendarWorkspace calendar)
    {
        foreach (var entry in calendar.Entries)
        {
            Log.Info($"Harness: calendar entry — {Describe(entry)}.");
        }
    }

    /// <summary>What was drawn where, which is the only proof of a side-by-side or a spanning bar.</summary>
    private static void ProbeLayout(CalendarWorkspace calendar)
    {
        foreach (var entry in calendar.Entries)
        {
            if (BoxOf(calendar, entry) is not { } box)
            {
                Log.Info($"Harness: calendar box — “{entry.Summary}” was not drawn.");
                continue;
            }

            Log.Info($"Harness: calendar box — “{entry.Summary}” at ({box.X:0},{box.Y:0}) {box.Width:0}x{box.Height:0}.");
        }
    }

    /// <summary>
    /// The navigator's bold days, and the same question asked of the store a second time.
    /// </summary>
    /// <remarks>
    /// The second reading is deliberately not the one the navigator used. The view fills its bold
    /// set from <c>ItemsBetween</c>, whose SQL has to reach back past the window for a series that
    /// started before it; this reads <em>every</em> row of every shown calendar and expands the
    /// lot. A day the second reading has and the first does not is a hole in that query, which is
    /// exactly the fault a bold day cannot be checked for by eye.
    /// </remarks>
    private static void ProbeNavigator(CalendarWorkspace calendar)
    {
        var navigator = calendar.Navigator;
        navigator.UpdateLayout();

        var bold = navigator.BusyDays.OrderBy(d => d).ToList();
        Log.Info($"Harness: navigator — anchor {navigator.Anchor:yyyy-MM-dd}, {navigator.MonthsShown} month(s), "
                 + $"block {navigator.RangeStart:yyyy-MM-dd}..{navigator.RangeEnd:yyyy-MM-dd}, "
                 + $"today {navigator.Today:yyyy-MM-dd}, {bold.Count} bold day(s).");
        Log.Info($"Harness: navigator bold — {(bold.Count == 0 ? "none" : string.Join(" ", bold.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))}.");

        var drawn = navigator.DrawnDays;
        if (drawn.Count == 0)
        {
            Log.Info("Harness: the date navigator has drawn no days to check.");
            return;
        }

        var from = drawn.Min();
        var to = drawn.Max();
        var truth = DaysWithSomethingOn(from, to);

        var missing = drawn.Where(d => truth.Contains(d) && !navigator.BusyDays.Contains(d)).OrderBy(d => d).ToList();
        var spurious = drawn.Where(d => !truth.Contains(d) && navigator.BusyDays.Contains(d)).OrderBy(d => d).ToList();

        Log.Info($"Harness: navigator store — {truth.Count(d => d >= from && d <= to)} day(s) with something on them "
                 + $"between {from:yyyy-MM-dd} and {to:yyyy-MM-dd}, read row by row.");
        Log.Info(missing.Count == 0 && spurious.Count == 0
            ? "Harness: navigator bold agrees with the store."
            : $"Harness: navigator bold DISAGREES — not bold but has items: "
              + $"{(missing.Count == 0 ? "none" : string.Join(" ", missing.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))}; "
              + $"bold but empty: "
              + $"{(spurious.Count == 0 ? "none" : string.Join(" ", spurious.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))}.");
    }

    /// <summary>
    /// Every day between two dates that is claimed on a shown calendar, read from whole
    /// collections rather than from a windowed query.
    /// </summary>
    /// <remarks>
    /// Free is skipped here for the same reason the view skips it: a day whose only entry shows as
    /// free is not a claimed day. A second reading that asked a different question from the first
    /// would report a disagreement on every run, which is a check nobody would read twice.
    /// </remarks>
    private static IReadOnlySet<DateOnly> DaysWithSomethingOn(DateOnly from, DateOnly to)
    {
        var days = new HashSet<DateOnly>();
        var zone = TimeZoneInfo.Local;
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(from.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue))).ToUniversalTime();

        foreach (var collection in App.Pim.Collections(CollectionKind.Events).Where(c => c.IsVisible))
        {
            var events = App.Pim.Items(collection.Id)
                .Where(i => i.SyncState != PimSyncState.Deleted)
                .Select(PimEventCodec.FromItem)
                .ToList();

            foreach (var occurrence in Recurrence.Expand(events, start, end, zone))
            {
                if (occurrence.Event.Busy == BusyStatus.Free) continue;
                var first = DateOnly.FromDateTime(occurrence.Start.AllDay
                    ? occurrence.Start.Wall
                    : TimeZoneInfo.ConvertTime(occurrence.StartUtc, zone).DateTime);
                var endWall = occurrence.End.AllDay
                    ? occurrence.End.Wall
                    : TimeZoneInfo.ConvertTime(occurrence.EndUtc, zone).DateTime;
                var last = endWall <= occurrence.Start.Wall
                    ? first
                    : DateOnly.FromDateTime(occurrence.AllDay ? endWall.AddDays(-1) : endWall.AddTicks(-1));
                if (last < first) last = first;

                for (var day = first; day <= last; day = day.AddDays(1))
                {
                    if (day >= from && day <= to) days.Add(day);
                }
            }
        }

        return days;
    }

    /// <summary>Every event row in every calendar, which is what a write is read back against.</summary>
    private static void ProbeStore()
    {
        foreach (var collection in App.Pim.Collections(CollectionKind.Events))
        {
            var rows = App.Pim.Items(collection.Id);
            Log.Info($"Harness: calendar store — “{collection.DisplayName}” (#{collection.Id}, {collection.Color}, "
                     + $"{(collection.IsVisible ? "shown" : "hidden")}{(collection.IsReadOnly ? ", read-only" : string.Empty)}) "
                     + $"holds {rows.Count} row(s).");

            foreach (var row in rows)
            {
                Log.Info($"Harness: calendar row — {row.Id} “{row.Summary}” {row.StartsLocal}–{row.EndsLocal}"
                         + $"{(row.AllDay ? " all-day" : string.Empty)}"
                         + $"{(row.Rrule is { Length: > 0 } rule ? $" RRULE={rule}" : string.Empty)}"
                         + $"{(row.RecurrenceId is { Length: > 0 } rid ? $" RECURRENCE-ID={rid}" : string.Empty)}"
                         + $"{(row.IsOverride ? " override" : string.Empty)}"
                         + $" busy={row.Busy} cats=[{row.Categories}] sync={row.SyncState}"
                         + $"{(row.ReminderMinutes is { } bell ? $" reminder={bell}" : string.Empty)}.");
            }
        }
    }

    private static string Describe(CalendarEntry entry)
        => $"“{entry.Summary}” {entry.StartWall:yyyy-MM-dd HH:mm}–{entry.EndWall:yyyy-MM-dd HH:mm}"
           + $"{(entry.AllDay ? " all-day" : string.Empty)}{(entry.IsMultiDay ? " multi-day" : string.Empty)}"
           + $" item {entry.ItemId} on {entry.CollectionName}"
           + $" {(entry.Occurrence.Event.IsOverride ? "override" : entry.Occurrence.IsPartOfSeries ? "series" : "single")}"
           + $" {entry.Busy.ToString().ToLowerInvariant()}"
           + $" cats=[{string.Join("/", entry.Occurrence.Event.Categories)}]"
           + $" colour={(entry.Colour is { } c ? c.ToString() : "default")}";
}
