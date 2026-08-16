namespace Mailbox.Store.Pim;

/// <summary>
/// The PIM store's schema — calendars now, task lists, note lists and address books as their
/// phases arrive — as an ordered list of steps that only ever gets appended to, for the same
/// reasons the mail store's is (<see cref="Schema.Migrations"/>).
/// </summary>
/// <remarks>
/// One collection table serves every kind, as the plan's schema sketch has it (§4): a
/// collection is a calendar, a task list, a note list or an address book, told apart by
/// <c>kind</c>; an item is one VEVENT, VTODO, VJOURNAL or vCard, its raw payload kept verbatim
/// beside the columns the views read — a parsing mistake is recoverable, and a server gets back
/// exactly what it sent plus what changed. Times are stored as UTC instants for querying and
/// as the local wall time with its zone for correctness across a DST change (§9): an 09:00
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
    ];

    public static int Latest => Steps.Count;
}
