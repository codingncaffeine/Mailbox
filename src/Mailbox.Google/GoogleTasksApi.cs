using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols.OAuth;

namespace Mailbox.Google;

/// <summary>Google refused a request, and this is what it said.</summary>
public sealed class GoogleApiException(HttpStatusCode status, string reason, string? detail = null)
    : Exception($"Google Tasks answered {(int)status} ({reason}){(detail is { Length: > 0 } ? ": " + detail : ".")}")
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The machine-readable reason — <c>rateLimitExceeded</c>, <c>notFound</c>.</summary>
    public string Reason { get; } = reason;

    /// <summary>
    /// True when the sign-in is the problem rather than the request. The caller then asks the
    /// user to sign in again instead of retrying a request that will keep failing.
    /// </summary>
    public bool NeedsSignIn => Status is HttpStatusCode.Unauthorized;

    /// <summary>True when waiting is the right answer: a quota, or Google having a moment.</summary>
    public bool WorthRetrying =>
        Status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable;

    /// <summary>True when the thing asked about is not there — a list or task removed elsewhere.</summary>
    public bool Gone => Status is HttpStatusCode.NotFound or HttpStatusCode.Gone;
}

/// <summary>
/// The five requests this application makes of the Google Tasks API.
/// </summary>
/// <remarks>
/// Written against the REST interface rather than the official client library, which is a
/// generated surface over the whole of Google's API estate carrying an authentication stack of its
/// own — where what is wanted here is five calls and a bearer token this application already knows
/// how to obtain and renew (§5).
/// <para>
/// Takes an <see cref="HttpMessageHandler"/> so a fake Google is a handler, and an
/// <see cref="IAccessTokenSource"/> rather than a token, because a token is good for about an hour
/// and a sync started with one taken at load time would present a stale one. Asking per request is
/// what makes the renewal happen where it can be reported.
/// </para>
/// </remarks>
public sealed class GoogleTasksApi : IDisposable
{
    /// <summary>Where the API lives. A field so a test can point at the fake's own origin.</summary>
    public static readonly Uri Root = new("https://tasks.googleapis.com/tasks/v1/");

    /// <summary>Google's own ceiling on a page, and what it uses when nobody says.</summary>
    private const int PageSize = 100;

    /// <summary>
    /// How many pages one poll will walk before giving up on it.
    /// </summary>
    /// <remarks>
    /// A guard rather than a limit anybody should reach: 100 pages is 10,000 tasks in one poll,
    /// and a page token that never changes — which is a shape of API bug that has happened — would
    /// otherwise be an endless loop against somebody's quota.
    /// </remarks>
    private const int PageLimit = 100;

    private readonly HttpClient _http;
    private readonly IAccessTokenSource _tokens;

    public GoogleTasksApi(IAccessTokenSource tokens, HttpMessageHandler? handler = null, Uri? root = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };

        _http.BaseAddress = root ?? Root;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Every task list on the account.</summary>
    public async Task<IReadOnlyList<GoogleTaskList>> ListsAsync(CancellationToken cancellationToken = default)
    {
        var lists = new List<GoogleTaskList>();
        string? page = null;

        for (var walked = 0; walked < PageLimit; walked++)
        {
            var url = $"users/@me/lists?maxResults={PageSize}"
                      + (page is { Length: > 0 } ? $"&pageToken={Uri.EscapeDataString(page)}" : string.Empty);

            using var document = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var value) ? value.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;

                    lists.Add(new GoogleTaskList(
                        id,
                        item.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                        item.TryGetProperty("updated", out var updated)
                        && DateTimeOffset.TryParse(updated.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal, out var moment)
                            ? moment
                            : null));
                }
            }

            page = NextPage(root);
            if (page is null) break;
        }

        return lists;
    }

    /// <summary>
    /// The tasks in a list, following the pages.
    /// </summary>
    /// <param name="since">
    /// Only what changed after this, which is the whole of the incremental sync — there is no
    /// sync token here and no ETag worth a precondition. Null reads the list.
    /// </param>
    /// <remarks>
    /// Completed, hidden and deleted are all asked for on purpose. Without <c>showCompleted</c> a
    /// task that was ticked elsewhere simply vanishes from the answer and would be taken for one
    /// nobody had touched; without <c>showDeleted</c> a removal has no tombstone and the task
    /// would live here forever; and <c>showHidden</c> is what keeps a completed task visible after
    /// somebody presses Google's own "clear completed", which hides rather than deletes.
    /// </remarks>
    public async Task<IReadOnlyList<GoogleTask>> TasksAsync(
        string listId, DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var tasks = new List<GoogleTask>();
        string? page = null;

        for (var walked = 0; walked < PageLimit; walked++)
        {
            var url = $"lists/{Uri.EscapeDataString(listId)}/tasks"
                      + $"?maxResults={PageSize}&showCompleted=true&showHidden=true&showDeleted=true"
                      + (since is { } moment
                          ? $"&updatedMin={Uri.EscapeDataString(moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))}"
                          : string.Empty)
                      + (page is { Length: > 0 } ? $"&pageToken={Uri.EscapeDataString(page)}" : string.Empty);

            using var document = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var task = GoogleTask.Read(item);
                    if (task.Id.Length > 0) tasks.Add(task);
                }
            }

            page = NextPage(root);
            if (page is null) break;

            if (walked == PageLimit - 1)
            {
                Log.Warn($"Stopped reading “{listId}” after {PageLimit} pages; the rest waits for the next poll.");
            }
        }

        return tasks;
    }

    /// <summary>Puts a new task on a list, and hands back what Google made of it.</summary>
    public async Task<GoogleTask> InsertAsync(string listId, GoogleTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return await SendAsync(
            HttpMethod.Post, $"lists/{Uri.EscapeDataString(listId)}/tasks", task.ToJson(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Changes a task that is already there.
    /// </summary>
    /// <remarks>
    /// PATCH rather than PUT, and this is the one place it matters: a PUT replaces the resource,
    /// so the fields this application does not send — the position in the list, a sub-task's
    /// parent — would be cleared by every save, reordering a list from a client that never showed
    /// the order.
    /// </remarks>
    public async Task<GoogleTask> PatchAsync(string listId, GoogleTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return await SendAsync(
            HttpMethod.Patch,
            $"lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(task.Id)}",
            task.ToJson(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a task. A task already gone is not an error worth raising.</summary>
    public async Task DeleteAsync(string listId, string taskId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}");

        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Somebody else removing it first is the outcome this asked for.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return;

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken, body).ConfigureAwait(false);
        }

        return JsonDocument.Parse(body);
    }

    private async Task<GoogleTask> SendAsync(
        HttpMethod method, string url, string json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken, body).ConfigureAwait(false);
        }

        using var document = JsonDocument.Parse(body);
        return GoogleTask.Read(document.RootElement);
    }

    /// <summary>
    /// The bearer, fetched per request.
    /// </summary>
    /// <remarks>
    /// On the request rather than on the client's default headers: the default would be captured
    /// once and go stale, and a header set from several requests at once on a shared client is a
    /// race that shows up as one poll signing in as the moment before.
    /// </remarks>
    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokens.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<GoogleApiException> FailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken, string? body = null)
    {
        body ??= await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Google's error shape is {"error":{"code":…,"message":…,"errors":[{"reason":…}]}}. A
        // gateway in front of it answers HTML, which is not a protocol error worth a stack trace
        // but is worth not pretending to have parsed.
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var text) ? text.GetString() : null;
                var reason = error.TryGetProperty("errors", out var errors)
                             && errors.ValueKind == JsonValueKind.Array
                             && errors.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first
                             && first.TryGetProperty("reason", out var named)
                    ? named.GetString() ?? string.Empty
                    : string.Empty;

                return new GoogleApiException(response.StatusCode, reason, message);
            }
        }
        catch (JsonException)
        {
            // Fall through to the status alone, which is all that is really known.
        }

        return new GoogleApiException(response.StatusCode, response.ReasonPhrase ?? string.Empty);
    }

    private static string? NextPage(JsonElement root)
        => root.TryGetProperty("nextPageToken", out var token)
           && token.ValueKind == JsonValueKind.String
           && token.GetString() is { Length: > 0 } value
            ? value
            : null;

    public void Dispose() => _http.Dispose();
}
