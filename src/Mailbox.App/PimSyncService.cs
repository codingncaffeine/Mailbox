using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;
using Mailbox.Dav;
using Mailbox.Google;
using Mailbox.Protocols;
using Mailbox.Protocols.OAuth;
using Mailbox.Store.Pim;

namespace Mailbox.App;

/// <summary>What a run over every collection came to, for the status line and the progress dialog.</summary>
public sealed record CalendarSyncReport(int Collections, int Pulled, int Removed, int Pushed, IReadOnlyList<DavConflict> Conflicts)
{
    public static readonly CalendarSyncReport Nothing = new(0, 0, 0, 0, []);

    /// <summary>
    /// Google Tasks' own conflicts, which are a different record because they are found a
    /// different way: there is no precondition to refuse the write, so a collision is arithmetic
    /// rather than a status code.
    /// </summary>
    public IReadOnlyList<GoogleConflict> GoogleConflicts { get; init; } = [];

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
public sealed class PimSyncService(
    PimRepository repository,
    ICredentialStore secrets,
    OAuthAccounts? oauth = null,
    SettingsStore? settings = null)
{
    private readonly PimRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ICredentialStore _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

    /// <summary>The keyring purpose a DAV password is filed under.</summary>
    public const string Purpose = "caldav";

    /// <summary>Where a Google Tasks account's own client registration is kept (§5).</summary>
    public static string ClientIdSetting(string account) => $"pim.google.{account}.client";

    /// <summary>
    /// The calendars this reader publishes. Set by the shell; null in a test or a seed, where
    /// publishing is nobody's business.
    /// </summary>
    public Mailbox.Core.Calendars.PublishedCalendars? Published { get; set; }

    /// <summary>
    /// Writes one published calendar to its address and says how it went, in a sentence fit for
    /// the status bar.
    /// </summary>
    /// <remarks>
    /// Anonymous: a published calendar is written to whatever address the reader gave, and this
    /// does not yet ask for a sign-in to go with one. A server that wants one answers 401 and is
    /// told so plainly rather than failing quietly — which is the difference between a feature
    /// with a stated limit and a button that does nothing.
    /// </remarks>
    public async Task<string> PublishAsync(long collectionId, CancellationToken cancellationToken = default)
    {
        if (Published?.For(collectionId) is not { } entry) return "That calendar is not published.";
        if (_repository.Collection(collectionId) is not { } calendar) return "That calendar is no longer here.";
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var url)) return "That is not an address a calendar can be written to.";

        try
        {
            using var client = new DavClient();
            var outcome = await CalendarPublisher
                .PublishAsync(client, _repository, calendar, url, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Ok)
            {
                Log.Warn($"Publish: “{calendar.DisplayName}” refused by {url.Host} — {outcome.Refused}.");
                return outcome.Refused?.StartsWith("401", StringComparison.Ordinal) == true
                    ? $"{url.Host} wants a sign-in, and publishing does not ask for one yet."
                    : $"“{calendar.DisplayName}” was not published: {outcome.Refused}.";
            }

            Published.Published(collectionId, DateTimeOffset.UtcNow);
            Log.Info($"Publish: “{calendar.DisplayName}” — {outcome.Written} event(s) to {url}.");
            return $"“{calendar.DisplayName}” published: {outcome.Written} appointment{(outcome.Written == 1 ? string.Empty : "s")}.";
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"Publish: “{calendar.DisplayName}” could not be written.", ex);
            return $"“{calendar.DisplayName}” could not be published: {ex.Message}";
        }
    }

    /// <summary>
    /// Puts every published calendar up again, which is what makes it publishing rather than an
    /// export somebody has to remember to repeat.
    /// </summary>
    private async Task PublishAllAsync(CancellationToken cancellationToken)
    {
        if (Published is not { All.Count: > 0 } published) return;

        foreach (var entry in published.All.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A calendar that has since been deleted stops being published with it: leaving the
            // entry would keep writing a document nothing here can account for.
            if (_repository.Collection(entry.CollectionId) is null)
            {
                published.Remove(entry.CollectionId);
                Log.Info($"Publish: collection {entry.CollectionId} is gone; it is no longer published.");
                continue;
            }

            await PublishAsync(entry.CollectionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Syncs every calendar that has a server behind it.</summary>
    public async Task<CalendarSyncReport> SyncAsync(CancellationToken cancellationToken = default)
    {
        // Google Tasks is REST, not DAV. Handing one of its lists to the DAV engine would send a
        // PROPFIND to a JSON API, so the two are told apart once, here, by the only thing that
        // could tell them apart: the host in the URL.
        var google = await SyncGoogleAsync(cancellationToken).ConfigureAwait(false);

        // What this machine sends out goes before what it fetches, for the reason the mail
        // journal plays before its fetch: a reader subscribed to a calendar published here sees
        // the change on their next poll rather than the one after it.
        await PublishAllAsync(cancellationToken).ConfigureAwait(false);

        var collections = _repository.Collections()
            .Where(c => c.DavUrl is { Length: > 0 } && !GoogleTasks.Owns(c))
            .GroupBy(c => c.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (collections.Count == 0) return google;

        var pulled = 0;
        var removed = 0;
        var pushed = 0;
        var touched = 0;
        var conflicts = new List<DavConflict>();

        foreach (var account in collections)
        {
            // Subscriptions group under the empty account: nobody signed in to get one, so there
            // is no password to find and asking the keyring wakes it for nothing.
            var password = account.Key is { Length: > 0 }
                ? await _secrets.LoadAsync(account.Key, Purpose, cancellationToken).ConfigureAwait(false)
                : null;
            using var client = new DavClient(new DavCredentials(account.Key, password));

            foreach (var collection in account)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // A collection with nothing queued and an unmoved CTag needs no further
                    // requests at all.
                    //
                    // Both ways out of here are a successful check, and the Internet Calendars
                    // tab reports the check rather than the change — so the stamp is written on
                    // the cheap path too, which is the path that otherwise writes nothing and is
                    // also the path a healthy subscription takes nearly every time.
                    if (_repository.Queued(collection.Id).Count == 0
                        && await DavSync.For(client, _repository, collection).IsUnchangedAsync(collection, cancellationToken).ConfigureAwait(false))
                    {
                        _repository.SetCollectionChecked(collection.Id, DateTimeOffset.UtcNow);
                        continue;
                    }

                    var result = await DavSync.For(client, _repository, collection).SyncAsync(collection, cancellationToken).ConfigureAwait(false);
                    _repository.SetCollectionChecked(collection.Id, DateTimeOffset.UtcNow);
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

        var report = new CalendarSyncReport(
            touched + google.Collections,
            pulled + google.Pulled,
            removed + google.Removed,
            pushed + google.Pushed,
            conflicts)
        {
            GoogleConflicts = google.GoogleConflicts,
        };

        if (report.DidAnything || conflicts.Count > 0 || report.GoogleConflicts.Count > 0)
        {
            Log.Info(
                $"Collections: {report.Pulled} in, {report.Removed} removed, {report.Pushed} out, "
                + $"{conflicts.Count + report.GoogleConflicts.Count} conflict(s).");
        }

        return report;
    }

    /// <summary>
    /// The Google Tasks half of a Send/Receive.
    /// </summary>
    /// <remarks>
    /// Its own pass rather than a branch inside the DAV loop, because almost nothing is shared:
    /// a different credential (a bearer, not a password), a different discovery (the account's
    /// lists, re-read every poll so one made on a phone turns up), and a different idea of what
    /// incremental means.
    /// <para>
    /// A refusal about the sign-in stops that account and nothing else — a Google account whose
    /// consent was withdrawn should not take the calendars down with it.
    /// </para>
    /// </remarks>
    private async Task<CalendarSyncReport> SyncGoogleAsync(CancellationToken cancellationToken)
    {
        if (oauth is null) return CalendarSyncReport.Nothing;

        var accounts = _repository.Collections()
            .Where(GoogleTasks.Owns)
            .Select(c => c.Account)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (accounts.Count == 0) return CalendarSyncReport.Nothing;

        var pulled = 0;
        var removed = 0;
        var pushed = 0;
        var touched = 0;
        var conflicts = new List<GoogleConflict>();

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clientId = settings?.GetString(ClientIdSetting(account)) ?? string.Empty;
            if (clientId.Length == 0)
            {
                // Google ships no registration and never will (§5), so an account with no client
                // ID of its own cannot sign in at all. Said once, not once per list.
                Log.Warn($"Google Tasks for {account} has no client ID; sign in again to set one.");
                continue;
            }

            using var api = new GoogleTasksApi(
                oauth.For(account, OAuthProviders.Google, clientId));

            try
            {
                await GoogleTasks.RefreshListsAsync(api, _repository, account, cancellationToken).ConfigureAwait(false);

                var lists = _repository.Collections()
                    .Where(c => GoogleTasks.Owns(c) && string.Equals(c.Account, account, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var sync = new GoogleTasksSync(api, _repository);

                foreach (var list in lists)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await sync.SyncAsync(list, cancellationToken).ConfigureAwait(false);
                    pulled += result.Pulled;
                    removed += result.Removed;
                    pushed += result.Pushed;
                    conflicts.AddRange(result.Conflicts);
                    touched++;
                }
            }
            catch (OAuthException ex)
            {
                Log.Warn($"Google Tasks for {account} could not sign in: {ex.Message}");
            }
            catch (GoogleApiException ex)
            {
                Log.Warn($"Google Tasks for {account} could not be synchronised: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                Log.Warn($"Google Tasks for {account} could not be reached.", ex);
            }
            catch (TaskCanceledException)
            {
                throw;
            }
        }

        return new CalendarSyncReport(touched, pulled, removed, pushed, []) { GoogleConflicts = conflicts };
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
