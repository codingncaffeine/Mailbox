using Microsoft.Data.Sqlite;

namespace Mailbox.Store.Pim;

/// <summary>
/// Everything the modules ask of the PIM store: the collections, the items in them, and the
/// items in a span of time — which is what a calendar view is.
/// </summary>
public sealed class PimRepository(PimStore store)
{
    private readonly PimStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public PimStore Store => _store;

    // ---- Collections ------------------------------------------------------------------------

    private const string CollectionSelect =
        "SELECT id, account, kind, display_name, color, dav_url, ctag, sync_token, is_visible, is_readonly, is_default, ordinal FROM collections";

    public IReadOnlyList<Collection> Collections(CollectionKind? kind = null)
        => kind is { } k
            ? _store.Query(CollectionSelect + " WHERE kind = $kind ORDER BY ordinal, id", ReadCollection, ("$kind", KindText(k)))
            : _store.Query(CollectionSelect + " ORDER BY kind, ordinal, id", ReadCollection);

    public Collection? Collection(long id)
        => _store.Query(CollectionSelect + " WHERE id = $id", ReadCollection, ("$id", id)).FirstOrDefault();

    /// <summary>Makes a collection; the first of its kind becomes the default.</summary>
    public Collection AddCollection(CollectionKind kind, string displayName, string color = "", string account = "", string? davUrl = null, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return _store.InTransaction(() =>
        {
            var first = _store.ScalarLong("SELECT COUNT(*) FROM collections WHERE kind = $kind", ("$kind", KindText(kind))) == 0;
            var ordinal = _store.ScalarLong("SELECT COALESCE(MAX(ordinal), -1) + 1 FROM collections WHERE kind = $kind", ("$kind", KindText(kind)));
            _store.Execute(
                """
                INSERT INTO collections (account, kind, display_name, color, dav_url, is_readonly, is_default, ordinal, created_utc)
                VALUES ($account, $kind, $name, $color, $url, $readonly, $default, $ordinal, $now)
                """,
                ("$account", account ?? string.Empty), ("$kind", KindText(kind)), ("$name", displayName.Trim()), ("$color", color ?? string.Empty),
                ("$url", davUrl), ("$readonly", readOnly ? 1 : 0), ("$default", first ? 1 : 0), ("$ordinal", ordinal),
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            return Collection(_store.LastInsertId)!;
        });
    }

    /// <summary>The calendar new appointments go into — made, named "Calendar", when there is none, as the reference starts with one.</summary>
    public Collection DefaultCalendar()
    {
        var calendars = Collections(CollectionKind.Events);
        return calendars.FirstOrDefault(c => c.IsDefault) ?? calendars.FirstOrDefault() ?? AddCollection(CollectionKind.Events, "Calendar");
    }

    public void RenameCollection(long id, string displayName)
        => _store.Execute("UPDATE collections SET display_name = $name WHERE id = $id", ("$name", displayName.Trim()), ("$id", id));

    public void SetCollectionColor(long id, string color)
        => _store.Execute("UPDATE collections SET color = $color WHERE id = $id", ("$color", color ?? string.Empty), ("$id", id));

    public void SetCollectionVisible(long id, bool visible)
        => _store.Execute("UPDATE collections SET is_visible = $v WHERE id = $id", ("$v", visible ? 1 : 0), ("$id", id));

    /// <summary>One default per kind: this one, and the others of its kind not.</summary>
    public void SetDefaultCollection(long id)
        => _store.InTransaction(() =>
        {
            var kind = _store.Query("SELECT kind FROM collections WHERE id = $id", r => r.GetString(0), ("$id", id)).FirstOrDefault();
            if (kind is null) return 0;
            _store.Execute("UPDATE collections SET is_default = 0 WHERE kind = $kind", ("$kind", kind));
            return _store.Execute("UPDATE collections SET is_default = 1 WHERE id = $id", ("$id", id));
        });

    public void SetCollectionSync(long id, string? ctag, string? syncToken)
        => _store.Execute("UPDATE collections SET ctag = $ctag, sync_token = $token WHERE id = $id", ("$ctag", ctag), ("$token", syncToken), ("$id", id));

    /// <summary>Removes a collection and everything in it.</summary>
    public void RemoveCollection(long id)
        => _store.Execute("DELETE FROM collections WHERE id = $id", ("$id", id));

    // ---- Items ------------------------------------------------------------------------------

    private const string ItemSelect =
        """
        SELECT id, collection_id, uid, kind, dav_href, etag, raw_payload, summary, description, location,
               starts_utc, ends_utc, starts_local, ends_local, tz_id, all_day, status, priority, percent_complete,
               completed_utc, rrule, recurrence_id, is_override, sequence, organizer, busy, reminder_minutes,
               categories, last_modified, sync_state
        FROM pim_items
        """;

    public PimItem? Item(long id)
        => _store.Query(ItemSelect + " WHERE id = $id", ReadItem, ("$id", id)).FirstOrDefault();

    /// <summary>The master and every override that share a UID within a collection.</summary>
    public IReadOnlyList<PimItem> ItemsByUid(long collectionId, string uid)
        => _store.Query(ItemSelect + " WHERE collection_id = $c AND uid = $uid ORDER BY is_override, id", ReadItem, ("$c", collectionId), ("$uid", uid));

    /// <summary>Everything in a collection, for a sync or an export.</summary>
    public IReadOnlyList<PimItem> Items(long collectionId)
        => _store.Query(ItemSelect + " WHERE collection_id = $c ORDER BY starts_utc, id", ReadItem, ("$c", collectionId));

    /// <summary>
    /// The items a span of time can show: every one whose instants touch it, and every
    /// repeating master that started before it ends — which the scheduling layer then expands
    /// into the occurrences that actually fall inside. Overrides ride along with their master
    /// by UID. Only visible collections, unless asked otherwise.
    /// </summary>
    public IReadOnlyList<PimItem> ItemsBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyCollection<long>? collectionIds = null, CollectionKind kind = CollectionKind.Events)
    {
        var from = fromUtc.ToUnixTimeSeconds();
        var to = toUtc.ToUnixTimeSeconds();
        var scope = collectionIds is { Count: > 0 }
            ? $" AND i.collection_id IN ({string.Join(",", collectionIds.Select(id => id.ToString()))})"
            : " AND c.is_visible = 1";

        return _store.Query(
            ItemSelect
            + $"""

                WHERE id IN (
                    SELECT i.id FROM pim_items i JOIN collections c ON c.id = i.collection_id
                    WHERE i.kind = $kind AND i.sync_state <> 'deleted'{scope}
                      AND (
                            (i.starts_utc IS NOT NULL AND i.starts_utc < $to AND COALESCE(i.ends_utc, i.starts_utc + 1) > $from)
                         OR (i.rrule IS NOT NULL AND i.is_override = 0 AND (i.starts_utc IS NULL OR i.starts_utc < $to))
                         OR (i.is_override = 1 AND i.uid IN (
                                SELECT m.uid FROM pim_items m WHERE m.collection_id = i.collection_id AND m.rrule IS NOT NULL AND m.is_override = 0
                                  AND (m.starts_utc IS NULL OR m.starts_utc < $to)))
                      ))
                ORDER BY starts_utc, id
                """,
            ReadItem, ("$kind", KindText(kind)), ("$from", from), ("$to", to));
    }

    /// <summary>Stores a new item and returns it with its id.</summary>
    public PimItem AddItem(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _store.InTransaction(() =>
        {
            _store.Execute(
                """
                INSERT INTO pim_items
                    (collection_id, uid, kind, dav_href, etag, raw_payload, summary, description, location,
                     starts_utc, ends_utc, starts_local, ends_local, tz_id, all_day, status, priority, percent_complete,
                     completed_utc, rrule, recurrence_id, is_override, sequence, organizer, busy, reminder_minutes,
                     categories, last_modified, sync_state)
                VALUES
                    ($collection, $uid, $kind, $href, $etag, $raw, $summary, $description, $location,
                     $starts, $ends, $startsLocal, $endsLocal, $tz, $allDay, $status, $priority, $percent,
                     $completed, $rrule, $recurrenceId, $override, $sequence, $organizer, $busy, $reminder,
                     $categories, $modified, $sync)
                """,
                Parameters(item));
            var id = _store.LastInsertId;
            IndexItem(id, item);
            return item with { Id = id };
        });
    }

    /// <summary>Replaces an item's columns and text by id.</summary>
    public bool UpdateItem(PimItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _store.InTransaction(() =>
        {
            var changed = _store.Execute(
                """
                UPDATE pim_items SET
                    collection_id = $collection, uid = $uid, kind = $kind, dav_href = $href, etag = $etag, raw_payload = $raw,
                    summary = $summary, description = $description, location = $location,
                    starts_utc = $starts, ends_utc = $ends, starts_local = $startsLocal, ends_local = $endsLocal, tz_id = $tz,
                    all_day = $allDay, status = $status, priority = $priority, percent_complete = $percent, completed_utc = $completed,
                    rrule = $rrule, recurrence_id = $recurrenceId, is_override = $override, sequence = $sequence, organizer = $organizer,
                    busy = $busy, reminder_minutes = $reminder, categories = $categories, last_modified = $modified, sync_state = $sync
                WHERE id = $id
                """,
                [.. Parameters(item), ("$id", item.Id)]);
            if (changed > 0) IndexItem(item.Id, item);
            return changed > 0;
        });
    }

    /// <summary>Marks how the server stands to an item, without touching its content.</summary>
    public void SetSyncState(long id, PimSyncState state, string? etag = null, string? href = null)
        => _store.Execute(
            "UPDATE pim_items SET sync_state = $state, etag = COALESCE($etag, etag), dav_href = COALESCE($href, dav_href) WHERE id = $id",
            ("$state", SyncText(state)), ("$etag", etag), ("$href", href), ("$id", id));

    /// <summary>Removes an item for good — its overrides too, when it is a series' master.</summary>
    public int DeleteItem(long id)
        => _store.InTransaction(() =>
        {
            var master = Item(id);
            if (master is null) return 0;
            _store.Execute("DELETE FROM pim_fts WHERE rowid = $id", ("$id", id));
            var removed = _store.Execute("DELETE FROM pim_items WHERE id = $id", ("$id", id));
            if (!master.IsOverride && master.Rrule is not null)
            {
                foreach (var over in ItemsByUid(master.CollectionId, master.Uid).Where(i => i.IsOverride))
                {
                    _store.Execute("DELETE FROM pim_fts WHERE rowid = $id", ("$id", over.Id));
                    removed += _store.Execute("DELETE FROM pim_items WHERE id = $id", ("$id", over.Id));
                }
            }

            return removed;
        });

    /// <summary>Full-text search over summaries, descriptions, locations and attendees, best first.</summary>
    public IReadOnlyList<PimItem> Search(string query, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var ids = _store.Query(
            "SELECT rowid FROM pim_fts WHERE pim_fts MATCH $q ORDER BY bm25(pim_fts) LIMIT $limit",
            r => r.GetInt64(0), ("$q", FtsQuery(query)), ("$limit", limit));
        return ids.Select(Item).Where(i => i is not null).Cast<PimItem>().ToList();
    }

    private void IndexItem(long id, PimItem item)
    {
        _store.Execute("DELETE FROM pim_fts WHERE rowid = $id", ("$id", id));
        _store.Execute(
            "INSERT INTO pim_fts (rowid, summary, description, location, attendees) VALUES ($id, $s, $d, $l, $a)",
            ("$id", id), ("$s", item.Summary), ("$d", item.Description), ("$l", item.Location),
            ("$a", string.Join(' ', Attendees(id).Select(a => a.Name.Length > 0 ? a.Name + " " + a.Address : a.Address))));
    }

    /// <summary>A search string as an FTS5 query: every word a prefix, so "stan" finds "standup".</summary>
    private static string FtsQuery(string text)
        => string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => "\"" + w.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));

    // ---- Attendees --------------------------------------------------------------------------

    public sealed record Attendee(string Address, string Name, string Role, string PartStat, bool Rsvp);

    public IReadOnlyList<Attendee> Attendees(long itemId)
        => _store.Query(
            "SELECT address, name, role, partstat, rsvp FROM pim_attendees WHERE item_id = $id ORDER BY ordinal",
            r => new Attendee(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0), ("$id", itemId));

    public void SetAttendees(long itemId, IReadOnlyList<Attendee> attendees)
        => _store.InTransaction(() =>
        {
            _store.Execute("DELETE FROM pim_attendees WHERE item_id = $id", ("$id", itemId));
            var ordinal = 0;
            foreach (var a in attendees)
            {
                _store.Execute(
                    "INSERT INTO pim_attendees (item_id, address, name, role, partstat, rsvp, ordinal) VALUES ($id, $address, $name, $role, $partstat, $rsvp, $ordinal)",
                    ("$id", itemId), ("$address", a.Address), ("$name", a.Name), ("$role", a.Role), ("$partstat", a.PartStat), ("$rsvp", a.Rsvp ? 1 : 0), ("$ordinal", ordinal++));
            }

            if (Item(itemId) is { } item) IndexItem(itemId, item);
            return 0;
        });

    // ---- Reading and writing rows -----------------------------------------------------------

    private static (string, object?)[] Parameters(PimItem item) =>
    [
        ("$collection", item.CollectionId), ("$uid", item.Uid), ("$kind", KindText(item.Kind)), ("$href", item.DavHref), ("$etag", item.Etag),
        ("$raw", item.RawPayload), ("$summary", item.Summary), ("$description", item.Description), ("$location", item.Location),
        ("$starts", item.StartsUtc?.ToUnixTimeSeconds()), ("$ends", item.EndsUtc?.ToUnixTimeSeconds()),
        ("$startsLocal", item.StartsLocal), ("$endsLocal", item.EndsLocal), ("$tz", item.TzId), ("$allDay", item.AllDay ? 1 : 0),
        ("$status", item.Status), ("$priority", item.Priority), ("$percent", item.PercentComplete), ("$completed", item.CompletedUtc?.ToUnixTimeSeconds()),
        ("$rrule", item.Rrule), ("$recurrenceId", item.RecurrenceId), ("$override", item.IsOverride ? 1 : 0), ("$sequence", item.Sequence),
        ("$organizer", item.Organizer), ("$busy", item.Busy), ("$reminder", item.ReminderMinutes), ("$categories", item.Categories),
        ("$modified", item.LastModified.ToUnixTimeSeconds()), ("$sync", SyncText(item.SyncState)),
    ];

    private static PimItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        CollectionId = r.GetInt64(1),
        Uid = r.GetString(2),
        Kind = ParseKind(r.GetString(3)),
        DavHref = r.IsDBNull(4) ? null : r.GetString(4),
        Etag = r.IsDBNull(5) ? null : r.GetString(5),
        RawPayload = r.GetString(6),
        Summary = r.GetString(7),
        Description = r.GetString(8),
        Location = r.GetString(9),
        StartsUtc = r.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(10)),
        EndsUtc = r.IsDBNull(11) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(11)),
        StartsLocal = r.IsDBNull(12) ? null : r.GetString(12),
        EndsLocal = r.IsDBNull(13) ? null : r.GetString(13),
        TzId = r.IsDBNull(14) ? null : r.GetString(14),
        AllDay = r.GetInt32(15) != 0,
        Status = r.GetString(16),
        Priority = r.GetInt32(17),
        PercentComplete = r.GetInt32(18),
        CompletedUtc = r.IsDBNull(19) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(19)),
        Rrule = r.IsDBNull(20) ? null : r.GetString(20),
        RecurrenceId = r.IsDBNull(21) ? null : r.GetString(21),
        IsOverride = r.GetInt32(22) != 0,
        Sequence = r.GetInt32(23),
        Organizer = r.GetString(24),
        Busy = r.GetString(25),
        ReminderMinutes = r.IsDBNull(26) ? null : r.GetInt32(26),
        Categories = r.GetString(27),
        LastModified = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(28)),
        SyncState = ParseSync(r.GetString(29)),
    };

    private static Collection ReadCollection(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), ParseKind(r.GetString(2)), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.GetInt32(8) != 0, r.GetInt32(9) != 0, r.GetInt32(10) != 0, r.GetInt32(11));

    private static string KindText(CollectionKind kind) => kind switch
    {
        CollectionKind.Tasks => "vtodo",
        CollectionKind.Journal => "vjournal",
        CollectionKind.Contacts => "vcard",
        _ => "vevent",
    };

    private static CollectionKind ParseKind(string text) => text switch
    {
        "vtodo" => CollectionKind.Tasks,
        "vjournal" => CollectionKind.Journal,
        "vcard" => CollectionKind.Contacts,
        _ => CollectionKind.Events,
    };

    private static string SyncText(PimSyncState state) => state switch
    {
        PimSyncState.New => "new",
        PimSyncState.Modified => "modified",
        PimSyncState.Deleted => "deleted",
        _ => "synced",
    };

    private static PimSyncState ParseSync(string text) => text switch
    {
        "new" => PimSyncState.New,
        "modified" => PimSyncState.Modified,
        "deleted" => PimSyncState.Deleted,
        _ => PimSyncState.Synced,
    };
}
