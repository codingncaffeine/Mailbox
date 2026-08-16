namespace Mailbox.Store.Pim;

/// <summary>What a collection holds — the iCalendar or vCard component kind, which decides the module.</summary>
public enum CollectionKind
{
    Events,
    Tasks,
    Journal,
    Contacts,
}

/// <summary>Whether a local change has reached the collection's server yet.</summary>
public enum PimSyncState
{
    /// <summary>The server has this as it is here — or the collection has no server.</summary>
    Synced,
    New,
    Modified,
    Deleted,
}

/// <summary>A calendar, a task list, a note list or an address book.</summary>
public sealed record Collection(
    long Id,
    string Account,
    CollectionKind Kind,
    string DisplayName,
    string Color,
    string? DavUrl,
    string? Ctag,
    string? SyncToken,
    bool IsVisible,
    bool IsReadOnly,
    bool IsDefault,
    int Ordinal)
{
    /// <summary>A collection with no server: made here, kept here.</summary>
    public bool IsLocal => string.IsNullOrEmpty(Account);
}

/// <summary>
/// One VEVENT, VTODO, VJOURNAL or vCard, as the store keeps it: the raw text verbatim, and the
/// columns the views and the sync read beside it.
/// </summary>
/// <remarks>
/// Instants are UTC for range queries; the wall time and its zone are kept as written so a
/// repeating 09:00 stays at 09:00 across a DST change (§9). Which of the two a view believes
/// is the scheduling layer's business, not the store's.
/// </remarks>
public sealed record PimItem
{
    public long Id { get; init; }
    public required long CollectionId { get; init; }
    public required string Uid { get; init; }
    public required CollectionKind Kind { get; init; }
    public required string RawPayload { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public DateTimeOffset? StartsUtc { get; init; }
    public DateTimeOffset? EndsUtc { get; init; }
    /// <summary>The start as written, <c>yyyy-MM-ddTHH:mm:ss</c>, in <see cref="TzId"/>.</summary>
    public string? StartsLocal { get; init; }
    public string? EndsLocal { get; init; }
    /// <summary>The IANA zone the wall times are in; null for a floating or UTC time.</summary>
    public string? TzId { get; init; }
    public bool AllDay { get; init; }
    public string Status { get; init; } = string.Empty;
    public int Priority { get; init; }
    public int PercentComplete { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    /// <summary>The RRULE, without the property name, when the item repeats.</summary>
    public string? Rrule { get; init; }
    /// <summary>For an override: the occurrence it replaces, as the RECURRENCE-ID value.</summary>
    public string? RecurrenceId { get; init; }
    public bool IsOverride { get; init; }
    public int Sequence { get; init; }
    public string Organizer { get; init; } = string.Empty;
    /// <summary>free · tentative · busy · oof — the reference's Show As.</summary>
    public string Busy { get; init; } = "busy";
    public int? ReminderMinutes { get; init; }
    public string Categories { get; init; } = string.Empty;
    public DateTimeOffset LastModified { get; init; }
    public PimSyncState SyncState { get; init; } = PimSyncState.Synced;
    public string? DavHref { get; init; }
    public string? Etag { get; init; }
}
