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
        // one and a stranger in another. The design's junk filter reads the same table.
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
        // the render path forbids it to draw a message. So it happens once, when the mail arrives
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

        // ---- 9: the Auto-Complete List ------------------------------------------------------
        //
        // Every address a message has been sent to, weighted by how often and how recently, so
        // the To line can offer it back as it is typed. Per account file like everything else
        // — the compose window merges across accounts when it asks — and keyed on the address
        // alone, because "which name did they use last time" is what the display name column
        // is for, not a second row.
        """
        CREATE TABLE nickname_cache (
            address        TEXT    NOT NULL PRIMARY KEY,
            display_name   TEXT    NOT NULL DEFAULT '',
            weight         INTEGER NOT NULL DEFAULT 0,
            last_used_utc  INTEGER NOT NULL
        );
        """,

        // ---- 10: IMAP -----------------------------------------------------------------------
        //
        // A folder learns which server folder it stands for and where its sync has got to. The
        // store stays authoritative for state: read, flagged, categories, where a message
        // is — and IMAP becomes a two-way sync of the subset the server also keeps. So a local
        // change to a synced folder is written to sync_ops as well as to the row, and the next
        // send/receive plays the journal to the server before it pulls; offline, the journal is
        // simply longer. Every op names the server folder and UID it acts on, because by the
        // time it is played the local row may have moved, changed, or gone.
        """
        ALTER TABLE folders ADD COLUMN imap_path     TEXT;
        ALTER TABLE folders ADD COLUMN uidvalidity   INTEGER;
        ALTER TABLE folders ADD COLUMN uidnext       INTEGER;
        ALTER TABLE folders ADD COLUMN highestmodseq INTEGER;
        -- 0 for a server folder that is listed but never pulled: a mailbox's own "everything"
        -- view holds a second copy of every message and would double the store.
        ALTER TABLE folders ADD COLUMN synced        INTEGER NOT NULL DEFAULT 1;

        CREATE TABLE sync_ops (
            id                INTEGER PRIMARY KEY,
            kind              TEXT    NOT NULL CHECK (kind IN ('flags', 'move', 'delete', 'append')),
            -- The folder the message is in on the server when the op is played.
            folder_id         INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            server_uid        TEXT,
            -- The local row, for writing a new UID back after a move or an append. Null once
            -- the row is gone, which a delete op does not mind.
            message_id        INTEGER          REFERENCES messages(id) ON DELETE SET NULL,
            target_folder_id  INTEGER          REFERENCES folders(id)  ON DELETE CASCADE,
            flag              TEXT    CHECK (flag IN ('seen', 'flagged')),
            value             INTEGER,
            created_utc       INTEGER NOT NULL,
            attempts          INTEGER NOT NULL DEFAULT 0,
            last_error        TEXT
        );

        CREATE INDEX sync_ops_by_folder ON sync_ops (folder_id, server_uid);
        CREATE INDEX sync_ops_by_message ON sync_ops (message_id);
        """,

        // ---- 11: the message body, so search reaches it -------------------------------------
        //
        // The FTS index was built with a `body` column that the triggers filled with '' —
        // subject, sender and preview were searchable, the body was not. The plain text of the
        // message goes into `body_text` now, and the triggers index it. Existing rows keep an
        // empty body (their text is in the blob, and re-indexing every blob on migration is the
        // wrong thing to do to a large store on startup); mail received from here on is searchable
        // in full. Kept out of the messages row until now because a hundred-thousand-message
        // folder is the case the list is built to survive, and the list never needs the body.
        """
        ALTER TABLE messages ADD COLUMN body_text TEXT NOT NULL DEFAULT '';

        DROP TRIGGER messages_fts_insert;
        DROP TRIGGER messages_fts_delete;
        DROP TRIGGER messages_fts_update;

        CREATE TRIGGER messages_fts_insert AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts (rowid, subject, from_name, from_address, preview, body)
            VALUES (new.id, new.subject, new.from_name, new.from_address, new.preview, new.body_text);
        END;

        CREATE TRIGGER messages_fts_delete AFTER DELETE ON messages BEGIN
            INSERT INTO messages_fts (messages_fts, rowid, subject, from_name, from_address,
                                      preview, body)
            VALUES ('delete', old.id, old.subject, old.from_name, old.from_address,
                    old.preview, old.body_text);
        END;

        CREATE TRIGGER messages_fts_update AFTER UPDATE ON messages BEGIN
            INSERT INTO messages_fts (messages_fts, rowid, subject, from_name, from_address,
                                      preview, body)
            VALUES ('delete', old.id, old.subject, old.from_name, old.from_address,
                    old.preview, old.body_text);
            INSERT INTO messages_fts (rowid, subject, from_name, from_address, preview, body)
            VALUES (new.id, new.subject, new.from_name, new.from_address, new.preview, new.body_text);
        END;
        """,

        // ---- 12: the junk filter's corpus and the blocked list -------------------------------
        //
        // The design's naive-Bayes filter is trained on the user's own Mark as Junk and Not Junk, and
        // this is where the training lives. `junk_tokens` holds each token's spam and ham counts;
        // `junk_corpus` holds the message totals those counts are normalised against — a single
        // row, because there is one corpus per account file. The corpus is local and this table
        // is the whole of it: nothing is uploaded, there is no shared reputation.
        //
        // `blocked_senders` is the other half of the lists that always win over the classifier,
        // the mirror of `safe_senders` from schema 7.
        """
        CREATE TABLE junk_tokens (
            token       TEXT    NOT NULL PRIMARY KEY,
            spam_count  INTEGER NOT NULL DEFAULT 0,
            ham_count   INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE junk_corpus (
            id             INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            spam_messages  INTEGER NOT NULL DEFAULT 0,
            ham_messages   INTEGER NOT NULL DEFAULT 0
        );

        INSERT INTO junk_corpus (id, spam_messages, ham_messages) VALUES (1, 0, 0);

        CREATE TABLE blocked_senders (
            address    TEXT    NOT NULL PRIMARY KEY,
            added_utc  INTEGER NOT NULL
        );
        """,

        // ---- 13: follow-up flags with a due date --------------------------------------------
        //
        // A message could be flagged (is_flagged from schema 1); this gives the flag a due date
        // and a completed state, which is what makes it a follow-up rather than a bookmark. A
        // completed follow-up keeps its record — the reference shows a check where the flag was —
        // so the two are separate: is_flagged says there is a follow-up, follow_up_complete says
        // it is done. Reminders (a popup at the due time) waited on a notification surface that did not exist yet.
        """
        ALTER TABLE messages ADD COLUMN follow_up_due      INTEGER;
        ALTER TABLE messages ADD COLUMN follow_up_complete INTEGER NOT NULL DEFAULT 0;

        CREATE INDEX messages_by_followup ON messages (follow_up_due)
            WHERE follow_up_due IS NOT NULL;
        """,

        // ---- 14: what POP3 has already collected, whatever became of it -----------------------
        //
        // A POP3 poll knew what it had by the messages it held, so a message deleted here for
        // good — Deleted Items emptied, junk dropped — was a message it no longer knew, and with
        // leave-on-server on (the default) the next poll fetched it again as new. The UIDL of
        // everything collected is kept here for as long as the server still lists it, so a poll
        // asks "have I seen this?" rather than "do I still hold this?". Pruned against the
        // server's list after each poll, so it never outgrows the mailbox. One row per UIDL:
        // one account per file, so no account column.
        """
        CREATE TABLE pop3_seen (
            uidl            TEXT    NOT NULL PRIMARY KEY,
            first_seen_utc  INTEGER NOT NULL
        );

        INSERT OR IGNORE INTO pop3_seen (uidl, first_seen_utc)
        SELECT m.server_uid, m.received_utc
        FROM messages m JOIN folders f ON f.id = m.folder_id
        WHERE m.server_uid IS NOT NULL AND f.imap_path IS NULL
          AND (SELECT protocol FROM accounts ORDER BY id LIMIT 1) = 'pop3';
        """,

        // ---- 15: the rest of the junk lists -------------------------------------------------
        //
        // The Junk Options dialog's other three lists, beside safe_senders (7) and
        // blocked_senders (12). Safe recipients: a list or alias mail is addressed to whose
        // members are never junked, matched against To and Cc. Blocked top-level domains and
        // blocked encodings: the International tab, both matched on arrival. Senders and
        // recipients may be a whole domain, written as "@example.com" in the same column.
        """
        CREATE TABLE safe_recipients (
            address    TEXT    NOT NULL PRIMARY KEY,
            added_utc  INTEGER NOT NULL
        );

        CREATE TABLE blocked_tlds (
            tld        TEXT    NOT NULL PRIMARY KEY,
            added_utc  INTEGER NOT NULL
        );

        CREATE TABLE blocked_encodings (
            charset    TEXT    NOT NULL PRIMARY KEY,
            added_utc  INTEGER NOT NULL
        );
        """,

        // ---- 16: snooze ---------------------------------------------------------------------
        //
        // The design's Snooze: a snoozed message leaves the list until the time set here, then comes
        // back to the top of its folder as unread. Local only — the server never hears of it —
        // and a column rather than a folder, so the message stays where it is and its flags,
        // categories and threading go on meaning what they meant.
        """
        ALTER TABLE messages ADD COLUMN snooze_until INTEGER;

        CREATE INDEX messages_by_snooze ON messages (snooze_until)
            WHERE snooze_until IS NOT NULL;
        """,

        // ---- 17: rules -----------------------------------------------------------------------
        //
        // The Rules and Alerts wizard's rules, per account like everything in this file — the
        // reference keeps rules per account too. The conditions, actions and exceptions are one
        // JSON document (Mailbox.Core.Rules writes and reads it), because they are a tree the
        // wizard edits whole and nothing queries by. Ordinal is the order they run in.
        """
        CREATE TABLE rules (
            id            INTEGER PRIMARY KEY,
            name          TEXT    NOT NULL,
            enabled       INTEGER NOT NULL DEFAULT 1,
            ordinal       INTEGER NOT NULL DEFAULT 0,
            definition    TEXT    NOT NULL,
            created_utc   INTEGER NOT NULL
        );
        """,

        // ---- 18: Recover Deleted Items -----------------------------------------------------
        //
        // The design's holding area. A message deleted for good here — Deleted Items emptied,
        // Shift+Delete, a rule — keeps its raw bytes and enough of its row to be listed, for a
        // retention window, and can be put back where it was. The messages row itself goes, so
        // nothing else sees it; the blob stays, so restoring is a re-file rather than a hope.
        // Local deletes only: mail that vanished on the server was deleted somewhere else, and
        // is not this store's to keep.
        """
        CREATE TABLE recoverable (
            id                    INTEGER PRIMARY KEY,
            blob_id               INTEGER NOT NULL REFERENCES blobs(id),
            original_folder_id    INTEGER,
            original_folder_name  TEXT    NOT NULL DEFAULT '',
            message_id            TEXT,
            from_name             TEXT    NOT NULL DEFAULT '',
            from_address          TEXT    NOT NULL DEFAULT '',
            subject               TEXT    NOT NULL DEFAULT '',
            preview               TEXT    NOT NULL DEFAULT '',
            body_text             TEXT    NOT NULL DEFAULT '',
            sent_utc              INTEGER,
            received_utc          INTEGER NOT NULL,
            size_bytes            INTEGER NOT NULL DEFAULT 0,
            is_read               INTEGER NOT NULL DEFAULT 0,
            is_flagged            INTEGER NOT NULL DEFAULT 0,
            has_attachment        INTEGER NOT NULL DEFAULT 0,
            deleted_utc           INTEGER NOT NULL
        );

        CREATE INDEX recoverable_by_deleted ON recoverable (deleted_utc);
        """,

        // ---- 19: search folders, and the columns their queries need -------------------------
        //
        // A search folder is a saved query listed under Search Folders in the folder pane,
        // kept per account as Core's JSON document. Three of the reference's templates
        // — mail sent directly to me, to a public group, marked important — ask things the row
        // could not answer: who a message was sent to, and its importance. Both are on the row
        // now, filled as mail arrives; existing rows read as sent to nobody at normal importance
        // until they are refetched, and the templates say so.
        """
        ALTER TABLE messages ADD COLUMN importance   INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE messages ADD COLUMN to_addresses TEXT    NOT NULL DEFAULT '';
        ALTER TABLE messages ADD COLUMN cc_addresses TEXT    NOT NULL DEFAULT '';

        CREATE TABLE search_folders (
            id            INTEGER PRIMARY KEY,
            name          TEXT    NOT NULL,
            ordinal       INTEGER NOT NULL DEFAULT 0,
            definition    TEXT    NOT NULL,
            created_utc   INTEGER NOT NULL
        );
        """,

        // ---- 20: the rest of a follow-up flag ------------------------------------------------
        //
        // The Custom flag dialog's other three fields: what the flag says ("Follow up", "Call",
        // "Review" and the rest), when it starts, and when to be reminded. The reminder is what
        // the Reminders window and its toast fire on; dismissed is null, snoozed is later. Left
        // null on every existing flag, which reads as "no reminder", which is what they had.
        """
        ALTER TABLE messages ADD COLUMN follow_up_type  TEXT;
        ALTER TABLE messages ADD COLUMN follow_up_start INTEGER;
        ALTER TABLE messages ADD COLUMN reminder_utc    INTEGER;

        CREATE INDEX messages_by_reminder ON messages (reminder_utc)
            WHERE reminder_utc IS NOT NULL;
        """,

        // ---- 21: Focused Inbox -----------------------------------------------------------------
        //
        // The design's Focused Inbox: each message is Focused or Other, decided locally as it arrives
        // and changeable by hand; a sender the reader has said "always" about is remembered so
        // the next message from them goes where the reader put the last. Existing rows are
        // Focused, which is what an Inbox with the feature off already was.
        """
        ALTER TABLE messages ADD COLUMN is_focused INTEGER NOT NULL DEFAULT 1;

        CREATE TABLE focus_overrides (
            address    TEXT    NOT NULL PRIMARY KEY,
            focused    INTEGER NOT NULL,
            added_utc  INTEGER NOT NULL
        );
        """,

        // ---- 22: Ignore Conversation ----------------------------------------------------------
        //
        // A conversation the reader has ignored: what is in it goes to Deleted Items, and so does
        // every message that arrives in it after — an arrival handler checks the thread key. Keyed
        // on the same normalised subject the list threads by, so "ignore" and "conversation" mean
        // the same thing here as on screen. Stop Ignoring deletes the row.
        """
        CREATE TABLE ignored_conversations (
            thread_key  TEXT    NOT NULL PRIMARY KEY,
            subject     TEXT    NOT NULL DEFAULT '',
            added_utc   INTEGER NOT NULL
        );
        """,

        // ---- 23: server-side rules -----------------------------------------------------------
        //
        // A rule marked server_side is compiled to Sieve and put on the server by ManageSieve;
        // it runs there, and RulesHandler leaves it alone here while the server has the current
        // script. sieve_state is one row: the script last put on the server, when, the script
        // that was active before it (included first so it keeps running), and whether the
        // server is behind — set when rules or folder names change or a publish fails, cleared
        // by the next successful publish. While it is behind, the server-side rules run here
        // as well, so nothing is lost for a publish that could not happen.
        """
        ALTER TABLE rules ADD COLUMN server_side INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE sieve_state (
            id             INTEGER PRIMARY KEY CHECK (id = 1),
            script         TEXT    NOT NULL,
            include        TEXT,
            published_utc  INTEGER NOT NULL,
            stale          INTEGER NOT NULL DEFAULT 0
        );
        """,

        // ---- 24: views ------------------------------------------------------------------------
        //
        // Change View and Advanced View Settings. A folder's current view — layout, columns,
        // grouping, sort, filter, formatting — is one JSON document on the folder (Core's
        // MailView writes and reads it), null for the shipped Compact view untouched. Views a
        // reader saves by name live in `views`, per account like everything here, so Manage
        // Views can offer them to any folder and Reset can go back to what was saved.
        """
        ALTER TABLE folders ADD COLUMN view_json TEXT;
        CREATE TABLE views (
            id            INTEGER PRIMARY KEY,
            name          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            definition    TEXT    NOT NULL,
            created_utc   INTEGER NOT NULL
        );
        """,

        // ---- 25: AutoArchive ------------------------------------------------------------------
        //
        // A message's own expiry — its Expires header, when it has one — so AutoArchive's
        // "delete expired items" has something to go on; and a folder's own AutoArchive choice
        // (Core's FolderArchivePolicy as JSON: off, the default settings, or its own), null for a
        // folder that follows the default.
        """
        ALTER TABLE messages ADD COLUMN expires_utc INTEGER;
        ALTER TABLE folders ADD COLUMN autoarchive_json TEXT;
        """,

        // ---- 26: headers without their messages ------------------------------------------------
        //
        // Send/Receive's Server group. `header_only` marks a row the server has told us about and
        // whose message has not been fetched — sender, subject, size and date, with no blob under
        // it; `marked_download` is the reader saying they want that one after all. Both are false
        // for every row an ordinary sync writes, which is why they default to 0 rather than being
        // backfilled.
        """
        ALTER TABLE messages ADD COLUMN header_only INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE messages ADD COLUMN marked_download INTEGER NOT NULL DEFAULT 0;
        """,

        // ---- 27: what an article list has to draw -----------------------------------------------
        //
        // A feed item's own address and its picture. Both are already in the message — as
        // X-Mailbox-Feed-Link and X-Mailbox-Feed-Image — and both are needed once per visible
        // row: the article list draws a thumbnail beside every entry, and opening the original
        // is one press. Reading them from the message would mean loading and parsing the whole
        // of it for every row on screen, so they are columns.
        //
        // Null for everything that is not a feed item, which is nearly every row in the store.
        """
        ALTER TABLE messages ADD COLUMN feed_link  TEXT;
        ALTER TABLE messages ADD COLUMN feed_image TEXT;
        """,

        // ---- 28: boards --------------------------------------------------------------------------
        //
        // Named collections an article is saved into. Its own pair of tables rather than a reuse
        // of the colour categories, which are the same shape and the wrong meaning: a board would
        // then appear in the mail module's Categorize menu and every colour category would appear
        // in the boards pane, and both are wrong in a way no amount of naming fixes.
        //
        // The join carries when it was saved, because that is what a board is ordered by — a keep
        // pile is read newest-kept-first, not newest-published-first, and a category assignment
        // has nowhere to put that.
        //
        // Both sides cascade: deleting a board leaves its articles alone, and deleting an article
        // takes its membership with it.
        """
        CREATE TABLE boards (
            id            INTEGER PRIMARY KEY,
            name          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            description   TEXT    NOT NULL DEFAULT '',
            ordinal       INTEGER NOT NULL DEFAULT 0,
            created_utc   INTEGER NOT NULL
        );

        CREATE TABLE board_items (
            board_id      INTEGER NOT NULL REFERENCES boards(id)   ON DELETE CASCADE,
            message_id    INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            saved_utc     INTEGER NOT NULL,
            PRIMARY KEY (board_id, message_id)
        );

        CREATE INDEX board_items_by_message ON board_items (message_id);
        """,

        // ---- 29: how long an article takes to read ------------------------------------------------
        //
        // A column, for the reason the feed's link and picture are columns: the article list draws
        // it on every visible row, and working it out would mean loading and parsing the whole
        // message for each one. Words rather than minutes, because what counts as a minute is a
        // rendering decision and the words are the fact.
        //
        // Null for everything that is not a feed article, and for feed articles filed before this
        // existed — the list says nothing for those rather than guessing.
        """
        ALTER TABLE messages ADD COLUMN feed_words INTEGER;
        """,

        // ---- 30: the search index, redeclared over the column messages actually has ---------
        //
        // messages_fts declared `body` while the content table calls the column `body_text`, so
        // every read-through and the rebuild command answered "no such column: T.body". Nothing
        // ever noticed — the triggers pass their values explicitly and every query joins on the
        // rowid — but an index that cannot be rebuilt is an index that cannot be repaired.
        // Dropping the virtual table drops its shadow tables; the triggers are dropped and
        // remade against the new name, and the rebuild refills the index from the content table
        // itself — the operation this step exists to make possible, run here as its own proof.
        """
        DROP TRIGGER messages_fts_insert;
        DROP TRIGGER messages_fts_delete;
        DROP TRIGGER messages_fts_update;
        DROP TABLE messages_fts;

        CREATE VIRTUAL TABLE messages_fts USING fts5 (
            subject,
            from_name,
            from_address,
            preview,
            body_text,
            content = 'messages',
            content_rowid = 'id',
            tokenize = 'unicode61 remove_diacritics 2'
        );

        INSERT INTO messages_fts (messages_fts) VALUES ('rebuild');

        CREATE TRIGGER messages_fts_insert AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts (rowid, subject, from_name, from_address, preview, body_text)
            VALUES (new.id, new.subject, new.from_name, new.from_address, new.preview, new.body_text);
        END;

        CREATE TRIGGER messages_fts_delete AFTER DELETE ON messages BEGIN
            INSERT INTO messages_fts (messages_fts, rowid, subject, from_name, from_address,
                                      preview, body_text)
            VALUES ('delete', old.id, old.subject, old.from_name, old.from_address,
                    old.preview, old.body_text);
        END;

        CREATE TRIGGER messages_fts_update AFTER UPDATE ON messages BEGIN
            INSERT INTO messages_fts (messages_fts, rowid, subject, from_name, from_address,
                                      preview, body_text)
            VALUES ('delete', old.id, old.subject, old.from_name, old.from_address,
                    old.preview, old.body_text);
            INSERT INTO messages_fts (rowid, subject, from_name, from_address, preview, body_text)
            VALUES (new.id, new.subject, new.from_name, new.from_address, new.preview, new.body_text);
        END;
        """,

        // ---- 31: one name per shelf, the top level included ---------------------------------
        //
        // folders has carried UNIQUE (account_id, parent_id, name) from the start and it never
        // fired at an account's top level, because SQLite holds two NULL parent_ids to be
        // distinct rows. Two Archives could stand side by side — and Favourites, keyed on the
        // path, marked both for either. The partial index is the constraint the original
        // declaration meant. Twins a store already holds are renamed first, the id as the
        // suffix because it is the one string guaranteed not to make a third twin; the first
        // of each name keeps it.
        """
        UPDATE folders SET name = name || ' (' || id || ')'
        WHERE parent_id IS NULL
          AND EXISTS (SELECT 1 FROM folders earlier
                      WHERE earlier.account_id = folders.account_id
                        AND earlier.parent_id IS NULL
                        AND earlier.name = folders.name
                        AND earlier.id < folders.id);

        CREATE UNIQUE INDEX folders_top_level_name
            ON folders (account_id, name) WHERE parent_id IS NULL;
        """,

        // ---- 32: replies find their parents ------------------------------------------------
        //
        // The thread key is decided by the reply headers now, and storing a message asks two
        // questions the schema had no index for: whose reply is this, and has this message's
        // reply already arrived. message_id has carried an index from the start; this is the
        // other half, partial because most mail is not a reply.
        """
        CREATE INDEX messages_by_in_reply_to ON messages (in_reply_to)
            WHERE in_reply_to IS NOT NULL;
        """,

        // ---- 33: what kind of item a message is --------------------------------------------
        //
        // By Type could only ever say "Message": nothing on a row recorded that it carries a
        // meeting request or a read receipt. The mark is written when the message is stored —
        // detection needs the MIME, and the list must not open blobs to draw headers — and
        // NULL, the ordinary message, is what every existing row already means.
        """
        ALTER TABLE messages ADD COLUMN item_type TEXT;
        """,

        // ---- 34: what the server holds beyond the offline window ---------------------------
        //
        // A windowed folder looked complete: nothing recorded that the server held older mail,
        // so the list could not say so and nothing could offer to fetch it. The sync counts
        // what it skipped for being older than the window and writes it here; zero — every
        // existing folder's value — is what a fully-downloaded folder already means.
        """
        ALTER TABLE folders ADD COLUMN server_older INTEGER NOT NULL DEFAULT 0;
        """,

        // ---- 35: answered and forwarded, the Icon column's other two states ----------------
        //
        // Nothing recorded that a message had been replied to or forwarded, so the Icon column
        // could only tell read from unread. Stamped when a reply or forward of the message is
        // queued, and taken from the server's own \Answered and $Forwarded marks on sync;
        // zero — every existing row — is what a message never answered already means.
        //
        // The journal's flag column carries the two new marks to the server, and its CHECK
        // named only the first two — which SQLite cannot widen in place, so the table is
        // rebuilt around whatever ops are waiting. The indexes go and come back by the same
        // names; the foreign keys are re-declared as they were.
        """
        ALTER TABLE messages ADD COLUMN answered INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE messages ADD COLUMN forwarded INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE sync_ops_new (
            id                INTEGER PRIMARY KEY,
            kind              TEXT    NOT NULL CHECK (kind IN ('flags', 'move', 'delete', 'append')),
            folder_id         INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            server_uid        TEXT,
            message_id        INTEGER          REFERENCES messages(id) ON DELETE SET NULL,
            target_folder_id  INTEGER          REFERENCES folders(id)  ON DELETE CASCADE,
            flag              TEXT    CHECK (flag IN ('seen', 'flagged', 'answered', 'forwarded')),
            value             INTEGER,
            created_utc       INTEGER NOT NULL,
            attempts          INTEGER NOT NULL DEFAULT 0,
            last_error        TEXT
        );

        INSERT INTO sync_ops_new SELECT * FROM sync_ops;
        DROP TABLE sync_ops;
        ALTER TABLE sync_ops_new RENAME TO sync_ops;

        CREATE INDEX sync_ops_by_folder ON sync_ops (folder_id, server_uid);
        CREATE INDEX sync_ops_by_message ON sync_ops (message_id);
        """,

        // ---- 36: the read-receipt question is asked once -----------------------------------
        //
        // A message that asks for a read receipt is answered when it is first displayed —
        // sent, or declined, per the Tracking radios — and this records that the question is
        // settled either way, so a message re-opened across sessions is never asked about
        // twice. Local bookkeeping only: nothing on the server carries it, so it is never
        // journalled. Zero — every existing row — means the question is still open, which for
        // old mail somebody has plainly already seen errs on the side of asking.
        """
        ALTER TABLE messages ADD COLUMN receipt_settled INTEGER NOT NULL DEFAULT 0;
        """,
    ];

    /// <summary>The version a store is brought up to.</summary>
    public static int Latest => Steps.Count;
}
