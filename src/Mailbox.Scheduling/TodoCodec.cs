using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Ical.Net.Serialization.DataTypes;
using IcalCalendar = Ical.Net.Calendar;

namespace Mailbox.Scheduling;

/// <summary>
/// The application's tasks to and from RFC 5545 text, on the same terms as the appointment
/// codec: the text is the truth the store and the server share, and this is the only place the
/// application reads or writes it.
/// </summary>
/// <remarks>
/// A VTODO is a VEVENT with different names for the same ideas — DUE where an appointment has
/// DTEND, PERCENT-COMPLETE and COMPLETED where it has a Show As — so the two codecs share the
/// time mapping and nothing else. Sharing more would mean one record with the union of both
/// vocabularies, and a task carrying an attendee's response is not a thing.
/// </remarks>
public static class TodoCodec
{
    /// <summary>The PRODID written into task lists this application makes.</summary>
    public const string ProductId = "-//Mailbox//Mailbox Tasks//EN";

    /// <summary>The property carrying the two states RFC 5545 has no word for.</summary>
    private const string ProgressProperty = "X-MAILBOX-TASK-STATUS";

    /// <summary>What CLASS says about a task the reference calls Private.</summary>
    private const string PrivateClass = "PRIVATE";

    /// <summary>One VTODO block — <c>BEGIN:VTODO</c> to <c>END:VTODO</c> — as the store keeps a row.</summary>
    public static string Serialize(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new ComponentSerializer().SerializeToString(ToIcal(task)) ?? string.Empty;
    }

    /// <summary>
    /// A whole VCALENDAR — the tasks given, a master and its overrides sharing a UID, with a
    /// VTIMEZONE for every zone they name — as a server is sent one.
    /// </summary>
    public static string SerializeCalendar(IEnumerable<TaskItem> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var calendar = new IcalCalendar { ProductId = ProductId };
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            calendar.Todos.Add(ToIcal(task));
            foreach (var tz in new[] { task.Start?.TzId, task.Due?.TzId, task.RecurrenceId?.TzId })
                if (tz is not null && !string.Equals(tz, "UTC", StringComparison.OrdinalIgnoreCase)) zones.Add(tz);
        }

        foreach (var tz in zones)
        {
            try { calendar.AddTimeZone(tz); }
            catch (Exception) { /* a zone this machine does not know is still named on the DUE; the reader resolves it. */ }
        }

        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    /// <summary>
    /// The tasks in RFC 5545 text: a VCALENDAR, or a bare VTODO block as the store keeps one.
    /// A series comes back as its master and then its overrides.
    /// </summary>
    /// <exception cref="FormatException">The text is not iCalendar.</exception>
    public static IReadOnlyList<TaskItem> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("BEGIN:VTODO", StringComparison.OrdinalIgnoreCase))
            trimmed = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:" + ProductId + "\r\n" + trimmed.TrimEnd() + "\r\nEND:VCALENDAR\r\n";

        // Reading the components is inside the guard as well as loading them, for the reason the
        // appointment codec gives: Ical.Net accepts a component and then throws from a property
        // getter, which would take the caller down past every guard round Load.
        try
        {
            var calendar = IcalCalendar.Load(trimmed);
            if (calendar is null) throw new FormatException("The text is not an iCalendar object.");

            return calendar.Todos
                .Select(FromIcal)
                .OrderBy(t => t.Uid, StringComparer.Ordinal)
                .ThenBy(t => t.IsOverride ? 1 : 0)
                .ThenBy(t => t.RecurrenceId?.Wall ?? DateTime.MinValue)
                .ToList();
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new FormatException("The text is not an iCalendar object.", ex);
        }
    }

    /// <summary>The application's task as Ical.Net holds one.</summary>
    internal static Todo ToIcal(TaskItem task)
    {
        var ical = new Todo
        {
            Uid = task.Uid,
            Summary = task.Summary,
            Sequence = task.Sequence,
            LastModified = new CalDateTime(task.LastModified.UtcDateTime, "UTC", true),
            DtStamp = new CalDateTime(task.LastModified.UtcDateTime, "UTC", true),
            Priority = task.PriorityNumber,
            PercentComplete = task.PercentComplete,
            Status = Status(task.Progress),
        };

        if (task.Description.Length > 0) ical.Description = task.Description;
        if (task.Start is { } start) ical.DtStart = ICalendarCodec.ToCal(start);
        if (task.Due is { } due) ical.Due = ICalendarCodec.ToCal(due);
        if (task.CompletedUtc is { } done) ical.Completed = new CalDateTime(done.UtcDateTime, "UTC", true);
        if (!string.IsNullOrWhiteSpace(task.Rrule)) ical.RecurrenceRule = new RecurrenceRule(task.Rrule);
        foreach (var ex in task.ExceptionDates) ical.ExceptionDates.Add(ICalendarCodec.ToCal(ex));
        if (task.RecurrenceId is { } rid) ical.RecurrenceIdentifier = new RecurrenceIdentifier(ICalendarCodec.ToCal(rid), null);
        if (task.Owner.Length > 0) ical.Organizer = new Organizer(task.Owner.Contains(':', StringComparison.Ordinal) ? task.Owner : "mailto:" + task.Owner);
        foreach (var category in task.Categories) ical.Categories.Add(category);

        // CLASS is written only when it is private. PUBLIC is the standard's own default, so
        // saying it adds a property to every task in the file to state what its absence states.
        if (task.IsPrivate) ical.Class = PrivateClass;

        // The two states RFC 5545 cannot say. Written beside NEEDS-ACTION rather than instead of
        // it, so a client that ignores the property still has a true statement.
        if (task.Progress is TaskProgress.Waiting or TaskProgress.Deferred)
        {
            ical.Properties.Add(new CalendarProperty(ProgressProperty, task.Progress.ToString().ToUpperInvariant()));
        }

        if (task.ReminderMinutes is { } minutes)
        {
            ical.Alarms.Add(new Alarm
            {
                Action = AlarmAction.Display,
                Description = task.Summary.Length > 0 ? task.Summary : "Reminder",
                Trigger = new Trigger(Duration.FromMinutes(-minutes)),
            });
        }

        return ical;
    }

    /// <summary>Ical.Net's task as the application holds one.</summary>
    internal static TaskItem FromIcal(Todo ical)
    {
        int? reminder = null;
        foreach (var alarm in ical.Alarms)
        {
            if (alarm.Trigger is { IsRelative: true, Duration: { } d })
            {
                var minutes = (int)Math.Round(-d.ToTimeSpanUnspecified().TotalMinutes);
                if (minutes >= 0 && (reminder is null || minutes > reminder)) reminder = minutes;
            }
        }

        string? rrule = null;
        if (ical.RecurrenceRule is { } rule)
        {
            rrule = new RecurrenceRuleSerializer(new SerializationContext()).SerializeToString(rule);
            if (string.IsNullOrEmpty(rrule)) rrule = null;
        }

        var completed = ical.Completed is { } c ? new DateTimeOffset(c.AsUtc, TimeSpan.Zero) : (DateTimeOffset?)null;
        var progress = Progress(ical.Status, ical.Properties.Get<string>(ProgressProperty), completed, ical.PercentComplete);

        return new TaskItem
        {
            Uid = ical.Uid ?? TaskItem.NewUid(),
            Summary = ical.Summary ?? string.Empty,
            Description = ical.Description ?? string.Empty,
            Start = ical.DtStart is { } s ? ICalendarCodec.FromCal(s) : null,
            Due = ical.Due is { } due ? ICalendarCodec.FromCal(due) : null,
            CompletedUtc = completed,
            Progress = progress,
            // A completed task says so whether or not it counted itself, which is what the
            // reference's own tick does and what a list groups by.
            PercentComplete = progress == TaskProgress.Completed && ical.PercentComplete <= 0 ? 100 : ical.PercentComplete,
            Urgency = TaskItem.UrgencyFor(ical.Priority),
            Rrule = rrule,
            ExceptionDates = ical.ExceptionDates.GetAllDates().Select(ICalendarCodec.FromCal).ToList(),
            RecurrenceId = ical.RecurrenceIdentifier is { } rid ? ICalendarCodec.FromCal(rid.StartTime) : null,
            ReminderMinutes = reminder,
            Categories = ical.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).ToList(),
            IsPrivate = IsPrivateClass(ical.Class),
            Owner = ical.Organizer?.Value is { } org
                ? org.ToString().StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? org.ToString()["mailto:".Length..] : org.ToString()
                : string.Empty,
            Sequence = ical.Sequence,
            LastModified = ical.LastModified is { } lm ? new DateTimeOffset(lm.AsUtc, TimeSpan.Zero)
                : ical.DtStamp is { } ds ? new DateTimeOffset(ds.AsUtc, TimeSpan.Zero)
                : DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Whether a CLASS value means the reference's Private.
    /// </summary>
    /// <remarks>
    /// CONFIDENTIAL counts. RFC 5545 offers three values and the reference's button offers a tick,
    /// so both of the values that mean "not for everyone this list is shared with" read as the tick
    /// being on. The one thing that costs: a task another client marked CONFIDENTIAL is written
    /// back as PRIVATE if it is saved here, a tick having no way to say which of the two it was.
    /// </remarks>
    public static bool IsPrivateClass(string? value)
        => value?.Trim() is { Length: > 0 } word
           && (word.Equals(PrivateClass, StringComparison.OrdinalIgnoreCase)
               || word.Equals("CONFIDENTIAL", StringComparison.OrdinalIgnoreCase));

    /// <summary>The STATUS a progress is written as.</summary>
    public static string Status(TaskProgress progress) => progress switch
    {
        TaskProgress.InProgress => TodoStatus.InProcess,
        TaskProgress.Completed => TodoStatus.Completed,
        _ => TodoStatus.NeedsAction,
    };

    /// <summary>
    /// What a task's state is, read from what the text says about it: the extra property first,
    /// then STATUS, then the two numbers — a task another client marked done by writing COMPLETED
    /// and nothing else is still done.
    /// </summary>
    public static TaskProgress Progress(string? status, string? extra, DateTimeOffset? completed, int percent)
    {
        if (Enum.TryParse<TaskProgress>(extra?.Trim(), ignoreCase: true, out var stated)
            && stated is TaskProgress.Waiting or TaskProgress.Deferred)
        {
            return stated;
        }

        if (string.Equals(status, TodoStatus.Completed, StringComparison.OrdinalIgnoreCase)) return TaskProgress.Completed;
        if (completed is not null || percent >= 100) return TaskProgress.Completed;
        if (string.Equals(status, TodoStatus.InProcess, StringComparison.OrdinalIgnoreCase)) return TaskProgress.InProgress;
        return percent > 0 ? TaskProgress.InProgress : TaskProgress.NotStarted;
    }

    /// <summary>The store's word for a progress, which is what its Status column keeps.</summary>
    public static string ProgressWord(TaskProgress progress) => progress switch
    {
        TaskProgress.InProgress => "in-progress",
        TaskProgress.Completed => "completed",
        TaskProgress.Waiting => "waiting",
        TaskProgress.Deferred => "deferred",
        _ => "not-started",
    };

    /// <summary>
    /// The reference's own words for the five states, as its form and its Status column write
    /// them.
    /// </summary>
    /// <remarks>
    /// Here rather than in the window that first needed them: the task window, the detailed view
    /// and anything else that shows a state should all write the same five words, and two lists of
    /// them is how they stop agreeing.
    /// </remarks>
    public static string ProgressLabel(TaskProgress progress) => progress switch
    {
        TaskProgress.NotStarted => "Not Started",
        TaskProgress.InProgress => "In Progress",
        TaskProgress.Completed => "Completed",
        TaskProgress.Waiting => "Waiting on someone else",
        _ => "Deferred",
    };

    /// <summary>The store's word back into a progress; anything unknown has not been started.</summary>
    public static TaskProgress ProgressFromWord(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "in-progress" => TaskProgress.InProgress,
        "completed" => TaskProgress.Completed,
        "waiting" => TaskProgress.Waiting,
        "deferred" => TaskProgress.Deferred,
        _ => TaskProgress.NotStarted,
    };
}
