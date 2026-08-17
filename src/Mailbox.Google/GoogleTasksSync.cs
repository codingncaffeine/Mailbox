using System.Globalization;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Google;

/// <summary>What one poll of one Google task list came to.</summary>
public sealed record GoogleSyncResult(int Pulled, int Removed, int Pushed, IReadOnlyList<GoogleConflict> Conflicts)
{
    public static readonly GoogleSyncResult Nothing = new(0, 0, 0, []);
}

/// <summary>
/// A task that changed here and there between two polls.
/// </summary>
/// <remarks>
/// Reported rather than resolved, as the DAV engine's are: which copy is wanted is the reader's
/// answer, not this code's.
/// </remarks>
/// <param name="TheirStamp">
/// Google's own stamp on the copy being argued about. Carried because settling the argument in
/// this machine's favour has to record it: the next poll asks "is the server where I last saw
/// it?", and answering that with a moment from this machine's clock would raise the same conflict
/// again, forever.
/// </param>
public sealed record GoogleConflict(
    long ItemId, long CollectionId, string TaskId, string Summary, string TheirTitle, string? TheirStamp);

/// <summary>
/// Keeping a Google task list and a local one in step, without a precondition to lean on.
/// </summary>
/// <remarks>
/// The shape differs from the DAV engine's in one structural way, and it is worth understanding
/// before changing anything here. CalDAV has <c>If-Match</c>: the server refuses a write that
/// would land on something that moved, so the DAV engine can push first and let a 412 be the
/// detector. Google Tasks has no such precondition — a PATCH lands whatever happened in between,
/// last write wins, silently.
/// <para>
/// So this <b>pulls first and pushes second</b>, and the detector is arithmetic rather than a
/// status code: a task that came back changed and is also sitting in the outgoing queue changed in
/// both places, and that is a conflict. It costs no extra request, and without it the only
/// available behaviour is to overwrite whatever the phone did and never mention it.
/// </para>
/// <para>
/// Incremental sync is <c>updatedMin</c> and nothing else — there is no sync token and no CTag —
/// so the moment to ask from next time is kept in the collection's <c>sync_token</c> column. It is
/// taken from <b>the newest stamp the server itself put on anything</b>, never from this machine's
/// clock: the two clocks are not the same clock, and a marker written from this one runs ahead of
/// the server's and steps over every change made in the gap, permanently. A minute is taken off it
/// as well, so a few tasks are re-read each poll rather than one being missed — they compare equal
/// and cost nothing.
/// </para>
/// </remarks>
public sealed class GoogleTasksSync(GoogleTasksApi api, PimRepository repository)
{
    private readonly GoogleTasksApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>
    /// How far behind the newest server stamp the next poll asks from.
    /// </summary>
    /// <remarks>
    /// Insurance against the server's own stamps not being perfectly ordered across whatever is
    /// behind the API. The cost is re-reading a few tasks that have not changed, which is a
    /// comparison; the cost of being wrong the other way is a change nobody ever sees again.
    /// </remarks>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(1);

    /// <summary>Pull, then push, over one list.</summary>
    public async Task<GoogleSyncResult> SyncAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (GoogleTasks.ListId(collection) is not { Length: > 0 } listId) return GoogleSyncResult.Nothing;

        var (pulled, removed, conflicts, pulledAt) = await PullAsync(collection, listId, cancellationToken).ConfigureAwait(false);
        var (pushed, pushedAt) = await PushAsync(collection, listId, conflicts, cancellationToken).ConfigureAwait(false);

        // The server's clock, not this one. Written only once both halves have worked: moving the
        // marker before the push would make a failed push invisible to the next poll.
        var newest = Later(pulledAt, pushedAt);
        if (newest is { } moment)
        {
            _repository.SetCollectionSync(collection.Id, null, Stamp(moment - Overlap));
        }

        return new GoogleSyncResult(pulled, removed, pushed, conflicts);
    }

    private async Task<(int Pulled, int Removed, List<GoogleConflict> Conflicts, DateTimeOffset? Newest)> PullAsync(
        Collection collection, string listId, CancellationToken cancellationToken)
    {
        var tasks = await _api.TasksAsync(listId, Since(collection), cancellationToken).ConfigureAwait(false);
        return Apply(_repository, collection, tasks, cancellationToken);
    }

    /// <summary>
    /// What a pull does once the tasks are in hand.
    /// </summary>
    /// <remarks>
    /// Separated from the request so it can be given an answer that came from somewhere else —
    /// which is how the fidelity harness exercises it, a poll being HTTP and a capture run having
    /// no business on the network. Static for the same reason: an answer that arrived by other
    /// means has no API behind it to construct.
    /// </remarks>
    public static (int Pulled, int Removed, List<GoogleConflict> Conflicts, DateTimeOffset? Newest) Apply(
        PimRepository repository, Collection collection, IReadOnlyList<GoogleTask> tasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(tasks);

        var newest = tasks.Count == 0 ? null : tasks.Max(t => t.Updated);

        // What is waiting to go out. A task in here that also came back changed is the collision
        // this engine has to find for itself.
        var queued = repository.Queued(collection.Id)
            .Where(q => q.ItemId is not null)
            .ToDictionary(q => q.ItemId!.Value, q => q.Op, EqualityComparer<long>.Default);

        var pulled = 0;
        var removed = 0;
        var conflicts = new List<GoogleConflict>();

        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = repository.ItemByHref(collection.Id, task.Id);

            if (task.Deleted)
            {
                // A tombstone for a row this machine had. One for a row it never had is the
                // ordinary case on a first poll and is nothing at all.
                if (row is null) continue;

                // A local edit to a task somebody else deleted loses the argument: the task is
                // gone at the source and there is nothing left to write it back to. Said out loud
                // rather than silently, because it is the one case where local work disappears.
                if (queued.ContainsKey(row.Id))
                {
                    Log.Warn($"“{row.Summary}” was deleted in the Google list; the change made here could not be sent.");
                }

                repository.DeleteItem(row.Id);
                removed++;
                continue;
            }

            if (row is null)
            {
                var made = GoogleTaskCodec.Merge(null, task);
                repository.AddItem(PimTodoCodec.ToItem(made, collection.Id, null, PimSyncState.Synced) with
                {
                    RawPayload = TodoCodec.Serialize(made),
                    DavHref = task.Id,
                    Etag = Stamp(task.Updated),
                });
                pulled++;
                continue;
            }

            var mine = PimTodoCodec.FromItem(row);

            if (queued.TryGetValue(row.Id, out var op))
            {
                // Being in the answer is not the same as having moved. The poll asks from a
                // little before the last stamp on purpose, and Google re-stamps tasks of its own
                // accord, so the question is whether the server's copy is where this machine last
                // saw it — which is what the stored stamp says.
                if (string.Equals(row.Etag, Stamp(task.Updated), StringComparison.Ordinal))
                {
                    // It is. Only this end moved, so the queued change simply goes out below.
                    continue;
                }

                // Both ends moved. Nothing is written either way and the reader is asked.
                if (GoogleTaskCodec.Differs(mine, task))
                {
                    conflicts.Add(new GoogleConflict(
                        row.Id, collection.Id, task.Id, row.Summary, task.Title, Stamp(task.Updated)));
                    continue;
                }

                // Moved to the same place on both sides — usually this machine's own write coming
                // back. Nothing to do but stop trying to send it again.
                if (op == "put")
                {
                    repository.SetSyncState(row.Id, PimSyncState.Synced, Stamp(task.Updated), task.Id);
                    foreach (var entry in repository.Queued(collection.Id).Where(q => q.ItemId == row.Id))
                    {
                        repository.Dequeue(entry.Id);
                    }
                }

                continue;
            }

            if (!GoogleTaskCodec.Differs(mine, task))
            {
                // The stamp moved but nothing this application can see did — Google reorders and
                // re-stamps tasks on its own. Record the stamp so the next poll is quieter.
                repository.SetSyncState(row.Id, PimSyncState.Synced, Stamp(task.Updated), task.Id);
                continue;
            }

            // The merge is the whole point: everything Google does not know about this task —
            // its priority, its categories, its recurrence — is on the row already and stays.
            var merged = GoogleTaskCodec.Merge(mine, task);
            repository.UpdateItem(PimTodoCodec.ToItem(merged, collection.Id, row, PimSyncState.Synced) with
            {
                RawPayload = TodoCodec.Serialize(merged),
                DavHref = task.Id,
                Etag = Stamp(task.Updated),
            });
            pulled++;
        }

        return (pulled, removed, conflicts, newest);
    }

    private async Task<(int Pushed, DateTimeOffset? Newest)> PushAsync(
        Collection collection, string listId, List<GoogleConflict> conflicts, CancellationToken cancellationToken)
    {
        var held = conflicts.Select(c => c.ItemId).ToHashSet();
        var pushed = 0;
        DateTimeOffset? newest = null;

        foreach (var change in _repository.Queued(collection.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An item the pull just found a conflict on waits for the reader to settle it.
            if (change.ItemId is { } waiting && held.Contains(waiting)) continue;

            try
            {
                if (change.Op == "delete")
                {
                    if (change.Href is { Length: > 0 } id)
                    {
                        await _api.DeleteAsync(listId, id, cancellationToken).ConfigureAwait(false);
                    }

                    if (change.ItemId is { } gone) _repository.DeleteItem(gone);
                    _repository.Dequeue(change.Id);
                    pushed++;
                    continue;
                }

                if (change.ItemId is not { } id2 || _repository.Item(id2) is not { } row)
                {
                    // The row went away under the queue entry. Nothing to send.
                    _repository.Dequeue(change.Id);
                    continue;
                }

                var task = GoogleTaskCodec.ToGoogle(PimTodoCodec.FromItem(row), row.DavHref ?? string.Empty);

                var answer = row.DavHref is { Length: > 0 }
                    ? await _api.PatchAsync(listId, task, cancellationToken).ConfigureAwait(false)
                    : await _api.InsertAsync(listId, task, cancellationToken).ConfigureAwait(false);

                _repository.SetSyncState(row.Id, PimSyncState.Synced, Stamp(answer.Updated), answer.Id);
                _repository.Dequeue(change.Id);
                newest = Later(newest, answer.Updated);
                pushed++;
            }
            catch (GoogleApiException ex) when (ex.Gone && change.Op == "put")
            {
                // The task was removed at Google while this edit waited. Sending it again as a
                // new one would resurrect something somebody deleted, so the row goes instead and
                // the reader is told rather than left with a task that quietly stops syncing.
                Log.Warn($"“{change.Href}” is no longer in the Google list; the change made here was dropped.");
                if (change.ItemId is { } orphan) _repository.DeleteItem(orphan);
                _repository.Dequeue(change.Id);
            }
            catch (GoogleApiException ex) when (ex.WorthRetrying)
            {
                // A quota or a bad five minutes. Left queued, with the reason on it, so the next
                // poll tries again rather than the whole list stopping.
                _repository.QueueFailed(change.Id, ex.Message);
                Log.Warn($"Google Tasks would not take a change yet: {ex.Message}");
                break;
            }
            catch (GoogleApiException ex)
            {
                _repository.QueueFailed(change.Id, ex.Message);
                Log.Warn($"A change could not be sent to Google Tasks: {ex.Message}");
            }
        }

        return (pushed, newest);
    }

    /// <summary>The later of two moments, either of which may not be there.</summary>
    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>
    /// Settles a conflict by keeping what is here: the queued change goes out on the next poll.
    /// </summary>
    /// <remarks>
    /// The row takes the server's stamp for the copy it is overruling — which is what makes the
    /// next poll see a server that has not moved since, and push instead of arguing again.
    /// </remarks>
    public void KeepLocal(GoogleConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        _repository.SetSyncState(conflict.ItemId, PimSyncState.Modified, conflict.TheirStamp, conflict.TaskId);
    }

    /// <summary>Settles a conflict by taking Google's copy: the queued change is dropped.</summary>
    public void KeepServer(GoogleConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        foreach (var entry in _repository.Queued(conflict.CollectionId).Where(q => q.ItemId == conflict.ItemId))
        {
            _repository.Dequeue(entry.Id);
        }

        // Marked Synced and left with no stamp, so the next poll sees the server's version as
        // new to this machine and merges it in the ordinary way.
        _repository.SetSyncState(conflict.ItemId, PimSyncState.Synced, null, conflict.TaskId);
    }

    /// <summary>What the last poll asked from, or null when this list has never been polled.</summary>
    private static DateTimeOffset? Since(Collection collection)
        => collection.SyncToken is { Length: > 0 } text
           && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var moment)
            ? moment
            : null;

    private static string? Stamp(DateTimeOffset? moment)
        => moment?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
