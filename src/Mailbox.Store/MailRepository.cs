using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Mailbox.Store;

/// <summary>
/// Typed access to the store. The only place SQL is written for ordinary reads and writes.
/// </summary>
/// <remarks>
/// Deliberately not an ORM. The queries a mail client runs are few, known, and want to be read
/// as queries — the list's is one statement with an index behind it, and hiding it behind an
/// expression tree makes the thing that matters for a ten-thousand-message folder harder to see,
/// not easier.
/// </remarks>
public sealed class MailRepository(MailStore store)
{
    private readonly MailStore _store = store;

    /// <summary>The store beneath. Exposed for the few callers that need raw SQL.</summary>
    public MailStore Store => _store;

    // ---- Accounts -------------------------------------------------------------------------

    public Account AddAccount(string address, string displayName, MailProtocol protocol)
    {
        _isImap = null;
        var now = DateTimeOffset.UtcNow;
        _store.Execute(
            """
            INSERT INTO accounts (address, display_name, protocol, ordinal, created_utc)
            VALUES ($address, $name, $protocol, (SELECT count(*) FROM accounts), $created)
            """,
            ("$address", address),
            ("$name", displayName),
            ("$protocol", Wire(protocol)),
            ("$created", now.ToUnixTimeSeconds()));

        return GetAccount(_store.LastInsertId)!;
    }

    public Account? GetAccount(long id) => _store.Query(
        "SELECT * FROM accounts WHERE id = $id", ReadAccount, ("$id", id)).FirstOrDefault();

    public IReadOnlyList<Account> Accounts() => _store.Query(
        "SELECT * FROM accounts ORDER BY ordinal, id", ReadAccount);

    public void RemoveAccount(long id)
        => _store.Execute("DELETE FROM accounts WHERE id = $id", ("$id", id));

    /// <summary>Renames an account, which is what the list's Name column shows.</summary>
    public void RenameAccount(long id, string displayName) => _store.Execute(
        "UPDATE accounts SET display_name = $name WHERE id = $id",
        ("$name", displayName), ("$id", id));

    // ---- Folders --------------------------------------------------------------------------

    public Folder AddFolder(long accountId, string name, FolderRole role = FolderRole.None,
        long? parentId = null, string? imapPath = null)
    {
        _store.Execute(
            """
            INSERT INTO folders (account_id, parent_id, name, role, ordinal, imap_path)
            VALUES ($account, $parent, $name, $role,
                    (SELECT count(*) FROM folders WHERE account_id = $account), $path)
            """,
            ("$account", accountId), ("$parent", parentId), ("$name", name),
            ("$role", role.ToString().ToLowerInvariant()), ("$path", imapPath));

        return GetFolder(_store.LastInsertId)!;
    }

    /// <summary>The folder standing for a server folder, by the server's name for it.</summary>
    public Folder? FolderByPath(long accountId, string imapPath) => _store.Query(
        FolderSelect + " WHERE f.account_id = $account AND f.imap_path = $path",
        ReadFolder, ("$account", accountId), ("$path", imapPath)).FirstOrDefault();

    /// <summary>
    /// Ties a folder to a server folder, taking the server's name and place for it. A role
    /// folder created before the account was ever synced becomes the server's own this way,
    /// rather than sitting beside it as a second Sent Items.
    /// </summary>
    public void MapFolder(long folderId, string imapPath, string name, long? parentId) => _store.Execute(
        """
        UPDATE folders SET imap_path = $path, name = $name, parent_id = $parent WHERE id = $id
        """,
        ("$path", imapPath), ("$name", name), ("$parent", parentId), ("$id", folderId));

    /// <summary>Records where a folder's sync has got to.</summary>
    public void SetFolderSyncState(long folderId, long? uidValidity, long? uidNext, long? highestModSeq)
        => _store.Execute(
            """
            UPDATE folders SET uidvalidity = $validity, uidnext = $next, highestmodseq = $modseq
            WHERE id = $id
            """,
            ("$validity", uidValidity), ("$next", uidNext), ("$modseq", highestModSeq), ("$id", folderId));

    /// <summary>Whether a server folder is pulled as well as listed.</summary>
    public void SetFolderSynced(long folderId, bool synced) => _store.Execute(
        "UPDATE folders SET synced = $synced WHERE id = $id",
        ("$synced", synced ? 1 : 0), ("$id", folderId));

    /// <summary>
    /// Forgets everything a folder held from the server: its messages, and where the sync had
    /// got to. What UIDVALIDITY changing means — every UID it knew is now meaningless — and the
    /// next sync fetches the folder afresh, flags and all.
    /// </summary>
    public int ResetFolderFromServer(long folderId) => _store.InTransaction(() =>
    {
        var ids = _store.Query(
            "SELECT id FROM messages WHERE folder_id = $folder AND server_uid IS NOT NULL",
            r => r.GetInt64(0), ("$folder", folderId));

        var removed = ids.Count == 0 ? 0 : DeleteRows(ids);
        _store.Execute(
            "DELETE FROM sync_ops WHERE folder_id = $folder OR target_folder_id = $folder",
            ("$folder", folderId));
        SetFolderSyncState(folderId, null, null, null);
        return removed;
    });

    /// <summary>Removes a folder and everything in it. A server folder that has gone.</summary>
    public void RemoveFolder(long folderId) => _store.InTransaction(() =>
    {
        var ids = _store.Query(
            "SELECT id FROM messages WHERE folder_id = $folder", r => r.GetInt64(0), ("$folder", folderId));
        if (ids.Count > 0) DeleteRows(ids);
        _store.Execute("DELETE FROM folders WHERE id = $id", ("$id", folderId));
        return 0;
    });

    public Folder? GetFolder(long id) => _store.Query(
        FolderSelect + " WHERE f.id = $id", ReadFolder, ("$id", id)).FirstOrDefault();

    /// <summary>Finds a folder by what it is for, which is how the shell asks.</summary>
    public Folder? FolderWithRole(long accountId, FolderRole role) => _store.Query(
        FolderSelect + " WHERE f.account_id = $account AND f.role = $role",
        ReadFolder,
        ("$account", accountId), ("$role", role.ToString().ToLowerInvariant())).FirstOrDefault();

    public IReadOnlyList<Folder> Folders(long accountId) => _store.Query(
        FolderSelect + " WHERE f.account_id = $account ORDER BY f.ordinal, f.id",
        ReadFolder, ("$account", accountId));

    /// <summary>
    /// The standard set every account shows. POP3 has no folders on the server, so these are
    /// ours; an IMAP account will map the server's to these roles instead.
    /// </summary>
    public IReadOnlyList<Folder> CreateStandardFolders(long accountId)
    {
        FolderRole[] roles =
        [
            FolderRole.Inbox, FolderRole.Drafts, FolderRole.Sent,
            FolderRole.Deleted, FolderRole.Junk, FolderRole.Archive, FolderRole.Outbox,
        ];

        return [.. roles.Select(role => AddFolder(accountId, DisplayName(role), role))];
    }

    private static string DisplayName(FolderRole role) => role switch
    {
        FolderRole.Inbox => "Inbox",
        FolderRole.Drafts => "Drafts",
        FolderRole.Sent => "Sent Items",
        FolderRole.Deleted => "Deleted Items",
        FolderRole.Junk => "Junk Email",
        FolderRole.Archive => "Archive",
        FolderRole.Outbox => "Outbox",
        _ => "Folder",
    };

    // ---- Messages -------------------------------------------------------------------------

    /// <summary>
    /// Files a message, or does nothing if that server id is already in the folder.
    /// </summary>
    /// <returns>The new row's id, or null when it was already there.</returns>
    /// <remarks>
    /// The insert is <c>OR IGNORE</c> against the folder/server-uid unique index rather than a
    /// check followed by an insert: two polls racing would both pass the check. Letting the
    /// index decide means the second one loses, which is the outcome wanted.
    /// </remarks>
    public long? AddMessage(long folderId, MessageSummary message, byte[]? raw = null)
    {
        return _store.InTransaction<long?>(() =>
        {
            long? blobId = raw is null ? null : StoreBlob(raw);

            var inserted = _store.Execute(
                """
                INSERT OR IGNORE INTO messages
                    (folder_id, blob_id, server_uid, message_id, in_reply_to, thread_key,
                     from_name, from_address, subject, preview, body_text, sent_utc, received_utc,
                     size_bytes, is_read, is_flagged, has_attachment)
                VALUES
                    ($folder, $blob, $uid, $messageId, NULL, $thread,
                     $fromName, $fromAddress, $subject, $preview, $bodyText, $sent, $received,
                     $size, $read, $flagged, $attachment)
                """,
                ("$folder", folderId),
                ("$blob", blobId),
                ("$uid", message.ServerUid),
                ("$messageId", message.MessageId),
                ("$thread", ThreadKey(message.Subject)),
                ("$fromName", message.FromName),
                ("$fromAddress", message.FromAddress),
                ("$subject", message.Subject),
                ("$preview", message.Preview),
                ("$bodyText", message.BodyText),
                ("$sent", message.Sent?.ToUnixTimeSeconds()),
                ("$received", message.Received.ToUnixTimeSeconds()),
                ("$size", message.SizeBytes),
                ("$read", message.IsRead ? 1 : 0),
                ("$flagged", message.IsFlagged ? 1 : 0),
                ("$attachment", message.HasAttachment ? 1 : 0));

            if (inserted != 0)
            {
                var id = _store.LastInsertId;

                // A row with no server id in a folder that stands for one on the server was
                // made here — a sent copy, a draft — and belongs on the server too.
                if (message.ServerUid is null && raw is not null && IsSyncedFolder(folderId))
                {
                    JournalAppend(folderId, id);
                }

                return id;
            }

            // Nothing filed, so the blob written above has nothing pointing at it.
            if (blobId is { } orphan) _store.Execute("DELETE FROM blobs WHERE id = $id", ("$id", orphan));
            return null;
        });
    }

    /// <summary>Stamps the server's id on a row, after a move or an append has given it one.</summary>
    public void SetServerUid(long messageId, string? serverUid) => _store.Execute(
        "UPDATE messages SET server_uid = $uid WHERE id = $id",
        ("$uid", serverUid), ("$id", messageId));

    /// <summary>
    /// Stamps a server UID onto a message already in the folder that has none but the same
    /// Message-ID, and returns true if it did.
    /// </summary>
    /// <remarks>
    /// The backstop for a move whose new UID could not be written back at the time — a server
    /// without UIDPLUS, found by nothing. Without it, the moved row (its UID cleared by the
    /// move) would look like a new message on the next pull of its folder and be downloaded a
    /// second time. Matched on the Message-ID, which travels with the message.
    /// </remarks>
    public bool AdoptServerUid(long folderId, string messageIdHeader, string serverUid)
    {
        if (string.IsNullOrEmpty(messageIdHeader)) return false;

        return _store.Execute(
            """
            UPDATE messages SET server_uid = $uid
            WHERE folder_id = $folder AND server_uid IS NULL AND message_id = $mid
            """,
            ("$uid", serverUid), ("$folder", folderId), ("$mid", messageIdHeader)) > 0;
    }

    /// <summary>The rows in a folder keyed by server id, for reconciling against the server.</summary>
    public Dictionary<string, long> MessageIdsByServerUid(long folderId) => _store.Query(
        "SELECT server_uid, id FROM messages WHERE folder_id = $folder AND server_uid IS NOT NULL",
        r => (Uid: r.GetString(0), Id: r.GetInt64(1)), ("$folder", folderId))
        .ToDictionary(x => x.Uid, x => x.Id, StringComparer.Ordinal);

    /// <summary>
    /// Takes the server's word for a message's flags. Used by the sync for changes made
    /// elsewhere; the journal is not written, because this is the server telling us.
    /// </summary>
    public void ApplyServerFlags(long messageId, bool read, bool flagged) => _store.Execute(
        "UPDATE messages SET is_read = $read, is_flagged = $flagged WHERE id = $id",
        ("$read", read ? 1 : 0), ("$flagged", flagged ? 1 : 0), ("$id", messageId));

    /// <summary>The read and flagged state of a row, for deciding whether the server differs.</summary>
    public (bool IsRead, bool IsFlagged)? Flags(long messageId) => _store.Query(
        "SELECT is_read, is_flagged FROM messages WHERE id = $id",
        r => (r.GetInt32(0) != 0, r.GetInt32(1) != 0), ("$id", messageId))
        .Select(f => ((bool, bool)?)f).FirstOrDefault();

    /// <summary>
    /// Removes rows the server no longer has. Not journalled: the change came from the server.
    /// </summary>
    public int DeleteByServerUids(long folderId, IReadOnlyCollection<string> serverUids)
    {
        if (serverUids.Count == 0) return 0;

        return _store.InTransaction(() =>
        {
            var removed = 0;
            foreach (var chunk in serverUids.Chunk(500))
            {
                var ids = _store.Query(
                    $"""
                     SELECT id FROM messages
                     WHERE folder_id = $folder AND server_uid IN ({string.Join(',', chunk.Select(Quote))})
                     """,
                    r => r.GetInt64(0), ("$folder", folderId));
                if (ids.Count > 0) removed += DeleteRows(ids);
            }

            return removed;
        });
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>True when this folder already holds that server id.</summary>
    public bool HasServerUid(long folderId, string serverUid) => _store.ScalarLong(
        "SELECT count(*) FROM messages WHERE folder_id = $folder AND server_uid = $uid",
        ("$folder", folderId), ("$uid", serverUid)) > 0;

    /// <summary>Every server id in a folder, for working out what a poll has not seen.</summary>
    public HashSet<string> ServerUids(long folderId) =>
    [
        .. _store.Query(
            "SELECT server_uid FROM messages WHERE folder_id = $folder AND server_uid IS NOT NULL",
            r => r.GetString(0), ("$folder", folderId)),
    ];

    /// <summary>
    /// Every server id the account holds, across all its folders. POP3's dedupe uses this
    /// rather than the inbox's alone, because the junk filter files some arriving mail straight
    /// into Junk — and a message the inbox does not know is not a message to download again.
    /// </summary>
    public HashSet<string> ServerUidsInAccount(long accountId) =>
    [
        .. _store.Query(
            """
            SELECT m.server_uid FROM messages m
            JOIN folders f ON f.id = m.folder_id
            WHERE f.account_id = $account AND m.server_uid IS NOT NULL
            """,
            r => r.GetString(0), ("$account", accountId)),
    ];

    /// <summary>
    /// Server ids downloaded before <paramref name="cutoff"/>, for "remove from the server
    /// after this many days".
    /// </summary>
    /// <remarks>
    /// <c>received_utc</c> is when the message was written here, not the date in its header, so
    /// the age is time-since-download — which is what the setting means and the only reading of
    /// it that cannot delete mail off a server the moment it is collected.
    /// </remarks>
    public HashSet<string> ServerUidsOlderThan(long folderId, DateTimeOffset cutoff) =>
    [
        .. _store.Query(
            """
            SELECT server_uid FROM messages
            WHERE folder_id = $folder AND server_uid IS NOT NULL AND received_utc < $cutoff
            """,
            r => r.GetString(0),
            ("$folder", folderId),
            ("$cutoff", cutoff.ToUnixTimeSeconds())),
    ];

    public IReadOnlyList<MessageSummary> Messages(long folderId, int limit = 500) => _store.Query(
        MessageSelect + " WHERE folder_id = $folder ORDER BY received_utc DESC LIMIT $limit",
        ReadMessage, ("$folder", folderId), ("$limit", limit));

    public MessageSummary? GetMessage(long id) => _store.Query(
        MessageSelect + " WHERE id = $id", ReadMessage, ("$id", id)).FirstOrDefault();

    /// <summary>
    /// Marks many at once. One statement rather than one per row: selecting a thousand messages
    /// and marking them read is an ordinary thing to do, and a thousand round trips is not.
    /// </summary>
    public int SetRead(IReadOnlyCollection<long> messageIds, bool read)
    {
        if (messageIds.Count == 0) return 0;

        return _store.InTransaction(() =>
        {
            var changed = _store.Execute(
                $"UPDATE messages SET is_read = $read WHERE id IN ({Ids(messageIds)})",
                ("$read", read ? 1 : 0));
            JournalFlag(messageIds, SyncFlag.Seen, read);
            return changed;
        });
    }

    public int SetFlagged(IReadOnlyCollection<long> messageIds, bool flagged)
    {
        if (messageIds.Count == 0) return 0;

        return _store.InTransaction(() =>
        {
            var changed = _store.Execute(
                $"UPDATE messages SET is_flagged = $flagged WHERE id IN ({Ids(messageIds)})",
                ("$flagged", flagged ? 1 : 0));
            JournalFlag(messageIds, SyncFlag.Flagged, flagged);
            return changed;
        });
    }

    /// <summary>
    /// Moves messages between folders. On a synced account the move is journalled for the
    /// server as well, and the row gives up its server id until the server has said what the
    /// message is called where it now lives — UIDs belong to a folder, and the old one could
    /// collide with a message already there.
    /// </summary>
    public int MoveMessages(IReadOnlyCollection<long> messageIds, long toFolderId)
    {
        if (messageIds.Count == 0) return 0;

        return _store.InTransaction(() =>
        {
            var moving = messageIds.Where(id => JournalMove(id, toFolderId)).ToList();
            if (moving.Count > 0)
            {
                _store.Execute(
                    $"UPDATE messages SET server_uid = NULL WHERE id IN ({Ids(moving)})");
            }

            return _store.Execute(
                $"UPDATE messages SET folder_id = $folder WHERE id IN ({Ids(messageIds)})",
                ("$folder", toFolderId));
        });
    }

    /// <summary>
    /// Deletes many, and the raw copies behind them. Messages go before blobs: while a message
    /// exists it references its blob, and removing the blob first fails the foreign key.
    /// </summary>
    public int DeleteMessages(IReadOnlyCollection<long> messageIds)
    {
        if (messageIds.Count == 0) return 0;

        return _store.InTransaction(() =>
        {
            foreach (var id in messageIds) JournalDelete(id);
            return DeleteRows(messageIds);
        });
    }

    /// <summary>The rows and their blobs, no journal. What every delete ends in.</summary>
    private int DeleteRows(IReadOnlyCollection<long> messageIds)
    {
        var list = Ids(messageIds);
        var blobs = _store.Query(
            $"SELECT blob_id FROM messages WHERE id IN ({list}) AND blob_id IS NOT NULL",
            r => r.GetInt64(0));

        var removed = _store.Execute($"DELETE FROM messages WHERE id IN ({list})");

        if (blobs.Count > 0)
        {
            _store.Execute($"DELETE FROM blobs WHERE id IN ({Ids(blobs)})");
        }

        return removed;
    }

    /// <summary>
    /// Renders ids into an IN list. Safe because they are longs the caller already read out of
    /// this database — there is no string here for anything to be injected through, and a
    /// parameter per id would blow past SQLite's variable limit on a large selection.
    /// </summary>
    private static string Ids(IEnumerable<long> ids) => string.Join(',', ids);

    public void SetRead(long messageId, bool read) => SetRead([messageId], read);

    public void SetFlagged(long messageId, bool flagged) => SetFlagged([messageId], flagged);

    public void MoveMessage(long messageId, long toFolderId) => MoveMessages([messageId], toFolderId);

    /// <summary>Removes a message and the raw copy behind it.</summary>
    public void DeleteMessage(long messageId) => DeleteMessages([messageId]);

    // ---- The sync journal ---------------------------------------------------------------------
    //
    // The store is authoritative (§4), and IMAP is a two-way sync of the part of its state the
    // server also keeps. So a change made here to a synced folder is written to the journal as
    // well as to the row, in the same transaction, and the next send/receive plays the journal to
    // the server before it pulls. Every entry names the server folder and UID it acts on, because
    // by the time it is played the row may have moved again, or gone.

    private bool? _isImap;

    /// <summary>
    /// Whether this store belongs to an IMAP account. A store holds exactly one account, so this
    /// is a fact about the file; cached once an account exists, because it is asked on every
    /// flag change.
    /// </summary>
    private bool IsImapStore
    {
        get
        {
            if (_isImap is { } known) return known;

            var protocol = _store.Query(
                "SELECT protocol FROM accounts ORDER BY id LIMIT 1", r => r.GetString(0)).FirstOrDefault();
            if (protocol is null) return false;

            _isImap = protocol == "imap";
            return _isImap.Value;
        }
    }

    /// <summary>A folder that stands for one on the server and is pulled from it.</summary>
    private bool IsSyncedFolder(long folderId) => IsImapStore && _store.ScalarLong(
        "SELECT count(*) FROM folders WHERE id = $id AND imap_path IS NOT NULL AND synced = 1",
        ("$id", folderId)) > 0;

    /// <summary>What the journal needs to know about a row before it changes.</summary>
    private sealed record RowOrigin(long Id, long FolderId, string? ServerUid, bool Synced);

    private RowOrigin? Origin(long messageId) => _store.Query(
        """
        SELECT m.id, m.folder_id, m.server_uid, f.imap_path IS NOT NULL AND f.synced = 1
        FROM messages m JOIN folders f ON f.id = m.folder_id
        WHERE m.id = $id
        """,
        r => new RowOrigin(r.GetInt64(0), r.GetInt64(1), Nullable(r, "server_uid"), r.GetInt64(3) != 0),
        ("$id", messageId)).FirstOrDefault();

    private void JournalFlag(IReadOnlyCollection<long> messageIds, SyncFlag flag, bool value)
    {
        if (!IsImapStore) return;

        foreach (var id in messageIds)
        {
            if (Origin(id) is not { Synced: true, ServerUid: { } uid } origin) continue;

            // The latest word on a flag is the only one worth keeping: two entries for the same
            // flag on the same message would be played in order and land on the last anyway.
            _store.Execute(
                """
                DELETE FROM sync_ops
                WHERE kind = 'flags' AND folder_id = $folder AND server_uid = $uid AND flag = $flag
                """,
                ("$folder", origin.FolderId), ("$uid", uid), ("$flag", Wire(flag)));

            _store.Execute(
                """
                INSERT INTO sync_ops (kind, folder_id, server_uid, message_id, flag, value, created_utc)
                VALUES ('flags', $folder, $uid, $message, $flag, $value, $now)
                """,
                ("$folder", origin.FolderId), ("$uid", uid), ("$message", id),
                ("$flag", Wire(flag)), ("$value", value ? 1 : 0), ("$now", Now()));
        }
    }

    /// <summary>
    /// Journals a move, and says whether the row must give up its server id.
    /// </summary>
    /// <remarks>
    /// The interesting cases are the ones where the message is already on its way somewhere:
    /// a row waiting to be appended is simply appended where it is going instead; a row whose
    /// earlier move is still waiting has that move retargeted, or cancelled outright if it is
    /// going back where it came from. A move to a folder that is only local takes the message
    /// off the server, which is what dragging server mail into a local folder means.
    /// </remarks>
    private bool JournalMove(long messageId, long toFolderId)
    {
        if (!IsImapStore) return false;
        if (Origin(messageId) is not { } origin || origin.FolderId == toFolderId) return false;

        var targetSynced = IsSyncedFolder(toFolderId);

        if (origin.ServerUid is null)
        {
            // Not on the server yet, or between folders. Whatever is pending follows the row.
            if (PendingAppend(messageId) is { } append)
            {
                if (targetSynced)
                {
                    _store.Execute("UPDATE sync_ops SET folder_id = $to WHERE id = $id",
                        ("$to", toFolderId), ("$id", append.Id));
                }
                else
                {
                    _store.Execute("DELETE FROM sync_ops WHERE id = $id", ("$id", append.Id));
                }
            }
            else if (PendingMove(messageId) is { } move)
            {
                if (move.FolderId == toFolderId)
                {
                    // Back where it started: nothing for the server to do, and the row takes
                    // its old id back.
                    _store.Execute("DELETE FROM sync_ops WHERE id = $id", ("$id", move.Id));
                    _store.Execute("UPDATE messages SET server_uid = $uid WHERE id = $id",
                        ("$uid", move.ServerUid), ("$id", messageId));
                }
                else if (targetSynced)
                {
                    _store.Execute("UPDATE sync_ops SET target_folder_id = $to WHERE id = $id",
                        ("$to", toFolderId), ("$id", move.Id));
                }
                else
                {
                    _store.Execute(
                        "UPDATE sync_ops SET kind = 'delete', target_folder_id = NULL WHERE id = $id",
                        ("$id", move.Id));
                }
            }

            return false;
        }

        if (!origin.Synced) return false;

        _store.Execute(
            """
            INSERT INTO sync_ops (kind, folder_id, server_uid, message_id, target_folder_id, created_utc)
            VALUES ($kind, $folder, $uid, $message, $target, $now)
            """,
            ("$kind", targetSynced ? "move" : "delete"),
            ("$folder", origin.FolderId), ("$uid", origin.ServerUid), ("$message", messageId),
            ("$target", targetSynced ? toFolderId : null), ("$now", Now()));

        return true;
    }

    private void JournalDelete(long messageId)
    {
        if (!IsImapStore) return;
        if (Origin(messageId) is not { } origin) return;

        // Flags on a message about to go are moot.
        _store.Execute(
            "DELETE FROM sync_ops WHERE kind = 'flags' AND message_id = $id", ("$id", messageId));

        if (origin.ServerUid is null)
        {
            if (PendingAppend(messageId) is { } append)
            {
                _store.Execute("DELETE FROM sync_ops WHERE id = $id", ("$id", append.Id));
            }
            else if (PendingMove(messageId) is { } move)
            {
                _store.Execute(
                    "UPDATE sync_ops SET kind = 'delete', target_folder_id = NULL WHERE id = $id",
                    ("$id", move.Id));
            }

            return;
        }

        if (!origin.Synced) return;

        _store.Execute(
            """
            INSERT INTO sync_ops (kind, folder_id, server_uid, message_id, created_utc)
            VALUES ('delete', $folder, $uid, $message, $now)
            """,
            ("$folder", origin.FolderId), ("$uid", origin.ServerUid), ("$message", messageId), ("$now", Now()));
    }

    private void JournalAppend(long folderId, long messageId) => _store.Execute(
        """
        INSERT INTO sync_ops (kind, folder_id, message_id, created_utc)
        VALUES ('append', $folder, $message, $now)
        """,
        ("$folder", folderId), ("$message", messageId), ("$now", Now()));

    private SyncOp? PendingAppend(long messageId) => _store.Query(
        SyncOpSelect + " WHERE kind = 'append' AND message_id = $id", ReadSyncOp, ("$id", messageId))
        .FirstOrDefault();

    private SyncOp? PendingMove(long messageId) => _store.Query(
        SyncOpSelect + " WHERE kind = 'move' AND message_id = $id", ReadSyncOp, ("$id", messageId))
        .FirstOrDefault();

    /// <summary>Everything waiting to be played, oldest first.</summary>
    public IReadOnlyList<SyncOp> PendingOps() => _store.Query(
        SyncOpSelect + " ORDER BY id", ReadSyncOp);

    /// <summary>
    /// The server ids in a folder that a pending op still refers to. The sync leaves these
    /// alone when it reconciles: a message with a delete waiting is not "new on the server",
    /// and one with a flag waiting keeps the flag it was given here.
    /// </summary>
    public HashSet<string> PendingUidsIn(long folderId) =>
    [
        .. _store.Query(
            "SELECT server_uid FROM sync_ops WHERE folder_id = $folder AND server_uid IS NOT NULL",
            r => r.GetString(0), ("$folder", folderId)),
    ];

    public void CompleteOps(IEnumerable<long> opIds)
    {
        var list = Ids(opIds);
        if (list.Length == 0) return;
        _store.Execute($"DELETE FROM sync_ops WHERE id IN ({list})");
    }

    /// <summary>Counts a failed attempt and keeps why, so a persistent one can be reported.</summary>
    public void FailOps(IEnumerable<long> opIds, string error)
    {
        var list = Ids(opIds);
        if (list.Length == 0) return;
        _store.Execute(
            $"UPDATE sync_ops SET attempts = attempts + 1, last_error = $error WHERE id IN ({list})",
            ("$error", error));
    }

    /// <summary>
    /// Gives up on a move the server would not make: the row goes back where it was, with the
    /// id it had, so the store says what the server says rather than something in between.
    /// </summary>
    public void AbandonMove(SyncOp op)
    {
        _store.InTransaction(() =>
        {
            if (op.MessageId is { } id && op.ServerUid is not null)
            {
                _store.Execute(
                    "UPDATE messages SET folder_id = $folder, server_uid = $uid WHERE id = $id",
                    ("$folder", op.FolderId), ("$uid", op.ServerUid), ("$id", id));
            }

            _store.Execute("DELETE FROM sync_ops WHERE id = $id", ("$id", op.Id));
            return 0;
        });
    }

    private const string SyncOpSelect =
        """
        SELECT id, kind, folder_id, server_uid, message_id, target_folder_id, flag, value,
               created_utc, attempts, last_error
        FROM sync_ops
        """;

    private static SyncOp ReadSyncOp(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1) switch
        {
            "move" => SyncOpKind.Move,
            "delete" => SyncOpKind.Delete,
            "append" => SyncOpKind.Append,
            _ => SyncOpKind.Flags,
        },
        r.GetInt64(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetInt64(4),
        r.IsDBNull(5) ? null : r.GetInt64(5),
        r.IsDBNull(6) ? null : r.GetString(6) == "flagged" ? SyncFlag.Flagged : SyncFlag.Seen,
        r.IsDBNull(7) ? null : r.GetInt64(7) != 0,
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)),
        r.GetInt32(9),
        r.IsDBNull(10) ? null : r.GetString(10));

    private static string Wire(SyncFlag flag) => flag == SyncFlag.Flagged ? "flagged" : "seen";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Search. Ranked by BM25, which FTS5 provides without asking.</summary>
    public IReadOnlyList<MessageSummary> Search(string term, long? folderId = null, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];

        var scope = folderId is null ? string.Empty : " AND m.folder_id = $folder";
        return _store.Query(
            $"""
             SELECT m.* FROM messages m
             JOIN messages_fts ON messages_fts.rowid = m.id
             WHERE messages_fts MATCH $term{scope}
             ORDER BY bm25(messages_fts), m.received_utc DESC
             LIMIT $limit
             """,
            ReadMessage,
            ("$term", Sanitise(term)), ("$folder", folderId), ("$limit", limit));
    }

    /// <summary>
    /// Makes user input safe to hand to FTS5. Quoting each word turns everything into a literal
    /// term, so a stray quote or bracket is searched for rather than treated as syntax and
    /// throwing at the user.
    /// </summary>
    internal static string Sanitise(string term) => string.Join(
        ' ',
        term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => '"' + word.Replace("\"", "\"\"") + '"'));

    // ---- Safe senders -------------------------------------------------------------------------

    /// <summary>
    /// Whether this sender's remote images may load without asking.
    /// </summary>
    /// <remarks>
    /// Matched on the address rather than the domain. "Always allow images from this sender" is
    /// a statement about one correspondent, and a domain is shared with everyone else who has
    /// an account there.
    /// </remarks>
    public bool IsSafeSender(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        return _store.ScalarLong(
            "SELECT count(*) FROM safe_senders WHERE address = $address",
            ("$address", address.Trim().ToLowerInvariant())) > 0;
    }

    public void AddSafeSender(string address, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(address)) return;

        _store.Execute(
            """
            INSERT INTO safe_senders (address, added_utc) VALUES ($address, $now)
            ON CONFLICT(address) DO NOTHING
            """,
            ("$address", address.Trim().ToLowerInvariant()),
            ("$now", now.ToUnixTimeSeconds()));
    }

    public void RemoveSafeSender(string address) => _store.Execute(
        "DELETE FROM safe_senders WHERE address = $address",
        ("$address", address.Trim().ToLowerInvariant()));

    public IReadOnlyList<string> SafeSenders() => _store.Query(
        "SELECT address FROM safe_senders ORDER BY address", r => r.GetString(0));

    // ---- Blocked senders ----------------------------------------------------------------------

    /// <summary>Whether this sender is on the blocked list — junked whatever the classifier says.</summary>
    public bool IsBlockedSender(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        return _store.ScalarLong(
            "SELECT count(*) FROM blocked_senders WHERE address = $address",
            ("$address", address.Trim().ToLowerInvariant())) > 0;
    }

    public void AddBlockedSender(string address, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(address)) return;

        _store.Execute(
            "INSERT INTO blocked_senders (address, added_utc) VALUES ($address, $now) ON CONFLICT(address) DO NOTHING",
            ("$address", address.Trim().ToLowerInvariant()), ("$now", now.ToUnixTimeSeconds()));
    }

    public void RemoveBlockedSender(string address) => _store.Execute(
        "DELETE FROM blocked_senders WHERE address = $address",
        ("$address", address.Trim().ToLowerInvariant()));

    public IReadOnlyList<string> BlockedSenders() => _store.Query(
        "SELECT address FROM blocked_senders ORDER BY address", r => r.GetString(0));

    // ---- The junk corpus ----------------------------------------------------------------------
    //
    // The training the naive-Bayes filter (§7.8) weighs a message against. It is the whole corpus
    // — local, never uploaded — and the classifier reaches it through Mailbox.Junk's IJunkCorpus,
    // which JunkCorpus below implements over these methods.

    /// <summary>The message totals the per-token counts are normalised against.</summary>
    public (long Spam, long Ham) JunkMessageTotals() => _store.Query(
        "SELECT spam_messages, ham_messages FROM junk_corpus WHERE id = 1",
        r => (r.GetInt64(0), r.GetInt64(1))).FirstOrDefault();

    /// <summary>The spam and ham counts for a set of tokens, one read for the lot.</summary>
    public Dictionary<string, (long Spam, long Ham)> JunkCounts(IReadOnlyCollection<string> tokens)
    {
        var found = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        if (tokens.Count == 0) return found;

        foreach (var chunk in tokens.Chunk(400))
        {
            var list = string.Join(',', chunk.Select(Quote));
            foreach (var (token, spam, ham) in _store.Query(
                $"SELECT token, spam_count, ham_count FROM junk_tokens WHERE token IN ({list})",
                r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2))))
            {
                found[token] = (spam, ham);
            }
        }

        return found;
    }

    /// <summary>
    /// Trains a message into the corpus, or (with <paramref name="add"/> false) trains it back
    /// out — for a message re-marked the other way. Counts never drop below zero.
    /// </summary>
    public void TrainJunk(IReadOnlyCollection<string> tokens, bool spam, bool add)
    {
        _store.InTransaction(() =>
        {
            var step = add ? 1 : -1;

            _store.Execute(
                spam
                    ? "UPDATE junk_corpus SET spam_messages = max(0, spam_messages + $step) WHERE id = 1"
                    : "UPDATE junk_corpus SET ham_messages = max(0, ham_messages + $step) WHERE id = 1",
                ("$step", step));

            foreach (var token in tokens.Distinct())
            {
                if (spam)
                {
                    _store.Execute(
                        """
                        INSERT INTO junk_tokens (token, spam_count, ham_count) VALUES ($t, max(0, $step), 0)
                        ON CONFLICT(token) DO UPDATE SET spam_count = max(0, spam_count + $step)
                        """,
                        ("$t", token), ("$step", step));
                }
                else
                {
                    _store.Execute(
                        """
                        INSERT INTO junk_tokens (token, spam_count, ham_count) VALUES ($t, 0, max(0, $step))
                        ON CONFLICT(token) DO UPDATE SET ham_count = max(0, ham_count + $step)
                        """,
                        ("$t", token), ("$step", step));
                }
            }

            return 0;
        });
    }

    /// <summary>
    /// Every domain this mailbox deals with: its own accounts, and everyone it has mail from.
    /// </summary>
    /// <remarks>
    /// The input to the lookalike check. A domain one character away from one of these is worth
    /// warning about; one character away from a domain nobody here has ever heard of is not.
    /// </remarks>
    public IReadOnlyList<string> FamiliarDomains() => _store.Query(
        """
        SELECT DISTINCT lower(substr(from_address, instr(from_address, '@') + 1)) AS domain
        FROM messages
        WHERE instr(from_address, '@') > 0
        UNION
        SELECT DISTINCT lower(substr(address, instr(address, '@') + 1))
        FROM accounts
        WHERE instr(address, '@') > 0
        """,
        r => r.GetString(0));

    // ---- Message authentication ---------------------------------------------------------------

    /// <summary>
    /// Records what verifying a message's own DKIM signatures came to.
    /// </summary>
    /// <remarks>
    /// Written once, by whatever received the message, because verifying needs a key from DNS
    /// and §19 does not allow that on the path that draws a message. Re-checking later would
    /// also be checking against a key that may since have rotated, and reporting a rotation as
    /// a forgery is worse than not checking twice.
    /// </remarks>
    public void RecordAuthentication(
        long messageId, string dkim, string? signingDomain, DateTimeOffset now) => _store.Execute(
        """
        INSERT INTO message_authentication (message_id, dkim, signing_domain, checked_utc)
        VALUES ($id, $dkim, $domain, $now)
        ON CONFLICT(message_id) DO UPDATE SET
            dkim = excluded.dkim,
            signing_domain = excluded.signing_domain,
            checked_utc = excluded.checked_utc
        """,
        ("$id", messageId),
        ("$dkim", dkim),
        ("$domain", (object?)signingDomain ?? DBNull.Value),
        ("$now", now.ToUnixTimeSeconds()));

    /// <summary>
    /// What was recorded for a message, or null if it has never been checked.
    /// </summary>
    /// <remarks>
    /// Null is the answer for every message received before local verification existed, and for
    /// every message received offline. The pane says so rather than implying a result.
    /// </remarks>
    public MessageAuthentication? Authentication(long messageId) => _store.Query(
        """
        SELECT dkim, signing_domain, checked_utc
        FROM message_authentication WHERE message_id = $id
        """,
        r => new MessageAuthentication(
            r.GetString(0),
            Nullable(r, "signing_domain"),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2))),
        ("$id", messageId)).FirstOrDefault();

    // ---- Categories -------------------------------------------------------------------------

    public IReadOnlyList<Category> Categories() => _store.Query(
        "SELECT * FROM categories ORDER BY ordinal, id",
        r => new Category(
            r.GetInt64(r.GetOrdinal("id")),
            r.GetString(r.GetOrdinal("name")),
            r.GetString(r.GetOrdinal("colour_token")),
            Nullable(r, "shortcut"),
            r.GetInt32(r.GetOrdinal("ordinal"))));

    /// <summary>
    /// Categories for a set of messages, keyed by message. One query rather than one per row:
    /// the list asks for a page at a time and a query per row would undo the page.
    /// </summary>
    public Dictionary<long, List<Category>> CategoriesFor(IReadOnlyCollection<long> messageIds)
    {
        var found = new Dictionary<long, List<Category>>();
        if (messageIds.Count == 0) return found;

        foreach (var (messageId, category) in _store.Query(
            $"""
             SELECT mc.message_id, c.* FROM message_categories mc
             JOIN categories c ON c.id = mc.category_id
             WHERE mc.message_id IN ({Ids(messageIds)})
             ORDER BY c.ordinal, c.id
             """,
            r => (
                r.GetInt64(r.GetOrdinal("message_id")),
                new Category(
                    r.GetInt64(r.GetOrdinal("id")),
                    r.GetString(r.GetOrdinal("name")),
                    r.GetString(r.GetOrdinal("colour_token")),
                    Nullable(r, "shortcut"),
                    r.GetInt32(r.GetOrdinal("ordinal"))))))
        {
            if (!found.TryGetValue(messageId, out var list)) found[messageId] = list = [];
            list.Add(category);
        }

        return found;
    }

    public void Assign(IReadOnlyCollection<long> messageIds, long categoryId)
    {
        if (messageIds.Count == 0) return;

        _store.InTransaction(() =>
        {
            foreach (var id in messageIds)
            {
                _store.Execute(
                    """
                    INSERT OR IGNORE INTO message_categories (message_id, category_id)
                    VALUES ($message, $category)
                    """,
                    ("$message", id), ("$category", categoryId));
            }

            return 0;
        });
    }

    public void Unassign(IReadOnlyCollection<long> messageIds, long categoryId)
    {
        if (messageIds.Count == 0) return;

        _store.Execute(
            $"""
             DELETE FROM message_categories
             WHERE category_id = $category AND message_id IN ({Ids(messageIds)})
             """,
            ("$category", categoryId));
    }

    // ---- Auto-Complete List -------------------------------------------------------------------

    /// <summary>
    /// Remembers who a message went to. Called from the send path with every recipient, so the
    /// list is fed by what was actually addressed rather than by anything typed and abandoned.
    /// </summary>
    /// <remarks>
    /// The address is the key and the name is the latest one used with it: a correspondent who
    /// changes how they sign their name gets one entry that follows them, not two that compete.
    /// A blank name never overwrites a real one — a reply typed as a bare address is not a
    /// reason to forget what someone is called.
    /// </remarks>
    public void RecordRecipients(IEnumerable<(string Address, string? DisplayName)> recipients,
        DateTimeOffset now)
    {
        _store.InTransaction(() =>
        {
            foreach (var (address, name) in recipients)
            {
                if (string.IsNullOrWhiteSpace(address) || !address.Contains('@')) continue;

                _store.Execute(
                    """
                    INSERT INTO nickname_cache (address, display_name, weight, last_used_utc)
                    VALUES ($address, $name, 1, $now)
                    ON CONFLICT(address) DO UPDATE SET
                        display_name = CASE WHEN length(excluded.display_name) > 0
                                            THEN excluded.display_name ELSE display_name END,
                        weight = weight + 1,
                        last_used_utc = excluded.last_used_utc
                    """,
                    ("$address", address.Trim().ToLowerInvariant()),
                    ("$name", (name ?? string.Empty).Trim()),
                    ("$now", now.ToUnixTimeSeconds()));
            }

            return 0;
        });
    }

    /// <summary>
    /// Entries matching what has been typed so far — by the start of the address, of the name,
    /// or of any word in the name — most used first, most recent breaking ties.
    /// </summary>
    /// <remarks>
    /// Word starts matter because people type surnames: "smi" should find "Alex Smith". The
    /// match is done in SQL against a lower-cased copy so a long list is still one indexed
    /// statement rather than a scan through every row in managed code.
    /// </remarks>
    public IReadOnlyList<Nickname> SuggestRecipients(string typed, int limit = 8)
    {
        var prefix = typed.Trim().ToLowerInvariant();
        if (prefix.Length == 0) return [];

        return _store.Query(
            """
            SELECT address, display_name, weight, last_used_utc
            FROM nickname_cache
            WHERE address LIKE $prefix || '%' ESCAPE '\'
               OR lower(display_name) LIKE $prefix || '%' ESCAPE '\'
               OR lower(display_name) LIKE '% ' || $prefix || '%' ESCAPE '\'
            ORDER BY weight DESC, last_used_utc DESC, address
            LIMIT $limit
            """,
            r => new Nickname(
                r.GetString(0),
                r.GetString(1),
                r.GetInt32(2),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3))),
            ("$prefix", EscapeLike(prefix)),
            ("$limit", limit));
    }

    /// <summary>Takes one entry out — the ✕ on a suggestion, for an address that was a mistake.</summary>
    public void ForgetRecipient(string address) => _store.Execute(
        "DELETE FROM nickname_cache WHERE address = $address",
        ("$address", address.Trim().ToLowerInvariant()));

    /// <summary>Empties the list. The Options page's button.</summary>
    public int ClearRecipients() => _store.Execute("DELETE FROM nickname_cache");

    /// <summary>How many entries the list holds, for the button's confirmation.</summary>
    public long RecipientCount() => _store.ScalarLong("SELECT count(*) FROM nickname_cache");

    /// <summary>
    /// Makes typed text safe as a LIKE prefix: the two wildcards and the escape itself are
    /// escaped, so a typed underscore matches an underscore rather than any character.
    /// </summary>
    internal static string EscapeLike(string text) => text
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    // ---- Outbox ---------------------------------------------------------------------------

    /// <summary>
    /// Queued messages ready to try. An item still in <c>sending</c> from a previous run is
    /// picked up too: the process that claimed it is gone, and leaving it claimed forever would
    /// silently strand the mail.
    /// </summary>
    public IReadOnlyList<OutboxItem> DueOutbox(long accountId, DateTimeOffset now) => _store.Query(
        """
        SELECT * FROM outbox
        WHERE account_id = $account
          AND state IN ('queued', 'sending')
          AND (next_try_utc IS NULL OR next_try_utc <= $now)
        ORDER BY queued_utc
        """,
        ReadOutbox, ("$account", accountId), ("$now", now.ToUnixTimeSeconds()));

    public IReadOnlyList<OutboxItem> Outbox(long accountId) => _store.Query(
        "SELECT * FROM outbox WHERE account_id = $account ORDER BY queued_utc",
        ReadOutbox, ("$account", accountId));

    public void SetOutboxState(long id, OutboxState state) => _store.Execute(
        "UPDATE outbox SET state = $state WHERE id = $id",
        ("$state", state.ToString().ToLowerInvariant()), ("$id", id));

    /// <summary>Puts an item back in the queue to try again later, counting the attempt.</summary>
    public void DeferOutbox(long id, DateTimeOffset nextTry, string reason) => _store.Execute(
        """
        UPDATE outbox
        SET state = 'queued', attempts = attempts + 1, next_try_utc = $next, last_error = $error
        WHERE id = $id
        """,
        ("$next", nextTry.ToUnixTimeSeconds()), ("$error", reason), ("$id", id));

    /// <summary>
    /// Holds an item back until a chosen time. Delayed delivery, not a retry.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="DeferOutbox"/> because that one counts an attempt and records a
    /// reason, both of which are failure bookkeeping. A message the user asked to send later has
    /// not failed at anything, and must not burn its retry budget waiting.
    /// </remarks>
    public void ScheduleOutbox(long id, DateTimeOffset notBefore) => _store.Execute(
        "UPDATE outbox SET state = 'queued', next_try_utc = $next WHERE id = $id",
        ("$next", notBefore.ToUnixTimeSeconds()), ("$id", id));

    /// <summary>Gives up on an item, keeping why so the user can be told.</summary>
    public void FailOutbox(long id, string reason) => _store.Execute(
        """
        UPDATE outbox
        SET state = 'failed', attempts = attempts + 1, next_try_utc = NULL, last_error = $error
        WHERE id = $id
        """,
        ("$error", reason), ("$id", id));

    /// <summary>Holds everything queued, which is what Work Offline does.</summary>
    public int HoldOutbox(long accountId) => _store.Execute(
        "UPDATE outbox SET state = 'held' WHERE account_id = $a AND state = 'queued'",
        ("$a", accountId));

    /// <summary>Releases what Work Offline held.</summary>
    public int ReleaseOutbox(long accountId) => _store.Execute(
        "UPDATE outbox SET state = 'queued', next_try_utc = NULL WHERE account_id = $a AND state = 'held'",
        ("$a", accountId));

    /// <summary>
    /// Takes a message back out of the outbox, if it is still waiting to go.
    /// </summary>
    /// <returns>The message as it was queued, or null if it is too late.</returns>
    /// <remarks>
    /// Undo Send (§12). The whole thing turns on the word <em>if</em>: the row is only removed
    /// when it is still queued and its hold has not expired, and the check and the delete happen
    /// in one transaction — so a send that started a moment ago wins, and the caller is told the
    /// message has gone rather than being handed bytes that are already on their way.
    /// <para>
    /// The blob is left behind. It is content-addressed and the sender may hold a reference to
    /// it, and an orphan costs a few kilobytes where a premature delete costs the message.
    /// </para>
    /// </remarks>
    public byte[]? WithdrawOutbox(long id, DateTimeOffset now) => _store.InTransaction(() =>
    {
        var blobId = _store.Query(
            """
            SELECT blob_id FROM outbox
            WHERE id = $id
              AND state = 'queued'
              AND next_try_utc IS NOT NULL
              AND next_try_utc > $now
            """,
            r => r.GetInt64(0),
            ("$id", id), ("$now", now.ToUnixTimeSeconds())).FirstOrDefault();

        if (blobId == 0) return null;

        var raw = LoadBlob(blobId);
        _store.Execute("DELETE FROM outbox WHERE id = $id", ("$id", id));

        return raw;
    });

    private static OutboxItem ReadOutbox(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),
        r.GetInt64(r.GetOrdinal("account_id")),
        r.GetInt64(r.GetOrdinal("blob_id")),
        Enum.TryParse<OutboxState>(r.GetString(r.GetOrdinal("state")), ignoreCase: true, out var s)
            ? s
            : OutboxState.Queued,
        r.GetInt32(r.GetOrdinal("attempts")),
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(r.GetOrdinal("queued_utc"))),
        r.IsDBNull(r.GetOrdinal("next_try_utc"))
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(r.GetOrdinal("next_try_utc"))),
        Nullable(r, "last_error"));

    // ---- Blobs ----------------------------------------------------------------------------

    /// <summary>
    /// Stores the raw message, deflated. Compression is recorded per blob rather than assumed,
    /// so the format can change later without a migration that rewrites every message.
    /// </summary>
    public long StoreBlob(byte[] raw)
    {
        var packed = Deflate(raw);
        var useCompression = packed.Length < raw.Length;

        _store.Execute(
            "INSERT INTO blobs (bytes, byte_length, compression) VALUES ($bytes, $length, $how)",
            ("$bytes", useCompression ? packed : raw),
            ("$length", raw.Length),
            ("$how", useCompression ? "deflate" : "none"));

        return _store.LastInsertId;
    }

    public byte[]? LoadBlob(long id) => _store.Query(
        "SELECT bytes, compression FROM blobs WHERE id = $id",
        r => r.GetString(1) == "deflate"
            ? Inflate((byte[])r["bytes"])
            : (byte[])r["bytes"],
        ("$id", id)).FirstOrDefault();

    /// <summary>The raw RFC822 behind a message, if it was kept.</summary>
    public byte[]? LoadRaw(long messageId)
    {
        var blobId = _store.Query(
            "SELECT blob_id FROM messages WHERE id = $id AND blob_id IS NOT NULL",
            r => r.GetInt64(0), ("$id", messageId)).FirstOrDefault();

        return blobId == 0 ? null : LoadBlob(blobId);
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        return output.ToArray();
    }

    private static byte[] Inflate(byte[] packed)
    {
        using var input = new MemoryStream(packed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    // ---- Reading rows -----------------------------------------------------------------------

    private const string FolderSelect =
        """
        SELECT f.*,
               (SELECT count(*) FROM messages m WHERE m.folder_id = f.id) AS total,
               (SELECT count(*) FROM messages m WHERE m.folder_id = f.id AND m.is_read = 0) AS unread
        FROM folders f
        """;

    private const string MessageSelect = "SELECT * FROM messages";

    private static Account ReadAccount(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("address")),
        r.GetString(r.GetOrdinal("display_name")),
        r.GetString(r.GetOrdinal("protocol")) == "imap" ? MailProtocol.Imap : MailProtocol.Pop3,
        r.GetInt32(r.GetOrdinal("ordinal")),
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(r.GetOrdinal("created_utc"))));

    private static Folder ReadFolder(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),
        r.GetInt64(r.GetOrdinal("account_id")),
        r.IsDBNull(r.GetOrdinal("parent_id")) ? null : r.GetInt64(r.GetOrdinal("parent_id")),
        r.GetString(r.GetOrdinal("name")),
        Enum.TryParse<FolderRole>(r.GetString(r.GetOrdinal("role")), ignoreCase: true, out var role)
            ? role
            : FolderRole.None,
        r.GetInt32(r.GetOrdinal("ordinal")))
    {
        Total = r.GetInt32(r.GetOrdinal("total")),
        Unread = r.GetInt32(r.GetOrdinal("unread")),
        ImapPath = Nullable(r, "imap_path"),
        Synced = r.GetInt32(r.GetOrdinal("synced")) != 0,
        UidValidity = NullableLong(r, "uidvalidity"),
        UidNext = NullableLong(r, "uidnext"),
        HighestModSeq = NullableLong(r, "highestmodseq"),
    };

    private static long? NullableLong(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    private static MessageSummary ReadMessage(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),
        r.GetInt64(r.GetOrdinal("folder_id")),
        Nullable(r, "server_uid"),
        Nullable(r, "message_id"),
        r.GetString(r.GetOrdinal("from_name")),
        r.GetString(r.GetOrdinal("from_address")),
        r.GetString(r.GetOrdinal("subject")),
        r.GetString(r.GetOrdinal("preview")),
        r.IsDBNull(r.GetOrdinal("sent_utc"))
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(r.GetOrdinal("sent_utc"))),
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(r.GetOrdinal("received_utc"))),
        r.GetInt64(r.GetOrdinal("size_bytes")),
        r.GetInt32(r.GetOrdinal("is_read")) != 0,
        r.GetInt32(r.GetOrdinal("is_flagged")) != 0,
        r.GetInt32(r.GetOrdinal("has_attachment")) != 0);

    private static string? Nullable(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static string Wire(MailProtocol protocol) => protocol switch
    {
        MailProtocol.Imap => "imap",
        _ => "pop3",
    };

    /// <summary>
    /// Groups replies with what they reply to. Subject-based for now; Phase 8's conversation
    /// view replaces it with References-header threading, which this deliberately does not
    /// pretend to be.
    /// </summary>
    internal static string ThreadKey(string subject)
    {
        var trimmed = subject.AsSpan().Trim();

        while (true)
        {
            var before = trimmed.Length;
            foreach (var prefix in (string[])["re:", "fw:", "fwd:"])
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed[prefix.Length..].TrimStart();
                }
            }

            if (trimmed.Length == before) break;
        }

        return trimmed.ToString().ToLowerInvariant();
    }
}
