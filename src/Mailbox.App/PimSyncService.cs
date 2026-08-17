using Mailbox.Core.Diagnostics;
using Mailbox.Dav;
using Mailbox.Protocols;
using Mailbox.Store.Pim;

namespace Mailbox.App;

/// <summary>What a run over every collection came to, for the status line and the progress dialog.</summary>
public sealed record CalendarSyncReport(int Collections, int Pulled, int Removed, int Pushed, IReadOnlyList<DavConflict> Conflicts)
{
    public static readonly CalendarSyncReport Nothing = new(0, 0, 0, 0, []);

    public bool DidAnything => Pulled + Removed + Pushed > 0;
}

/// <summary>
/// Runs the DAV engine over the collections this machine has — calendars and address books
/// alike — on the same schedule mail is collected on.
/// </summary>
/// <remarks>
/// Send/Receive is one button in the reference and it covers the calendars too, so this hangs off
/// the same command rather than off a second one. A collection with no <c>dav_url</c> is local and
/// is skipped; a collection whose CTag has not moved is skipped after one request, which is what
/// makes polling several calendars affordable.
/// <para>
/// Credentials come from the keyring under the DAV account's address, as the mail accounts' do.
/// Nothing here writes a password anywhere.
/// </para>
/// </remarks>
public sealed class PimSyncService(PimRepository repository, ICredentialStore secrets)
{
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ICredentialStore _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

    /// <summary>The keyring purpose a DAV password is filed under.</summary>
    public const string Purpose = "caldav";

    /// <summary>Syncs every calendar that has a server behind it.</summary>
    public async Task<CalendarSyncReport> SyncAsync(CancellationToken cancellationToken = default)
    {
        var collections = _repository.Collections()
            .Where(c => c.DavUrl is { Length: > 0 })
            .GroupBy(c => c.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (collections.Count == 0) return CalendarSyncReport.Nothing;

        var pulled = 0;
        var removed = 0;
        var pushed = 0;
        var touched = 0;
        var conflicts = new List<DavConflict>();

        foreach (var account in collections)
        {
            var password = await _secrets.LoadAsync(account.Key, Purpose, cancellationToken).ConfigureAwait(false);
            using var client = new DavClient(new DavCredentials(account.Key, password));

            foreach (var collection in account)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // A collection with nothing queued and an unmoved CTag needs no further
                    // requests at all.
                    if (_repository.Queued(collection.Id).Count == 0
                        && await DavSync.For(client, _repository, collection).IsUnchangedAsync(collection, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var result = await DavSync.For(client, _repository, collection).SyncAsync(collection, cancellationToken).ConfigureAwait(false);
                    pulled += result.Pulled;
                    removed += result.Removed;
                    pushed += result.Pushed;
                    conflicts.AddRange(result.Conflicts);
                    touched++;
                }
                catch (HttpRequestException ex)
                {
                    Log.Warn($"“{collection.DisplayName}” could not be synchronised.", ex);
                }
                catch (TaskCanceledException)
                {
                    throw;
                }
            }
        }

        var report = new CalendarSyncReport(touched, pulled, removed, pushed, conflicts);
        if (report.DidAnything || conflicts.Count > 0)
        {
            Log.Info($"Collections: {pulled} in, {removed} removed, {pushed} out, {conflicts.Count} conflict(s).");
        }

        return report;
    }

    /// <summary>
    /// Records that an item has to reach its server. A local calendar has none, so nothing is
    /// queued and the change is simply done.
    /// </summary>
    public void QueuePut(PimItem item)
    {
        if (!IsRemote(item.CollectionId)) return;
        _repository.Queue(item.CollectionId, item.Id, "put");
    }

    /// <summary>
    /// Takes an item off the calendar. On a server-backed one the row is kept, marked deleted and
    /// queued, so a delete made offline still reaches the server; the sync removes the row once
    /// the server has agreed.
    /// </summary>
    public void Remove(PimItem item)
    {
        if (!IsRemote(item.CollectionId))
        {
            _repository.DeleteItem(item.Id);
            return;
        }

        _repository.SetSyncState(item.Id, PimSyncState.Deleted);
        _repository.Queue(item.CollectionId, item.Id, "delete");
    }

    /// <summary>
    /// Moves an item to another collection, and hands back the row it now is.
    /// </summary>
    /// <remarks>
    /// The store does the work (<see cref="PimRepository.MoveItem"/>); it is offered here because
    /// this is where the rest of the application asks for anything that has to reach a server.
    /// </remarks>
    public PimItem Move(PimItem item, long toCollectionId)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _repository.MoveItem(item, toCollectionId);
    }

    private bool IsRemote(long collectionId)
        => _repository.Collection(collectionId)?.DavUrl is { Length: > 0 };
}
