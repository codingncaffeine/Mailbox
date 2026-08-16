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
/// </remarks>
public sealed record DavConflict(long ItemId, string Href, string Summary, string? ServerPayload);

/// <summary>
/// The sync engine: pull what the server has, push what this machine has, and never let either
/// overwrite the other by accident.
/// </summary>
/// <remarks>
/// Push before pull, for the same reason the mail journal plays before its fetch (§4): the
/// server's answer then already reflects what was done here, and the two never argue over one
/// item. What is pushed comes off <c>dav_queue</c>, which is what makes an offline change a
/// longer queue rather than a lost edit.
/// <para>
/// Incremental where the server can: <c>sync-collection</c> with a token, falling back to a CTag
/// check and an ETag diff where it cannot. Refetching a whole collection every poll is not
/// acceptable at real sizes, and several servers still do not implement RFC 6578.
/// </para>
/// </remarks>
public sealed class CalDavSync(DavClient client, PimRepository repository)
{
    private readonly DavClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>Pushes this collection's queue, then pulls what changed on the server.</summary>
    public async Task<DavSyncResult> SyncAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.DavUrl is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var root))
        {
            return DavSyncResult.Nothing;
        }

        var (pushed, conflicts) = await PushAsync(collection, root, cancellationToken).ConfigureAwait(false);
        var (pulled, removed) = await PullAsync(collection, root, cancellationToken).ConfigureAwait(false);
        return new DavSyncResult(pulled, removed, pushed, conflicts);
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

                var url = CalDavDiscovery.Absolute(root, gone)!;
                var response = await _client.DeleteAsync(url, change.Etag, cancellationToken).ConfigureAwait(false);
                if (response.Ok || response.Status == HttpStatusCode.NotFound)
                {
                    if (change.ItemId is { } id) _repository.DeleteItem(id);
                    _repository.Dequeue(change.Id);
                    pushed++;
                }
                else if (response.Conflict)
                {
                    conflicts.Add(new DavConflict(change.ItemId ?? 0, gone, string.Empty, null));
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
            var target = CalDavDiscovery.Absolute(root, href)!;
            var payload = Whole(item);

            var write = await _client
                .PutAsync(target, payload, item.Etag, ifNoneMatch: item.Etag is null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (write.Ok)
            {
                // A server that answered without an ETag has to be asked what it stored, or the
                // next update would send no precondition at all.
                var etag = write.Etag;
                if (etag is null)
                {
                    var reread = await _client.GetAsync(target, cancellationToken).ConfigureAwait(false);
                    etag = reread.Etag;
                }

                _repository.SetSyncState(item.Id, PimSyncState.Synced, etag, href);
                _repository.Dequeue(change.Id);
                pushed++;
            }
            else if (write.Conflict)
            {
                var server = await _client.GetAsync(target, cancellationToken).ConfigureAwait(false);
                conflicts.Add(new DavConflict(item.Id, href, item.Summary, server.Ok ? server.Body : null));
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

        // The CTag is what makes the next poll one request instead of this one.
        var ctag = await ReadCtagAsync(root, cancellationToken).ConfigureAwait(false);
        _repository.SetCollectionSync(collection.Id, ctag ?? collection.Ctag, collection.SyncToken);
        return (pulled, removed);
    }

    /// <summary>
    /// True when nothing in this collection has changed since the last sync — one request, which
    /// is what makes polling a dozen calendars affordable.
    /// </summary>
    public async Task<bool> IsUnchangedAsync(Collection collection, CancellationToken cancellationToken = default)
    {
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
                .ReportAsync(root, DavXml.CalendarMultiget(slice), depth: 1, cancellationToken)
                .ConfigureAwait(false);

            if (response.Status != HttpStatusCode.MultiStatus) continue;

            foreach (var resource in response.MultiStatus.Found)
            {
                if (resource.Data is not { Length: > 0 } payload) continue;
                written += Store(collection, resource.Href, resource.Etag, payload);
            }
        }

        return written;
    }

    /// <summary>
    /// Writes one server payload into the store: a VCALENDAR may hold a series' master and its
    /// overrides together, and each becomes its own row under the same UID.
    /// </summary>
    internal int Store(Collection collection, string href, string? etag, string payload)
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

        var existing = _repository.ItemsByUid(collection.Id, events[0].Uid);
        var written = 0;

        foreach (var calendarEvent in events)
        {
            var match = existing.FirstOrDefault(i =>
                i.IsOverride == calendarEvent.IsOverride
                && (!calendarEvent.IsOverride
                    || string.Equals(i.RecurrenceId, ICalendarCodec.RecurrenceIdText(calendarEvent.RecurrenceId!), StringComparison.Ordinal)));

            var row = PimEventCodec.ToItem(calendarEvent, collection.Id, match, PimSyncState.Synced) with
            {
                DavHref = href,
                Etag = etag,
                // The server's copy of the whole resource, verbatim: it is what a later PUT has
                // to send back, and re-serializing it would drop properties the server cares
                // about (§4).
                RawPayload = events.Count == 1 ? payload : ICalendarCodec.Serialize(calendarEvent),
            };

            if (match is null) _repository.AddItem(row);
            else _repository.UpdateItem(row);
            written++;
        }

        // An override the server has dropped goes with it.
        foreach (var orphan in existing.Where(i => i.IsOverride && !events.Any(e =>
                     e.IsOverride && string.Equals(i.RecurrenceId, ICalendarCodec.RecurrenceIdText(e.RecurrenceId!), StringComparison.Ordinal))))
        {
            _repository.DeleteItem(orphan.Id);
        }

        return written;
    }

    /// <summary>
    /// The whole VCALENDAR a PUT sends: a series' master and every override together, because a
    /// server keeps one resource per UID and a PUT of the master alone deletes the overrides.
    /// </summary>
    private string Whole(PimItem item)
    {
        var family = _repository.ItemsByUid(item.CollectionId, item.Uid);
        if (family.Count <= 1) return item.RawPayload;
        return ICalendarCodec.SerializeCalendar(family.Select(PimEventCodec.FromItem).ToList());
    }

    /// <summary>The path a new item is written to: its UID, as every server expects.</summary>
    internal static string NewHref(string uid)
        => Uri.EscapeDataString(uid.Replace('/', '-')) + ".ics";
}
