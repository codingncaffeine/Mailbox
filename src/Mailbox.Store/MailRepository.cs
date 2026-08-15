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
        long? parentId = null)
    {
        _store.Execute(
            """
            INSERT INTO folders (account_id, parent_id, name, role, ordinal)
            VALUES ($account, $parent, $name, $role,
                    (SELECT count(*) FROM folders WHERE account_id = $account))
            """,
            ("$account", accountId), ("$parent", parentId), ("$name", name),
            ("$role", role.ToString().ToLowerInvariant()));

        return GetFolder(_store.LastInsertId)!;
    }

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
                     from_name, from_address, subject, preview, sent_utc, received_utc,
                     size_bytes, is_read, is_flagged, has_attachment)
                VALUES
                    ($folder, $blob, $uid, $messageId, NULL, $thread,
                     $fromName, $fromAddress, $subject, $preview, $sent, $received,
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
                ("$sent", message.Sent?.ToUnixTimeSeconds()),
                ("$received", message.Received.ToUnixTimeSeconds()),
                ("$size", message.SizeBytes),
                ("$read", message.IsRead ? 1 : 0),
                ("$flagged", message.IsFlagged ? 1 : 0),
                ("$attachment", message.HasAttachment ? 1 : 0));

            if (inserted != 0) return _store.LastInsertId;

            // Nothing filed, so the blob written above has nothing pointing at it.
            if (blobId is { } orphan) _store.Execute("DELETE FROM blobs WHERE id = $id", ("$id", orphan));
            return null;
        });
    }

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

        return _store.Execute(
            $"UPDATE messages SET is_read = $read WHERE id IN ({Ids(messageIds)})",
            ("$read", read ? 1 : 0));
    }

    public int SetFlagged(IReadOnlyCollection<long> messageIds, bool flagged)
    {
        if (messageIds.Count == 0) return 0;

        return _store.Execute(
            $"UPDATE messages SET is_flagged = $flagged WHERE id IN ({Ids(messageIds)})",
            ("$flagged", flagged ? 1 : 0));
    }

    public int MoveMessages(IReadOnlyCollection<long> messageIds, long toFolderId)
    {
        if (messageIds.Count == 0) return 0;

        return _store.Execute(
            $"UPDATE messages SET folder_id = $folder WHERE id IN ({Ids(messageIds)})",
            ("$folder", toFolderId));
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
        });
    }

    /// <summary>
    /// Renders ids into an IN list. Safe because they are longs the caller already read out of
    /// this database — there is no string here for anything to be injected through, and a
    /// parameter per id would blow past SQLite's variable limit on a large selection.
    /// </summary>
    private static string Ids(IEnumerable<long> ids) => string.Join(',', ids);

    public void SetRead(long messageId, bool read) => _store.Execute(
        "UPDATE messages SET is_read = $read WHERE id = $id",
        ("$read", read ? 1 : 0), ("$id", messageId));

    public void SetFlagged(long messageId, bool flagged) => _store.Execute(
        "UPDATE messages SET is_flagged = $flagged WHERE id = $id",
        ("$flagged", flagged ? 1 : 0), ("$id", messageId));

    public void MoveMessage(long messageId, long toFolderId) => _store.Execute(
        "UPDATE messages SET folder_id = $folder WHERE id = $id",
        ("$folder", toFolderId), ("$id", messageId));

    /// <summary>
    /// Removes a message and the raw copy behind it. The message goes first: while it exists it
    /// references the blob, and deleting the blob out from under it fails the foreign key.
    /// </summary>
    public void DeleteMessage(long messageId)
    {
        _store.InTransaction(() =>
        {
            var blobId = _store.Query(
                "SELECT blob_id FROM messages WHERE id = $id AND blob_id IS NOT NULL",
                r => r.GetInt64(0), ("$id", messageId)).FirstOrDefault();

            var removed = _store.Execute("DELETE FROM messages WHERE id = $id", ("$id", messageId));

            if (blobId != 0) _store.Execute("DELETE FROM blobs WHERE id = $id", ("$id", blobId));
            return removed;
        });
    }

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
    };

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
