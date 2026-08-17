namespace Mailbox.Scheduling;

/// <summary>
/// Where a task has got to. The reference offers five; RFC 5545 defines three of them.
/// </summary>
/// <remarks>
/// Waiting and Deferred have no STATUS value of their own, so they travel as NEEDS-ACTION with
/// <c>X-MAILBOX-TASK-STATUS</c> beside it — the same shape the appointment codec uses for Show As,
/// and the same bargain: a client that does not know the property sees a task that still needs
/// doing, which is true, rather than one that has been cancelled, which is not.
/// </remarks>
public enum TaskProgress
{
    NotStarted,
    InProgress,
    Completed,
    Waiting,
    Deferred,
}

/// <summary>
/// How urgent a task is. RFC 5545 counts 1 (highest) to 9; the reference offers three, and the
/// two are reconciled by <see cref="TaskItem.PriorityNumber"/>.
/// </summary>
public enum TaskUrgency
{
    Low,
    Normal,
    High,
}

/// <summary>
/// A task as the application thinks of it: what it is, when it starts, when it is due, how far
/// through it is, and how it repeats.
/// </summary>
/// <remarks>
/// One of these per VTODO, exactly as one <see cref="CalendarEvent"/> is one VEVENT — a series'
/// master with its <see cref="Rrule"/>, or an override of one occurrence with its
/// <see cref="RecurrenceId"/>. It keeps <see cref="EventTime"/> rather than instants for the
/// reason an appointment does: a task due at 09:00 every Monday is due at 09:00 the week the
/// clocks change too.
/// <para>
/// A task need not have either date. One with neither is on the list and nowhere else, which is
/// what the reference's own "no date" tasks are, so both are nullable and neither is required.
/// </para>
/// </remarks>
public sealed record TaskItem
{
    public required string Uid { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>When work starts. Null for a task that is simply due.</summary>
    public EventTime? Start { get; init; }

    /// <summary>When it is due. Null for a task with no date at all.</summary>
    public EventTime? Due { get; init; }

    /// <summary>When it was finished, which RFC 5545 states in UTC.</summary>
    public DateTimeOffset? CompletedUtc { get; init; }

    public TaskProgress Progress { get; init; } = TaskProgress.NotStarted;

    /// <summary>0 to 100. Completing a task takes it to 100 and undoing that takes it back.</summary>
    public int PercentComplete { get; init; }

    public TaskUrgency Urgency { get; init; } = TaskUrgency.Normal;

    /// <summary>The RRULE without its property name — <c>FREQ=WEEKLY;BYDAY=MO</c> — for a series' master.</summary>
    public string? Rrule { get; init; }

    public IReadOnlyList<EventTime> ExceptionDates { get; init; } = [];

    /// <summary>The occurrence this replaces, for an override.</summary>
    public EventTime? RecurrenceId { get; init; }

    public bool IsOverride => RecurrenceId is not null;

    /// <summary>Minutes before the due date a reminder is shown, or null for none.</summary>
    public int? ReminderMinutes { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    /// Kept to oneself when the list is shared, which is RFC 5545's <c>CLASS:PRIVATE</c> and the
    /// reference's own Private button.
    /// </summary>
    /// <remarks>
    /// A statement to whoever else reads the list rather than a lock: nothing here is encrypted by
    /// it, and a server that ignores CLASS shows the task to everyone it shows the list to. That is
    /// what the property means in the standard, and the reference's button means no more.
    /// </remarks>
    public bool IsPrivate { get; init; }

    /// <summary>Whoever owns it — the reference's Owner column, and RFC 5545's ORGANIZER.</summary>
    public string Owner { get; init; } = string.Empty;

    public int Sequence { get; init; }

    public DateTimeOffset LastModified { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True once it is done, whichever of the two ways says so.</summary>
    public bool IsComplete => Progress == TaskProgress.Completed || PercentComplete >= 100;

    /// <summary>
    /// Whether it is late: due before the moment given and not yet done. An all-day due date is
    /// late once the day is over, not once the day has begun.
    /// </summary>
    public bool IsOverdue(DateTimeOffset asOf, TimeZoneInfo? zone = null)
    {
        if (IsComplete || Due is not { } due) return false;
        var deadline = due.AllDay ? due.Add(TimeSpan.FromDays(1)).ToUtc(zone) : due.ToUtc(zone);
        return deadline <= asOf;
    }

    /// <summary>The PRIORITY a VTODO carries: 1 for high, 5 for normal, 9 for low.</summary>
    public int PriorityNumber => Urgency switch
    {
        TaskUrgency.High => 1,
        TaskUrgency.Low => 9,
        _ => 5,
    };

    /// <summary>RFC 5545's nine steps read back as the three the reference offers.</summary>
    public static TaskUrgency UrgencyFor(int priority) => priority switch
    {
        >= 1 and <= 4 => TaskUrgency.High,
        >= 6 and <= 9 => TaskUrgency.Low,
        _ => TaskUrgency.Normal,
    };

    /// <summary>A new task's identifier, in the same form an appointment's takes.</summary>
    public static string NewUid() => CalendarEvent.NewUid();

    // A record compares its lists by reference unless told otherwise, which would make two
    // identical tasks differ — the trap CalendarEvent documents and this shares.
    public bool Equals(TaskItem? other)
        => other is not null
           && Uid == other.Uid && Summary == other.Summary && Description == other.Description
           && Start == other.Start && Due == other.Due && CompletedUtc == other.CompletedUtc
           && Progress == other.Progress && PercentComplete == other.PercentComplete
           && Urgency == other.Urgency && Rrule == other.Rrule && RecurrenceId == other.RecurrenceId
           && ExceptionDates.SequenceEqual(other.ExceptionDates)
           && ReminderMinutes == other.ReminderMinutes
           && Categories.SequenceEqual(other.Categories, StringComparer.Ordinal)
           && IsPrivate == other.IsPrivate
           && Owner == other.Owner && Sequence == other.Sequence && LastModified == other.LastModified;

    public override int GetHashCode()
        => HashCode.Combine(Uid, Summary, Start, Due, Progress, PercentComplete, Rrule, LastModified);
}
