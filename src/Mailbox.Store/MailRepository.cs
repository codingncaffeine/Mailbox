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
    public void MapFolder(long folderId, string imapPath, string name, long? parentId)
    {
        var before = _store.Query("SELECT imap_path FROM folders WHERE id = $id", r => r.IsDBNull(0) ? null : r.GetString(0), ("$id", folderId)).FirstOrDefault();
        _store.Execute(
            """
            UPDATE folders SET imap_path = $path, name = $name, parent_id = $parent WHERE id = $id
            """,
            ("$path", imapPath), ("$name", name), ("$parent", parentId), ("$id", folderId));

        // A server-side rule files by the server's name for the folder; a new name means a new script.
        if (!string.Equals(before, imapPath, StringComparison.Ordinal)) MarkSieveStale();
    }

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

    /// <summary>
    /// Renames a folder here, and re-paths it and everything under it when it has a server
    /// name — the server has already renamed the tree, or is about to.
    /// </summary>
    public void RenameFolder(long folderId, string name, string? newImapPath)
    {
        _store.InTransaction(() =>
        {
            var before = GetFolder(folderId);
            if (before is null) return 0;

            _store.Execute("UPDATE folders SET name = $name, imap_path = $path WHERE id = $id",
                ("$name", name), ("$path", newImapPath ?? before.ImapPath), ("$id", folderId));

            if (before.ImapPath is { } oldPath && newImapPath is { } newPath && oldPath != newPath)
            {
                foreach (var child in _store.Query(
                             "SELECT id, imap_path FROM folders WHERE account_id = $account AND imap_path LIKE $prefix || '%'",
                             r => (Id: r.GetInt64(0), Path: r.GetString(1)),
                             ("$account", before.AccountId), ("$prefix", oldPath + "/")))
                {
                    _store.Execute("UPDATE folders SET imap_path = $path WHERE id = $id",
                        ("$path", newPath + child.Path[oldPath.Length..]), ("$id", child.Id));
                }

                MarkSieveStale();
            }

            return 0;
        });
    }

    /// <summary>
    /// Puts a folder under another parent, or at the top, keeping its name; the folders under
    /// it come along. With a server name, the tree is re-pathed the way <see cref="RenameFolder"/>
    /// re-paths it — the server has already renamed the tree, or is about to.
    /// </summary>
    /// <returns>False when the move would put a folder inside itself, or the folder is unknown.</returns>
    public bool MoveFolder(long folderId, long? newParentId, string? newImapPath)
    {
        return _store.InTransaction(() =>
        {
            var before = GetFolder(folderId);
            if (before is null) return false;

            // Not into itself, nor into anything under it.
            for (var up = newParentId; up is { } id;)
            {
                if (id == folderId) return false;
                up = GetFolder(id)?.ParentId;
            }

            _store.Execute("UPDATE folders SET parent_id = $parent, imap_path = $path WHERE id = $id",
                ("$parent", newParentId), ("$path", newImapPath ?? before.ImapPath), ("$id", folderId));

            if (before.ImapPath is { } oldPath && newImapPath is { } newPath && oldPath != newPath)
            {
                foreach (var child in _store.Query(
                             "SELECT id, imap_path FROM folders WHERE account_id = $account AND imap_path LIKE $prefix || '%'",
                             r => (Id: r.GetInt64(0), Path: r.GetString(1)),
                             ("$account", before.AccountId), ("$prefix", oldPath + "/")))
                {
                    _store.Execute("UPDATE folders SET imap_path = $path WHERE id = $id",
                        ("$path", newPath + child.Path[oldPath.Length..]), ("$id", child.Id));
                }
            }

            MarkSieveStale();
            return true;
        });
    }

    /// <summary>Removes a folder, the folders under it, and everything in all of them.</summary>
    public void RemoveFolderTree(long folderId)
    {
        _store.InTransaction(() =>
        {
            foreach (var child in _store.Query("SELECT id FROM folders WHERE parent_id = $id", r => r.GetInt64(0), ("$id", folderId)))
            {
                RemoveFolderTree(child);
            }

            RemoveFolder(folderId);
            return 0;
        });
    }

    /// <summary>Removes a folder and everything in it. A server folder that has gone.</summary>
    /// <summary>
    /// Puts a parent's children in this order — Move Up, Move Down and Sort Subfolders A to Z.
    /// </summary>
    /// <remarks>
    /// The ordinal is local to a parent, and the folder pane reads it before the id, so a folder
    /// that has never been moved keeps the order it arrived in. Written whole rather than as a
    /// swap: two rows trading ordinals is the same statement twice and leaves a gap behind if a
    /// sibling was deleted between them, and the list is short enough that rewriting it costs
    /// nothing.
    /// </remarks>
    public void OrderFolders(IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _store.InTransaction(() =>
        {
            for (var i = 0; i < ids.Count; i++)
            {
                _store.Execute("UPDATE folders SET ordinal = $ordinal WHERE id = $id", ("$ordinal", i), ("$id", ids[i]));
            }

            return 0;
        });
    }

    public void RemoveFolder(long folderId) => _store.InTransaction(() =>
    {
        var ids = _store.Query(
            "SELECT id FROM messages WHERE folder_id = $folder", r => r.GetInt64(0), ("$folder", folderId));
        if (ids.Count > 0) DeleteRows(ids);
        _store.Execute("DELETE FROM folders WHERE id = $id", ("$id", folderId));
        MarkSieveStale();
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
                     size_bytes, is_read, is_flagged, has_attachment, importance, to_addresses, cc_addresses, expires_utc,
                     header_only, feed_link, feed_image, feed_words)
                VALUES
                    ($folder, $blob, $uid, $messageId, NULL, $thread,
                     $fromName, $fromAddress, $subject, $preview, $bodyText, $sent, $received,
                     $size, $read, $flagged, $attachment, $importance, $to, $cc, $expires,
                     $headerOnly, $feedLink, $feedImage, $feedWords)
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
                ("$attachment", message.HasAttachment ? 1 : 0),
                ("$importance", message.Importance),
                ("$to", string.Join(',', message.To)),
                ("$cc", string.Join(',', message.Cc)),
                ("$expires", message.Expires?.ToUnixTimeSeconds()),
                ("$headerOnly", message.HeaderOnly ? 1 : 0),
                ("$feedLink", message.FeedLink.Length > 0 ? message.FeedLink : null),
                ("$feedImage", message.FeedImage.Length > 0 ? message.FeedImage : null),
                ("$feedWords", message.FeedWords > 0 ? message.FeedWords : null));

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
    /// <summary>
    /// Every Message-ID the folder holds, for an importer's known-by-identity skip: re-running
    /// an interrupted import tops the folder up rather than doubling it.
    /// </summary>
    public HashSet<string> MessageIdsIn(long folderId)
        => [.. _store.Query(
            "SELECT message_id FROM messages WHERE folder_id = $folder AND message_id IS NOT NULL AND message_id <> ''",
            r => r.GetString(0), ("$folder", folderId))];

    public Dictionary<string, long> MessageIdsByServerUid(long folderId) => _store.Query(
        "SELECT server_uid, id FROM messages WHERE folder_id = $folder AND server_uid IS NOT NULL",
        r => (Uid: r.GetString(0), Id: r.GetInt64(1)), ("$folder", folderId))
        .ToDictionary(x => x.Uid, x => x.Id, StringComparer.Ordinal);

    /// <summary>
    /// The rows in a folder keyed by server id, with the Message-ID each one carries.
    /// </summary>
    /// <remarks>
    /// For a source that can revise something it has already sent, which is a feed: the server id
    /// says <em>which</em> article a row is, and the Message-ID — written from a fingerprint of
    /// what the article said — says <em>which version</em> of it. One query rather than a load of
    /// every message in the folder, because this runs over every entry of every feed on every
    /// poll.
    /// </remarks>
    public Dictionary<string, (long Id, string MessageId)> ServerUidIndex(long folderId) => _store.Query(
        """
        SELECT server_uid, id, coalesce(message_id, '')
          FROM messages
         WHERE folder_id = $folder AND server_uid IS NOT NULL
        """,
        r => (Uid: r.GetString(0), Id: r.GetInt64(1), MessageId: r.GetString(2)), ("$folder", folderId))
        .ToDictionary(x => x.Uid, x => (x.Id, x.MessageId), StringComparer.Ordinal);

    /// <summary>
    /// Puts a new version of a message over the old one, keeping the row.
    /// </summary>
    /// <remarks>
    /// The row keeps its id, so everything hung off it survives the revision: whether it was
    /// read, a flag, a category, a follow-up, its place in a conversation. That is the whole
    /// point — a publisher correcting a typo should not cost a reader the note they made on the
    /// article, and should not deliver the article to them a second time either.
    /// <para>
    /// The read state is deliberately not reset. A feed that rewrites its markup on every publish
    /// would otherwise mark everything unread again on every poll, which reads as a fault; a
    /// reader who wants to know an article changed has the date, which does move.
    /// </para>
    /// </remarks>
    public bool ReplaceMessage(long messageId, MessageSummary revised, byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(revised);
        ArgumentNullException.ThrowIfNull(raw);

        return _store.InTransaction(() =>
        {
            var previous = _store.Query(
                "SELECT blob_id FROM messages WHERE id = $id",
                r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$id", messageId)).FirstOrDefault();

            var blobId = StoreBlob(raw);
            var changed = _store.Execute(
                """
                UPDATE messages
                   SET blob_id = $blob, message_id = $messageId, subject = $subject, preview = $preview,
                       body_text = $bodyText, from_name = $fromName, from_address = $fromAddress,
                       sent_utc = $sent, size_bytes = $size, has_attachment = $attachment,
                       thread_key = $thread, feed_link = $feedLink, feed_image = $feedImage,
                       feed_words = $feedWords
                 WHERE id = $id
                """,
                ("$blob", blobId),
                ("$messageId", revised.MessageId),
                ("$subject", revised.Subject),
                ("$preview", revised.Preview),
                ("$bodyText", revised.BodyText),
                ("$fromName", revised.FromName),
                ("$fromAddress", revised.FromAddress),
                ("$sent", revised.Sent?.ToUnixTimeSeconds()),
                ("$size", raw.LongLength),
                ("$attachment", revised.HasAttachment ? 1 : 0),
                ("$thread", ThreadKey(revised.Subject)),
                ("$feedLink", revised.FeedLink.Length > 0 ? revised.FeedLink : null),
                ("$feedImage", revised.FeedImage.Length > 0 ? revised.FeedImage : null),
                ("$feedWords", revised.FeedWords > 0 ? revised.FeedWords : null),
                ("$id", messageId));

            if (changed == 0)
            {
                // The row went while this was being written. The blob has nothing pointing at it.
                _store.Execute("DELETE FROM blobs WHERE id = $id", ("$id", blobId));
                return false;
            }

            // The old body, if nothing else still refers to it. A revision that left it behind
            // would grow the store by the size of the article on every correction — and one that
            // deleted it unconditionally would empty a copy of the article filed in another
            // folder, because a copy is a second row over the same blob.
            if (previous is { } orphan && orphan != blobId) DeleteOrphanBlobs([orphan]);

            return true;
        });
    }

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
    /// it that cannot delete mail off a server the moment it is collected. The seen list counts
    /// too, so a message deleted here still comes off the server on the day it would have.
    /// </remarks>
    public HashSet<string> ServerUidsOlderThan(long folderId, DateTimeOffset cutoff) =>
    [
        .. _store.Query(
            """
            SELECT server_uid FROM messages
            WHERE folder_id = $folder AND server_uid IS NOT NULL AND received_utc < $cutoff
            UNION
            SELECT uidl FROM pop3_seen WHERE first_seen_utc < $cutoff
            """,
            r => r.GetString(0),
            ("$folder", folderId),
            ("$cutoff", cutoff.ToUnixTimeSeconds())),
    ];

    // ---- What POP3 has collected --------------------------------------------------------------
    //
    // A poll decides what is new by what it has seen, not by what it still holds: a message
    // deleted here for good must not come back as new mail on the next poll because the server,
    // asked to leave it, left it. So every collected UIDL is written here, and the list is
    // trimmed to what the server still lists after each poll.

    /// <summary>Every UIDL this account has ever collected and the server still lists.</summary>
    public HashSet<string> SeenUidls() =>
        [.. _store.Query("SELECT uidl FROM pop3_seen", r => r.GetString(0))];

    /// <summary>Records that a UIDL has been collected, whatever becomes of the message.</summary>
    public void RecordSeenUidl(string uidl, DateTimeOffset now) => _store.Execute(
        "INSERT OR IGNORE INTO pop3_seen (uidl, first_seen_utc) VALUES ($uidl, $now)",
        ("$uidl", uidl), ("$now", now.ToUnixTimeSeconds()));

    /// <summary>
    /// Forgets the UIDLs the server no longer lists. Run after a poll has the server's full
    /// list, so the table tracks the mailbox rather than its whole history.
    /// </summary>
    public int PruneSeenUidls(IReadOnlyCollection<string> stillOnServer)
    {
        if (stillOnServer.Count == 0) return _store.Execute("DELETE FROM pop3_seen");

        return _store.InTransaction(() =>
        {
            _store.Execute("CREATE TEMP TABLE IF NOT EXISTS pop3_listed (uidl TEXT PRIMARY KEY)");
            _store.Execute("DELETE FROM pop3_listed");
            foreach (var chunk in stillOnServer.Chunk(500))
            {
                _store.Execute(
                    $"INSERT OR IGNORE INTO pop3_listed (uidl) VALUES {string.Join(',', chunk.Select(u => "(" + Quote(u) + ")"))}");
            }

            var pruned = _store.Execute("DELETE FROM pop3_seen WHERE uidl NOT IN (SELECT uidl FROM pop3_listed)");
            _store.Execute("DELETE FROM pop3_listed");
            return pruned;
        });
    }

    /// <summary>
    /// A folder's messages, newest first — the ones on show. A snoozed message is not among
    /// them until its time comes; <see cref="Snoozed"/> lists those.
    /// </summary>
    public IReadOnlyList<MessageSummary> Messages(long folderId, int limit = 500) => Messages(folderId, null, limit);

    /// <summary>
    /// A folder's messages, or — with <paramref name="focused"/> set — only its Focused or its
    /// Other half, which is what the Inbox lists when Focused Inbox is on.
    /// </summary>
    public IReadOnlyList<MessageSummary> Messages(long folderId, bool? focused, int limit = 500)
    {
        var half = focused switch { true => " AND is_focused = 1", false => " AND is_focused = 0", null => string.Empty };
        return _store.Query(
            MessageSelect + " WHERE folder_id = $folder" + half + Awake + " ORDER BY received_utc DESC LIMIT $limit",
            ReadMessage, ("$folder", folderId), ("$limit", limit));
    }

    // ---- Ignore Conversation --------------------------------------------------------------------

    /// <summary>The thread key the list groups by, for a subject — the store's own rule, exposed.</summary>
    public static string ThreadKeyOf(string subject) => ThreadKey(subject ?? string.Empty);

    /// <summary>Whether a conversation is ignored, by its thread key.</summary>
    public bool IsIgnored(string threadKey) => _store.ScalarLong(
        "SELECT count(*) FROM ignored_conversations WHERE thread_key = $key", ("$key", threadKey)) > 0;

    /// <summary>Starts ignoring a conversation. Its messages are moved by the caller.</summary>
    public void Ignore(string threadKey, string subject, DateTimeOffset now) => _store.Execute(
        """
        INSERT INTO ignored_conversations (thread_key, subject, added_utc) VALUES ($key, $subject, $now)
        ON CONFLICT(thread_key) DO NOTHING
        """,
        ("$key", threadKey), ("$subject", subject), ("$now", now.ToUnixTimeSeconds()));

    /// <summary>Stop Ignoring: the conversation arrives in the Inbox again from now on.</summary>
    public void Unignore(string threadKey) => _store.Execute(
        "DELETE FROM ignored_conversations WHERE thread_key = $key", ("$key", threadKey));

    /// <summary>The messages of a conversation across the account's folders, oldest first — the Outbox aside.</summary>
    public IReadOnlyList<MessageSummary> MessagesInThread(string threadKey, bool includeDeleted = false)
    {
        var excluded = includeDeleted ? "'outbox'" : "'outbox', 'deleted'";
        return _store.Query(
            $"""
             SELECT m.* FROM messages m JOIN folders f ON f.id = m.folder_id
             WHERE m.thread_key = $key AND f.role NOT IN ({excluded})
             ORDER BY m.received_utc, m.id
             """,
            ReadMessage, ("$key", threadKey));
    }

    // ---- Focused Inbox (§12) --------------------------------------------------------------------

    /// <summary>Puts messages in Focused or Other.</summary>
    public int SetFocused(IReadOnlyCollection<long> messageIds, bool focused)
    {
        if (messageIds.Count == 0) return 0;

        return _store.Execute(
            $"UPDATE messages SET is_focused = $focused WHERE id IN ({Ids(messageIds)})",
            ("$focused", focused ? 1 : 0));
    }

    /// <summary>
    /// "Always move to Other/Focused": remembers the sender, and moves what is already in the
    /// Inbox from them. Returns how many existing messages moved.
    /// </summary>
    public int SetFocusOverride(string address, bool focused, DateTimeOffset now)
    {
        var key = address.Trim().ToLowerInvariant();
        if (key.Length == 0) return 0;

        return _store.InTransaction(() =>
        {
            _store.Execute(
                """
                INSERT INTO focus_overrides (address, focused, added_utc) VALUES ($address, $focused, $now)
                ON CONFLICT(address) DO UPDATE SET focused = excluded.focused, added_utc = excluded.added_utc
                """,
                ("$address", key), ("$focused", focused ? 1 : 0), ("$now", now.ToUnixTimeSeconds()));

            return _store.Execute(
                """
                UPDATE messages SET is_focused = $focused
                WHERE lower(from_address) = $address AND is_focused <> $focused
                  AND folder_id IN (SELECT id FROM folders WHERE role = 'inbox')
                """,
                ("$focused", focused ? 1 : 0), ("$address", key));
        });
    }

    /// <summary>What the reader has said about a sender: true Focused, false Other, null nothing.</summary>
    public bool? FocusOverride(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;

        return _store.Query(
            "SELECT focused FROM focus_overrides WHERE address = $address",
            r => r.GetInt32(0) != 0, ("$address", address.Trim().ToLowerInvariant()))
            .Select(f => (bool?)f).FirstOrDefault();
    }

    /// <summary>Whether the reader has ever written to this address — the Auto-Complete List knows.</summary>
    public bool HasWrittenTo(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        return _store.ScalarLong(
            "SELECT count(*) FROM nickname_cache WHERE address = $address",
            ("$address", address.Trim().ToLowerInvariant())) > 0;
    }

    /// <summary>The clause that keeps a snoozed message out of a list, on the store's own clock.</summary>
    private const string Awake = " AND (snooze_until IS NULL OR snooze_until <= strftime('%s','now'))";

    // ---- Snooze (§12) -----------------------------------------------------------------------
    //
    // A snoozed message leaves the list and comes back at the set time, unread and at the top —
    // its received time is moved to the moment it returned, which is what puts it there. Local
    // only: the server never hears of it, and the message stays in its folder throughout.

    /// <summary>The messages of a folder that are snoozed, soonest to return first.</summary>
    public IReadOnlyList<MessageSummary> Snoozed(long folderId) => _store.Query(
        MessageSelect + " WHERE folder_id = $folder AND snooze_until IS NOT NULL AND snooze_until > strftime('%s','now')"
        + " ORDER BY snooze_until, received_utc DESC",
        ReadMessage, ("$folder", folderId));

    /// <summary>Hides messages until <paramref name="until"/>.</summary>
    public int Snooze(IReadOnlyCollection<long> messageIds, DateTimeOffset until)
    {
        if (messageIds.Count == 0) return 0;

        return _store.Execute(
            $"UPDATE messages SET snooze_until = $until WHERE id IN ({Ids(messageIds)})",
            ("$until", until.ToUnixTimeSeconds()));
    }

    /// <summary>Brings messages back now, without waiting: they return unread and at the top.</summary>
    public int Unsnooze(IReadOnlyCollection<long> messageIds, DateTimeOffset now)
    {
        if (messageIds.Count == 0) return 0;

        return _store.Execute(
            $"""
             UPDATE messages SET snooze_until = NULL, is_read = 0, received_utc = $now
             WHERE id IN ({Ids(messageIds)}) AND snooze_until IS NOT NULL
             """,
            ("$now", now.ToUnixTimeSeconds()));
    }

    /// <summary>
    /// Puts a message's snooze back exactly as it was, for Undo.
    /// </summary>
    /// <remarks>
    /// Neither <see cref="Snooze"/> nor <see cref="Unsnooze"/> is the other's inverse: unsnoozing
    /// also marks a message unread and moves its arrival to now, so that it comes back at the top
    /// of the folder where the reader will see it. Taking either of them back therefore means
    /// writing the three columns together, with the values the row carried beforehand, rather
    /// than calling the opposite command and leaving two of them changed. Local, like the rest of
    /// snoozing: the server is never told.
    /// </remarks>
    public int RestoreSnooze(long messageId, DateTimeOffset? until, bool read, DateTimeOffset received)
        => _store.Execute(
            "UPDATE messages SET snooze_until = $until, is_read = $read, received_utc = $received WHERE id = $id",
            ("$until", until?.ToUnixTimeSeconds()),
            ("$read", read ? 1 : 0),
            ("$received", received.ToUnixTimeSeconds()),
            ("$id", messageId));

    /// <summary>
    /// Brings back every message whose time has come: unread, at the top of its folder. Called
    /// on a timer; returns what woke, for the toast, and nothing when nothing did.
    /// </summary>
    public IReadOnlyList<(long FolderId, long MessageId)> WakeSnoozed(DateTimeOffset now) => _store.InTransaction(() =>
    {
        var due = _store.Query(
            "SELECT folder_id, id FROM messages WHERE snooze_until IS NOT NULL AND snooze_until <= $now",
            r => (r.GetInt64(0), r.GetInt64(1)), ("$now", now.ToUnixTimeSeconds()));

        if (due.Count > 0)
        {
            _store.Execute(
                $"""
                 UPDATE messages SET snooze_until = NULL, is_read = 0, received_utc = $now
                 WHERE id IN ({Ids(due.Select(d => d.Item2))})
                 """,
                ("$now", now.ToUnixTimeSeconds()));
        }

        return (IReadOnlyList<(long, long)>)due;
    });

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
    /// Flags messages for follow-up with an optional due date, clearing any completed mark.
    /// </summary>
    /// <remarks>
    /// A follow-up is a flag with a date and a done state — the reference's flag menu. The flag
    /// itself is <c>is_flagged</c>, so it journals to an IMAP server like any flag; the due date
    /// and the completed state are local, because IMAP has no notion of either.
    /// </remarks>
    public void SetFollowUp(IReadOnlyCollection<long> messageIds, DateTimeOffset? due)
    {
        if (messageIds.Count == 0) return;

        _store.InTransaction(() =>
        {
            _store.Execute(
                $"""
                 UPDATE messages SET is_flagged = 1, follow_up_complete = 0, follow_up_due = $due
                 WHERE id IN ({Ids(messageIds)})
                 """,
                ("$due", due?.ToUnixTimeSeconds()));
            JournalFlag(messageIds, SyncFlag.Flagged, true);
            return 0;
        });
    }

    /// <summary>
    /// The Custom flag dialog's whole flag at once: what it says, when it starts, when it is due,
    /// and when to be reminded. Sets the flag as <see cref="SetFollowUp"/> does.
    /// </summary>
    public void SetCustomFollowUp(IReadOnlyCollection<long> messageIds, string? type,
        DateTimeOffset? start, DateTimeOffset? due, DateTimeOffset? reminder)
    {
        if (messageIds.Count == 0) return;

        _store.InTransaction(() =>
        {
            _store.Execute(
                $"""
                 UPDATE messages SET is_flagged = 1, follow_up_complete = 0, follow_up_type = $type,
                     follow_up_start = $start, follow_up_due = $due, reminder_utc = $reminder
                 WHERE id IN ({Ids(messageIds)})
                 """,
                ("$type", type), ("$start", start?.ToUnixTimeSeconds()),
                ("$due", due?.ToUnixTimeSeconds()), ("$reminder", reminder?.ToUnixTimeSeconds()));
            JournalFlag(messageIds, SyncFlag.Flagged, true);
            return 0;
        });
    }

    /// <summary>Marks a follow-up complete: the flag clears, and a check takes its place. The reminder goes with it.</summary>
    public void CompleteFollowUp(IReadOnlyCollection<long> messageIds)
    {
        if (messageIds.Count == 0) return;

        _store.InTransaction(() =>
        {
            _store.Execute(
                $"UPDATE messages SET is_flagged = 0, follow_up_complete = 1, reminder_utc = NULL WHERE id IN ({Ids(messageIds)})");
            JournalFlag(messageIds, SyncFlag.Flagged, false);
            return 0;
        });
    }

    /// <summary>Clears a follow-up entirely — no flag, no dates, no reminder, no completed mark.</summary>
    public void ClearFollowUp(IReadOnlyCollection<long> messageIds)
    {
        if (messageIds.Count == 0) return;

        _store.InTransaction(() =>
        {
            _store.Execute(
                $"""
                 UPDATE messages SET is_flagged = 0, follow_up_complete = 0, follow_up_due = NULL,
                     follow_up_type = NULL, follow_up_start = NULL, reminder_utc = NULL
                 WHERE id IN ({Ids(messageIds)})
                 """);
            JournalFlag(messageIds, SyncFlag.Flagged, false);
            return 0;
        });
    }

    // ---- Reminders ---------------------------------------------------------------------------
    //
    // A flag may carry a time to be reminded at. The Reminders window lists what is due;
    // Dismiss clears the time, Snooze pushes it on. Nothing here fires — the shell's timer asks.

    /// <summary>Sets or clears (null) the reminder on flagged messages.</summary>
    public void SetReminder(IReadOnlyCollection<long> messageIds, DateTimeOffset? when)
    {
        if (messageIds.Count == 0) return;

        _store.Execute(
            $"UPDATE messages SET reminder_utc = $when WHERE id IN ({Ids(messageIds)})",
            ("$when", when?.ToUnixTimeSeconds()));
    }

    /// <summary>The flagged messages whose reminder time has come, soonest first.</summary>
    public IReadOnlyList<MessageSummary> DueReminders(DateTimeOffset now) => _store.Query(
        MessageSelect + " WHERE reminder_utc IS NOT NULL AND reminder_utc <= $now AND is_flagged = 1 ORDER BY reminder_utc",
        ReadMessage, ("$now", now.ToUnixTimeSeconds()));

    /// <summary>
    /// Every message flagged for follow-up, which is what the to-do list holds beside the tasks.
    /// </summary>
    /// <remarks>
    /// The reference's own To-Do List is tasks and flagged mail together, and this is the mail
    /// half. Deleted messages are left out — a flag on something in the bin is not outstanding —
    /// and a completed follow-up comes only when it is asked for, exactly as a finished task does.
    /// </remarks>
    public IReadOnlyList<MessageSummary> FlaggedMessages(bool includeComplete = false, int limit = 500)
        => _store.Query(
            "SELECT m.* FROM messages m JOIN folders f ON f.id = m.folder_id WHERE "
            + (includeComplete
                ? "(m.is_flagged = 1 OR m.follow_up_complete = 1)"
                : "m.is_flagged = 1 AND m.follow_up_complete = 0")
            + " AND f.role NOT IN ('outbox', 'deleted', 'junk')"
            + " ORDER BY COALESCE(m.follow_up_due, m.received_utc) LIMIT $limit",
            ReadMessage,
            ("$limit", limit));

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

    /// <summary>Sets the importance the list's column shows. Local: the message's own header is not rewritten.</summary>
    public int SetImportance(IReadOnlyCollection<long> messageIds, int importance)
    {
        if (messageIds.Count == 0) return 0;

        return _store.Execute(
            $"UPDATE messages SET importance = $importance WHERE id IN ({Ids(messageIds)})",
            ("$importance", Math.Clamp(importance, 0, 2)));
    }

    // ---- Headers without their messages (Send/Receive's Server group) ----------------------

    /// <summary>
    /// Marks headers for download, or takes the mark off. Returns how many rows changed.
    /// </summary>
    /// <remarks>
    /// Only a header can be marked: a message that is already here has nothing left to fetch,
    /// and a mark on it would be a promise the next send/receive could not keep.
    /// </remarks>
    public int MarkForDownload(IReadOnlyCollection<long> messageIds, bool marked)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0) return 0;

        return _store.Execute(
            $"UPDATE messages SET marked_download = $marked WHERE header_only = 1 AND id IN ({Ids(messageIds)})",
            ("$marked", marked ? 1 : 0));
    }

    /// <summary>The headers in a folder whose messages have not been fetched.</summary>
    public IReadOnlyList<MessageSummary> Headers(long folderId) => _store.Query(
        MessageSelect + " WHERE folder_id = $folder AND header_only = 1 ORDER BY received_utc DESC",
        ReadMessage, ("$folder", folderId));

    /// <summary>
    /// Every header marked for download in an account, with the folder each is in.
    /// </summary>
    /// <remarks>
    /// Across the account rather than one folder: the reference's Process Marked Headers works
    /// on what has been marked wherever it was marked, and a reader who marked in three folders
    /// expects one press to fetch all three.
    /// </remarks>
    public IReadOnlyList<(Folder Folder, MessageSummary Message)> MarkedForDownload(long accountId)
    {
        var folders = Folders(accountId).ToDictionary(f => f.Id, f => f);

        return
        [
            .. _store.Query(
                    MessageSelect + " WHERE header_only = 1 AND marked_download = 1"
                    + " AND folder_id IN (SELECT id FROM folders WHERE account_id = $account)"
                    + " ORDER BY received_utc DESC",
                    ReadMessage, ("$account", accountId))
                .Where(m => folders.ContainsKey(m.FolderId))
                .Select(m => (folders[m.FolderId], m)),
        ];
    }

    /// <summary>
    /// Puts a message under a header: the raw bytes, and the fields only the body could fill.
    /// </summary>
    /// <remarks>
    /// The row stays where it is and keeps its id, so a flag, a category or a follow-up put on
    /// the header while it was only a header survives the message arriving under it.
    /// </remarks>
    public bool FillHeader(long messageId, MessageSummary filled, byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(filled);
        ArgumentNullException.ThrowIfNull(raw);

        return _store.InTransaction(() =>
        {
            var blobId = StoreBlob(raw);
            var changed = _store.Execute(
                """
                UPDATE messages
                   SET blob_id = $blob, preview = $preview, body_text = $bodyText,
                       size_bytes = $size, has_attachment = $attachment, importance = $importance,
                       to_addresses = $to, cc_addresses = $cc, expires_utc = $expires,
                       header_only = 0, marked_download = 0
                 WHERE id = $id AND header_only = 1
                """,
                ("$blob", blobId),
                ("$preview", filled.Preview),
                ("$bodyText", filled.BodyText),
                ("$size", raw.LongLength),
                ("$attachment", filled.HasAttachment ? 1 : 0),
                ("$importance", filled.Importance),
                ("$to", string.Join(',', filled.To)),
                ("$cc", string.Join(',', filled.Cc)),
                ("$expires", filled.Expires?.ToUnixTimeSeconds()),
                ("$id", messageId));

            // Nothing filled means the row was not a header any more — another window, or a
            // sync, got there first. The blob written above then has nothing pointing at it.
            if (changed == 0) _store.Execute("DELETE FROM blobs WHERE id = $id", ("$id", blobId));

            return changed != 0;
        });
    }

    /// <summary>
    /// Copies messages into another folder: a new row over the same raw bytes, with the read and
    /// flagged state of the original. On a synced folder the copy is appended to the server.
    /// </summary>
    /// <returns>
    /// The ids of the copies, in the order they were made. Taking a copy back means deleting the
    /// rows it made, and nothing else can name them: a copy is a new row, not a moved one.
    /// </returns>
    public IReadOnlyList<long> CopyMessages(IReadOnlyCollection<long> messageIds, long toFolderId)
    {
        if (messageIds.Count == 0) return [];

        return _store.InTransaction<IReadOnlyList<long>>(() =>
        {
            var copies = new List<long>(messageIds.Count);
            foreach (var id in messageIds)
            {
                var row = _store.Query(
                    """
                    SELECT blob_id, message_id, from_name, from_address, subject, preview, body_text, sent_utc,
                           received_utc, size_bytes, is_read, is_flagged, has_attachment, importance, to_addresses, cc_addresses,
                           feed_link, feed_image
                    FROM messages WHERE id = $id
                    """,
                    r => new object?[]
                    {
                        r.IsDBNull(0) ? null : r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3),
                        r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetInt64(7),
                        r.GetInt64(8), r.GetInt64(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13),
                        r.GetString(14), r.GetString(15),
                        r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetString(17),
                    },
                    ("$id", id)).FirstOrDefault();
                if (row is null) continue;

                // The blob is shared: two rows over one set of bytes. Deleting either row keeps
                // the bytes for the holding area, and a purge takes them only when nothing else
                // points at them — the foreign key refuses otherwise.
                _store.Execute(
                    """
                    INSERT INTO messages
                        (folder_id, blob_id, server_uid, message_id, thread_key, from_name, from_address, subject, preview,
                         body_text, sent_utc, received_utc, size_bytes, is_read, is_flagged, has_attachment, importance,
                         to_addresses, cc_addresses,
                         feed_link, feed_image)
                    VALUES ($folder, $blob, NULL, $mid, $thread, $fromName, $fromAddress, $subject, $preview,
                            $body, $sent, $received, $size, $read, $flagged, $attachment, $importance, $to, $cc,
                            $feedLink, $feedImage)
                    """,
                    ("$folder", toFolderId), ("$blob", row[0]), ("$mid", row[1]), ("$thread", ThreadKey((string)row[4]!)),
                    ("$fromName", row[2]), ("$fromAddress", row[3]), ("$subject", row[4]), ("$preview", row[5]),
                    ("$body", row[6]), ("$sent", row[7]), ("$received", row[8]), ("$size", row[9]), ("$read", row[10]),
                    ("$flagged", row[11]), ("$attachment", row[12]), ("$importance", row[13]), ("$to", row[14]), ("$cc", row[15]),
                    ("$feedLink", row[16]), ("$feedImage", row[17]));

                var newId = _store.LastInsertId;
                if (row[0] is not null && IsSyncedFolder(toFolderId)) JournalAppend(toFolderId, newId);
                copies.Add(newId);
            }

            return copies;
        });
    }

    /// <summary>
    /// Deletes many for good — as far as the folders are concerned. The rows go, and on IMAP the
    /// server is told; the raw bytes and the row's own columns are kept in the recoverable
    /// holding area (§11) for the retention window, so Recover Deleted Items can put a message
    /// back where it was.
    /// </summary>
    public int DeleteMessages(IReadOnlyCollection<long> messageIds)
    {
        if (messageIds.Count == 0) return 0;

        return _store.InTransaction(() =>
        {
            foreach (var id in messageIds) JournalDelete(id);
            KeepRecoverable(messageIds, DateTimeOffset.UtcNow);
            return DeleteRows(messageIds, keepBlobs: true);
        });
    }

    /// <summary>The rows and their blobs, no journal. What every delete ends in.</summary>
    private int DeleteRows(IReadOnlyCollection<long> messageIds, bool keepBlobs = false)
    {
        var list = Ids(messageIds);
        var blobs = keepBlobs
            ? []
            : _store.Query(
                $"SELECT blob_id FROM messages WHERE id IN ({list}) AND blob_id IS NOT NULL",
                r => r.GetInt64(0));

        var removed = _store.Execute($"DELETE FROM messages WHERE id IN ({list})");

        if (blobs.Count > 0) DeleteOrphanBlobs(blobs);

        return removed;
    }

    /// <summary>
    /// Deletes blobs nothing points at any more. A copied message shares its bytes with the
    /// original, and the holding area holds them too, so a blob goes only when the last row
    /// over it has.
    /// </summary>
    private void DeleteOrphanBlobs(IReadOnlyCollection<long> blobIds)
    {
        if (blobIds.Count == 0) return;

        _store.Execute(
            $"""
             DELETE FROM blobs WHERE id IN ({Ids(blobIds)})
               AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.blob_id = blobs.id)
               AND NOT EXISTS (SELECT 1 FROM recoverable r WHERE r.blob_id = blobs.id)
               AND NOT EXISTS (SELECT 1 FROM outbox o WHERE o.blob_id = blobs.id)
             """);
    }

    // ---- Recover Deleted Items (§11) ------------------------------------------------------------

    /// <summary>Copies what a row says about itself into the holding area, blob and all.</summary>
    private void KeepRecoverable(IReadOnlyCollection<long> messageIds, DateTimeOffset now) => _store.Execute(
        $"""
         INSERT INTO recoverable
             (blob_id, original_folder_id, original_folder_name, message_id, from_name, from_address,
              subject, preview, body_text, sent_utc, received_utc, size_bytes, is_read, is_flagged,
              has_attachment, deleted_utc)
         SELECT m.blob_id, m.folder_id, f.name, m.message_id, m.from_name, m.from_address,
                m.subject, m.preview, m.body_text, m.sent_utc, m.received_utc, m.size_bytes, m.is_read,
                m.is_flagged, m.has_attachment, $now
         FROM messages m JOIN folders f ON f.id = m.folder_id
         WHERE m.id IN ({Ids(messageIds)}) AND m.blob_id IS NOT NULL
         """,
        ("$now", now.ToUnixTimeSeconds()));

    /// <summary>What can still be recovered, most recently deleted first.</summary>
    public IReadOnlyList<RecoverableMessage> Recoverable() => _store.Query(
        """
        SELECT id, original_folder_id, original_folder_name, from_name, from_address, subject,
               received_utc, deleted_utc, size_bytes
        FROM recoverable ORDER BY deleted_utc DESC, id DESC
        """,
        r => new RecoverableMessage(
            r.GetInt64(0),
            r.IsDBNull(1) ? null : r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(7)),
            r.GetInt64(8)));

    public long RecoverableCount() => _store.ScalarLong("SELECT count(*) FROM recoverable");

    /// <summary>
    /// Puts recoverable messages back: into the folder they came from, or the one named
    /// <paramref name="fallbackFolderId"/> when that folder has gone. Returns how many came back.
    /// </summary>
    /// <remarks>
    /// A re-file rather than an undelete: the row is written afresh from the kept columns and
    /// the kept blob, with no server id, so on IMAP it is appended to the server like a message
    /// made here. It comes back with the read and flagged state it had.
    /// </remarks>
    public int Restore(IReadOnlyCollection<long> recoverableIds, long fallbackFolderId)
    {
        if (recoverableIds.Count == 0) return 0;

        return _store.InTransaction(() =>
        {
            var restored = 0;
            foreach (var id in recoverableIds)
            {
                var row = _store.Query(
                    """
                    SELECT blob_id, original_folder_id, message_id, from_name, from_address, subject, preview,
                           body_text, sent_utc, received_utc, size_bytes, is_read, is_flagged, has_attachment
                    FROM recoverable WHERE id = $id
                    """,
                    r => new
                    {
                        BlobId = r.GetInt64(0),
                        FolderId = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                        MessageId = r.IsDBNull(2) ? null : r.GetString(2),
                        FromName = r.GetString(3),
                        FromAddress = r.GetString(4),
                        Subject = r.GetString(5),
                        Preview = r.GetString(6),
                        Body = r.GetString(7),
                        Sent = r.IsDBNull(8) ? (long?)null : r.GetInt64(8),
                        Received = r.GetInt64(9),
                        Size = r.GetInt64(10),
                        Read = r.GetInt32(11) != 0,
                        Flagged = r.GetInt32(12) != 0,
                        Attachment = r.GetInt32(13) != 0,
                    },
                    ("$id", id)).FirstOrDefault();
                if (row is null) continue;

                var target = row.FolderId is { } original && GetFolder(original) is not null ? original : fallbackFolderId;

                _store.Execute(
                    """
                    INSERT INTO messages
                        (folder_id, blob_id, server_uid, message_id, thread_key, from_name, from_address, subject,
                         preview, body_text, sent_utc, received_utc, size_bytes, is_read, is_flagged, has_attachment)
                    VALUES ($folder, $blob, NULL, $mid, $thread, $fromName, $fromAddress, $subject,
                            $preview, $body, $sent, $received, $size, $read, $flagged, $attachment)
                    """,
                    ("$folder", target), ("$blob", row.BlobId), ("$mid", row.MessageId),
                    ("$thread", ThreadKey(row.Subject)), ("$fromName", row.FromName), ("$fromAddress", row.FromAddress),
                    ("$subject", row.Subject), ("$preview", row.Preview), ("$body", row.Body), ("$sent", row.Sent),
                    ("$received", row.Received), ("$size", row.Size), ("$read", row.Read ? 1 : 0),
                    ("$flagged", row.Flagged ? 1 : 0), ("$attachment", row.Attachment ? 1 : 0));

                var messageId = _store.LastInsertId;
                if (IsSyncedFolder(target)) JournalAppend(target, messageId);

                _store.Execute("DELETE FROM recoverable WHERE id = $id", ("$id", id));
                restored++;
            }

            return restored;
        });
    }

    /// <summary>Removes recoverable messages for good, blobs and all — Purge Selected Items.</summary>
    public int Purge(IReadOnlyCollection<long> recoverableIds)
    {
        if (recoverableIds.Count == 0) return 0;

        return _store.InTransaction(() =>
        {
            var list = Ids(recoverableIds);
            var blobs = _store.Query($"SELECT blob_id FROM recoverable WHERE id IN ({list})", r => r.GetInt64(0));
            var removed = _store.Execute($"DELETE FROM recoverable WHERE id IN ({list})");
            if (blobs.Count > 0) DeleteOrphanBlobs(blobs);
            return removed;
        });
    }

    /// <summary>The retention window: everything deleted before <paramref name="cutoff"/> goes for good.</summary>
    public int PurgeRecoverableOlderThan(DateTimeOffset cutoff)
    {
        var due = _store.Query(
            "SELECT id FROM recoverable WHERE deleted_utc < $cutoff", r => r.GetInt64(0),
            ("$cutoff", cutoff.ToUnixTimeSeconds()));
        return Purge(due);
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

    /// <summary>
    /// How many times an operation is replayed to the server before it is left alone.
    /// </summary>
    /// <remarks>
    /// The attempts were counted and never read, so an op the server will always refuse — a
    /// move to a folder that no longer exists, a flag on a message whose id will not parse —
    /// was replayed on every send/receive for ever, and nothing said so. Five rides out the
    /// failures that do pass: a dropped connection, a token being refreshed, a server restart.
    /// </remarks>
    public const int MaxOpAttempts = 5;

    /// <summary>
    /// The operations still to be played to the server, oldest first.
    /// </summary>
    /// <remarks>
    /// Ones that have failed <see cref="MaxOpAttempts"/> times are not offered. They stay in the
    /// table with what went wrong, and <see cref="StuckOps"/> reports them.
    /// </remarks>
    public IReadOnlyList<SyncOp> PendingOps() => _store.Query(
        SyncOpSelect + " WHERE attempts < $max ORDER BY id", ReadSyncOp, ("$max", MaxOpAttempts));

    /// <summary>The operations the server has refused often enough to be left alone.</summary>
    public IReadOnlyList<SyncOp> StuckOps() => _store.Query(
        SyncOpSelect + " WHERE attempts >= $max ORDER BY id", ReadSyncOp, ("$max", MaxOpAttempts));

    /// <summary>Puts a stuck operation back, for a reader who has fixed what refused it.</summary>
    public void RetryOp(long id)
        => _store.Execute("UPDATE sync_ops SET attempts = 0 WHERE id = $id", ("$id", id));

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
        => Search(Mailbox.Core.Search.SearchQuery.Parse(term), folderId, limit);

    /// <summary>
    /// Search, with the reference's keywords: the words go to the full-text index — a
    /// <c>from:</c>, <c>subject:</c> or <c>body:</c> word to that column of it — and the rest
    /// become predicates on the row. A query with no words at all is a plain scan of the row's
    /// columns, newest first.
    /// </summary>
    public IReadOnlyList<MessageSummary> Search(Mailbox.Core.Search.SearchQuery query, long? folderId = null, int limit = 200)
        => Search(query, folderId is { } one ? [one] : null, limit);

    /// <summary>
    /// The same search, over a set of folders rather than one.
    /// </summary>
    /// <remarks>
    /// For a scope that spans several — every feed, everything under one heading. One query
    /// rather than one per folder: a reader with fifty subscriptions typing into a search box
    /// would otherwise fire fifty queries per keystroke.
    /// <para>
    /// An empty set is a scope with nothing in it, which is not the same as no scope at all and
    /// must not quietly become a search of the whole store.
    /// </para>
    /// </remarks>
    public IReadOnlyList<MessageSummary> Search(
        Mailbox.Core.Search.SearchQuery query, IReadOnlyCollection<long>? folderIds, int limit = 200)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsEmpty) return [];
        if (folderIds is { Count: 0 }) return [];

        var where = new List<string>();
        var parameters = new List<(string, object?)> { ("$limit", limit) };

        if (folderIds is { Count: 1 })
        {
            where.Add("m.folder_id = $folder");
            parameters.Add(("$folder", folderIds.First()));
        }
        else if (folderIds is { Count: > 1 })
        {
            where.Add($"m.folder_id IN ({Ids(folderIds)})");
        }

        // The full-text half. FTS5's column filter — subject:word — is what from:, subject: and
        // body: turn into; the from column pair is (from_name OR from_address).
        var match = new List<string>();
        match.AddRange(query.Words.Select(Quote1));
        match.AddRange(query.Subject.Select(w => "subject:" + Quote1(w)));
        match.AddRange(query.Body.Select(w => "body:" + Quote1(w)));
        match.AddRange(query.From.Select(w => "{from_name from_address}:" + Quote1(w)));

        var sql = match.Count > 0
            ? "SELECT m.* FROM messages m JOIN messages_fts ON messages_fts.rowid = m.id WHERE messages_fts MATCH $term"
            : "SELECT m.* FROM messages m WHERE 1";
        // Explicit ANDs: FTS5 accepts an implicit AND between plain terms but not before a
        // column-set clause, and a search for "budget from:alice" writes exactly that.
        if (match.Count > 0) parameters.Add(("$term", string.Join(" AND ", match)));

        // The rest, on the row.
        var n = 0;
        string P(object? value) { var name = "$p" + n++; parameters.Add((name, value)); return name; }

        foreach (var w in query.To) where.Add($"m.to_addresses LIKE '%' || {P(w.ToLowerInvariant())} || '%'");
        foreach (var w in query.Cc) where.Add($"m.cc_addresses LIKE '%' || {P(w.ToLowerInvariant())} || '%'");
        foreach (var c in query.Categories)
        {
            where.Add($"EXISTS (SELECT 1 FROM message_categories mc JOIN categories c ON c.id = mc.category_id WHERE mc.message_id = m.id AND lower(c.name) = {P(c.ToLowerInvariant())})");
        }

        if (query.HasAttachment is { } attachment) where.Add($"m.has_attachment = {(attachment ? 1 : 0)}");
        if (query.IsRead is { } read) where.Add($"m.is_read = {(read ? 1 : 0)}");
        if (query.IsFlagged is { } flagged) where.Add($"m.is_flagged = {(flagged ? 1 : 0)}");
        if (query.Importance is { } importance) where.Add($"m.importance = {importance}");
        if (query.Size is { } size)
        {
            where.Add(size.Bound switch
            {
                Mailbox.Core.Search.Bound.After => $"m.size_bytes > {P(size.Bytes)}",
                Mailbox.Core.Search.Bound.Before => $"m.size_bytes < {P(size.Bytes)}",
                _ => $"m.size_bytes BETWEEN {P(size.Bytes * 9 / 10)} AND {P(size.Bytes * 11 / 10)}",
            });
        }
        if (query.Received is { } received)
        {
            if (received.After is { } a) where.Add($"m.received_utc >= {P(a.ToUnixTimeSeconds())}");
            if (received.Before is { } b) where.Add($"m.received_utc < {P(b.ToUnixTimeSeconds())}");
        }
        if (query.Sent is { } sent)
        {
            if (sent.After is { } a) where.Add($"m.sent_utc >= {P(a.ToUnixTimeSeconds())}");
            if (sent.Before is { } b) where.Add($"m.sent_utc < {P(b.ToUnixTimeSeconds())}");
        }
        if (query.Due is { } due)
        {
            where.Add("m.follow_up_due IS NOT NULL");
            if (due.After is { } a) where.Add($"m.follow_up_due >= {P(a.ToUnixTimeSeconds())}");
            if (due.Before is { } b) where.Add($"m.follow_up_due < {P(b.ToUnixTimeSeconds())}");
        }

        var clause = where.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", where);
        var order = match.Count > 0 ? "ORDER BY bm25(messages_fts), m.received_utc DESC" : "ORDER BY m.received_utc DESC";

        return _store.Query($"{sql}{clause} {order} LIMIT $limit", ReadMessage, [.. parameters]);
    }

    /// <summary>One word or phrase as an FTS5 literal: quoted, so a stray quote or bracket is text.</summary>
    private static string Quote1(string word) => '"' + word.Replace("\"", "\"\"") + '"';

    // ---- The junk lists -----------------------------------------------------------------------
    //
    // Five lists, one table each, and one shape: an entry is an address, or a whole domain
    // written as "@example.com", both lower-cased. A sender matches a list when its address is
    // on it or its domain is. Lists win over the classifier in both directions (§7.8), and the
    // safe-senders list doubles as the reading pane's "always allow images from this sender".

    /// <summary>
    /// The list entries a sender address matches: itself, and its domain in the "@domain" form.
    /// </summary>
    internal static (string Address, string? Domain) ListKeys(string address)
    {
        var trimmed = address.Trim().ToLowerInvariant();
        var at = trimmed.LastIndexOf('@');
        var domain = at >= 0 && at < trimmed.Length - 1 ? trimmed[at..] : null;
        return (trimmed, domain);
    }

    private bool ListHas(string table, string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        var (exact, domain) = ListKeys(address);
        return _store.ScalarLong(
            $"SELECT count(*) FROM {table} WHERE address = $address OR ($domain IS NOT NULL AND address = $domain)",
            ("$address", exact), ("$domain", domain)) > 0;
    }

    private void ListAdd(string table, string entry, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(entry)) return;

        _store.Execute(
            $"INSERT INTO {table} (address, added_utc) VALUES ($address, $now) ON CONFLICT(address) DO NOTHING",
            ("$address", entry.Trim().ToLowerInvariant()), ("$now", now.ToUnixTimeSeconds()));
    }

    private void ListRemove(string table, string entry) => _store.Execute(
        $"DELETE FROM {table} WHERE address = $address", ("$address", entry.Trim().ToLowerInvariant()));

    private IReadOnlyList<string> ListAll(string table) => _store.Query(
        $"SELECT address FROM {table} ORDER BY address", r => r.GetString(0));

    /// <summary>
    /// Whether this sender's mail is never junk, and its remote images may load without asking.
    /// </summary>
    /// <remarks>
    /// The address, or its whole domain if that was what was added — "Never Block Sender's
    /// Domain". Allowing images for a domain is a wider grant than for one correspondent, which
    /// is why the reading pane's bar adds the address alone; the domain form is the junk menu's.
    /// </remarks>
    public bool IsSafeSender(string address) => ListHas("safe_senders", address);

    public void AddSafeSender(string entry, DateTimeOffset now) => ListAdd("safe_senders", entry, now);

    public void RemoveSafeSender(string entry) => ListRemove("safe_senders", entry);

    public IReadOnlyList<string> SafeSenders() => ListAll("safe_senders");

    /// <summary>Whether this sender is on the blocked list — junked whatever the classifier says.</summary>
    public bool IsBlockedSender(string address) => ListHas("blocked_senders", address);

    public void AddBlockedSender(string entry, DateTimeOffset now) => ListAdd("blocked_senders", entry, now);

    public void RemoveBlockedSender(string entry) => ListRemove("blocked_senders", entry);

    public IReadOnlyList<string> BlockedSenders() => ListAll("blocked_senders");

    /// <summary>
    /// Whether mail addressed to any of these — a list, an alias — is never junk. The Safe
    /// Recipients list: membership of a list is vouched for by the list, not by each sender.
    /// </summary>
    public bool IsSafeRecipient(IEnumerable<string> recipients)
        => recipients.Any(r => ListHas("safe_recipients", r));

    public void AddSafeRecipient(string entry, DateTimeOffset now) => ListAdd("safe_recipients", entry, now);

    public void RemoveSafeRecipient(string entry) => ListRemove("safe_recipients", entry);

    public IReadOnlyList<string> SafeRecipients() => ListAll("safe_recipients");

    /// <summary>Whether the sender's top-level domain is one the reader has blocked outright.</summary>
    public bool IsBlockedTld(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        var dot = address.LastIndexOf('.');
        var at = address.LastIndexOf('@');
        if (dot < 0 || dot < at || dot == address.Length - 1) return false;

        return _store.ScalarLong(
            "SELECT count(*) FROM blocked_tlds WHERE tld = $tld",
            ("$tld", address[(dot + 1)..].Trim().ToLowerInvariant())) > 0;
    }

    public void SetBlockedTlds(IEnumerable<string> tlds, DateTimeOffset now) => _store.InTransaction(() =>
    {
        _store.Execute("DELETE FROM blocked_tlds");
        foreach (var tld in tlds.Select(t => t.Trim().TrimStart('.').ToLowerInvariant()).Where(t => t.Length > 0).Distinct())
        {
            _store.Execute("INSERT OR IGNORE INTO blocked_tlds (tld, added_utc) VALUES ($tld, $now)",
                ("$tld", tld), ("$now", now.ToUnixTimeSeconds()));
        }

        return 0;
    });

    public IReadOnlyList<string> BlockedTlds() => _store.Query(
        "SELECT tld FROM blocked_tlds ORDER BY tld", r => r.GetString(0));

    /// <summary>Whether a message written in this character set is blocked outright.</summary>
    public bool IsBlockedEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return false;

        return _store.ScalarLong(
            "SELECT count(*) FROM blocked_encodings WHERE charset = $charset",
            ("$charset", charset.Trim().ToLowerInvariant())) > 0;
    }

    public void SetBlockedEncodings(IEnumerable<string> charsets, DateTimeOffset now) => _store.InTransaction(() =>
    {
        _store.Execute("DELETE FROM blocked_encodings");
        foreach (var charset in charsets.Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct())
        {
            _store.Execute("INSERT OR IGNORE INTO blocked_encodings (charset, added_utc) VALUES ($charset, $now)",
                ("$charset", charset), ("$now", now.ToUnixTimeSeconds()));
        }

        return 0;
    });

    public IReadOnlyList<string> BlockedEncodings() => _store.Query(
        "SELECT charset FROM blocked_encodings ORDER BY charset", r => r.GetString(0));

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

    /// <summary>Creates a category. The name is unique, so a duplicate is refused by the index.</summary>
    public Category AddCategory(string name, string colourToken, string? shortcut = null)
    {
        _store.Execute(
            """
            INSERT INTO categories (name, colour_token, shortcut, ordinal)
            VALUES ($name, $colour, $shortcut, (SELECT count(*) FROM categories))
            """,
            ("$name", name), ("$colour", colourToken), ("$shortcut", shortcut));

        return _store.Query("SELECT * FROM categories WHERE id = $id",
            r => new Category(r.GetInt64(0), r.GetString(1), r.GetString(2), Nullable(r, "shortcut"), r.GetInt32(4)),
            ("$id", _store.LastInsertId)).First();
    }

    public void RenameCategory(long id, string name) => _store.Execute(
        "UPDATE categories SET name = $name WHERE id = $id", ("$name", name), ("$id", id));

    public void RecolourCategory(long id, string colourToken) => _store.Execute(
        "UPDATE categories SET colour_token = $colour WHERE id = $id", ("$colour", colourToken), ("$id", id));

    /// <summary>Sets or clears a category's keyboard shortcut. Null clears it.</summary>
    public void SetCategoryShortcut(long id, string? shortcut) => _store.Execute(
        "UPDATE categories SET shortcut = $shortcut WHERE id = $id", ("$shortcut", shortcut), ("$id", id));

    /// <summary>
    /// Removes a category. Its assignments go with it — the message_categories rows cascade on
    /// the foreign key — so a message keeps its other categories and simply loses this one.
    /// </summary>
    public void DeleteCategory(long id) => _store.Execute(
        "DELETE FROM categories WHERE id = $id", ("$id", id));

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

    /// <summary>
    /// Records the picture found for a feed article that arrived without one.
    /// </summary>
    /// <remarks>
    /// The column rather than the message, deliberately: the article list draws a thumbnail for
    /// every visible row and reads it from here, so a picture looked up once is on the row from
    /// then on without the message being reparsed — or rewritten, which for a revision-tracked
    /// feed item would be a change of a different kind.
    /// </remarks>
    public void SetFeedImage(long messageId, string url) => _store.Execute(
        "UPDATE messages SET feed_image = $url WHERE id = $id",
        ("$url", url.Length == 0 ? null : url), ("$id", messageId));

    // ---- Boards -----------------------------------------------------------------------------
    //
    // Named collections an article is saved into. The membership is a join rather than a column
    // because an article belongs to as many boards as the reader puts it on, and it carries when
    // it was saved because that is the order a keep pile is read in.

    /// <summary>Every board, in the reader's order, each with how many articles are on it.</summary>
    public IReadOnlyList<Board> Boards() => _store.Query(
        """
        SELECT b.id, b.name, b.description, b.ordinal, count(i.message_id) AS items
        FROM boards b LEFT JOIN board_items i ON i.board_id = b.id
        GROUP BY b.id ORDER BY b.ordinal, b.id
        """,
        r => new Board(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4)));

    public Board? BoardNamed(string name) => _store.Query(
        """
        SELECT b.id, b.name, b.description, b.ordinal, count(i.message_id) AS items
        FROM boards b LEFT JOIN board_items i ON i.board_id = b.id
        WHERE b.name = $name COLLATE NOCASE GROUP BY b.id
        """,
        r => new Board(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4)),
        ("$name", name.Trim())).FirstOrDefault();

    /// <summary>
    /// Makes a board, or hands back the one already under that name.
    /// </summary>
    /// <remarks>
    /// Idempotent rather than throwing on the unique index, because every caller wants the same
    /// thing — the board called this — and the menu that offers "New board…" is one keystroke
    /// away from a name that already exists.
    /// </remarks>
    public Board AddBoard(string name, DateTimeOffset now, string description = "")
    {
        var trimmed = name.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(trimmed);

        if (BoardNamed(trimmed) is { } already) return already;

        _store.Execute(
            """
            INSERT INTO boards (name, description, ordinal, created_utc)
            VALUES ($name, $description, (SELECT count(*) FROM boards), $now)
            """,
            ("$name", trimmed), ("$description", description.Trim()), ("$now", now.ToUnixTimeSeconds()));

        return BoardNamed(trimmed)!;
    }

    /// <summary>Renames a board. False when the new name is already another board's.</summary>
    public bool RenameBoard(long id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return false;
        if (BoardNamed(trimmed) is { } clash && clash.Id != id) return false;

        return _store.Execute(
            "UPDATE boards SET name = $name WHERE id = $id", ("$name", trimmed), ("$id", id)) > 0;
    }

    /// <summary>Sets what a board is for, which the article column shows under its name.</summary>
    public void DescribeBoard(long id, string description) => _store.Execute(
        "UPDATE boards SET description = $description WHERE id = $id",
        ("$description", description.Trim()), ("$id", id));

    /// <summary>
    /// Removes a board. The articles on it are untouched — the join rows cascade, the messages
    /// do not, and a reader tidying their boards is not asking to lose anything they read.
    /// </summary>
    public void DeleteBoard(long id) => _store.Execute("DELETE FROM boards WHERE id = $id", ("$id", id));

    /// <summary>Moves a board up or down the reader's order.</summary>
    public void ReorderBoards(IReadOnlyList<long> idsInOrder)
    {
        ArgumentNullException.ThrowIfNull(idsInOrder);

        _store.InTransaction(() =>
        {
            for (var at = 0; at < idsInOrder.Count; at++)
            {
                _store.Execute("UPDATE boards SET ordinal = $ordinal WHERE id = $id",
                    ("$ordinal", at), ("$id", idsInOrder[at]));
            }

            return 0;
        });
    }

    /// <summary>Saves messages onto a board. Saving one twice keeps the first time it was saved.</summary>
    /// <returns>How many were not already on it.</returns>
    public int SaveToBoard(IReadOnlyCollection<long> messageIds, long boardId, DateTimeOffset now)
    {
        if (messageIds.Count == 0) return 0;

        return _store.InTransaction(() =>
        {
            var added = 0;
            foreach (var id in messageIds)
            {
                added += _store.Execute(
                    """
                    INSERT INTO board_items (board_id, message_id, saved_utc) VALUES ($board, $message, $now)
                    ON CONFLICT(board_id, message_id) DO NOTHING
                    """,
                    ("$board", boardId), ("$message", id), ("$now", now.ToUnixTimeSeconds()));
            }

            return added;
        });
    }

    /// <summary>Takes messages off a board. The messages themselves stay where they are.</summary>
    public int RemoveFromBoard(IReadOnlyCollection<long> messageIds, long boardId)
        => messageIds.Count == 0
            ? 0
            : _store.Execute(
                $"DELETE FROM board_items WHERE board_id = $board AND message_id IN ({Ids(messageIds)})",
                ("$board", boardId));

    /// <summary>
    /// Which boards a set of messages is on, keyed by message. One query rather than one per
    /// row, for the reason <see cref="CategoriesFor"/> is one query.
    /// </summary>
    public Dictionary<long, List<Board>> BoardsFor(IReadOnlyCollection<long> messageIds)
    {
        var found = new Dictionary<long, List<Board>>();
        if (messageIds.Count == 0) return found;

        foreach (var (messageId, board) in _store.Query(
            $"""
             SELECT i.message_id, b.id, b.name, b.description, b.ordinal FROM board_items i
             JOIN boards b ON b.id = i.board_id
             WHERE i.message_id IN ({Ids(messageIds)})
             ORDER BY b.ordinal, b.id
             """,
            r => (r.GetInt64(0), new Board(r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt32(4)))))
        {
            if (!found.TryGetValue(messageId, out var list)) found[messageId] = list = [];
            list.Add(board);
        }

        return found;
    }

    /// <summary>
    /// What is on a board, most recently saved first.
    /// </summary>
    /// <remarks>
    /// By when it was saved rather than when it was published: a reader who saves a piece from
    /// last year expects to find it at the top of the board they just put it on, not buried
    /// under this morning's headlines.
    /// </remarks>
    public IReadOnlyList<MessageSummary> BoardMessages(long boardId, int limit = 500) => _store.Query(
        """
        SELECT m.* FROM messages m JOIN board_items i ON i.message_id = m.id
        WHERE i.board_id = $board ORDER BY i.saved_utc DESC, i.rowid DESC LIMIT $limit
        """,
        ReadMessage, ("$board", boardId), ("$limit", limit));

    /// <summary>
    /// What is on a board and when each was saved, for moving a board between stores.
    /// </summary>
    /// <remarks>
    /// The times matter: a board is read newest-saved-first, so a move that re-saved everything
    /// at the moment of the move would hand the reader their keep pile in an order they never
    /// put it in.
    /// </remarks>
    public IReadOnlyList<(long MessageId, DateTimeOffset SavedUtc)> BoardItems(long boardId) => _store.Query(
        "SELECT message_id, saved_utc FROM board_items WHERE board_id = $board ORDER BY saved_utc, rowid",
        r => (r.GetInt64(0), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1))),
        ("$board", boardId));

    /// <summary>Whether a message is on any board at all, which is what Delete asks before it acts.</summary>
    public bool IsOnAnyBoard(long messageId) => _store.ScalarLong(
        "SELECT count(*) FROM board_items WHERE message_id = $id", ("$id", messageId)) > 0;

    // ---- Rules --------------------------------------------------------------------------------
    //
    // The Rules and Alerts wizard's rules, in the order they run. The definition is Core's JSON
    // document; the store keeps the name, the switch and the order beside it so the dialog's
    // list can be drawn without parsing every rule.

    /// <summary>Every rule, in running order.</summary>
    public IReadOnlyList<Mailbox.Core.Rules.MailRule> Rules() => _store.Query(
        "SELECT id, name, enabled, ordinal, definition, server_side FROM rules ORDER BY ordinal, id",
        r => Mailbox.Core.Rules.MailRule.FromDefinition(
            r.GetInt64(0), r.GetString(1), r.GetInt32(2) != 0, r.GetInt32(3), r.GetString(4), r.GetInt32(5) != 0));

    /// <summary>Adds a rule at the end of the order and returns it with its id.</summary>
    public Mailbox.Core.Rules.MailRule AddRule(Mailbox.Core.Rules.MailRule rule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);

        _store.Execute(
            """
            INSERT INTO rules (name, enabled, ordinal, definition, created_utc, server_side)
            VALUES ($name, $enabled, (SELECT count(*) FROM rules), $definition, $now, $server)
            """,
            ("$name", rule.Name), ("$enabled", rule.Enabled ? 1 : 0),
            ("$definition", rule.DefinitionJson()), ("$now", now.ToUnixTimeSeconds()), ("$server", rule.ServerSide ? 1 : 0));
        if (rule.ServerSide) MarkSieveStale();

        return rule with { Id = _store.LastInsertId, Ordinal = (int)_store.ScalarLong("SELECT count(*) - 1 FROM rules") };
    }

    /// <summary>Replaces a rule's name, switch and definition. Its place in the order is kept.</summary>
    public void UpdateRule(Mailbox.Core.Rules.MailRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var wasServer = _store.ScalarLong("SELECT server_side FROM rules WHERE id = $id", ("$id", rule.Id)) != 0;
        _store.Execute(
            "UPDATE rules SET name = $name, enabled = $enabled, definition = $definition, server_side = $server WHERE id = $id",
            ("$name", rule.Name), ("$enabled", rule.Enabled ? 1 : 0),
            ("$definition", rule.DefinitionJson()), ("$server", rule.ServerSide ? 1 : 0), ("$id", rule.Id));
        if (wasServer || rule.ServerSide) MarkSieveStale();
    }

    /// <summary>
    /// Whether a rule runs on the server. Set by the wizard; cleared by the publisher for a rule
    /// the server turned out not to be able to run, so that it runs here instead. Does not mark
    /// the server behind — the publisher is the one calling.
    /// </summary>
    public void SetRuleServerSide(long id, bool serverSide) => _store.Execute(
        "UPDATE rules SET server_side = $server WHERE id = $id", ("$server", serverSide ? 1 : 0), ("$id", id));

    public void SetRuleEnabled(long id, bool enabled)
    {
        _store.Execute("UPDATE rules SET enabled = $enabled WHERE id = $id", ("$enabled", enabled ? 1 : 0), ("$id", id));
        if (_store.ScalarLong("SELECT server_side FROM rules WHERE id = $id", ("$id", id)) != 0) MarkSieveStale();
    }

    public void DeleteRule(long id)
    {
        var wasServer = _store.ScalarLong("SELECT server_side FROM rules WHERE id = $id", ("$id", id)) != 0;
        _store.Execute("DELETE FROM rules WHERE id = $id", ("$id", id));
        if (wasServer) MarkSieveStale();
    }

    /// <summary>Puts the rules in this order — Move Up and Move Down, written back whole.</summary>
    public void OrderRules(IReadOnlyList<long> ids)
    {
        _store.InTransaction(() =>
        {
            for (var i = 0; i < ids.Count; i++)
            {
                _store.Execute("UPDATE rules SET ordinal = $ordinal WHERE id = $id", ("$ordinal", i), ("$id", ids[i]));
            }

            return 0;
        });

        if (_store.ScalarLong("SELECT count(*) FROM rules WHERE server_side = 1") > 0) MarkSieveStale();
    }

    // ---- AutoArchive -----------------------------------------------------------------------------

    /// <summary>A folder's own AutoArchive choice, as Core's document, or null for the default.</summary>
    public string? FolderAutoArchive(long folderId) => _store.Query(
        "SELECT autoarchive_json FROM folders WHERE id = $id", r => r.IsDBNull(0) ? null : r.GetString(0), ("$id", folderId)).FirstOrDefault();

    public void SetFolderAutoArchive(long folderId, string? json) => _store.Execute(
        "UPDATE folders SET autoarchive_json = $json WHERE id = $id", ("$json", json), ("$id", folderId));

    /// <summary>The messages of a folder received before a moment — what AutoArchive moves or deletes.</summary>
    public IReadOnlyList<MessageSummary> MessagesOlderThan(long folderId, DateTimeOffset cutoff) => _store.Query(
        "SELECT * FROM messages WHERE folder_id = $folder AND received_utc < $cutoff ORDER BY received_utc",
        ReadMessage, ("$folder", folderId), ("$cutoff", cutoff.ToUnixTimeSeconds()));

    /// <summary>The messages whose own Expires header has passed, across the account.</summary>
    public IReadOnlyList<MessageSummary> ExpiredMessages(DateTimeOffset now) => _store.Query(
        """
        SELECT m.* FROM messages m JOIN folders f ON f.id = m.folder_id
        WHERE m.expires_utc IS NOT NULL AND m.expires_utc < $now AND f.role NOT IN ('outbox', 'deleted')
        ORDER BY m.received_utc
        """,
        ReadMessage, ("$now", now.ToUnixTimeSeconds()));

    // ---- Views ---------------------------------------------------------------------------------
    //
    // A folder's current view is a JSON document on its row; the views a reader saves by name
    // are rows of their own. The documents are Core's MailView; the store keeps them whole.

    /// <summary>The folder's current view document, or null for the shipped default untouched.</summary>
    public string? FolderView(long folderId) => _store.Query(
        "SELECT view_json FROM folders WHERE id = $id", r => r.IsDBNull(0) ? null : r.GetString(0), ("$id", folderId)).FirstOrDefault();

    /// <summary>Sets a folder's current view; null puts it back to the default.</summary>
    public void SetFolderView(long folderId, string? json) => _store.Execute(
        "UPDATE folders SET view_json = $json WHERE id = $id", ("$json", json), ("$id", folderId));

    /// <summary>Every saved view, by name.</summary>
    public IReadOnlyList<SavedView> Views() => _store.Query(
        "SELECT id, name, definition FROM views ORDER BY name COLLATE NOCASE",
        r => new SavedView(r.GetInt64(0), r.GetString(1), r.GetString(2)));

    public SavedView? ViewNamed(string name) => _store.Query(
        "SELECT id, name, definition FROM views WHERE name = $name COLLATE NOCASE",
        r => new SavedView(r.GetInt64(0), r.GetString(1), r.GetString(2)), ("$name", name)).FirstOrDefault();

    /// <summary>Saves a view under a name, replacing one of that name.</summary>
    public SavedView SaveView(string name, string definition, DateTimeOffset now)
    {
        _store.Execute(
            """
            INSERT INTO views (name, definition, created_utc) VALUES ($name, $definition, $now)
            ON CONFLICT(name) DO UPDATE SET definition = excluded.definition
            """,
            ("$name", name), ("$definition", definition), ("$now", now.ToUnixTimeSeconds()));
        return ViewNamed(name)!;
    }

    public void RenameView(long id, string name) => _store.Execute(
        "UPDATE views SET name = $name WHERE id = $id", ("$name", name), ("$id", id));

    public void DeleteView(long id) => _store.Execute("DELETE FROM views WHERE id = $id", ("$id", id));

    // ---- Server-side rules (Sieve) ---------------------------------------------------------------
    //
    // The script last put on the server, and whether the server is behind. The rules dialog
    // publishes; RulesHandler reads: while the state is current, the server-side rules are the
    // server's to run and are skipped here; while it is stale — rules changed, a folder renamed,
    // a publish failed — they run here as well.

    /// <summary>The script on the server, or null when Mailbox has never put one there.</summary>
    public SieveState? SieveState() => _store.Query(
        "SELECT script, include, published_utc, stale FROM sieve_state WHERE id = 1",
        r => new SieveState(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)), r.GetInt32(3) != 0)).FirstOrDefault();

    /// <summary>Records a script as put on the server and active, and the server as current.</summary>
    public void SetSieveState(string script, string? include, DateTimeOffset now) => _store.Execute(
        """
        INSERT INTO sieve_state (id, script, include, published_utc, stale) VALUES (1, $script, $include, $now, 0)
        ON CONFLICT(id) DO UPDATE SET script = excluded.script, include = excluded.include,
            published_utc = excluded.published_utc, stale = 0
        """,
        ("$script", script), ("$include", include), ("$now", now.ToUnixTimeSeconds()));

    /// <summary>The server no longer has Mailbox's script — it was taken down.</summary>
    public void ClearSieveState() => _store.Execute("DELETE FROM sieve_state WHERE id = 1");

    /// <summary>The server is behind: what it runs is not what the rules say.</summary>
    public void MarkSieveStale() => _store.Execute("UPDATE sieve_state SET stale = 1 WHERE id = 1");

    /// <summary>True while the server has the current script, so its rules need not run here.</summary>
    public bool ServerRulesCurrent() => _store.ScalarLong("SELECT count(*) FROM sieve_state WHERE id = 1 AND stale = 0") > 0;

    /// <summary>The store's own address, for the "my name" conditions.</summary>
    public string? OwnAddress() => _store.Query(
        "SELECT address FROM accounts ORDER BY id LIMIT 1", r => r.GetString(0)).FirstOrDefault();

    // ---- Search folders --------------------------------------------------------------------------
    //
    // A saved query, listed under Search Folders in the pane. Templates run as SQL over the
    // columns; a custom folder's conditions — the rules' own — are evaluated over the rows in
    // managed code, so the two never disagree about what "from" means.

    /// <summary>Every search folder, in order.</summary>
    public IReadOnlyList<SearchFolder> SearchFolders() => _store.Query(
        "SELECT id, name, ordinal, definition FROM search_folders ORDER BY ordinal, id",
        r => new SearchFolder(r.GetInt64(0), r.GetString(1), r.GetInt32(2),
            Mailbox.Core.Search.SearchFolderQuery.FromJson(r.GetString(3))));

    public SearchFolder AddSearchFolder(string name, Mailbox.Core.Search.SearchFolderQuery query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);

        _store.Execute(
            """
            INSERT INTO search_folders (name, ordinal, definition, created_utc)
            VALUES ($name, (SELECT count(*) FROM search_folders), $definition, $now)
            """,
            ("$name", name), ("$definition", query.ToJson()), ("$now", now.ToUnixTimeSeconds()));

        return SearchFolders().Single(f => f.Id == _store.LastInsertId);
    }

    public void UpdateSearchFolder(long id, string name, Mailbox.Core.Search.SearchFolderQuery query) => _store.Execute(
        "UPDATE search_folders SET name = $name, definition = $definition WHERE id = $id",
        ("$name", name), ("$definition", query.ToJson()), ("$id", id));

    public void DeleteSearchFolder(long id) => _store.Execute("DELETE FROM search_folders WHERE id = $id", ("$id", id));

    /// <summary>
    /// What a search folder finds: the account's messages that match, newest first, from every
    /// folder but the Outbox — and Deleted Items and Junk unless the query includes them.
    /// </summary>
    /// <param name="ownAddresses">The reader's addresses, for the "me" and "public groups" templates.</param>
    public IReadOnlyList<MessageSummary> SearchFolderResults(
        Mailbox.Core.Search.SearchFolderQuery query, IReadOnlyList<string> ownAddresses, DateTimeOffset now, int limit = 500)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(ownAddresses);

        var excluded = query.IncludeDeleted ? "'outbox'" : "'outbox', 'deleted', 'junk'";
        var scope = $"""
            SELECT m.* FROM messages m JOIN folders f ON f.id = m.folder_id
            WHERE f.role NOT IN ({excluded}){Awake.Replace("snooze_until", "m.snooze_until")}
            """;

        var (where, parameters) = Clause(query, ownAddresses, now);
        var custom = query.Kind == Mailbox.Core.Search.SearchFolderKind.Custom && query.Conditions.Count > 0;

        // The body text rides along only for a custom folder, whose conditions may read it; a
        // template's rows are list rows and stay as light as the list's own.
        Func<SqliteDataReader, MessageSummary> read = custom
            ? r => ReadMessage(r) with { BodyText = r.GetString(r.GetOrdinal("body_text")) }
            : ReadMessage;
        var rows = _store.Query(
            $"{scope} AND ({where}) ORDER BY m.received_utc DESC LIMIT $limit",
            read,
            [.. parameters, ("$limit", (object?)limit)]);

        if (!custom) return rows;

        // A custom folder: the SQL above narrowed by scope alone; the conditions decide here,
        // over the row's own facts. What a row cannot say — a header, the body beyond its text —
        // is read from the raw message when a condition needs it.
        var facts = new List<MessageSummary>();
        foreach (var row in rows)
        {
            var rule = new Mailbox.Core.Rules.MailRule { Conditions = query.Conditions };
            if (Mailbox.Core.Rules.RuleEvaluator.Matches(rule, FactsFor(row, ownAddresses))) facts.Add(row);
        }

        return facts;
    }

    /// <summary>How many of a search folder's results are unread, for the folder pane's count.</summary>
    /// <remarks>
    /// A count in SQL for a template, so the pane's badge costs a query rather than the rows;
    /// a custom folder has to run its conditions and counts what came back.
    /// </remarks>
    public int SearchFolderUnread(Mailbox.Core.Search.SearchFolderQuery query, IReadOnlyList<string> ownAddresses, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(ownAddresses);

        if (query.Kind == Mailbox.Core.Search.SearchFolderKind.Custom && query.Conditions.Count > 0)
        {
            return SearchFolderResults(query, ownAddresses, now, int.MaxValue).Count(m => !m.IsRead);
        }

        var excluded = query.IncludeDeleted ? "'outbox'" : "'outbox', 'deleted', 'junk'";
        var (where, parameters) = Clause(query, ownAddresses, now);
        return (int)_store.ScalarLong(
            $"""
             SELECT count(*) FROM messages m JOIN folders f ON f.id = m.folder_id
             WHERE f.role NOT IN ({excluded}) AND m.is_read = 0{Awake.Replace("snooze_until", "m.snooze_until")} AND ({where})
             """,
            parameters);
    }

    /// <summary>The WHERE clause for a template, and its parameters. A custom folder is scope alone.</summary>
    private (string Where, (string, object?)[] Parameters) Clause(
        Mailbox.Core.Search.SearchFolderQuery query, IReadOnlyList<string> own, DateTimeOffset now)
    {
        var mine = own.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).ToList();
        string MineIn(string column) => mine.Count == 0
            ? "0"
            : string.Join(" OR ", mine.Select(a => $"(',' || m.{column} || ',') LIKE '%,' || {Quote(a)} || ',%'"));

        switch (query.Kind)
        {
            case Mailbox.Core.Search.SearchFolderKind.Unread:
                return ("m.is_read = 0", []);
            case Mailbox.Core.Search.SearchFolderKind.Flagged:
                return ("m.is_flagged = 1", []);
            case Mailbox.Core.Search.SearchFolderKind.UnreadOrFlagged:
                return ("m.is_read = 0 OR m.is_flagged = 1", []);
            case Mailbox.Core.Search.SearchFolderKind.Important:
                return ("m.importance = 2", []);
            case Mailbox.Core.Search.SearchFolderKind.From:
            case Mailbox.Core.Search.SearchFolderKind.FromOrTo:
            {
                if (query.Values.Count == 0) return ("0", []);
                var people = query.Values.Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0).ToList();
                var from = string.Join(" OR ", people.Select(p => p.StartsWith('@')
                    ? $"lower(m.from_address) LIKE '%' || {Quote(p)}"
                    : $"(lower(m.from_address) = {Quote(p)} OR lower(m.from_name) = {Quote(p)})"));
                if (query.Kind == Mailbox.Core.Search.SearchFolderKind.From) return (from, []);
                var to = string.Join(" OR ", people.Select(p => p.StartsWith('@')
                    ? $"(',' || m.to_addresses || ',' || m.cc_addresses || ',') LIKE '%' || {Quote(p)} || ',%'"
                    : $"(',' || m.to_addresses || ',' || m.cc_addresses || ',') LIKE '%,' || {Quote(p)} || ',%'"));
                return ($"({from}) OR ({to})", []);
            }
            case Mailbox.Core.Search.SearchFolderKind.SentDirectlyToMe:
                return ($"({MineIn("to_addresses")})", []);
            case Mailbox.Core.Search.SearchFolderKind.SentToLists:
                // Not addressed to any of the reader's own addresses: it came through a list or
                // an alias. Rows from before the recipient columns existed read as unaddressed,
                // so they are left out rather than all matching.
                return ($"m.to_addresses <> '' AND NOT ({MineIn("to_addresses")}) AND NOT ({MineIn("cc_addresses")})", []);
            case Mailbox.Core.Search.SearchFolderKind.Categorized:
            {
                if (query.Values.Count == 0)
                {
                    return ("EXISTS (SELECT 1 FROM message_categories mc WHERE mc.message_id = m.id)", []);
                }

                var names = string.Join(',', query.Values.Select(Quote));
                return ($"EXISTS (SELECT 1 FROM message_categories mc JOIN categories c ON c.id = mc.category_id WHERE mc.message_id = m.id AND c.name IN ({names}))", []);
            }
            case Mailbox.Core.Search.SearchFolderKind.Large:
                return ("m.size_bytes > $threshold", [("$threshold", (long)Math.Max(0, query.Threshold) * 1024)]);
            case Mailbox.Core.Search.SearchFolderKind.Old:
                return ("m.received_utc < $cutoff", [("$cutoff", now.AddDays(-Math.Max(0, query.Threshold)).ToUnixTimeSeconds())]);
            case Mailbox.Core.Search.SearchFolderKind.WithAttachments:
                return ("m.has_attachment = 1", []);
            case Mailbox.Core.Search.SearchFolderKind.WithWords:
            {
                if (query.Values.Count == 0) return ("0", []);
                var words = string.Join(" OR ", query.Values.Select(w =>
                    $"(m.subject LIKE '%' || {Quote(w)} || '%' OR m.body_text LIKE '%' || {Quote(w)} || '%')"));
                return (words, []);
            }
            default:
                return ("1", []);
        }
    }

    /// <summary>A row's facts, for a custom search folder's conditions.</summary>
    private Mailbox.Core.Rules.RuleFacts FactsFor(MessageSummary row, IReadOnlyList<string> own)
    {
        var categories = CategoriesFor([row.Id]).GetValueOrDefault(row.Id)?.Select(c => c.Name).ToList() ?? [];
        return new Mailbox.Core.Rules.RuleFacts
        {
            FromAddress = row.FromAddress,
            FromName = row.FromName,
            To = row.To,
            Cc = row.Cc,
            Subject = row.Subject,
            Body = row.BodyText.Length > 0 ? row.BodyText : row.Preview,
            Headers = string.Empty,
            SizeBytes = row.SizeBytes,
            HasAttachment = row.HasAttachment,
            Importance = row.Importance,
            Received = row.Received,
            Categories = categories,
            IsFlagged = row.IsFlagged,
            OwnAddresses = own,
        };
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

    // A snoozed message is out of sight, so it is out of the counts too: the badge on a folder
    // says what is there to read, and a message that will come back at four is not there yet.
    private const string FolderSelect =
        """
        SELECT f.*,
               (SELECT count(*) FROM messages m WHERE m.folder_id = f.id
                  AND (m.snooze_until IS NULL OR m.snooze_until <= strftime('%s','now'))) AS total,
               (SELECT count(*) FROM messages m WHERE m.folder_id = f.id AND m.is_read = 0
                  AND (m.snooze_until IS NULL OR m.snooze_until <= strftime('%s','now'))) AS unread
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
        r.GetInt32(r.GetOrdinal("has_attachment")) != 0)
    {
        FollowUpDue = NullableLong(r, "follow_up_due") is { } due
            ? DateTimeOffset.FromUnixTimeSeconds(due)
            : null,
        FollowUpComplete = r.GetInt32(r.GetOrdinal("follow_up_complete")) != 0,
        SnoozedUntil = NullableLong(r, "snooze_until") is { } until
            ? DateTimeOffset.FromUnixTimeSeconds(until)
            : null,
        FollowUpType = Nullable(r, "follow_up_type"),
        FollowUpStart = NullableLong(r, "follow_up_start") is { } start ? DateTimeOffset.FromUnixTimeSeconds(start) : null,
        Reminder = NullableLong(r, "reminder_utc") is { } remind ? DateTimeOffset.FromUnixTimeSeconds(remind) : null,
        Importance = r.GetInt32(r.GetOrdinal("importance")),
        IsFocused = r.GetInt32(r.GetOrdinal("is_focused")) != 0,
        To = Split(r.GetString(r.GetOrdinal("to_addresses"))),
        Cc = Split(r.GetString(r.GetOrdinal("cc_addresses"))),
        Expires = NullableLong(r, "expires_utc") is { } expires ? DateTimeOffset.FromUnixTimeSeconds(expires) : null,
        HeaderOnly = r.GetInt32(r.GetOrdinal("header_only")) != 0,
        MarkedForDownload = r.GetInt32(r.GetOrdinal("marked_download")) != 0,
        FeedLink = Nullable(r, "feed_link") ?? string.Empty,
        FeedWords = NullableLong(r, "feed_words") is { } words ? (int)words : 0,
        FeedImage = Nullable(r, "feed_image") ?? string.Empty,
    };

    private static IReadOnlyList<string> Split(string joined)
        => joined.Length == 0 ? [] : joined.Split(',', StringSplitOptions.RemoveEmptyEntries);

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
