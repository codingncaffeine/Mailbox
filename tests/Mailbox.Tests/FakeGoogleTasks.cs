using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Mailbox.Tests;

/// <summary>
/// The Google Tasks API, in an <see cref="HttpMessageHandler"/>.
/// </summary>
/// <remarks>
/// It keeps a clock of its own rather than reading the machine's, because every interesting
/// question here is about ordering — what <c>updatedMin</c> returns, whether a task changed since
/// the last poll — and a test that had to sleep to make two moments differ would be both slow and
/// flaky. <see cref="Tick"/> moves it.
/// <para>
/// It answers what the real one answers in the two places that matter and are easy to get wrong:
/// a deleted task comes back as a tombstone rather than vanishing, and pages come back one at a
/// time when <see cref="PageSize"/> says so.
/// </para>
/// </remarks>
public sealed class FakeGoogleTasks : HttpMessageHandler
{
    private readonly Dictionary<string, string> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Row>> _tasks = new(StringComparer.Ordinal);
    private int _ids;

    private sealed record Row(
        string Id, string Title, string Notes, string Status, DateTimeOffset? Due,
        DateTimeOffset? Completed, bool Deleted, DateTimeOffset Updated, string Position);

    public FakeGoogleTasks(string listId = "list-1", string listTitle = "My Tasks")
    {
        _lists[listId] = listTitle;
        _tasks[listId] = new Dictionary<string, Row>(StringComparer.Ordinal);
        DefaultList = listId;
    }

    /// <summary>The list a test means when it does not say.</summary>
    public string DefaultList { get; }

    /// <summary>The server's own clock. Tests move it rather than waiting.</summary>
    public DateTimeOffset Now { get; private set; } = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>How many items a page holds; 0 for all of them in one.</summary>
    public int PageSize { get; set; }

    /// <summary>Every request made, as "METHOD path", for asserting a poll was one round trip.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Refuse the next request with this status.</summary>
    public HttpStatusCode? NextFailure { get; set; }

    /// <summary>
    /// Refuse the next write with this status, leaving reads alone — which is the shape a quota
    /// takes in practice, and the only way to exercise a push failing after a pull succeeded.
    /// </summary>
    public HttpStatusCode? NextWriteFailure { get; set; }

    /// <summary>Moves the clock, so a change made after this is newer than one made before.</summary>
    public void Tick(TimeSpan by) => Now = Now.Add(by);

    public void AddList(string id, string title)
    {
        _lists[id] = title;
        _tasks[id] = new Dictionary<string, Row>(StringComparer.Ordinal);
    }

    public void RemoveList(string id)
    {
        _lists.Remove(id);
        _tasks.Remove(id);
    }

    /// <summary>Puts a task on a list behind the client's back, as another client would.</summary>
    public string Publish(string title, string? notes = null, DateOnly? due = null, bool complete = false, string? listId = null)
    {
        var list = listId ?? DefaultList;
        var id = $"task-{++_ids}";
        _tasks[list][id] = new Row(
            id, title, notes ?? string.Empty,
            complete ? "completed" : "needsAction",
            due is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            complete ? Now : null,
            Deleted: false, Now, Position: id);
        return id;
    }

    /// <summary>Changes a task behind the client's back.</summary>
    public void Edit(string id, string? title = null, string? notes = null, bool? complete = null,
        DateOnly? due = null, bool clearDue = false, string? listId = null)
    {
        var list = listId ?? DefaultList;
        var row = _tasks[list][id];
        _tasks[list][id] = row with
        {
            Title = title ?? row.Title,
            Notes = notes ?? row.Notes,
            Status = complete is { } done ? (done ? "completed" : "needsAction") : row.Status,
            Completed = complete is true ? Now : complete is false ? null : row.Completed,
            Due = clearDue
                ? null
                : due is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : row.Due,
            Updated = Now,
        };
    }

    /// <summary>Deletes a task behind the client's back, leaving the tombstone a poll finds.</summary>
    public void Delete(string id, string? listId = null)
    {
        var list = listId ?? DefaultList;
        if (_tasks[list].TryGetValue(id, out var row))
        {
            _tasks[list][id] = row with { Deleted = true, Updated = Now };
        }
    }

    /// <summary>What the server holds, for asserting what a push actually sent.</summary>
    public (string Title, string Notes, string Status, DateOnly? Due)? Task(string id, string? listId = null)
        => _tasks[listId ?? DefaultList].TryGetValue(id, out var row) && !row.Deleted
            ? (row.Title, row.Notes, row.Status, row.Due is { } d ? DateOnly.FromDateTime(d.UtcDateTime) : null)
            : null;

    public int Count(string? listId = null) => _tasks[listId ?? DefaultList].Values.Count(r => !r.Deleted);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = Query(request.RequestUri.Query);
        Requests.Add($"{request.Method} {path}");

        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter.Length: > 0 })
        {
            return Error(HttpStatusCode.Unauthorized, "authError", "Login Required.");
        }

        if (NextFailure is { } failure)
        {
            NextFailure = null;
            return Error(failure, "backendError", "Try again.");
        }

        if (NextWriteFailure is { } writeFailure && request.Method != HttpMethod.Get)
        {
            NextWriteFailure = null;
            return Error(writeFailure, "rateLimitExceeded", "Too many requests. Try again later.");
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        if (path.EndsWith("/users/@me/lists", StringComparison.Ordinal)) return Lists();

        // /tasks/v1/lists/{list}/tasks[/{task}]
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(parts, "lists");
        if (at < 0 || parts.Length < at + 3) return Error(HttpStatusCode.NotFound, "notFound", "No such path.");

        var listId = Uri.UnescapeDataString(parts[at + 1]);
        if (!_tasks.TryGetValue(listId, out var tasks))
        {
            return Error(HttpStatusCode.NotFound, "notFound", "No such list.");
        }

        var taskId = parts.Length > at + 3 ? Uri.UnescapeDataString(parts[at + 3]) : null;

        return (request.Method.Method, taskId) switch
        {
            ("GET", null) => Page(tasks, query.GetValueOrDefault("updatedMin"), query.GetValueOrDefault("pageToken")),
            ("POST", null) => Insert(tasks, body),
            ("PATCH", not null) => Patch(tasks, taskId, body),
            ("DELETE", not null) => Remove(tasks, taskId),
            _ => Error(HttpStatusCode.MethodNotAllowed, "notImplemented", "Not here."),
        };
    }

    private HttpResponseMessage Lists()
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "tasks#taskLists");
            writer.WriteStartArray("items");
            foreach (var (id, title) in _lists)
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteString("title", title);
                writer.WriteString("updated", Stamp(Now));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Json(HttpStatusCode.OK, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private HttpResponseMessage Page(Dictionary<string, Row> tasks, string? updatedMin, string? pageToken)
    {
        var since = updatedMin is { Length: > 0 }
            ? DateTimeOffset.Parse(updatedMin, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
            : (DateTimeOffset?)null;

        var matching = tasks.Values
            .Where(r => since is null || r.Updated >= since)
            .OrderBy(r => r.Position, StringComparer.Ordinal)
            .ToList();

        var from = pageToken is { Length: > 0 } ? int.Parse(pageToken, CultureInfo.InvariantCulture) : 0;
        var take = PageSize > 0 ? PageSize : matching.Count;
        var page = matching.Skip(from).Take(take).ToList();
        var next = PageSize > 0 && from + take < matching.Count ? (from + take).ToString(CultureInfo.InvariantCulture) : null;

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "tasks#tasks");
            if (next is not null) writer.WriteString("nextPageToken", next);
            writer.WriteStartArray("items");
            foreach (var row in page) Write(writer, row);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Json(HttpStatusCode.OK, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private HttpResponseMessage Insert(Dictionary<string, Row> tasks, string body)
    {
        using var document = JsonDocument.Parse(body);
        var id = $"task-{++_ids}";
        var row = Apply(new Row(id, string.Empty, string.Empty, "needsAction", null, null, false, Now, id), document.RootElement);
        tasks[id] = row;
        return One(row);
    }

    private HttpResponseMessage Patch(Dictionary<string, Row> tasks, string id, string body)
    {
        if (!tasks.TryGetValue(id, out var row) || row.Deleted)
        {
            return Error(HttpStatusCode.NotFound, "notFound", "No such task.");
        }

        using var document = JsonDocument.Parse(body);
        var updated = Apply(row, document.RootElement);
        tasks[id] = updated;
        return One(updated);
    }

    private HttpResponseMessage Remove(Dictionary<string, Row> tasks, string id)
    {
        if (!tasks.TryGetValue(id, out var row)) return Error(HttpStatusCode.NotFound, "notFound", "No such task.");
        tasks[id] = row with { Deleted = true, Updated = Now };
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// A PATCH changes what it names and leaves the rest, which is the behaviour this application
    /// depends on to keep a task's position and parent.
    /// </summary>
    private Row Apply(Row row, JsonElement patch)
    {
        var title = patch.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : row.Title;
        var notes = patch.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : row.Notes;
        var status = patch.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : row.Status;

        var due = row.Due;
        if (patch.TryGetProperty("due", out var d))
        {
            due = d.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(d.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
                : null;
        }

        var completed = row.Completed;
        if (patch.TryGetProperty("completed", out var c))
        {
            completed = c.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(c.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
                : null;
        }

        if (status != "completed") completed = null;

        return row with { Title = title, Notes = notes, Status = status, Due = due, Completed = completed, Updated = Now };
    }

    private HttpResponseMessage One(Row row)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, row);
        return Json(HttpStatusCode.OK, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void Write(Utf8JsonWriter writer, Row row)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "tasks#task");
        writer.WriteString("id", row.Id);
        writer.WriteString("title", row.Title);
        writer.WriteString("notes", row.Notes);
        writer.WriteString("status", row.Status);
        writer.WriteString("updated", Stamp(row.Updated));
        writer.WriteString("position", row.Position);
        if (row.Due is { } due) writer.WriteString("due", Stamp(due));
        if (row.Completed is { } completed) writer.WriteString("completed", Stamp(completed));
        if (row.Deleted) writer.WriteBoolean("deleted", true);
        writer.WriteEndObject();
    }

    private static Dictionary<string, string> Query(string query)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0) found[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return found;
    }

    private static string Stamp(DateTimeOffset moment)
        => moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string reason, string message)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteNumber("code", (int)status);
            writer.WriteString("message", message);
            writer.WriteStartArray("errors");
            writer.WriteStartObject();
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Json(status, Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
