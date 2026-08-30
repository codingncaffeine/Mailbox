using System.Globalization;
using Mailbox.Store;
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

/// <summary>
/// The message a to-do row stands for, when the row is flagged mail rather than a task.
/// </summary>
/// <param name="Account">Which account's store it is in — every account has its own file.</param>
/// <param name="MessageId">Its row there.</param>
/// <param name="From">Who sent it, which is what the reference writes beside the subject.</param>
public sealed record FlaggedMessage(string Account, long MessageId, string From);

/// <summary>
/// The contact a to-do row stands for, when the row is a flagged contact.
/// </summary>
/// <remarks>
/// One field beyond the id, and it is the name: a contact's row says who it is where a message's
/// says what it is about, so the name is the row's own summary rather than something written
/// after it the way a sender is.
/// </remarks>
/// <param name="ItemId">Its row in the PIM store, which is the same numbering a task's uses.</param>
public sealed record FlaggedContact(long ItemId, string Name);

/// <summary>One line of the to-do list: the row it came from and everything drawing it needs.</summary>
/// <remarks>
/// A task or a flagged message, because the reference's own To-Do List holds both and treats them
/// alike — the same bands, the same tick box, the same red for what is late. A message is carried
/// as a <see cref="TaskItem"/> whose summary is the subject and whose due date is the flag's,
/// which is what lets one list draw two things without knowing about either store.
/// </remarks>
public sealed record TaskRow
{
    public required long ItemId { get; init; }
    public required long CollectionId { get; init; }
    public required TaskItem Task { get; init; }
    public required TaskBand Band { get; init; }

    /// <summary>Late and not done, which the reference draws in red.</summary>
    public required bool IsOverdue { get; init; }

    /// <summary>Set when this row is a flagged message rather than a task of its own.</summary>
    public FlaggedMessage? Message { get; init; }

    /// <summary>Set when this row is a flagged contact.</summary>
    public FlaggedContact? Contact { get; init; }

    public bool IsMessage => Message is not null;

    public bool IsContact => Contact is not null;

    /// <summary>True for a row that is somebody else's item, borrowed onto this list.</summary>
    /// <remarks>
    /// What the Tags group asks before it writes: Private is CLASS on a VTODO and Importance is
    /// PRIORITY, and neither a message nor a contact has anywhere to keep one.
    /// </remarks>
    public bool IsBorrowed => Message is not null || Contact is not null;

    public string Summary => Task.Summary.Length > 0 ? Task.Summary : "(No subject)";

    public bool IsComplete => Task.IsComplete;

    /// <summary>
    /// What tells two rows apart, since a task and a message are numbered by different stores and
    /// their ids collide as readily as not.
    /// </summary>
    public string Key => this switch
    {
        { Message: { } message } => $"m:{message.Account}:{message.MessageId}",

        // A contact and a task are numbered by the same store, so the letter is the whole of
        // what tells them apart.
        { Contact: not null } => $"c:{ItemId}",
        _ => $"t:{ItemId}",
    };

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
public sealed class TaskBook(PimRepository repository, Func<IReadOnlyList<(string Address, MailRepository Mail)>>? mailboxes = null)
{
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>
    /// Where the flagged mail comes from, when the list is showing any.
    /// </summary>
    /// <remarks>
    /// A function rather than a list, because accounts are opened and closed while the
    /// application runs; and optional, because the reading half is worth having on its own —
    /// the seed and the tests use a book with no mail behind it at all.
    /// </remarks>
    private readonly Func<IReadOnlyList<(string Address, MailRepository Mail)>>? _mailboxes = mailboxes;

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
    /// <param name="includeFlagged">
    /// Whether the flagged mail and contacts ride along. True is the To-Do List, which is the
    /// join; false is the reference's Tasks folder, the one list that shows tasks alone.
    /// </param>
    public IReadOnlyList<TaskRow> Rows(
        DateOnly today,
        bool includeCompleted = false,
        IReadOnlyCollection<long>? collectionIds = null,
        bool includeFlagged = true)
    {
        var rows = new List<TaskRow>();

        foreach (var list in Lists())
        {
            if (collectionIds is { Count: > 0 } ? !collectionIds.Contains(list.Id) : !list.IsVisible) continue;

            foreach (var item in _repository.Items(list.Id))
            {
                // A delete on a server-backed list keeps the row, marks it and queues it, so that
                // a delete made offline still reaches the server. It is gone as far as the reader
                // is concerned from the moment they said so.
                if (item.SyncState == PimSyncState.Deleted) continue;

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

        if (includeFlagged)
        {
            rows.AddRange(FlaggedMail(today, includeCompleted));
            rows.AddRange(FlaggedContacts(today, includeCompleted));
        }

        rows.Sort(Compare);
        return rows;
    }

    /// <summary>
    /// The flagged mail the reference lists beside the tasks, one row a message.
    /// </summary>
    /// <remarks>
    /// A flagged message is a to-do with somebody else's words in it: its subject is what the row
    /// says, its follow-up date is when it is due, and marking it complete is what the reference's
    /// own tick does — so it is carried as a task rather than as a second kind of thing, and only
    /// what acts on a row needs to know which it is.
    /// </remarks>
    private IEnumerable<TaskRow> FlaggedMail(DateOnly today, bool includeCompleted)
    {
        if (_mailboxes is null) yield break;

        foreach (var (address, mail) in _mailboxes())
        {
            foreach (var message in mail.FlaggedMessages(includeCompleted))
            {
                var complete = message.FollowUpComplete;
                var due = message.FollowUpDue?.LocalDateTime;

                var task = new TaskItem
                {
                    Uid = $"message-{message.Id}@mailbox",
                    Summary = message.Subject.Length > 0 ? message.Subject : "(No subject)",
                    Due = due is { } when ? EventTime.Date(DateOnly.FromDateTime(when)) : null,
                    Progress = complete ? TaskProgress.Completed : TaskProgress.NotStarted,
                    PercentComplete = complete ? 100 : 0,
                    CompletedUtc = complete ? message.Received : null,
                    LastModified = message.Received,
                };

                yield return new TaskRow
                {
                    ItemId = message.Id,
                    CollectionId = 0,
                    Task = task,
                    Band = BandOf(task, today),
                    IsOverdue = !complete && task.Due is { } date && Date(date) < today,
                    Message = new FlaggedMessage(address, message.Id, message.DisplayFrom),
                };
            }
        }
    }

    /// <summary>
    /// The flagged contacts, beside the tasks and the flagged mail — the same join, over this
    /// store rather than an account's.
    /// </summary>
    /// <remarks>
    /// Carried as a task for the same reason a message is: what the list draws is a summary, a
    /// due date and a tick, and a row that had to be asked which of three things it was before
    /// any of that could be worked out would put the whole list in the business of knowing about
    /// three stores. The contact's File As is the summary, because what a flagged contact means
    /// is "ring this person" and the person is the whole of it.
    /// </remarks>
    private IEnumerable<TaskRow> FlaggedContacts(DateOnly today, bool includeCompleted)
    {
        foreach (var item in _repository.FlaggedContacts(includeCompleted))
        {
            var due = item.FollowUpDue?.LocalDateTime;
            var name = item.FileAs.Length > 0 ? item.FileAs : item.Summary;

            var task = new TaskItem
            {
                Uid = $"contact-{item.Id}@mailbox",
                Summary = name.Length > 0 ? name : "(No name)",
                Due = due is { } when ? EventTime.Date(DateOnly.FromDateTime(when)) : null,
                Progress = item.FollowUpComplete ? TaskProgress.Completed : TaskProgress.NotStarted,
                PercentComplete = item.FollowUpComplete ? 100 : 0,
                CompletedUtc = item.FollowUpComplete ? item.FollowUpDue : null,
                LastModified = item.FollowUpDue ?? DateTimeOffset.UnixEpoch,
            };

            var band = BandOf(task, today);
            if (band == TaskBand.Completed && !includeCompleted) continue;

            yield return new TaskRow
            {
                ItemId = item.Id,
                CollectionId = item.CollectionId,
                Task = task,
                Band = band,
                IsOverdue = !item.FollowUpComplete && task.Due is { } date && Date(date) < today,
                Contact = new FlaggedContact(item.Id, task.Summary),
            };
        }
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

    /// <summary>
    /// The same rows in the order a table shows them: by due date, then by name.
    /// </summary>
    /// <remarks>
    /// A grouped list bands what is finished at the foot, because a band is a heading and
    /// "Completed" is one. A table has no headings — it has a sort column, and the reference's
    /// detailed view sorts by the due date — so a task that was ticked stays where its date puts
    /// it, struck through, rather than jumping to the bottom of the list under the pointer.
    /// </remarks>
    public static IReadOnlyList<TaskRow> ByDueDate(IEnumerable<TaskRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return
        [
            .. rows
                .OrderBy(r => r.Task.Due?.Wall ?? DateTime.MaxValue)
                .ThenBy(r => r.Summary, StringComparer.CurrentCultureIgnoreCase),
        ];
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
