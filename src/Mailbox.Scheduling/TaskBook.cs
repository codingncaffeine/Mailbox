using System.Globalization;
using Mailbox.Store.Pim;

namespace Mailbox.Scheduling;

/// <summary>
/// The band a task falls in when the list is arranged by due date, in the order the reference
/// lists them.
/// </summary>
/// <remarks>
/// Authored rather than measured: the one capture of the module holds a single group, "Today",
/// and the rest is the arrangement the reference's own To-Do Bar offers. A task that is late is
/// drawn in red inside Today rather than in a group of its own — which is what the capture shows
/// and invents no heading the reference may not have.
/// </remarks>
public enum TaskBand
{
    Today,
    Tomorrow,
    ThisWeek,
    NextWeek,
    NextMonth,
    Later,
    NoDate,
    Completed,
}

/// <summary>One line of the task list: the row it came from and everything drawing it needs.</summary>
public sealed record TaskRow
{
    public required long ItemId { get; init; }
    public required long CollectionId { get; init; }
    public required TaskItem Task { get; init; }
    public required TaskBand Band { get; init; }

    /// <summary>Late and not done, which the reference draws in red.</summary>
    public required bool IsOverdue { get; init; }

    public string Summary => Task.Summary.Length > 0 ? Task.Summary : "(No subject)";

    public bool IsComplete => Task.IsComplete;

    /// <summary>The due date as the list writes it, or nothing at all for a task with no date.</summary>
    public string DueText(IFormatProvider? culture = null)
        => Task.Due is { } due ? due.Wall.ToString("ddd dd/MM/yyyy", culture ?? CultureInfo.CurrentCulture) : string.Empty;
}

/// <summary>
/// The task lists and what is on them: the store's rows as the module draws them.
/// </summary>
/// <remarks>
/// The reading half of the module, the way <c>ContactBook</c> is the reading half of People —
/// rows built from the columns, the whole VTODO parsed only when one is opened. Writing stays
/// with the shell, which owns the queue a change has to join.
/// </remarks>
public sealed class TaskBook(PimRepository repository)
{
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>The task lists, in the order the navigation pane shows them.</summary>
    public IReadOnlyList<Collection> Lists() => _repository.Collections(CollectionKind.Tasks);

    /// <summary>
    /// Everything on the visible lists, banded and in the order the reference draws them: by
    /// band, then by due date, then by what it is called.
    /// </summary>
    /// <param name="today">Today, as the module believes it — pinned by the harness.</param>
    /// <param name="includeCompleted">
    /// The To-Do List hides what is done; the Simple List shows it. False leaves the Completed
    /// band out altogether rather than drawing an empty one.
    /// </param>
    /// <param name="collectionIds">Only these lists; null for every visible one.</param>
    public IReadOnlyList<TaskRow> Rows(
        DateOnly today,
        bool includeCompleted = false,
        IReadOnlyCollection<long>? collectionIds = null)
    {
        var rows = new List<TaskRow>();

        foreach (var list in Lists())
        {
            if (collectionIds is { Count: > 0 } ? !collectionIds.Contains(list.Id) : !list.IsVisible) continue;

            foreach (var item in _repository.Items(list.Id))
            {
                // From the columns, not the text: a list of five hundred tasks would otherwise
                // parse five hundred VTODOs to draw five hundred lines.
                var task = PimTodoCodec.FromColumns(item);
                var band = BandOf(task, today);
                if (band == TaskBand.Completed && !includeCompleted) continue;

                rows.Add(new TaskRow
                {
                    ItemId = item.Id,
                    CollectionId = list.Id,
                    Task = task,
                    Band = band,
                    IsOverdue = !task.IsComplete && task.Due is { } due && Date(due) < today,
                });
            }
        }

        rows.Sort(Compare);
        return rows;
    }

    /// <summary>The whole task, parsed, which is what opening one wants.</summary>
    public TaskItem? Open(long itemId)
        => _repository.Item(itemId) is { } item ? PimTodoCodec.FromItem(item) : null;

    /// <summary>Which band a task falls in.</summary>
    public static TaskBand BandOf(TaskItem task, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsComplete) return TaskBand.Completed;
        if (task.Due is not { } due) return TaskBand.NoDate;

        var day = Date(due);

        // Late counts as today: it is what wants doing now, and the row says so in red.
        if (day <= today) return TaskBand.Today;
        if (day == today.AddDays(1)) return TaskBand.Tomorrow;

        // The week runs to Sunday, the reference's own week ending where its date navigator's
        // rows do rather than seven days from now.
        var endOfWeek = today.AddDays(6 - (int)today.DayOfWeek);
        if (day <= endOfWeek) return TaskBand.ThisWeek;
        if (day <= endOfWeek.AddDays(7)) return TaskBand.NextWeek;

        var endOfMonth = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        if (day <= endOfMonth.AddMonths(1)) return TaskBand.NextMonth;
        return TaskBand.Later;
    }

    /// <summary>What the list writes above a band.</summary>
    public static string Heading(TaskBand band) => band switch
    {
        TaskBand.Today => "Today",
        TaskBand.Tomorrow => "Tomorrow",
        TaskBand.ThisWeek => "This Week",
        TaskBand.NextWeek => "Next Week",
        TaskBand.NextMonth => "Next Month",
        TaskBand.Later => "Later",
        TaskBand.NoDate => "No Date",
        _ => "Completed",
    };

    private static DateOnly Date(EventTime time) => DateOnly.FromDateTime(time.Wall);

    private static int Compare(TaskRow a, TaskRow b)
    {
        var byBand = a.Band.CompareTo(b.Band);
        if (byBand != 0) return byBand;

        var byDue = (a.Task.Due?.Wall ?? DateTime.MaxValue).CompareTo(b.Task.Due?.Wall ?? DateTime.MaxValue);
        if (byDue != 0) return byDue;

        return string.Compare(a.Summary, b.Summary, StringComparison.CurrentCultureIgnoreCase);
    }
}
