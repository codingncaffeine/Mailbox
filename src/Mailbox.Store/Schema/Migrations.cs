namespace Mailbox.Store.Schema;

/// <summary>
/// The schema, as an ordered list of steps that only ever gets appended to.
/// </summary>
/// <remarks>
/// A store holds mail that exists nowhere else once the server has dropped it — POP3 with
/// delete-on-download is the normal case, not an edge one. So migrations are forward-only and
/// additive, and an existing step is never edited: changing one would leave every store already
/// migrated past it silently different from a fresh one, with no way to tell which is which.
/// <para>
/// The version is <c>user_version</c>, SQLite's own header field, so it costs no table and
/// cannot disagree with the file it describes.
/// </para>
/// </remarks>
public static class Migrations
{
    /// <summary>Every step, in order. Index + 1 is the schema version it produces.</summary>
    public static readonly IReadOnlyList<string> Steps =
    [
        // ---- 1: accounts, folders, messages, and the blobs behind them ------------------
        """
        CREATE TABLE accounts (
            id            INTEGER PRIMARY KEY,
            address       TEXT    NOT NULL UNIQUE,
            display_name  TEXT    NOT NULL DEFAULT '',
            protocol      TEXT    NOT NULL CHECK (protocol IN ('pop3', 'imap')),
            ordinal       INTEGER NOT NULL DEFAULT 0,
            created_utc   INTEGER NOT NULL
        );

        CREATE TABLE folders (
            id            INTEGER PRIMARY KEY,
            account_id    INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            parent_id     INTEGER          REFERENCES folders(id)  ON DELETE CASCADE,
            name          TEXT    NOT NULL,
            -- Inbox, Sent, Drafts and so on, so the shell can find them without matching names
            -- in whatever language the server speaks.
            role          TEXT    NOT NULL DEFAULT 'none',
            ordinal       INTEGER NOT NULL DEFAULT 0,
            UNIQUE (account_id, parent_id, name)
        );

        -- Raw RFC822, kept verbatim and separately from the parsed columns. Every header we
        -- decided not to parse is still in here, which is what makes a later reparse possible.
        CREATE TABLE blobs (
            id            INTEGER PRIMARY KEY,
            bytes         BLOB    NOT NULL,
            byte_length   INTEGER NOT NULL,
            compression   TEXT    NOT NULL DEFAULT 'none'
        );

        CREATE TABLE messages (
            id            INTEGER PRIMARY KEY,
            folder_id     INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            blob_id       INTEGER          REFERENCES blobs(id),
            -- The server's own identity for the message: POP3 UIDL, IMAP UID. Unique per
            -- folder, and the thing that stops a re-poll duplicating everything.
            server_uid    TEXT,
            message_id    TEXT,
            in_reply_to   TEXT,
            thread_key    TEXT,
            from_name     TEXT    NOT NULL DEFAULT '',
            from_address  TEXT    NOT NULL DEFAULT '',
            subject       TEXT    NOT NULL DEFAULT '',
            preview       TEXT    NOT NULL DEFAULT '',
            sent_utc      INTEGER,
            received_utc  INTEGER NOT NULL,
            size_bytes    INTEGER NOT NULL DEFAULT 0,
            is_read       INTEGER NOT NULL DEFAULT 0,
            is_flagged    INTEGER NOT NULL DEFAULT 0,
            has_attachment INTEGER NOT NULL DEFAULT 0,
            UNIQUE (folder_id, server_uid)
        );

        CREATE INDEX messages_by_folder_date ON messages (folder_id, received_utc DESC);
        CREATE INDEX messages_by_thread      ON messages (thread_key);
        CREATE INDEX messages_by_message_id  ON messages (message_id);
        CREATE INDEX folders_by_account      ON folders  (account_id, ordinal);
        """,

        // ---- 2: full-text search ---------------------------------------------------------
        """
        -- External-content FTS: the index stores no copy of the text, it points back at
        -- messages. Halves the store's size and makes the index impossible to leave stale,
        -- because there is only one copy of the data.
        CREATE VIRTUAL TABLE messages_fts USING fts5 (
            subject,
            from_name,
            from_address,
            preview,
            body,
            content = 'messages',
            content_rowid = 'id',
            tokenize = 'unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER messages_fts_insert AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts (rowid, subject, from_name, from_address, preview, body)
            VALUES (new.id, new.subject, new.from_name, new.from_address, new.preview, '');
        END;

        CREATE TRIGGER messages_fts_delete AFTER DELETE ON messages BEGIN
            INSERT INTO messages_fts (messages_fts, rowid, subject, from_name, from_address,
                                      preview, body)
            VALUES ('delete', old.id, old.subject, old.from_name, old.from_address,
                    old.preview, '');
        END;

        CREATE TRIGGER messages_fts_update AFTER UPDATE ON messages BEGIN
            INSERT INTO messages_fts (messages_fts, rowid, subject, from_name, from_address,
                                      preview, body)
            VALUES ('delete', old.id, old.subject, old.from_name, old.from_address,
                    old.preview, '');
            INSERT INTO messages_fts (rowid, subject, from_name, from_address, preview, body)
            VALUES (new.id, new.subject, new.from_name, new.from_address, new.preview, '');
        END;
        """,

        // ---- 3: the outbox ---------------------------------------------------------------
        """
        -- Sending is a queue with its own state, not a method call. A message that failed to
        -- send has to survive the process that failed to send it, and has to say why.
        CREATE TABLE outbox (
            id            INTEGER PRIMARY KEY,
            account_id    INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            blob_id       INTEGER NOT NULL REFERENCES blobs(id),
            state         TEXT    NOT NULL DEFAULT 'queued'
                          CHECK (state IN ('queued', 'sending', 'sent', 'failed', 'held')),
            attempts      INTEGER NOT NULL DEFAULT 0,
            queued_utc    INTEGER NOT NULL,
            next_try_utc  INTEGER,
            last_error    TEXT
        );

        CREATE INDEX outbox_by_state ON outbox (state, next_try_utc);
        """,
    ];

    /// <summary>The version a store is brought up to.</summary>
    public static int Latest => Steps.Count;
}
