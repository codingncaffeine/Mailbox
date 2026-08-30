using MailKit;
using MimeKit;
using Mailbox.Core.Diagnostics;
using Mailbox.Security;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>What a sync did, for the send/receive result.</summary>
public sealed record SyncResult(int Downloaded, int OpsPlayed, int Removed, string? Error = null)
{
    public bool Succeeded => Error is null;

    /// <summary>
    /// The store ids of what arrived in the Inbox this sync — the messages a new-mail toast is
    /// about. Mail pulled into Sent Items or an archive folder is the server catching up, not
    /// news, so it is counted in <see cref="Downloaded"/> and not here.
    /// </summary>
    public IReadOnlyList<long> Arrived { get; init; } = [];

    public static SyncResult Failed(string error) => new(0, 0, 0, error);
}

/// <summary>
/// Keeps an IMAP account's store in step with the server.
/// </summary>
/// <remarks>
/// The store is authoritative: it holds all of read state, categories, flags and where a
/// message is, and IMAP is a two-way sync of the subset the server also keeps. So a sync plays
/// the local journal to the server <em>first</em> — the flags flipped, the messages moved and
/// deleted, the copies filed to Sent while offline — and only then pulls, so the server's answer
/// already reflects what was done here and the two cannot fight over the same message.
/// <para>
/// Every server operation is one the store recorded as a fact about a UID in a folder, so a
/// change made months ago offline plays exactly as one made a second ago. What the server will
/// not do — a move to a folder that has gone, a flag on a message expunged elsewhere — is
/// abandoned rather than retried forever, and the store is put back to what the server says.
/// </para>
/// </remarks>
public sealed class ImapSynchronizer(MailRepository repository, Func<DateTimeOffset>? now = null)
{
    private readonly MailRepository _repository = repository;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Lets a test supply a fake server. Null uses MailKit.</summary>
    public Func<IImapSession>? SessionFactory { get; set; }

    /// <summary>Checks each arriving message's DKIM as the receiver does, or null to check nothing.</summary>
    public DkimVerification? Authentication { get; set; }

    /// <summary>
    /// What acts on a message once it is stored — the junk filter, the rules — or null to leave
    /// everything where the server had it. Only mail new to the Inbox is handed over: a message
    /// pulled from Sent Items or an archive folder is the server catching up, not an arrival,
    /// and a rule that moved it would be undoing what the reader already did elsewhere.
    /// </summary>
    public IArrivalHandler? OnArrival { get; set; }

    /// <param name="onlyFolder">
    /// The one folder to pull, by the name the store keeps it under. Null for every folder worth
    /// pulling, which is what a send/receive does; Update Folder names one, and the reference's
    /// own button checks the folder in front of the reader rather than the whole account.
    /// </param>
    public async Task<SyncResult> SyncAsync(
        AccountConnection account,
        IProgress<PollProgress>? progress = null,
        CancellationToken cancellation = default,
        string? onlyFolder = null)
    {
        var session = SessionFactory?.Invoke() ?? new MailKitImapSession();

        try
        {
            progress?.Report(new PollProgress(account.Address, 0, 0, "Connecting"));
            await session.ConnectAsync(account.Incoming, cancellation);
            await session.AuthenticateAsync(account.Incoming, cancellation);

            var mapped = await MapFoldersAsync(session, account, cancellation);
            var played = await PlayJournalAsync(session, account, cancellation);

            // The journal is played whatever is being pulled: an operation made offline belongs
            // on the server whether or not the folder it touched is the one being looked at.
            if (onlyFolder is { Length: > 0 })
            {
                mapped = [.. mapped.Where(f => string.Equals(f.Name, onlyFolder, StringComparison.OrdinalIgnoreCase))];
            }

            var downloaded = 0;
            var removed = 0;
            var arrived = new List<long>();
            foreach (var folder in mapped)
            {
                cancellation.ThrowIfCancellationRequested();
                var (got, gone) = await PullAsync(session, account, folder, progress, cancellation);
                downloaded += got.Count;
                removed += gone;
                if (folder.Role == FolderRole.Inbox) arrived.AddRange(got.InPlace);
            }

            return new SyncResult(downloaded, played, removed) { Arrived = arrived };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"IMAP sync failed for {account.Address}.", ex);
            return SyncResult.Failed(Pop3Receiver.Explain(ex));
        }
        finally
        {
            if (session.IsConnected) await session.DisconnectAsync(CancellationToken.None);
            session.Dispose();
        }
    }

    // ---- Folders --------------------------------------------------------------------------

    /// <summary>
    /// Reconciles the folder list with the server's, and returns the folders worth pulling.
    /// </summary>
    /// <remarks>
    /// A role the account already has as a local-only folder — the Inbox and the rest, created
    /// when the account was added — is tied to the server's folder of that role rather than left
    /// beside it, so there is one Sent Items, not two. A folder the server has and we do not is
    /// created; one we have and the server has lost is dropped. A mailbox's own "all mail" or
    /// "starred" view is mapped so a move into it is understood, but never pulled: it is a second
    /// copy of mail held elsewhere, and pulling it would double the store.
    /// </remarks>
    private async Task<IReadOnlyList<Folder>> MapFoldersAsync(
        IImapSession session, AccountConnection account, CancellationToken cancellation)
    {
        var remote = await session.ListFoldersAsync(cancellation);
        var byPath = _repository.Folders(account.AccountId)
            .Where(f => f.ImapPath is not null)
            .ToDictionary(f => f.ImapPath!, StringComparer.Ordinal);

        var toPull = new List<Folder>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in remote)
        {
            seen.Add(entry.Path);
            var parentId = entry.ParentPath is { } parent
                ? _repository.FolderByPath(account.AccountId, parent)?.Id
                : null;

            Folder folder;
            if (byPath.TryGetValue(entry.Path, out var known))
            {
                folder = known;
            }
            else if (entry.Role is not FolderRole.None
                     && _repository.FolderWithRole(account.AccountId, entry.Role) is { ImapPath: null } roleFolder)
            {
                // A local-only role folder from account creation. Take it over rather than
                // making a second one.
                _repository.MapFolder(roleFolder.Id, entry.Path, entry.Name, parentId);
                folder = _repository.GetFolder(roleFolder.Id)!;
            }
            else
            {
                folder = _repository.AddFolder(account.AccountId, entry.Name, entry.Role, parentId, entry.Path);
            }

            // A view is listed so a move into it reads, but not pulled.
            var pull = entry.Selectable && !entry.IsView;
            if (folder.Synced != pull) _repository.SetFolderSynced(folder.Id, pull);
            if (pull) toPull.Add(_repository.GetFolder(folder.Id)!);
        }

        // A folder the server no longer lists. Local-only folders (the Outbox) are kept; a
        // mapped one that has gone from the server is dropped, with whatever it held.
        foreach (var folder in _repository.Folders(account.AccountId))
        {
            if (folder.ImapPath is { } path && !seen.Contains(path))
            {
                Log.Info($"Folder {path} is gone from the server; removing it locally.");
                _repository.RemoveFolder(folder.Id);
            }
        }

        // Pull the Inbox first: it is the one anybody is waiting to see.
        toPull.Sort((a, b) => (a.Role == FolderRole.Inbox ? 0 : 1).CompareTo(b.Role == FolderRole.Inbox ? 0 : 1));
        return toPull;
    }

    // ---- Playing the journal --------------------------------------------------------------

    /// <summary>
    /// Plays every pending local change to the server, grouped so a folder is opened once and a
    /// run of flag changes goes in one command.
    /// </summary>
    private async Task<int> PlayJournalAsync(
        IImapSession session, AccountConnection account, CancellationToken cancellation)
    {
        var pending = _repository.PendingOps();
        if (pending.Count == 0) return 0;

        var played = 0;

        // Flags, grouped by folder, flag and value: "mark these forty read" is one STORE.
        foreach (var group in pending
                     .Where(o => o.Kind == SyncOpKind.Flags && o.ServerUid is not null)
                     .GroupBy(o => (o.FolderId, o.Flag, o.Value)))
        {
            cancellation.ThrowIfCancellationRequested();
            var ops = group.ToList();
            try
            {
                var folder = _repository.GetFolder(group.Key.FolderId);
                if (folder?.ImapPath is null) { _repository.CompleteOps(ops.Select(o => o.Id)); continue; }

                await session.OpenAsync(folder.ImapPath, cancellation);
                var uids = ops.Select(o => long.Parse(o.ServerUid!)).ToList();
                var flag = group.Key.Flag == SyncFlag.Flagged ? MessageFlags.Flagged : MessageFlags.Seen;
                await session.StoreFlagsAsync(uids, flag, group.Key.Value == true, cancellation);

                _repository.CompleteOps(ops.Select(o => o.Id));
                played += ops.Count;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not set flags on {account.Address}: {ex.Message}");
                _repository.FailOps(ops.Select(o => o.Id), ex.Message);
            }
        }

        // Moves, deletes and appends, oldest first, each on its own because the destination
        // and the writeback differ. Re-read after the flags above so nothing is played twice.
        foreach (var op in _repository.PendingOps().Where(o => o.Kind != SyncOpKind.Flags))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (await PlayOneAsync(session, op, cancellation)) played++;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not play a {op.Kind} for {account.Address}: {ex.Message}");
                _repository.FailOps([op.Id], ex.Message);
            }
        }

        return played;
    }

    private async Task<bool> PlayOneAsync(IImapSession session, SyncOp op, CancellationToken cancellation)
    {
        switch (op.Kind)
        {
            case SyncOpKind.Move when op.ServerUid is { } uid && op.TargetFolderId is { } targetId:
            {
                var source = _repository.GetFolder(op.FolderId);
                var target = _repository.GetFolder(targetId);
                if (source?.ImapPath is null || target?.ImapPath is null)
                {
                    _repository.AbandonMove(op);
                    return false;
                }

                await session.OpenAsync(source.ImapPath, cancellation);
                var map = await session.MoveAsync([long.Parse(uid)], target.ImapPath, cancellation);

                // The message's UID in its new folder: from the server if UIDPLUS gave it,
                // else found by its Message-ID, else left null to be recovered on the next pull.
                if (op.MessageId is { } messageId)
                {
                    long? newUid = map.TryGetValue(long.Parse(uid), out var mapped) ? mapped : null;

                    // No UIDPLUS: find the message where it now lives by the Message-ID that
                    // travelled with it, so the next pull recognises it rather than downloading
                    // a second copy. Left null only if it has no Message-ID or the search finds
                    // nothing, in which case the pull's own dedupe is the backstop.
                    if (newUid is null && _repository.GetMessage(messageId)?.MessageId is { Length: > 0 } mid)
                    {
                        await session.OpenAsync(target.ImapPath, cancellation);
                        newUid = (await session.SearchByMessageIdAsync(mid, cancellation))
                            .Cast<long?>().LastOrDefault();
                        await session.OpenAsync(source.ImapPath, cancellation);
                    }

                    _repository.SetServerUid(messageId, newUid?.ToString());
                }

                _repository.CompleteOps([op.Id]);
                return true;
            }

            case SyncOpKind.Delete when op.ServerUid is { } uid:
            {
                var folder = _repository.GetFolder(op.FolderId);
                if (folder?.ImapPath is not null)
                {
                    await session.OpenAsync(folder.ImapPath, cancellation);
                    await session.ExpungeAsync([long.Parse(uid)], cancellation);
                }

                _repository.CompleteOps([op.Id]);
                return true;
            }

            case SyncOpKind.Append when op.MessageId is { } messageId:
            {
                var folder = _repository.GetFolder(op.FolderId);
                var raw = _repository.LoadRaw(messageId);
                if (folder?.ImapPath is null || raw is null)
                {
                    _repository.CompleteOps([op.Id]);
                    return false;
                }

                var flags = FlagsFor(messageId);
                var uid = await session.AppendAsync(folder.ImapPath, raw, flags, _now(), cancellation);
                _repository.SetServerUid(messageId, uid?.ToString());
                _repository.CompleteOps([op.Id]);
                return true;
            }

            default:
                // A move or delete whose UID went missing, or an append whose row is gone.
                _repository.CompleteOps([op.Id]);
                return false;
        }
    }

    private MessageFlags FlagsFor(long messageId)
    {
        var flags = MessageFlags.None;
        if (_repository.Flags(messageId) is { } state)
        {
            if (state.IsRead) flags |= MessageFlags.Seen;
            if (state.IsFlagged) flags |= MessageFlags.Flagged;
        }

        return flags;
    }

    // ---- Pulling --------------------------------------------------------------------------

    /// <summary>
    /// Brings one folder into step: new messages down, gone messages out, changed flags in.
    /// </summary>
    private async Task<(Pulled Downloaded, int Removed)> PullAsync(
        IImapSession session,
        AccountConnection account,
        Folder folder,
        IProgress<PollProgress>? progress,
        CancellationToken cancellation)
    {
        var state = await session.OpenAsync(folder.ImapPath!, cancellation);

        // UIDVALIDITY changing means every UID this folder knew now means something else. The
        // only safe reading is to forget the lot and fetch afresh.
        if (folder.UidValidity is { } known && known != state.UidValidity)
        {
            Log.Info($"{folder.ImapPath}: UIDVALIDITY changed ({known} → {state.UidValidity}); refetching.");
            _repository.ResetFolderFromServer(folder.Id);
            folder = _repository.GetFolder(folder.Id)!;
        }

        var serverUids = await session.SearchAllAsync(cancellation);
        var serverSet = serverUids.ToHashSet();
        var localByUid = _repository.MessageIdsByServerUid(folder.Id);

        // Gone from the server: expunged elsewhere. But not one this store is mid-move on —
        // its UID here is intentionally cleared, and it is not in localByUid to begin with.
        var vanished = localByUid.Keys.Where(uid => !serverSet.Contains(long.Parse(uid))).ToList();
        var removed = vanished.Count == 0 ? 0 : _repository.DeleteByServerUids(folder.Id, vanished);

        // Flags that changed on messages we already hold. CONDSTORE narrows it to what changed;
        // without it, a fetch of all of them, which is why the offline window matters.
        await ReconcileFlagsAsync(session, folder, state, localByUid, cancellation);

        // New on the server, within the offline window. The window is by arrival date, so a
        // folder years deep does not download in full on first sync.
        var cutoff = account.Sync.Cutoff(_now());
        var pendingUids = _repository.PendingUidsIn(folder.Id);
        var fresh = serverUids.Where(uid =>
            !localByUid.ContainsKey(uid.ToString()) && !pendingUids.Contains(uid.ToString())).ToList();

        var downloaded = await DownloadAsync(session, folder, fresh, cutoff, progress, account.Address, cancellation);

        _repository.SetFolderSyncState(folder.Id, state.UidValidity, state.UidNext,
            state.SupportsModSeq ? state.HighestModSeq : null);

        return (downloaded, removed);
    }

    private async Task ReconcileFlagsAsync(
        IImapSession session,
        Folder folder,
        FolderState state,
        IReadOnlyDictionary<string, long> localByUid,
        CancellationToken cancellation)
    {
        if (localByUid.Count == 0) return;

        IReadOnlyList<RemoteMessageInfo> changed;
        if (state.SupportsModSeq && folder.HighestModSeq is { } since && since > 0)
        {
            // CONDSTORE: only what changed since we last looked.
            changed = await session.FetchFlagsChangedSinceAsync(since, cancellation);
        }
        else
        {
            // No CONDSTORE: the flags of everything we hold, and compare. This is the cost the
            // offline window keeps bounded.
            changed = await session.FetchInfoAsync([.. localByUid.Keys.Select(long.Parse)], cancellation);
        }

        foreach (var info in changed)
        {
            if (!localByUid.TryGetValue(info.Uid.ToString(), out var messageId)) continue;

            var read = info.Flags.HasFlag(MessageFlags.Seen);
            var flagged = info.Flags.HasFlag(MessageFlags.Flagged);

            // Do not stamp over a change this store is still holding to play up: the local
            // value is the newer one until the server has been told.
            if (_repository.PendingUidsIn(folder.Id).Contains(info.Uid.ToString())) continue;

            _repository.ApplyServerFlags(messageId, read, flagged);
        }
    }

    /// <summary>What a pull of one folder brought: how many, and which of them stayed put.</summary>
    /// <param name="Count">Messages downloaded and stored, wherever the handler then put them.</param>
    /// <param name="InPlace">The ids still in the folder they were pulled into — the arrivals a toast is about.</param>
    private sealed record Pulled(int Count, IReadOnlyList<long> InPlace);

    private async Task<Pulled> DownloadAsync(
        IImapSession session,
        Folder folder,
        IReadOnlyList<long> uids,
        DateTimeOffset? cutoff,
        IProgress<PollProgress>? progress,
        string address,
        CancellationToken cancellation)
    {
        if (uids.Count == 0) return new Pulled(0, []);

        // Ask the server for arrival dates and flags first, in one fetch, then decide what is
        // within the offline window from that — rather than assuming UID order tracks arrival
        // order, which holds on a well-behaved server and not on every one.
        var info = (await session.FetchInfoAsync([.. uids], cancellation))
            .ToDictionary(i => i.Uid, i => i);

        var wanted = uids
            .Where(uid => cutoff is not { } limit
                          || !info.TryGetValue(uid, out var meta)
                          || meta.InternalDate is not { } arrived
                          || arrived >= limit)
            .OrderByDescending(u => u)
            .ToList();

        var count = 0;
        var inPlace = new List<long>();
        foreach (var uid in wanted)
        {
            cancellation.ThrowIfCancellationRequested();

            var message = await session.GetMessageAsync(uid, cancellation);
            if (message is null) continue;

            // A message moved into this folder whose new UID was never written back is here
            // already, without a UID. Adopt it rather than storing a second copy.
            if (!string.IsNullOrEmpty(message.MessageId)
                && _repository.AdoptServerUid(folder.Id, message.MessageId, uid.ToString()))
            {
                continue;
            }

            progress?.Report(new PollProgress(address, count + 1, wanted.Count, "Receiving"));

            var meta2 = info.GetValueOrDefault(uid);
            var read = meta2?.Flags.HasFlag(MessageFlags.Seen) ?? false;
            var flagged = meta2?.Flags.HasFlag(MessageFlags.Flagged) ?? false;

            using var buffer = new MemoryStream();
            await message.WriteToAsync(buffer, cancellation);
            var raw = buffer.ToArray();

            var summary = MessageMapper.ToSummary(message, uid.ToString(), raw.Length, _now(), read, flagged);
            var id = _repository.AddMessage(folder.Id, summary, raw);

            if (id is { } messageId)
            {
                count++;

                // Handed to the junk filter and the rules only when it is new to the Inbox. What
                // they do to it — a move, a delete — is journalled to the server like any change
                // made here, which is why it is stored where the server had it first.
                var endedIn = folder.Role == FolderRole.Inbox
                    ? Arrival.Handle(OnArrival, _repository, folder, messageId, message)
                    : folder.Id;

                if (endedIn is null) continue;

                if (endedIn == folder.Id) inPlace.Add(messageId);
                await Arrival.RecordSignatureAsync(_repository, Authentication, messageId, message, _now(), cancellation);
            }
        }

        return new Pulled(count, inPlace);
    }
}
