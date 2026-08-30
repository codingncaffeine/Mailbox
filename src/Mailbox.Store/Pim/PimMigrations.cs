namespace Mailbox.Store.Pim;

/// <summary>
/// The PIM store's schema — calendars now, task lists, note lists and address books as their
/// phases arrive — as an ordered list of steps that only ever gets appended to, for the same
/// reasons the mail store's is (<see cref="Schema.Migrations"/>).
/// </summary>
/// <remarks>
/// One collection table serves every kind, as the schema sketch has it: a
/// collection is a calendar, a task list, a note list or an address book, told apart by
/// <c>kind</c>; an item is one VEVENT, VTODO, VJOURNAL or vCard, its raw payload kept verbatim
/// beside the columns the views read — a parsing mistake is recoverable, and a server gets back
/// exactly what it sent plus what changed. Times are stored as UTC instants for querying and
/// as the local wall time with its zone for correctness across a DST change: an 09:00
/// weekly meeting stays at 09:00.
/// </remarks>
public static class PimMigrations
{
    public static readonly IReadOnlyList<string> Steps =
    [
        // ---- 1: collections and items ------------------------------------------------------
        """
        CREATE TABLE collections (
            id            INTEGER PRIMARY KEY,
            account       TEXT    NOT NULL DEFAULT '',        -- the DAV account's address; '' for a local collection
            kind          TEXT    NOT NULL CHECK (kind IN ('vevent', 'vtodo', 'vjournal', 'vcard')),
            display_name  TEXT    NOT NULL,
            color         TEXT    NOT NULL DEFAULT '',        -- a category token or #RRGGBB
            dav_url       TEXT,
            ctag          TEXT,
            sync_token    TEXT,
            is_visible    INTEGER NOT NULL DEFAULT 1,
            is_readonly   INTEGER NOT NULL DEFAULT 0,
            is_default    INTEGER NOT NULL DEFAULT 0,
            ordinal       INTEGER NOT NULL DEFAULT 0,
            created_utc   INTEGER NOT NULL
        );

        CREATE TABLE pim_items (
            id               INTEGER PRIMARY KEY,
            collection_id    INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            uid              TEXT    NOT NULL,
            kind             TEXT    NOT NULL,
            dav_href         TEXT,
            etag             TEXT,
            raw_payload      TEXT    NOT NULL,                -- the iCalendar / vCard text, verbatim
            summary          TEXT    NOT NULL DEFAULT '',
            description      TEXT    NOT NULL DEFAULT '',
            location         TEXT    NOT NULL DEFAULT '',
            starts_utc       INTEGER,                        -- unix seconds; the first occurrence for a series
            ends_utc         INTEGER,
            starts_local     TEXT,                           -- yyyy-MM-ddTHH:mm:ss, the wall time as written
            ends_local       TEXT,
            tz_id            TEXT,                           -- IANA zone of the wall time, or NULL for floating / UTC
            all_day          INTEGER NOT NULL DEFAULT 0,
            status           TEXT    NOT NULL DEFAULT '',
            priority         INTEGER NOT NULL DEFAULT 0,
            percent_complete INTEGER NOT NULL DEFAULT 0,
            completed_utc    INTEGER,
            rrule            TEXT,                           -- the RRULE line, when the item repeats
            recurrence_id    TEXT,                           -- for an override: which occurrence it replaces
            is_override      INTEGER NOT NULL DEFAULT 0,
            sequence         INTEGER NOT NULL DEFAULT 0,
            organizer        TEXT    NOT NULL DEFAULT '',
            busy             TEXT    NOT NULL DEFAULT 'busy',-- free | tentative | busy | oof
            reminder_minutes INTEGER,                        -- VALARM trigger before the start, or NULL
            categories       TEXT    NOT NULL DEFAULT '',    -- comma-separated category names
            last_modified    INTEGER NOT NULL,
            sync_state       TEXT    NOT NULL DEFAULT 'synced' -- synced | new | modified | deleted (a local change the server has not seen)
        );

        CREATE INDEX pim_items_collection ON pim_items(collection_id, starts_utc);
        CREATE INDEX pim_items_uid ON pim_items(collection_id, uid, recurrence_id);

        CREATE TABLE pim_attendees (
            item_id   INTEGER NOT NULL REFERENCES pim_items(id) ON DELETE CASCADE,
            address   TEXT    NOT NULL,
            name      TEXT    NOT NULL DEFAULT '',
            role      TEXT    NOT NULL DEFAULT '',
            partstat  TEXT    NOT NULL DEFAULT '',
            rsvp      INTEGER NOT NULL DEFAULT 0,
            ordinal   INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE dav_queue (
            id             INTEGER PRIMARY KEY,
            collection_id  INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            item_id        INTEGER,
            op             TEXT    NOT NULL CHECK (op IN ('put', 'delete')),
            state          TEXT    NOT NULL DEFAULT 'queued',
            attempts       INTEGER NOT NULL DEFAULT 0,
            last_error     TEXT,
            created_utc    INTEGER NOT NULL
        );

        CREATE VIRTUAL TABLE pim_fts USING fts5(
            summary, description, location, attendees,
            tokenize='unicode61 remove_diacritics 2'
        );
        """,

        // ---- 2: reminders ------------------------------------------------------------------
        // What has been dismissed is kept as the *start* of the occurrence it was dismissed for,
        // not as a flag: a repeating appointment's reminder has to come round again next week,
        // and a boolean would silence the whole series the first time it was dismissed.
        """
        ALTER TABLE pim_items ADD COLUMN reminder_dismissed_utc INTEGER;
        ALTER TABLE pim_items ADD COLUMN reminder_snoozed_utc INTEGER;
        """,

        // ---- 3: contacts -------------------------------------------------------------------
        // A contact's own columns beside the vCard, for the same reason an appointment has its
        // own: the list sorts and indexes by them and never parses a card to draw a row. File As
        // is the one that matters most — it is what the list orders by and what the index letters
        // down its side are taken from, and it is a decision a person can make and keep.
        //
        // Addresses and numbers go in a table rather than in three columns each. "Who is
        // a.person@example.com?" is a question the reading pane, the autocomplete and the group
        // editor all ask, and it wants an index, not three OR clauses. Photographs go in a table
        // of their own so that listing five hundred contacts does not read five hundred
        // photographs.
        """
        ALTER TABLE pim_items ADD COLUMN file_as    TEXT    NOT NULL DEFAULT '';
        ALTER TABLE pim_items ADD COLUMN first_name TEXT    NOT NULL DEFAULT '';
        ALTER TABLE pim_items ADD COLUMN last_name  TEXT    NOT NULL DEFAULT '';
        ALTER TABLE pim_items ADD COLUMN company    TEXT    NOT NULL DEFAULT '';
        ALTER TABLE pim_items ADD COLUMN job_title  TEXT    NOT NULL DEFAULT '';
        ALTER TABLE pim_items ADD COLUMN is_group   INTEGER NOT NULL DEFAULT 0;

        CREATE INDEX pim_items_filed ON pim_items(collection_id, file_as);

        CREATE TABLE pim_contact_fields (
            item_id  INTEGER NOT NULL REFERENCES pim_items(id) ON DELETE CASCADE,
            kind     TEXT    NOT NULL CHECK (kind IN ('email', 'phone', 'im')),
            value    TEXT    NOT NULL,
            label    TEXT    NOT NULL DEFAULT '',   -- business | home | mobile | businessfax | ...
            ordinal  INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX pim_contact_fields_item ON pim_contact_fields(item_id);
        CREATE INDEX pim_contact_fields_value ON pim_contact_fields(kind, value);

        CREATE TABLE pim_photos (
            item_id    INTEGER PRIMARY KEY REFERENCES pim_items(id) ON DELETE CASCADE,
            media_type TEXT NOT NULL DEFAULT 'image/jpeg',
            bytes      BLOB NOT NULL
        );
        """,

        // ---- 4: the colour categories -------------------------------------------------------
        // One set across every module, which is what the reference has: a
        // message, an appointment, a task, a note and a contact all take their colour from the
        // same list. It lives here rather than in a mail store because this file is the one
        // every module shares — a per-account list would give the same reader two of them.
        //
        // Items refer to a category by *name*, because that is what the standards carry:
        // iCalendar's CATEGORIES and vCard's are lists of names, and a note written here has to
        // arrive on another client saying what it says. The mail stores keep their own rows so
        // their join table still has something to point at; those are a mirror of this table,
        // kept in step by name.
        """
        CREATE TABLE categories (
            id           INTEGER PRIMARY KEY,
            name         TEXT    NOT NULL,
            colour_token TEXT    NOT NULL,
            shortcut     TEXT,
            ordinal      INTEGER NOT NULL DEFAULT 0
        );

        CREATE UNIQUE INDEX categories_name ON categories(name COLLATE NOCASE);
        """,

        // ---- 5: private items ----------------------------------------------------------------
        // RFC 5545's CLASS, as a column. The raw text is still the truth — this is what a list
        // reads, and a list draws its rows from the columns rather than parsing every item to
        // find out whether one of them is private.
        //
        // A column rather than a per-kind one because CLASS belongs to every component the file
        // holds: an appointment, a task and a journal entry all state it the same way, and a
        // vCard's own privacy is the same idea under another name.
        """
        ALTER TABLE pim_items ADD COLUMN is_private INTEGER NOT NULL DEFAULT 0;
        """,

        // ---- 6: the flag on an item ----------------------------------------------------------
        // The reference flags a contact the way it flags a message, and puts both on the same
        // to-do list. A vCard has nothing to say about a flag — when somebody means to ring a
        // person back is this reader's business and not the card's — so it lives beside the card
        // rather than in it, which is the call the folder pane's Favourites section makes too.
        //
        // Two columns rather than one, for the reason mail's are two: a flag that has been dealt
        // with is not the same as no flag, and the list draws the difference.
        """
        ALTER TABLE pim_items ADD COLUMN follow_up_due      INTEGER;
        ALTER TABLE pim_items ADD COLUMN follow_up_complete INTEGER NOT NULL DEFAULT 0;
        """,

        // ---- 7: linked contacts --------------------------------------------------------------
        // The card's X-MAILBOX-LINK lines, mirrored as a column the way is_private mirrors
        // CLASS: the People list shows linked cards as one person, and a list that had to parse
        // every card to know who links to whom would parse the whole book on every load. The
        // text is still the truth; the column is what a list may read. Newline-separated UIDs,
        // empty for the unlinked — including cards written before this step, which fill in the
        // next time they are saved.
        """
        ALTER TABLE pim_items ADD COLUMN links TEXT NOT NULL DEFAULT '';
        """,

        // ---- 8: when a collection was last checked -------------------------------------------
        // The Internet Calendars tab draws a "Last Updated on" column, and there was nothing
        // truthful to put in it: ctag and sync_token say what the server last showed, not when
        // this machine last looked, and the two part company exactly where the column is read.
        // A subscription nobody has published to for a month is still being checked every
        // download interval, and "is this still live?" is a question about the check.
        //
        // NULL is never checked, which is not the epoch: a subscription added a moment ago has
        // not been anywhere yet, and the column has to be able to say so.
        """
        ALTER TABLE collections ADD COLUMN last_checked_utc INTEGER;
        """,
    ];

    public static int Latest => Steps.Count;
}
