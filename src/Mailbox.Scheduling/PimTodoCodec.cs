using System.Globalization;
using Mailbox.Store.Pim;

namespace Mailbox.Scheduling;

/// <summary>
/// A task to and from the row the PIM store keeps for it. The row's raw VTODO text is the truth;
/// every other column is derived from the task here, so a query on the columns and a parse of the
/// text always agree.
/// </summary>
/// <remarks>
/// The columns a task needs were in the store from step 1 — <c>priority</c>,
/// <c>percent_complete</c>, <c>completed_utc</c> and <c>status</c> — so nothing about tasks needs
/// a migration; what needed writing was this.
/// </remarks>
public static class PimTodoCodec
{
    /// <summary>
    /// The row for a task. <paramref name="existing"/> carries the identity and sync bookkeeping
    /// forward when a stored task is edited; a new task gets a new row.
    /// </summary>
    public static PimItem ToItem(TaskItem task, long collectionId, PimItem? existing = null, PimSyncState? syncState = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        // A task can have a start, a due date, both or neither. The two instant columns are what
        // a range query reads, so they carry whichever end the task states — a task due on Friday
        // is found by a query over Friday whether or not anyone said when it starts.
        var span = task.Start ?? task.Due;
        var until = task.Due ?? task.Start;

        return new PimItem
        {
            Id = existing?.Id ?? 0,
            CollectionId = collectionId,
            Uid = task.Uid,
            Kind = CollectionKind.Tasks,
            RawPayload = TodoCodec.Serialize(task),
            Summary = task.Summary,
            Description = task.Description,
            StartsUtc = span?.ToUtc(),
            EndsUtc = until?.ToUtc(),
            // The wall-time columns keep what the task actually says, null and all, so rebuilding
            // from them cannot invent a start date the task never had.
            StartsLocal = task.Start?.ToLocalText(),
            EndsLocal = task.Due?.ToLocalText(),
            TzId = (until ?? span) is { AllDay: false } timed ? timed.TzId : null,
            AllDay = until?.AllDay ?? false,
            Status = TodoCodec.ProgressWord(task.Progress),
            Priority = task.PriorityNumber,
            PercentComplete = task.PercentComplete,
            CompletedUtc = task.CompletedUtc,
            Rrule = task.Rrule,
            RecurrenceId = task.RecurrenceId is { } rid ? ICalendarCodec.RecurrenceIdText(rid) : null,
            IsOverride = task.IsOverride,
            Sequence = task.Sequence,
            Organizer = task.Owner,
            ReminderMinutes = task.ReminderMinutes,
            Categories = string.Join(",", task.Categories),
            LastModified = task.LastModified,
            SyncState = syncState ?? (existing is null ? PimSyncState.New : existing.SyncState == PimSyncState.New ? PimSyncState.New : PimSyncState.Modified),
            DavHref = existing?.DavHref,
            Etag = existing?.Etag,
        };
    }

    /// <summary>
    /// The task a row holds: parsed from its VTODO text, or — when the text will not parse —
    /// rebuilt from the columns, so a damaged row is still on the list and can be fixed.
    /// </summary>
    public static TaskItem FromItem(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            var parsed = TodoCodec.Parse(item.RawPayload);
            var match = parsed.FirstOrDefault(t => item.IsOverride ? t.IsOverride : !t.IsOverride) ?? parsed.FirstOrDefault();
            if (match is not null) return match;
        }
        catch (FormatException)
        {
            // Fall through to the columns.
        }

        return FromColumns(item);
    }

    /// <summary>The task the columns alone describe.</summary>
    public static TaskItem FromColumns(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        EventTime? recurrenceId = null;
        if (item.IsOverride && !string.IsNullOrWhiteSpace(item.RecurrenceId))
        {
            var text = item.RecurrenceId.Trim();
            var utc = text.EndsWith('Z');
            if (utc) text = text[..^1];
            if (DateTime.TryParseExact(text, ["yyyyMMdd'T'HHmmss", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
                recurrenceId = text.Length == 8 ? new EventTime(wall, null, AllDay: true) : new EventTime(wall, utc ? "UTC" : item.TzId);
        }

        var progress = TodoCodec.ProgressFromWord(item.Status);

        return new TaskItem
        {
            Uid = item.Uid,
            Summary = item.Summary,
            Description = item.Description,
            Start = EventTime.FromLocalText(item.StartsLocal, item.TzId, item.AllDay),
            Due = EventTime.FromLocalText(item.EndsLocal, item.TzId, item.AllDay),
            CompletedUtc = item.CompletedUtc,
            Progress = progress,
            PercentComplete = item.PercentComplete,
            Urgency = TaskItem.UrgencyFor(item.Priority),
            Rrule = string.IsNullOrWhiteSpace(item.Rrule) ? null : item.Rrule,
            RecurrenceId = recurrenceId,
            ReminderMinutes = item.ReminderMinutes,
            Categories = item.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Owner = item.Organizer,
            Sequence = item.Sequence,
            LastModified = item.LastModified,
        };
    }

    /// <summary>
    /// The same task marked done, or put back: the tick sets all three of the things a task can
    /// say about being finished, because a client reading any one of them should agree with the
    /// other two.
    /// </summary>
    public static TaskItem Complete(TaskItem task, bool done, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(task);
        return done
            ? task with
            {
                Progress = TaskProgress.Completed,
                PercentComplete = 100,
                CompletedUtc = when,
                LastModified = when,
            }
            : task with
            {
                Progress = TaskProgress.NotStarted,
                PercentComplete = 0,
                CompletedUtc = null,
                LastModified = when,
            };
    }
}
