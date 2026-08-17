using System.Globalization;
using System.Text.Json;

namespace Mailbox.Google;

/// <summary>One of the lists a Google account keeps its tasks in.</summary>
public sealed record GoogleTaskList(string Id, string Title, DateTimeOffset? Updated);

/// <summary>
/// A task as Google keeps it, which is less than a task is.
/// </summary>
/// <remarks>
/// The whole record, and it is worth reading for what is not in it: no priority, no categories, no
/// recurrence, no reminder, no start date, and no time of day on the due date — Google's own
/// documentation says the time portion of <c>due</c> is ignored. Anything this application knows
/// about a task beyond these fields has nowhere to go, which is why a pull merges rather than
/// replaces (see <see cref="GoogleTaskCodec"/>).
/// <para>
/// <c>Position</c> and <c>Parent</c> are Google's ordering and sub-task nesting. They are read and
/// sent back untouched: neither has an equivalent here, and dropping them on an update would
/// reorder somebody's list from a client that never showed the order.
/// </para>
/// </remarks>
public sealed record GoogleTask
{
    public required string Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    /// <summary>Google's own clock on the last change. The only change marker there is.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary><c>needsAction</c> or <c>completed</c>.</summary>
    public string Status { get; init; } = NeedsAction;

    /// <summary>The due date. A date, whatever the time in the value says.</summary>
    public DateOnly? Due { get; init; }

    public DateTimeOffset? Completed { get; init; }

    /// <summary>A tombstone: the task was removed, and this is how a poll learns it.</summary>
    public bool Deleted { get; init; }

    /// <summary>
    /// Hidden by Google, which is what a completed task becomes when the list is cleared. Not the
    /// same as deleted — the task is still there and still ours to show.
    /// </summary>
    public bool Hidden { get; init; }

    public string Parent { get; init; } = string.Empty;

    public string Position { get; init; } = string.Empty;

    public const string NeedsAction = "needsAction";
    public const string CompletedStatus = "completed";

    public bool IsComplete => Status == CompletedStatus;

    /// <summary>
    /// Every task in an API answer — either the whole <c>{"items":[…]}</c> envelope or a bare
    /// array of them.
    /// </summary>
    /// <remarks>
    /// Public because the answer is worth reading somewhere other than at the end of a request:
    /// the fidelity harness delivers a saved one from a file, a poll being HTTP and a capture run
    /// having no business on the network.
    /// </remarks>
    public static IReadOnlyList<GoogleTask> ReadAll(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array
                ? array
                : default;

        if (items.ValueKind != JsonValueKind.Array) return [];

        var tasks = new List<GoogleTask>();
        foreach (var item in items.EnumerateArray())
        {
            var task = Read(item);
            if (task.Id.Length > 0) tasks.Add(task);
        }

        return tasks;
    }

    /// <summary>Reads one task out of an API response.</summary>
    internal static GoogleTask Read(JsonElement element) => new()
    {
        Id = Text(element, "id"),
        Title = Text(element, "title"),
        Notes = Text(element, "notes"),
        Updated = Moment(element, "updated"),
        Status = Text(element, "status") is { Length: > 0 } status ? status : NeedsAction,
        Due = Day(element, "due"),
        Completed = Moment(element, "completed"),
        Deleted = Flag(element, "deleted"),
        Hidden = Flag(element, "hidden"),
        Parent = Text(element, "parent"),
        Position = Text(element, "position"),
    };

    /// <summary>
    /// What is sent for an insert or an update.
    /// </summary>
    /// <remarks>
    /// A PATCH sends only what it means to change, so a field this application has no opinion
    /// about is left out rather than sent empty — sending <c>notes: ""</c> for a task whose notes
    /// were written on a phone would erase them. Title, notes, due and status are the four this
    /// application owns; the rest is Google's.
    /// <para>
    /// <c>due</c> is written as midnight UTC because Google records the date and discards the
    /// time; writing the local wall time instead is how a task due on the 1st becomes one due on
    /// the 31st for anyone west of Greenwich.
    /// </para>
    /// </remarks>
    internal string ToJson()
    {
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("title", Title);
            writer.WriteString("notes", Notes);
            writer.WriteString("status", Status);

            if (Due is { } due)
            {
                writer.WriteString("due",
                    due.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            }
            else
            {
                // Explicitly null, not absent: absent leaves whatever date is there, and a task
                // whose due date was cleared here has to lose it there as well.
                writer.WriteNull("due");
            }

            if (Status == CompletedStatus && Completed is { } completed)
            {
                writer.WriteString("completed",
                    completed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            }
            else if (Status != CompletedStatus)
            {
                writer.WriteNull("completed");
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Moment(JsonElement element, string name)
        => Text(element, name) is { Length: > 0 } text
           && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var moment)
            ? moment
            : null;

    /// <summary>
    /// The date out of a due value.
    /// </summary>
    /// <remarks>
    /// Read as UTC and then taken apart, because that is how it was written: Google states the due
    /// date as midnight UTC. Parsing it into local time first would move a due date across
    /// midnight for half the world.
    /// </remarks>
    private static DateOnly? Day(JsonElement element, string name)
        => Moment(element, name) is { } moment ? DateOnly.FromDateTime(moment.UtcDateTime) : null;
}
