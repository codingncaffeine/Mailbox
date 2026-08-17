using Mailbox.Scheduling;

namespace Mailbox.Google;

/// <summary>
/// A task here and a task there, and the four fields they have in common.
/// </summary>
/// <remarks>
/// This is the interesting half of the whole feature, because the two records are not the same
/// size. A task in this application is a VTODO: it can carry a priority, categories, a recurrence
/// rule, a reminder, a start date as well as a due date, a private class, a percentage and an
/// owner. A task at Google carries a title, notes, a due <em>date</em>, and whether it is done.
/// <para>
/// So the rule is <b>merge, never replace</b>. A pull overlays what Google knows onto the VTODO
/// that is already here and leaves the rest of it alone — otherwise ticking a task on a phone
/// would strip the priority, the categories and the recurrence it had, and the loss would look
/// like a sync working.
/// </para>
/// <para>
/// The same rule read the other way is why the divergence is stated rather than hidden: a task
/// <em>created</em> in a Google list has none of those fields to lose, and one that acquires them
/// here keeps them here. Nothing is silently dropped; what Google cannot hold simply stays on this
/// machine, which is the honest behaviour and the one a user can predict.
/// </para>
/// </remarks>
public static class GoogleTaskCodec
{
    /// <summary>What a Google task cannot carry, for the divergence note and for the tests.</summary>
    public static readonly IReadOnlyList<string> NotCarried =
        ["priority", "categories", "recurrence", "reminder", "start date", "private class", "the time of day on a due date"];

    /// <summary>
    /// The Google form of a task here.
    /// </summary>
    /// <param name="id">
    /// The Google id when this task is already on the list, empty for one being inserted.
    /// </param>
    /// <remarks>
    /// The due date is the date of whatever the task is due at, in the zone the task states it in.
    /// A task due at 17:00 on Friday in Berlin is due on Friday, and taking the UTC instant's date
    /// instead would make it Thursday for anyone far enough east.
    /// </remarks>
    public static GoogleTask ToGoogle(TaskItem task, string id = "")
    {
        ArgumentNullException.ThrowIfNull(task);

        return new GoogleTask
        {
            Id = id,
            Title = task.Summary,
            Notes = task.Description,
            Due = task.Due is { } due ? DateOnly.FromDateTime(due.Wall) : null,
            Status = task.IsComplete ? GoogleTask.CompletedStatus : GoogleTask.NeedsAction,

            // Google wants a moment for a completed task and refuses one for a task that is not
            // done. A task ticked here without a time — which the store allows — is recorded as
            // completed now, that being the only true thing available to say.
            Completed = task.IsComplete ? task.CompletedUtc ?? DateTimeOffset.UtcNow : null,
        };
    }

    /// <summary>
    /// Google's version of a task laid over the one that is here.
    /// </summary>
    /// <param name="existing">
    /// The task as this machine has it, or null for one that has only ever existed at Google.
    /// </param>
    /// <remarks>
    /// Four fields move and nothing else does. Note what completing does: it sets the progress,
    /// the percentage and the completion time together, because a task that is complete by one of
    /// the three and not the others is what every reader of it then disagrees about — the same
    /// rule <see cref="PimTodoCodec.Complete"/> holds to.
    /// </remarks>
    public static TaskItem Merge(TaskItem? existing, GoogleTask google)
    {
        ArgumentNullException.ThrowIfNull(google);

        var task = existing ?? new TaskItem { Uid = NewUid(google) };

        task = task with
        {
            Summary = google.Title,
            Description = google.Notes,

            // A due date and nothing else: Google has no start, and clearing a due date there
            // clears it here. The existing task's start is left where it is — it is one of the
            // things Google was never told about.
            Due = google.Due is { } due ? EventTime.Date(due) : null,
            LastModified = google.Updated ?? DateTimeOffset.UtcNow,
        };

        // The completion state is set through the same helper the rest of the application ticks a
        // task with, so the three fields that say "done" cannot come to disagree.
        var completedAt = google.Completed ?? google.Updated ?? DateTimeOffset.UtcNow;
        if (google.IsComplete != task.IsComplete || (google.IsComplete && task.CompletedUtc is null))
        {
            task = PimTodoCodec.Complete(task, google.IsComplete, completedAt);
        }
        else if (google.IsComplete && google.Completed is { } stated && task.CompletedUtc != stated)
        {
            task = task with { CompletedUtc = stated };
        }

        return task;
    }

    /// <summary>
    /// Whether what Google holds differs from what this machine does, in the fields they share.
    /// </summary>
    /// <remarks>
    /// Asked before a write, so a poll that brought back a task nobody changed does not queue a
    /// pointless round trip — and, more usefully, so the sync can tell a task that really moved
    /// from one whose <c>updated</c> stamp moved because something outside these four fields did.
    /// </remarks>
    public static bool Differs(TaskItem task, GoogleTask google)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(google);

        var mine = ToGoogle(task);
        return mine.Title != google.Title
               || mine.Notes != google.Notes
               || mine.Due != google.Due
               || mine.Status != google.Status;
    }

    /// <summary>
    /// A UID for a task that arrived from Google.
    /// </summary>
    /// <remarks>
    /// Built from Google's own id rather than a fresh GUID, so the same task pulled onto two
    /// machines is one task with one UID — which is what lets a list synced here and exported as
    /// iCalendar there still be recognised as itself.
    /// </remarks>
    public static string NewUid(GoogleTask google)
    {
        ArgumentNullException.ThrowIfNull(google);
        return $"{google.Id}@tasks.google.com";
    }
}
