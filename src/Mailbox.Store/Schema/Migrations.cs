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

        // ---- 4: which account is the default ---------------------------------------------
        """
        -- Which account new mail is sent from when nothing else decides. A column rather than a
        -- setting, because it has to be true of exactly one account and the database can say so.
        ALTER TABLE accounts ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0;

        -- The first account is the default until told otherwise.
        UPDATE accounts SET is_default = 1
        WHERE id = (SELECT id FROM accounts ORDER BY ordinal, id LIMIT 1);

        CREATE UNIQUE INDEX one_default_account ON accounts (is_default) WHERE is_default = 1;
        """,

        // ---- 5: one account per file ------------------------------------------------------
        """
        -- Each account now has its own store file, so a file holds exactly one account and
        -- "which is the default" is a question about the set of files rather than about any
        -- one of them. It lives in the settings file instead; a column here could only ever
        -- describe this file, and two files could both claim it with nothing to notice.
        DROP INDEX IF EXISTS one_default_account;
        ALTER TABLE accounts DROP COLUMN is_default;
        """,

        // ---- 6: colour categories ---------------------------------------------------------
        """
        -- Categories are named colours a message can carry several of. The colour is a token
        -- name rather than a value, so a category stays legible when the theme changes — a
        -- category holding #FF0000 would be invisible on the Black theme with nothing to do
        -- about it.
        CREATE TABLE categories (
            id            INTEGER PRIMARY KEY,
            name          TEXT    NOT NULL UNIQUE,
            colour_token  TEXT    NOT NULL,
            shortcut      TEXT,
            ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE message_categories (
            message_id    INTEGER NOT NULL REFERENCES messages(id)   ON DELETE CASCADE,
            category_id   INTEGER NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
            PRIMARY KEY (message_id, category_id)
        );

        CREATE INDEX categories_by_message ON message_categories (message_id);

        -- The reference's own six, by the names people already know them by.
        INSERT INTO categories (name, colour_token, ordinal) VALUES
            ('Red Category',    'category.red',    0),
            ('Orange Category', 'category.orange', 1),
            ('Yellow Category', 'category.yellow', 2),
            ('Green Category',  'category.green',  3),
            ('Blue Category',   'category.blue',   4),
            ('Purple Category', 'category.purple', 5);
        """,

        // ---- 7: senders whose images may load ----------------------------------------------
        //
        // Remote images are blocked for everyone by default, and this is the exception list.
        // Kept per account, like everything else in this file: "always allow images from this
        // sender" is a decision about one mailbox, and the same address may be a newsletter in
        // one and a stranger in another. §7.8's junk filter reads the same table.
        """
        CREATE TABLE safe_senders (
            address    TEXT    NOT NULL PRIMARY KEY,
            added_utc  INTEGER NOT NULL
        );
        """,

        // ---- 8: what checking a message's own signature came to -----------------------------
        //
        // Its own table rather than columns on the message, because it is learned after the
        // message is stored and may never be learned at all: verifying reads a key from DNS, and
        // §19 forbids doing that to draw a message. So it happens once, when the mail arrives
        // and the network is already in hand, and what the reading pane shows comes from here.
        //
        // A row's absence is meaningful — it says this message has never been checked, which is
        // a different thing from a check that failed, and the reader is told the difference.
        """
        CREATE TABLE message_authentication (
            message_id      INTEGER NOT NULL PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
            -- The verdict as AuthVerdict spells it: none, pass, fail, softfail, neutral, error.
            dkim            TEXT    NOT NULL,
            -- The d= tag of the signature that passed, which is who the pass is evidence about.
            signing_domain  TEXT,
            checked_utc     INTEGER NOT NULL
        );
        """,
    ];

    /// <summary>The version a store is brought up to.</summary>
    public static int Latest => Steps.Count;
}
