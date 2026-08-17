using Mailbox.Store.Pim;

namespace Mailbox.Scheduling;

/// <summary>A task whose reminder time has come.</summary>
/// <param name="ItemId">The row it is on, so dismissing it can be recorded.</param>
/// <param name="DueUtc">When the task is due, which is what its alarm hangs from.</param>
public sealed record DueTask(long ItemId, string Summary, DateTimeOffset DueUtc);

/// <summary>
/// Which tasks are due to be reminded about, now.
/// </summary>
/// <remarks>
/// The same shape <see cref="AppointmentReminders"/> has, and per occurrence for the same reason:
/// a task that repeats is one row with many due dates, and dismissing this week's must not silence
/// next week's. Two things differ, and both come from what a task is:
/// <list type="bullet">
/// <item>A VTODO's alarm hangs from its <b>DUE</b>, not from a start — a task with no due date has
/// nothing to be early about, so it never reminds however its window is filled in.</item>
/// <item>It does not stop. An appointment that has been and gone stops being shown, because it is
/// over; an overdue task is exactly the thing a reminder is for, so it keeps coming back until it
/// is dismissed or finished.</item>
/// </list>
/// </remarks>
public static class TaskReminders
{
    /// <summary>
    /// How far back a repeating task is expanded to find the occurrence still unanswered.
    /// </summary>
    /// <remarks>
    /// A year, rather than the two days an appointment's horizon is: a reminder that has been
    /// ignored since spring is still the one this task owes, and a window that only looked at this
    /// week would quietly forget it.
    /// </remarks>
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(366);

    /// <summary>Everything whose reminder has come and not been dealt with, soonest first.</summary>
    public static IReadOnlyList<DueTask> Due(PimRepository repository, DateTimeOffset nowUtc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var due = new List<DueTask>();

        foreach (var item in repository.ItemsWithReminders(CollectionKind.Tasks))
        {
            if (item.ReminderMinutes is not { } minutes) continue;

            var task = PimTodoCodec.FromItem(item);

            // A finished task says nothing more, and one with no due date has nothing to be early
            // about — RFC 5545 hangs a VTODO's alarm off the DUE.
            if (task.IsComplete || task.Due is null) continue;

            var (dismissed, snoozed) = repository.ReminderState(item.Id);

            foreach (var moment in Occurrences(repository, item, task, nowUtc, zone))
            {
                if (dismissed is { } last && moment <= last) continue;

                var at = snoozed ?? moment.AddMinutes(-minutes);
                if (at > nowUtc) continue;

                due.Add(new DueTask(item.Id, task.Summary.Length > 0 ? task.Summary : "(no subject)", moment));
                break;
            }
        }

        return [.. due.OrderBy(d => d.DueUtc)];
    }

    /// <summary>
    /// When this task falls due, once for a task that happens once and in order for one that
    /// repeats.
    /// </summary>
    /// <remarks>
    /// A repeating task is expanded through the calendar's own machinery — the RRULE, its
    /// exceptions and its overrides, DST and all — by standing the task up as an event whose start
    /// and end are both its due date. That is what a due date is: an instant the rule steps
    /// through, and the one thing <see cref="Recurrence"/> needs to be given.
    /// </remarks>
    private static IEnumerable<DateTimeOffset> Occurrences(
        PimRepository repository,
        PimItem item,
        TaskItem task,
        DateTimeOffset nowUtc,
        TimeZoneInfo? zone)
    {
        if (task.Rrule is not { Length: > 0 })
        {
            yield return task.Due!.ToUtc(zone);
            yield break;
        }

        var family = new List<CalendarEvent> { AsEvent(task) };
        foreach (var over in repository.ItemsByUid(item.CollectionId, item.Uid).Where(i => i.IsOverride))
        {
            var edited = PimTodoCodec.FromItem(over);
            if (edited.Due is not null) family.Add(AsEvent(edited));
        }

        foreach (var occurrence in Recurrence.Expand(family, nowUtc - Horizon, nowUtc.AddDays(2), zone).OrderBy(o => o.StartUtc))
        {
            yield return occurrence.StartUtc;
        }
    }

    /// <summary>The task as the recurrence engine reads one: an instant at its due date.</summary>
    private static CalendarEvent AsEvent(TaskItem task) => new()
    {
        Uid = task.Uid,
        Summary = task.Summary,
        Start = task.Due!,
        End = task.Due!,
        Rrule = task.Rrule,
        ExceptionDates = task.ExceptionDates,
        RecurrenceId = task.RecurrenceId,
    };

    /// <summary>Marks this occurrence answered, so the next one in a series still comes round.</summary>
    public static void Dismiss(PimRepository repository, DueTask task)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(task);
        repository.SetReminderState(task.ItemId, task.DueUtc, null);
    }

    /// <summary>Puts it off, which is a time to fire again rather than a dismissal.</summary>
    public static void Snooze(PimRepository repository, DueTask task, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(task);
        var (dismissed, _) = repository.ReminderState(task.ItemId);
        repository.SetReminderState(task.ItemId, dismissed, until);
    }
}
