using System.Globalization;

namespace Mailbox.Scheduling;

/// <summary>
/// What the task list can be grouped by.
/// </summary>
/// <remarks>
/// The list was arranged by due date and by nothing else — the bands were an enum all the way
/// through, so "group by Categories" was not a menu entry away, it was a different kind of key.
/// This is that key: whatever the arrangement, a row lands in a <see cref="TaskGroup"/> with a
/// heading a person reads and an order the headings come in.
/// <para>
/// Assignment is deliberately absent. The reference offers it because it can hand a task to
/// somebody and watch them accept it; nothing here delegates, so a gallery entry for it would
/// group every task under one heading and mean nothing.
/// </para>
/// </remarks>
public enum TaskArrangement
{
    /// <summary>Today, Tomorrow, This Week and the rest — what the list has always done.</summary>
    DueDate,

    /// <summary>The same bands read off the start date, for work that is scheduled rather than owed.</summary>
    StartDate,

    Categories,

    /// <summary>Which task list it is on, which is the reference's Folder.</summary>
    Folder,

    /// <summary>A task, a flagged message or a flagged contact.</summary>
    Type,

    Importance,

    /// <summary>When it was last touched, in the same bands a date takes.</summary>
    Modified,
}

/// <summary>
/// One band of a grouped task list: what it is called, and where it comes.
/// </summary>
/// <param name="Key">
/// What tells two bands apart. Not the heading: two categories could share a heading only by
/// being the same category, but an empty heading is a real band ("None") and comparing on the
/// drawn text would fold every unheaded row together by accident.
/// </param>
/// <param name="Heading">What the list writes above the band.</param>
/// <param name="Order">Where the band comes among the others. Bands with equal order sort by heading.</param>
public readonly record struct TaskGroup(string Key, string Heading, int Order);

/// <summary>Turns a row into the band it belongs in, for each arrangement.</summary>
public static class TaskArrangements
{
    /// <summary>Every arrangement, in the order the gallery offers them.</summary>
    public static readonly IReadOnlyList<TaskArrangement> All =
    [
        TaskArrangement.Categories,
        TaskArrangement.StartDate,
        TaskArrangement.DueDate,
        TaskArrangement.Folder,
        TaskArrangement.Type,
        TaskArrangement.Importance,
        TaskArrangement.Modified,
    ];

    public static string Label(TaskArrangement arrangement) => arrangement switch
    {
        TaskArrangement.DueDate => "Due Date",
        TaskArrangement.StartDate => "Start Date",
        TaskArrangement.Modified => "Modified Date",
        _ => arrangement.ToString(),
    };

    /// <summary>
    /// The band a row falls in.
    /// </summary>
    /// <param name="row">The row to place.</param>
    /// <param name="arrangement">What the list is grouped by.</param>
    /// <param name="today">The day the date bands are measured against.</param>
    /// <param name="listName">
    /// What a task list is called, for the Folder arrangement. Given rather than looked up, so
    /// this stays a pure function of its inputs and can be tested without a store.
    /// </param>
    /// <remarks>
    /// A row with nothing to group by lands in a band that says so — "None", "No Date" — rather
    /// than being dropped or folded into the first band. A task with no category is a fact about
    /// the task, and a list that hid it while grouped by category would be losing rows.
    /// </remarks>
    public static TaskGroup GroupOf(
        TaskRow row, TaskArrangement arrangement, DateOnly today, Func<long, string>? listName = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        return arrangement switch
        {
            TaskArrangement.DueDate => Band(row.Band),

            // Read off the start date through the same banding, so "This Week" means the same
            // thing under either arrangement. A finished task is finished whichever date is
            // being grouped on, which is why the completed band survives the swap.
            TaskArrangement.StartDate => Band(row.Task.IsComplete
                ? TaskBand.Completed
                : TaskBook.BandOf(row.Task with { Due = row.Task.Start }, today)),

            TaskArrangement.Modified => Band(TaskBook.BandOf(
                row.Task with { Due = EventTime.Date(DateOnly.FromDateTime(row.Task.LastModified.LocalDateTime)) },
                today)),

            // The first category, which is how the reference groups a row that has several: a
            // row appears once, under the category it was given first.
            TaskArrangement.Categories => row.Task.Categories.FirstOrDefault() is { Length: > 0 } category
                ? new TaskGroup("cat:" + category.ToLowerInvariant(), category, 0)
                : new TaskGroup("cat:", "None", 1),

            TaskArrangement.Folder => Folder(row, listName),

            TaskArrangement.Type => row switch
            {
                { IsMessage: true } => new TaskGroup("type:message", "Mail", 1),
                { IsContact: true } => new TaskGroup("type:contact", "Contact", 2),
                _ => new TaskGroup("type:task", "Task", 0),
            },

            // High first: the point of grouping by importance is to see what matters at the top.
            _ => row.Task.Urgency switch
            {
                TaskUrgency.High => new TaskGroup("imp:high", "High", 0),
                TaskUrgency.Low => new TaskGroup("imp:low", "Low", 2),
                _ => new TaskGroup("imp:normal", "Normal", 1),
            },
        };
    }

    /// <summary>The due-date bands as groups, keeping the order the enum already declares.</summary>
    public static TaskGroup Band(TaskBand band)
        => new("band:" + band, TaskBook.Heading(band), (int)band);

    /// <summary>
    /// Which list a row is on.
    /// </summary>
    /// <remarks>
    /// A borrowed row — flagged mail, a flagged contact — is not on a task list at all, so it
    /// gets a band of its own rather than being filed under whichever collection its id happens
    /// to belong to, which would be a different store's numbering.
    /// </remarks>
    private static TaskGroup Folder(TaskRow row, Func<long, string>? listName)
    {
        if (row.IsMessage) return new TaskGroup("folder:mail", "Mail", 1);
        if (row.IsContact) return new TaskGroup("folder:contacts", "Contacts", 1);

        var name = listName?.Invoke(row.CollectionId) ?? string.Empty;
        return new TaskGroup(
            "folder:" + row.CollectionId.ToString(CultureInfo.InvariantCulture),
            name.Length > 0 ? name : "Tasks",
            0);
    }

    /// <summary>
    /// The rows stamped with their band and put in band order.
    /// </summary>
    /// <remarks>
    /// Stable within a band: the incoming order is the sort the caller already chose — by due
    /// date and then by name — and re-sorting inside a band would throw that away.
    /// </remarks>
    public static IReadOnlyList<TaskRow> Arrange(
        IEnumerable<TaskRow> rows, TaskArrangement arrangement, DateOnly today, Func<long, string>? listName = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return
        [
            .. rows
                .Select(r => r with { Group = GroupOf(r, arrangement, today, listName) })
                .OrderBy(r => r.Group.Order)
                .ThenBy(r => r.Group.Heading, StringComparer.CurrentCultureIgnoreCase),
        ];
    }
}
