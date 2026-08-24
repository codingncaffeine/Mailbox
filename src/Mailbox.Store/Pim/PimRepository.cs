using System.Globalization;
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
               categories, last_modified, sync_state,
               file_as, first_name, last_name, company, job_title, is_group, is_private,
               follow_up_due, follow_up_complete, links
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
                     categories, last_modified, sync_state,
                     file_as, first_name, last_name, company, job_title, is_group, is_private,
                     follow_up_due, follow_up_complete, links)
                VALUES
                    ($collection, $uid, $kind, $href, $etag, $raw, $summary, $description, $location,
                     $starts, $ends, $startsLocal, $endsLocal, $tz, $allDay, $status, $priority, $percent,
                     $completed, $rrule, $recurrenceId, $override, $sequence, $organizer, $busy, $reminder,
                     $categories, $modified, $sync,
                     $fileAs, $firstName, $lastName, $company, $jobTitle, $isGroup, $isPrivate,
                     $followUpDue, $followUpComplete, $links)
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
                    busy = $busy, reminder_minutes = $reminder, categories = $categories, last_modified = $modified, sync_state = $sync,
                    file_as = $fileAs, first_name = $firstName, last_name = $lastName, company = $company,
                    job_title = $jobTitle, is_group = $isGroup, is_private = $isPrivate,
                    follow_up_due = $followUpDue, follow_up_complete = $followUpComplete, links = $links
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

    /// <summary>
    /// Moves an item into another collection, and hands back the row it now is.
    /// </summary>
    /// <remarks>
    /// <b>A move is a delete there and a create here, not a change of column.</b> An item on a
    /// server lives at an href inside its own collection, and the queue reads that href off the
    /// row when it comes to push: rewriting <c>collection_id</c> in place would leave the resource
    /// where it was and leave the delete that should have removed it with no address to send. So
    /// the old row goes the way any delete goes — deleted outright on a local collection, kept and
    /// marked and queued where a server has to be told — and a new row is written into the
    /// destination with no href or tag of its own, which is what makes the sync create it there.
    /// <para>
    /// A repeating item's overrides travel with their master, for the same reason they are PUT
    /// with it: one resource on the server, a family here.
    /// </para>
    /// <para>
    /// What is carried is the row and its own text, which is the truth. Rows derived from that
    /// text and hung off the item's id — attendees, a contact's addresses, a photograph — are not,
    /// so this is for the kinds that keep none: notes, journal entries and tasks.
    /// </para>
    /// </remarks>
    public PimItem MoveItem(PimItem item, long toCollectionId)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.CollectionId == toCollectionId) return item;

        var family = item.IsOverride || item.Rrule is not { Length: > 0 }
            ? [item]
            : ItemsByUid(item.CollectionId, item.Uid);

        var remote = Collection(toCollectionId)?.DavUrl is { Length: > 0 };
        var leaving = Collection(item.CollectionId)?.DavUrl is { Length: > 0 };

        return _store.InTransaction(() =>
        {
            PimItem? moved = null;
            foreach (var member in family)
            {
                var made = AddItem(member with
                {
                    Id = 0,
                    CollectionId = toCollectionId,
                    DavHref = null,
                    Etag = null,
                    SyncState = PimSyncState.New,
                });

                if (remote) Queue(toCollectionId, made.Id, "put");

                if (leaving)
                {
                    SetSyncState(member.Id, PimSyncState.Deleted);
                    Queue(member.CollectionId, member.Id, "delete");
                }
                else
                {
                    DeleteItem(member.Id);
                }

                moved ??= made;
            }

            return moved ?? item;
        });
    }

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
        // The fourth column is who the item concerns: an appointment's attendees, and a contact's
        // own addresses and numbers — the same question asked of two kinds of item.
        var people = item.Kind == CollectionKind.Contacts
            ? string.Join(' ', new[] { item.FileAs, item.FirstName, item.LastName, item.Company, item.JobTitle }
                    .Where(p => p is { Length: > 0 })
                    .Concat(ContactFields(id).Select(f => f.Value)))
            : string.Join(' ', Attendees(id).Select(a => a.Name.Length > 0 ? a.Name + " " + a.Address : a.Address));

        _store.Execute("DELETE FROM pim_fts WHERE rowid = $id", ("$id", id));
        _store.Execute(
            "INSERT INTO pim_fts (rowid, summary, description, location, attendees) VALUES ($id, $s, $d, $l, $a)",
            ("$id", id), ("$s", item.Summary), ("$d", item.Description), ("$l", item.Location), ("$a", people));
    }

    /// <summary>A search string as an FTS5 query: every word a prefix, so "stan" finds "standup".</summary>
    private static string FtsQuery(string text)
        => string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => "\"" + w.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));

    // ---- Contacts ---------------------------------------------------------------------------

    /// <summary>
    /// An address book's contacts, in the order the list shows them: by File As, which is what
    /// the index letters down its side are taken from.
    /// </summary>
    /// <param name="collectionIds">Only these address books; null for every visible one.</param>
    public IReadOnlyList<PimItem> Contacts(IReadOnlyCollection<long>? collectionIds = null)
    {
        var scope = collectionIds is { Count: > 0 }
            ? $" AND i.collection_id IN ({string.Join(",", collectionIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))})"
            : " AND c.is_visible = 1";

        return _store.Query(
            ItemSelect
            + $"""

                WHERE id IN (
                    SELECT i.id FROM pim_items i JOIN collections c ON c.id = i.collection_id
                    WHERE i.kind = 'vcard' AND i.sync_state <> 'deleted'{scope})
                ORDER BY file_as COLLATE NOCASE, summary COLLATE NOCASE, id
                """,
            ReadItem);
    }

    /// <summary>
    /// Who holds this address, over every address book.
    /// </summary>
    /// <remarks>
    /// The question the reading pane asks of a sender, the compose window of a recipient and the
    /// group editor of a member. Indexed rather than scanned, and case-insensitive because an
    /// address is.
    /// </remarks>
    public IReadOnlyList<PimItem> ContactsWithAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return [];
        return _store.Query(
            ItemSelect
            + """

                WHERE id IN (
                    SELECT item_id FROM pim_contact_fields
                    WHERE kind = 'email' AND value = $value COLLATE NOCASE)
                  AND sync_state <> 'deleted'
                ORDER BY file_as COLLATE NOCASE, id
                """,
            ReadItem, ("$value", address.Trim()));
    }

    /// <summary>
    /// Contacts whose name, company or address begins with what has been typed — the address
    /// book's half of the compose window's autocomplete.
    /// </summary>
    public IReadOnlyList<PimItem> FindContacts(string prefix, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return [];
        var like = prefix.Trim().Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

        return _store.Query(
            ItemSelect
            + """

                WHERE kind = 'vcard' AND sync_state <> 'deleted'
                  AND (summary LIKE $like ESCAPE '\' COLLATE NOCASE
                       OR file_as LIKE $like ESCAPE '\' COLLATE NOCASE
                       OR first_name LIKE $like ESCAPE '\' COLLATE NOCASE
                       OR last_name LIKE $like ESCAPE '\' COLLATE NOCASE
                       OR company LIKE $like ESCAPE '\' COLLATE NOCASE
                       OR id IN (SELECT item_id FROM pim_contact_fields WHERE kind = 'email' AND value LIKE $like ESCAPE '\' COLLATE NOCASE))
                ORDER BY file_as COLLATE NOCASE, id
                LIMIT $limit
                """,
            ReadItem, ("$like", like), ("$limit", limit));
    }

    /// <summary>A contact's addresses and numbers, in the order the card shows them.</summary>
    public IReadOnlyList<ContactField> ContactFields(long itemId)
        => _store.Query(
            "SELECT kind, value, label, ordinal FROM pim_contact_fields WHERE item_id = $id ORDER BY kind, ordinal",
            r => new ContactField(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)),
            ("$id", itemId));

    /// <summary>
    /// Replaces a contact's addresses and numbers, and re-indexes it — the addresses are part of
    /// what search has to find a person by, and they are not written until after the row is.
    /// </summary>
    public void SetContactFields(long itemId, IReadOnlyList<ContactField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _store.InTransaction(() =>
        {
            _store.Execute("DELETE FROM pim_contact_fields WHERE item_id = $id", ("$id", itemId));

            var ordinal = 0;
            foreach (var field in fields.Where(f => f.Value is { Length: > 0 }))
            {
                _store.Execute(
                    "INSERT INTO pim_contact_fields (item_id, kind, value, label, ordinal) VALUES ($id, $kind, $value, $label, $ordinal)",
                    ("$id", itemId), ("$kind", field.Kind), ("$value", field.Value.Trim()),
                    ("$label", field.Label), ("$ordinal", field.Ordinal == 0 ? ordinal++ : field.Ordinal));
            }

            if (Item(itemId) is { } item) IndexItem(itemId, item);
            return 0;
        });
    }

    /// <summary>A contact's photograph, or null when it has none.</summary>
    public (string MediaType, byte[] Bytes)? ContactPhoto(long itemId)
        => _store.Query(
            "SELECT media_type, bytes FROM pim_photos WHERE item_id = $id",
            r => (r.GetString(0), (byte[])r.GetValue(1)),
            ("$id", itemId)).Cast<(string, byte[])?>().FirstOrDefault();

    /// <summary>Stores a contact's photograph, or takes it away when there is none.</summary>
    public void SetContactPhoto(long itemId, byte[]? bytes, string mediaType = "image/jpeg")
    {
        if (bytes is not { Length: > 0 })
        {
            _store.Execute("DELETE FROM pim_photos WHERE item_id = $id", ("$id", itemId));
            return;
        }

        _store.Execute(
            "INSERT INTO pim_photos (item_id, media_type, bytes) VALUES ($id, $type, $bytes) " +
            "ON CONFLICT(item_id) DO UPDATE SET media_type = $type, bytes = $bytes",
            ("$id", itemId), ("$type", mediaType), ("$bytes", bytes));
    }

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

    // ---- Reminders ------------------------------------------------------------------------------

    /// <summary>
    /// Items with a reminder that could be due — everything with one set, hidden calendars
    /// included, since a reminder is about the appointment and not about what is on screen.
    /// </summary>
    /// <remarks>
    /// Which occurrence is actually due is the scheduling layer's decision, not the store's: a
    /// series has one row and many occurrences, and only an expansion knows which of them the
    /// clock has reached.
    /// </remarks>
    public IReadOnlyList<PimItem> ItemsWithReminders(CollectionKind kind = CollectionKind.Events)
        => _store.Query(
            ItemSelect + " WHERE kind = $kind AND reminder_minutes IS NOT NULL AND sync_state <> 'deleted' ORDER BY starts_utc, id",
            ReadItem,
            ("$kind", KindText(kind)));

    /// <summary>What has been dismissed and what has been put off, for one item.</summary>
    public (DateTimeOffset? Dismissed, DateTimeOffset? Snoozed) ReminderState(long id)
        => _store.Query(
                "SELECT reminder_dismissed_utc, reminder_snoozed_utc FROM pim_items WHERE id = $id",
                r => (
                    r.IsDBNull(0) ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),
                    r.IsDBNull(1) ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1))),
                ("$id", id))
            .FirstOrDefault();

    /// <summary>
    /// Records that a reminder has been dealt with. <paramref name="dismissedOccurrence"/> is the
    /// start of the occurrence dismissed, so the next one in a series still comes round.
    /// </summary>
    public void SetReminderState(long id, DateTimeOffset? dismissedOccurrence, DateTimeOffset? snoozedUntil)
        => _store.Execute(
            "UPDATE pim_items SET reminder_dismissed_utc = $dismissed, reminder_snoozed_utc = $snoozed WHERE id = $id",
            ("$dismissed", dismissedOccurrence?.ToUnixTimeSeconds()),
            ("$snoozed", snoozedUntil?.ToUnixTimeSeconds()),
            ("$id", id));

    // ---- The colour categories ------------------------------------------------------------------

    /// <summary>
    /// The one set of colour categories, in the order the reader put them in.
    /// </summary>
    /// <remarks>
    /// Here rather than in a mail store because every module shares this file and none shares
    /// those: a per-account list would give one reader two of them (§9's "one colour category set
    /// applying to every item type in every module"). Items name a category rather than pointing
    /// at it, because that is what iCalendar and vCard carry.
    /// </remarks>
    public IReadOnlyList<Category> Categories() => _store.Query(
        "SELECT id, name, colour_token, shortcut, ordinal FROM categories ORDER BY ordinal, id",
        ReadCategory);

    public Category? CategoryNamed(string name) => _store.Query(
        "SELECT id, name, colour_token, shortcut, ordinal FROM categories WHERE name = $name COLLATE NOCASE",
        ReadCategory,
        ("$name", name)).FirstOrDefault();

    /// <summary>Makes a category, or hands back the one that already has the name.</summary>
    public Category AddCategory(string name, string colourToken, string? shortcut = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _store.InTransaction(() =>
        {
            if (CategoryNamed(name.Trim()) is { } existing) return existing;

            _store.Execute(
                """
                INSERT INTO categories (name, colour_token, shortcut, ordinal)
                VALUES ($name, $colour, $shortcut, (SELECT COALESCE(MAX(ordinal), -1) + 1 FROM categories))
                """,
                ("$name", name.Trim()), ("$colour", colourToken ?? string.Empty), ("$shortcut", shortcut));

            return _store.Query(
                "SELECT id, name, colour_token, shortcut, ordinal FROM categories WHERE id = $id",
                ReadCategory,
                ("$id", _store.LastInsertId)).First();
        });
    }

    public void RenameCategory(long id, string name)
        => _store.Execute("UPDATE categories SET name = $name WHERE id = $id", ("$name", name.Trim()), ("$id", id));

    public void RecolourCategory(long id, string colourToken)
        => _store.Execute("UPDATE categories SET colour_token = $colour WHERE id = $id", ("$colour", colourToken ?? string.Empty), ("$id", id));

    /// <summary>Sets or clears a category's keyboard shortcut. Null clears it.</summary>
    public void SetCategoryShortcut(long id, string? shortcut)
        => _store.Execute("UPDATE categories SET shortcut = $shortcut WHERE id = $id", ("$shortcut", shortcut), ("$id", id));

    public void DeleteCategory(long id)
        => _store.Execute("DELETE FROM categories WHERE id = $id", ("$id", id));

    /// <summary>
    /// Every item carrying a category by that name, whichever module it belongs to.
    /// </summary>
    /// <remarks>
    /// Matched against the derived column rather than the payload, and bounded by commas so that
    /// "Blue" does not find "Blueprints": the column is written as a comma-separated list, so a
    /// name is between two of them once the whole string is fenced with one at each end.
    /// </remarks>
    public IReadOnlyList<PimItem> ItemsWithCategory(string name)
        => string.IsNullOrWhiteSpace(name)
            ? []
            : _store.Query(
                ItemSelect + " WHERE ',' || replace(categories, ', ', ',') || ',' LIKE $pattern COLLATE NOCASE ORDER BY id",
                ReadItem,
                ("$pattern", "%," + name.Trim() + ",%"));

    private static Category ReadCategory(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetInt32(4));

    // ---- The offline queue --------------------------------------------------------------------

    /// <summary>One local change waiting to reach its server.</summary>
    /// <param name="Op">"put" or "delete".</param>
    /// <param name="ItemId">The row it is about; null once a delete's row has gone.</param>
    /// <param name="Href">The server path a delete is for, kept because the row will not be there to ask.</param>
    public sealed record QueuedChange(long Id, long CollectionId, long? ItemId, string Op, string? Href, string? Etag, int Attempts, string? LastError);

    private const string QueueSelect =
        "SELECT q.id, q.collection_id, q.item_id, q.op, i.dav_href, i.etag, q.attempts, q.last_error FROM dav_queue q LEFT JOIN pim_items i ON i.id = q.item_id";

    /// <summary>
    /// Records that an item has to be pushed. One entry per item and operation: queueing the same
    /// item twice before a sync would send it twice, and the second send would fail its own
    /// precondition.
    /// </summary>
    public long Queue(long collectionId, long? itemId, string op, string? href = null)
        => _store.InTransaction(() =>
        {
            if (itemId is { } id)
            {
                _store.Execute("DELETE FROM dav_queue WHERE item_id = $item AND op = $op", ("$item", id), ("$op", op));
            }

            _store.Execute(
                "INSERT INTO dav_queue (collection_id, item_id, op, state, created_utc) VALUES ($c, $i, $op, 'queued', $now)",
                ("$c", collectionId), ("$i", itemId), ("$op", op), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

            var queued = _store.LastInsertId;
            if (href is { Length: > 0 }) _store.Execute("UPDATE dav_queue SET last_error = NULL WHERE id = $id", ("$id", queued));
            return queued;
        });

    /// <summary>What is waiting to go, oldest first; a collection's own when one is named.</summary>
    public IReadOnlyList<QueuedChange> Queued(long? collectionId = null)
        => collectionId is { } id
            ? _store.Query(QueueSelect + " WHERE q.state = 'queued' AND q.collection_id = $c ORDER BY q.id", ReadQueued, ("$c", id))
            : _store.Query(QueueSelect + " WHERE q.state = 'queued' ORDER BY q.id", ReadQueued);

    public void Dequeue(long id) => _store.Execute("DELETE FROM dav_queue WHERE id = $id", ("$id", id));

    /// <summary>Leaves a failed change queued with what went wrong, so a later sync retries it.</summary>
    public void QueueFailed(long id, string error)
        => _store.Execute(
            "UPDATE dav_queue SET attempts = attempts + 1, last_error = $error WHERE id = $id",
            ("$error", error), ("$id", id));

    private static QueuedChange ReadQueued(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7));

    /// <summary>Every item in a collection with its server path and tag, for a sync's diff.</summary>
    public IReadOnlyDictionary<string, (long Id, string? Etag)> HrefsIn(long collectionId)
    {
        var map = new Dictionary<string, (long, string?)>(StringComparer.Ordinal);
        foreach (var row in _store.Query(
                     "SELECT id, dav_href, etag FROM pim_items WHERE collection_id = $c AND dav_href IS NOT NULL",
                     r => (Id: r.GetInt64(0), Href: r.GetString(1), Etag: r.IsDBNull(2) ? null : r.GetString(2)),
                     ("$c", collectionId)))
        {
            map[row.Href] = (row.Id, row.Etag);
        }

        return map;
    }

    /// <summary>The row a server path names, or null when this store has never seen it.</summary>
    public PimItem? ItemByHref(long collectionId, string href)
        => _store.Query(ItemSelect + " WHERE collection_id = $c AND dav_href = $href", ReadItem, ("$c", collectionId), ("$href", href))
            .FirstOrDefault();

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
        ("$fileAs", item.FileAs), ("$firstName", item.FirstName), ("$lastName", item.LastName),
        ("$company", item.Company), ("$jobTitle", item.JobTitle), ("$isGroup", item.IsGroup ? 1 : 0),
        ("$isPrivate", item.IsPrivate ? 1 : 0),
        ("$followUpDue", item.FollowUpDue?.ToUnixTimeSeconds()), ("$followUpComplete", item.FollowUpComplete ? 1 : 0),
        ("$links", string.Join("\n", item.Links)),
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
        FileAs = r.GetString(30),
        FirstName = r.GetString(31),
        LastName = r.GetString(32),
        Company = r.GetString(33),
        JobTitle = r.GetString(34),
        IsGroup = r.GetInt32(35) != 0,
        IsPrivate = r.GetInt32(36) != 0,
        FollowUpDue = r.IsDBNull(37) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(37)),
        FollowUpComplete = r.GetInt32(38) != 0,
        Links = r.GetString(39) is { Length: > 0 } links
            ? links.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [],
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
