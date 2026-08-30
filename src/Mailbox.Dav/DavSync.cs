using System.Net;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Dav;

/// <summary>What a sync of one collection came to.</summary>
/// <param name="Pulled">Items written into the store from the server.</param>
/// <param name="Removed">Items the server no longer has, taken out here.</param>
/// <param name="Pushed">Local changes the server accepted.</param>
/// <param name="Conflicts">Local changes the server refused because its own copy had moved.</param>
public sealed record DavSyncResult(int Pulled, int Removed, int Pushed, IReadOnlyList<DavConflict> Conflicts)
{
    public static readonly DavSyncResult Nothing = new(0, 0, 0, []);
}

/// <summary>
/// A local change the server would not take: its copy changed underneath us.
/// </summary>
/// <remarks>
/// Reported rather than resolved. RFC 4791's ETag precondition exists so that a client can tell
/// the difference between "my change went" and "someone else's did", and a sync that quietly
/// picks a winner is how a calendar loses an appointment nobody noticed writing.
/// <para>
/// Both copies travel with the report — the server's payload and the tag it is filed under — so
/// the reader can be shown the two and asked, and so whichever answer comes back can be carried
/// out without a second round trip. <see cref="DavSync.KeepLocal"/> and
/// <see cref="DavSync.KeepServer"/> are the two answers.
/// </para>
/// </remarks>
/// <param name="ServerEtag">What the server's copy is filed under, which is the precondition a
/// "keep mine" has to carry or it would be refused all over again.</param>
/// <param name="LocalDelete">True when the change the server refused was a deletion.</param>
public sealed record DavConflict(
    long ItemId,
    long CollectionId,
    string Href,
    string Summary,
    string? ServerPayload,
    string? ServerEtag,
    bool LocalDelete = false);

/// <summary>
/// The sync engine: pull what the server has, push what this machine has, and never let either
/// overwrite the other by accident.
/// </summary>
/// <remarks>
/// Push before pull, for the same reason the mail journal plays before its fetch: the
/// server's answer then already reflects what was done here, and the two never argue over one
/// item. What is pushed comes off <c>dav_queue</c>, which is what makes an offline change a
/// longer queue rather than a lost edit.
/// <para>
/// Incremental where the server can: <c>sync-collection</c> with a token, falling back to a CTag
/// check and an ETag diff where it cannot. Refetching a whole collection every poll is not
/// acceptable at real sizes, and several servers still do not implement RFC 6578.
/// </para>
/// </remarks>
public sealed class DavSync(DavClient client, PimRepository repository, IDavPayload payload)
{
    private readonly DavClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IDavPayload _payload = payload ?? throw new ArgumentNullException(nameof(payload));

    /// <summary>A sync for a calendar, which is what most collections are.</summary>
    public DavSync(DavClient client, PimRepository repository)
        : this(client, repository, DavPayloads.Calendar)
    {
    }

    /// <summary>The sync a collection wants, by what it holds.</summary>
    public static DavSync For(DavClient client, PimRepository repository, Collection collection)
        => new(client, repository, DavPayloads.For(collection?.Kind ?? CollectionKind.Events));

    /// <summary>Pushes this collection's queue, then pulls what changed on the server.</summary>
    public async Task<DavSyncResult> SyncAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.DavUrl is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var root))
        {
            return DavSyncResult.Nothing;
        }

        // A subscription is one document at an address, not a collection: there is no queue to
        // push, no CTag to file, and neither pull path below can see it.
        if (IsSubscription(collection))
        {
            var (got, gone) = await FetchDocumentAsync(collection, root, cancellationToken).ConfigureAwait(false);
            return new DavSyncResult(got, gone, 0, []);
        }

        var (pushed, conflicts) = await PushAsync(collection, root, cancellationToken).ConfigureAwait(false);
        var (pulled, removed) = await PullAsync(collection, root, cancellationToken).ConfigureAwait(false);
        await RememberCtagAsync(collection, root, moved: pushed + pulled + removed > 0, cancellationToken).ConfigureAwait(false);
        return new DavSyncResult(pulled, removed, pushed, conflicts);
    }

    /// <summary>
    /// True for an internet calendar subscription: a read-only collection of this machine's own
    /// with an address.
    /// </summary>
    /// <remarks>
    /// A shared CalDAV calendar is read-only too, and the account is what tells the two apart —
    /// a subscription belongs to nobody's account because nobody signed in to get it. One is a
    /// document to fetch; the other is a collection to sync.
    /// </remarks>
    internal static bool IsSubscription(Collection collection)
        => collection is { IsReadOnly: true, DavUrl: { Length: > 0 } } && collection.IsLocal;

    /// <summary>
    /// Fills a subscription: fetch the document at its address and make the collection say what
    /// the document says.
    /// </summary>
    /// <remarks>
    /// An internet calendar is not CalDAV, and this is why it needs a path of its own. What is at
    /// the end of a <c>webcal:</c> address is a static <c>.ics</c> file on a web server, and
    /// neither pull path can read one: <c>sync-collection</c> is a REPORT the server will not
    /// answer, and the ETag diff is a PROPFIND of a collection that is not one. Both come back
    /// with nothing, which is what a subscription used to fill with.
    /// <para>
    /// The document is the whole truth of that calendar, so this replaces rather than merges: an
    /// event the publisher dropped is dropped here. That is safe for a subscription and for
    /// nothing else — it is read-only, so there is no local edit for a replace to lose, which is
    /// the very thing <see cref="StoreCalendar"/> goes to such lengths to protect.
    /// </para>
    /// <para>
    /// Fetched whole every poll. A conditional GET would save the body on an unchanged calendar
    /// and wants an <c>If-None-Match</c> the client does not send yet; a published calendar is
    /// small and the interval is minutes, so the cost is worth less than the surface.
    /// </para>
    /// </remarks>
    public async Task<(int Pulled, int Removed)> FetchDocumentAsync(
        Collection collection, Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        // What was read last time, so an unchanged calendar costs a header rather than a body.
        // Some published calendars are megabytes and are fetched on every send/receive; a
        // server that still has this version answers 304 and sends nothing.
        var response = await _client
            .GetAsync(url, collection.Ctag, cancellationToken)
            .ConfigureAwait(false);

        if (response.NotModified) return (0, 0);
        if (!response.Ok) return (0, 0);

        IReadOnlyList<CalendarEvent> events;
        try
        {
            events = ICalendarCodec.Parse(response.Body);
        }
        catch (FormatException)
        {
            // A publisher serving an error page rather than a calendar empties nothing: the
            // collection keeps what it last had, which is better than a calendar that vanishes
            // whenever the far end has a bad day.
            return (0, 0);
        }

        if (events.Count == 0) return (0, 0);

        var href = url.ToString();
        var pulled = 0;
        var kept = new HashSet<string>(StringComparer.Ordinal);

        // By UID, because a published document holds many unrelated events where a CalDAV
        // resource holds one series — which is the assumption StoreCalendar is built on and the
        // reason it cannot be used here.
        foreach (var series in events.GroupBy(e => e.Uid, StringComparer.Ordinal))
        {
            kept.Add(series.Key);
            var existing = _repository.ItemsByUid(collection.Id, series.Key);

            foreach (var calendarEvent in series)
            {
                var match = existing.FirstOrDefault(i =>
                    i.IsOverride == calendarEvent.IsOverride
                    && (!calendarEvent.IsOverride
                        || string.Equals(i.RecurrenceId, ICalendarCodec.RecurrenceIdText(calendarEvent.RecurrenceId!), StringComparison.Ordinal)));

                var row = PimEventCodec.ToItem(calendarEvent, collection.Id, match, PimSyncState.Synced) with
                {
                    DavHref = href,
                    Etag = response.Etag,
                    RawPayload = ICalendarCodec.Serialize(calendarEvent),
                };

                if (match is null) _repository.AddItem(row);
                else _repository.UpdateItem(row);
                pulled++;
            }
        }

        var removed = 0;
        foreach (var item in _repository.Items(collection.Id))
        {
            if (kept.Contains(item.Uid)) continue;
            _repository.DeleteItem(item.Id);
            removed++;
        }

        // Kept against the collection, which is where a CalDAV collection's own version tag
        // lives: for a document the two mean the same thing — the version this store holds.
        if (response.Etag is { Length: > 0 })
        {
            _repository.SetCollectionSync(collection.Id, response.Etag, collection.SyncToken);
        }

        return (pulled, removed);
    }

    // ---- Push --------------------------------------------------------------------------------

    /// <summary>Drains the offline queue for one collection.</summary>
    public async Task<(int Pushed, IReadOnlyList<DavConflict> Conflicts)> PushAsync(
        Collection collection,
        Uri root,
        CancellationToken cancellationToken = default)
    {
        var conflicts = new List<DavConflict>();
        var pushed = 0;

        foreach (var change in _repository.Queued(collection.Id))
        {
            if (collection.IsReadOnly)
            {
                _repository.QueueFailed(change.Id, "The calendar is read-only.");
                continue;
            }

            if (change.Op == "delete")
            {
                if (change.Href is not { Length: > 0 } gone)
                {
                    // Nothing on the server to remove — it never got there.
                    _repository.Dequeue(change.Id);
                    continue;
                }

                var url = DavDiscovery.Absolute(root, gone)!;
                var response = await _client.DeleteAsync(url, change.Etag, cancellationToken).ConfigureAwait(false);
                if (response.Ok || response.Status == HttpStatusCode.NotFound)
                {
                    if (change.ItemId is { } id) _repository.DeleteItem(id);
                    _repository.Dequeue(change.Id);
                    pushed++;
                }
                else if (response.Conflict)
                {
                    // The server's copy is fetched here too, so a refused delete is a choice
                    // between two things the reader can see rather than a bare complaint.
                    var theirs = await _client.GetAsync(url, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var local = change.ItemId is { } deleting ? _repository.Item(deleting) : null;
                    conflicts.Add(new DavConflict(
                        change.ItemId ?? 0, collection.Id, gone, local?.Summary ?? string.Empty,
                        theirs.Ok ? theirs.Body : null, theirs.Etag, LocalDelete: true));
                    _repository.QueueFailed(change.Id, "The calendar changed on the server.");
                }
                else
                {
                    _repository.QueueFailed(change.Id, $"The server answered {(int)response.Status}.");
                }

                continue;
            }

            if (change.ItemId is not { } itemId || _repository.Item(itemId) is not { } item)
            {
                _repository.Dequeue(change.Id);
                continue;
            }

            var href = item.DavHref is { Length: > 0 } existing ? existing : NewHref(item.Uid);
            var target = DavDiscovery.Absolute(root, href)!;
            var payload = _payload.Whole(_repository, item);

            var write = await _client
                .PutAsync(target, payload, item.Etag, ifNoneMatch: item.Etag is null, contentType: _payload.ContentType, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (write.Ok)
            {
                // A server that answered without an ETag has to be asked what it stored, or the
                // next update would send no precondition at all.
                var etag = write.Etag;
                if (etag is null)
                {
                    var reread = await _client.GetAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
                    etag = reread.Etag;
                }

                _repository.SetSyncState(item.Id, PimSyncState.Synced, etag, href);
                _repository.Dequeue(change.Id);
                pushed++;
            }
            else if (write.Conflict)
            {
                var server = await _client.GetAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
                conflicts.Add(new DavConflict(
                    item.Id, collection.Id, href, item.Summary,
                    server.Ok ? server.Body : null, server.Etag));
                _repository.QueueFailed(change.Id, "This appointment changed on the server as well.");
            }
            else
            {
                _repository.QueueFailed(change.Id, $"The server answered {(int)write.Status}.");
            }
        }

        return (pushed, conflicts);
    }

    // ---- Pull --------------------------------------------------------------------------------

    /// <summary>Brings the store up to what the server has, incrementally where it can.</summary>
    public async Task<(int Pulled, int Removed)> PullAsync(Collection collection, Uri root, CancellationToken cancellationToken = default)
    {
        // sync-collection first: one request that says what changed, which is the whole point of
        // RFC 6578. A server that will not answer it falls through to the CTag and ETag path.
        if (await SyncCollectionAsync(collection, root, cancellationToken).ConfigureAwait(false) is { } incremental)
        {
            return incremental;
        }

        return await EtagDiffAsync(collection, root, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int Pulled, int Removed)?> SyncCollectionAsync(Collection collection, Uri root, CancellationToken cancellationToken)
    {
        var response = await _client
            .ReportAsync(root, DavXml.SyncCollection(collection.SyncToken), depth: 1, cancellationToken)
            .ConfigureAwait(false);

        if (response.Status != HttpStatusCode.MultiStatus) return null;

        var multi = response.MultiStatus;
        if (multi.SyncToken is null && multi.Resources.Count == 0) return null;

        var changed = new List<string>();
        foreach (var resource in multi.Found)
        {
            if (resource.Href.EndsWith('/')) continue;
            var known = _repository.ItemByHref(collection.Id, resource.Href);
            if (known is null || !string.Equals(known.Etag, resource.Etag, StringComparison.Ordinal)) changed.Add(resource.Href);
        }

        var removed = 0;
        foreach (var gone in multi.Removed)
        {
            if (_repository.ItemByHref(collection.Id, gone) is { } item)
            {
                _repository.DeleteItem(item.Id);
                removed++;
            }
        }

        var pulled = await FetchAsync(collection, root, changed, cancellationToken).ConfigureAwait(false);
        _repository.SetCollectionSync(collection.Id, collection.Ctag, multi.SyncToken ?? collection.SyncToken);
        return (pulled, removed);
    }

    private async Task<(int Pulled, int Removed)> EtagDiffAsync(Collection collection, Uri root, CancellationToken cancellationToken)
    {
        var listing = await _client.PropFindAsync(root, DavXml.ItemEtags(), depth: 1, cancellationToken).ConfigureAwait(false);
        if (listing.Status != HttpStatusCode.MultiStatus) return (0, 0);

        var known = _repository.HrefsIn(collection.Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<string>();

        foreach (var resource in listing.MultiStatus.Found)
        {
            if (resource.IsCollection || resource.Etag is null) continue;
            seen.Add(resource.Href);
            if (!known.TryGetValue(resource.Href, out var stored) || !string.Equals(stored.Etag, resource.Etag, StringComparison.Ordinal))
            {
                changed.Add(resource.Href);
            }
        }

        var removed = 0;
        foreach (var (href, stored) in known)
        {
            if (seen.Contains(href)) continue;
            _repository.DeleteItem(stored.Id);
            removed++;
        }

        var pulled = await FetchAsync(collection, root, changed, cancellationToken).ConfigureAwait(false);
        return (pulled, removed);
    }

    /// <summary>
    /// Files the collection's CTag, which is what makes the next poll one request instead of all
    /// of these.
    /// </summary>
    /// <remarks>
    /// Read again only when something moved — including this machine's own push, which moves it
    /// as surely as anyone else's — and on the first sync of a collection that has never had one.
    /// A server that answers <c>sync-collection</c> has a CTag as well, and not filing it left
    /// the cheap check unable to fire on exactly the servers that poll most often: every idle
    /// poll of Radicale was a PROPFIND, a REPORT and a decision, where one PROPFIND would do.
    /// </remarks>
    private async Task RememberCtagAsync(Collection collection, Uri root, bool moved, CancellationToken cancellationToken)
    {
        var current = _repository.Collection(collection.Id) ?? collection;
        if (!moved && current.Ctag is { Length: > 0 }) return;

        if (await ReadCtagAsync(root, cancellationToken).ConfigureAwait(false) is { Length: > 0 } ctag)
        {
            _repository.SetCollectionSync(collection.Id, ctag, current.SyncToken);
        }
    }

    /// <summary>
    /// True when nothing in this collection has changed since the last sync — one request, which
    /// is what makes polling a dozen calendars affordable.
    /// </summary>
    public async Task<bool> IsUnchangedAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        // A subscription is a published file, not a DAV collection: a PROPFIND at it is a
        // guaranteed 405 in the publisher's log before the document fetch does the real work.
        // Its own cheap check is the fetch path's conditional request, so the answer here is
        // simply "go and fetch".
        if (IsSubscription(collection)) return false;

        if (collection.Ctag is not { Length: > 0 } ctag) return false;
        if (collection.DavUrl is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var root)) return false;
        var current = await ReadCtagAsync(root, cancellationToken).ConfigureAwait(false);
        return current is { Length: > 0 } && string.Equals(current, ctag, StringComparison.Ordinal);
    }

    private async Task<string?> ReadCtagAsync(Uri root, CancellationToken cancellationToken)
    {
        var response = await _client.PropFindAsync(root, DavXml.CollectionProperties(), depth: 0, cancellationToken).ConfigureAwait(false);
        return response.MultiStatus.Found.Select(r => r.Ctag).FirstOrDefault(c => c is { Length: > 0 });
    }

    /// <summary>Fetches the payloads of the hrefs given and writes them into the store.</summary>
    private async Task<int> FetchAsync(Collection collection, Uri root, IReadOnlyList<string> hrefs, CancellationToken cancellationToken)
    {
        if (hrefs.Count == 0) return 0;

        var written = 0;

        // In batches: a multiget of ten thousand hrefs is a request some servers refuse and all
        // of them are slow to answer.
        const int Batch = 100;
        for (var start = 0; start < hrefs.Count; start += Batch)
        {
            var slice = hrefs.Skip(start).Take(Batch).ToList();
            var response = await _client
                .ReportAsync(root, _payload.Multiget(slice), depth: 1, cancellationToken)
                .ConfigureAwait(false);

            if (response.Status != HttpStatusCode.MultiStatus) continue;

            foreach (var resource in response.MultiStatus.Found)
            {
                if (resource.Data is not { Length: > 0 } payload) continue;
                written += _payload.Store(_repository, collection.Id, resource.Href, resource.Etag, payload, overLocalChanges: false);
            }
        }

        return written;
    }

    internal int Store(Collection collection, string href, string? etag, string payload)
        => _payload.Store(_repository, collection.Id, href, etag, payload, overLocalChanges: false);

    /// <summary>
    /// Writes one server payload into the store: a VCALENDAR may hold a series' master and its
    /// overrides together, and each becomes its own row under the same UID.
    /// </summary>
    /// <remarks>
    /// Static over the repository because settling a conflict writes the server's copy through
    /// exactly this path, and that happens long after the sync that found the conflict has gone.
    /// </remarks>
    /// <param name="overLocalChanges">
    /// False for a pull, which must leave a row whose own change has not gone up yet alone; true
    /// only when the reader has chosen the server's copy over it.
    /// </param>
    internal static int StoreCalendar(PimRepository repository, long collectionId, string href, string? etag, string payload, bool overLocalChanges = false)
    {
        IReadOnlyList<CalendarEvent> events;
        try
        {
            events = ICalendarCodec.Parse(payload);
        }
        catch (FormatException)
        {
            return 0;
        }

        if (events.Count == 0) return 0;

        var existing = repository.ItemsByUid(collectionId, events[0].Uid);
        var written = 0;

        foreach (var calendarEvent in events)
        {
            var match = existing.FirstOrDefault(i =>
                i.IsOverride == calendarEvent.IsOverride
                && (!calendarEvent.IsOverride
                    || string.Equals(i.RecurrenceId, ICalendarCodec.RecurrenceIdText(calendarEvent.RecurrenceId!), StringComparison.Ordinal)));

            // A row whose own change has not reached the server is the thing a conflict is about.
            // Writing the server's copy over it here would settle that conflict by losing the
            // edit — silently, in the same run that reported it — so the pull leaves it be and
            // the reader is asked instead.
            if (!overLocalChanges && match is { SyncState: not PimSyncState.Synced }) continue;

            var row = PimEventCodec.ToItem(calendarEvent, collectionId, match, PimSyncState.Synced) with
            {
                DavHref = href,
                Etag = etag,
                // The server's copy of the whole resource, verbatim: it is what a later PUT has
                // to send back, and re-serializing it would drop properties the server cares
                // about.
                RawPayload = events.Count == 1 ? payload : ICalendarCodec.Serialize(calendarEvent),
            };

            if (match is null) repository.AddItem(row);
            else repository.UpdateItem(row);
            written++;
        }

        // An override the server has dropped goes with it — unless it is one made here that the
        // server has not been told about yet, which is not dropped but unsent.
        foreach (var orphan in existing.Where(i => i.IsOverride
                     && (overLocalChanges || i.SyncState == PimSyncState.Synced)
                     && !events.Any(e =>
                     e.IsOverride && string.Equals(i.RecurrenceId, ICalendarCodec.RecurrenceIdText(e.RecurrenceId!), StringComparison.Ordinal))))
        {
            repository.DeleteItem(orphan.Id);
        }

        return written;
    }

    /// <summary>
    /// The same for a task list, over VTODOs.
    /// </summary>
    /// <remarks>
    /// A near-copy of <see cref="StoreCalendar"/> on purpose: what the two share is the shape of
    /// the decision — match by UID and RECURRENCE-ID, leave an unsent local change alone, keep the
    /// server's text verbatim when the resource holds one component — and what they do not share
    /// is every type in it. Folding them together would mean a generic over two codecs and two
    /// records to save fifteen lines that will not change again.
    /// </remarks>
    internal static int StoreTodos(PimRepository repository, long collectionId, string href, string? etag, string payload, bool overLocalChanges = false)
    {
        IReadOnlyList<TaskItem> tasks;
        try
        {
            tasks = TodoCodec.Parse(payload);
        }
        catch (FormatException)
        {
            return 0;
        }

        if (tasks.Count == 0) return 0;

        var existing = repository.ItemsByUid(collectionId, tasks[0].Uid);
        var written = 0;

        foreach (var task in tasks)
        {
            var match = existing.FirstOrDefault(i =>
                i.IsOverride == task.IsOverride
                && (!task.IsOverride
                    || string.Equals(i.RecurrenceId, ICalendarCodec.RecurrenceIdText(task.RecurrenceId!), StringComparison.Ordinal)));

            if (!overLocalChanges && match is { SyncState: not PimSyncState.Synced }) continue;

            var row = PimTodoCodec.ToItem(task, collectionId, match, PimSyncState.Synced) with
            {
                DavHref = href,
                Etag = etag,
                RawPayload = tasks.Count == 1 ? payload : TodoCodec.Serialize(task),
            };

            if (match is null) repository.AddItem(row);
            else repository.UpdateItem(row);
            written++;
        }

        foreach (var orphan in existing.Where(i => i.IsOverride
                     && (overLocalChanges || i.SyncState == PimSyncState.Synced)
                     && !tasks.Any(t =>
                     t.IsOverride && string.Equals(i.RecurrenceId, ICalendarCodec.RecurrenceIdText(t.RecurrenceId!), StringComparison.Ordinal))))
        {
            repository.DeleteItem(orphan.Id);
        }

        return written;
    }

    // ---- Settling a conflict -------------------------------------------------------------------

    /// <summary>
    /// Keep what this machine has: the change goes back on the queue carrying the server's own
    /// tag, so the next push satisfies the precondition instead of tripping over it again.
    /// </summary>
    /// <remarks>
    /// The tag is the whole mechanism. Re-queueing without it sends the same stale
    /// <c>If-Match</c> and earns the same 412 — the change would sit in the queue for ever
    /// looking as though it were about to go. Writing the server's tag onto the row is not a
    /// claim that the row matches the server; it is a statement that this copy is the one that
    /// knowingly replaces it.
    /// </remarks>
    /// <returns>False when the row has since gone, so there is nothing to keep.</returns>
    public static bool KeepLocal(PimRepository repository, DavConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(conflict);

        // No row left here means there is nothing local to keep — the answer to that conflict is
        // the server's copy or nothing at all.
        if (conflict.ItemId <= 0 || repository.Item(conflict.ItemId) is not { } item) return false;

        var op = conflict.LocalDelete ? "delete" : "put";
        repository.SetSyncState(
            item.Id,
            conflict.LocalDelete ? PimSyncState.Deleted : PimSyncState.Modified,
            conflict.ServerEtag ?? item.Etag,
            conflict.Href);

        Drop(repository, conflict, op);
        repository.Queue(conflict.CollectionId, item.Id, op, conflict.Href);
        return true;
    }

    /// <summary>
    /// Keep what the server has: its copy is written here, over whatever was in its place, and
    /// the refused change leaves the queue.
    /// </summary>
    /// <returns>False when the server's copy could not be read, so there is nothing to keep.</returns>
    public static bool KeepServer(PimRepository repository, DavConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(conflict);

        if (conflict.ServerPayload is not { Length: > 0 } payload) return false;

        // Which kind of thing is being kept follows from the collection it is in, so a caller
        // settling a conflict does not have to know or say.
        var kind = repository.Collection(conflict.CollectionId)?.Kind ?? CollectionKind.Events;
        if (DavPayloads.For(kind).Store(repository, conflict.CollectionId, conflict.Href, conflict.ServerEtag, payload, overLocalChanges: true) == 0)
        {
            return false;
        }

        Drop(repository, conflict, conflict.LocalDelete ? "delete" : "put");
        return true;
    }

    /// <summary>Takes the refused change off the queue.</summary>
    private static void Drop(PimRepository repository, DavConflict conflict, string op)
    {
        foreach (var queued in repository.Queued(conflict.CollectionId))
        {
            if (string.Equals(queued.Op, op, StringComparison.Ordinal) && queued.ItemId == conflict.ItemId)
            {
                repository.Dequeue(queued.Id);
            }
        }
    }

    /// <summary>The path a new item is written to: its UID, as every server expects.</summary>
    internal static string NewHref(string uid)
        => Uri.EscapeDataString(uid.Replace('/', '-')) + ".ics";
}
