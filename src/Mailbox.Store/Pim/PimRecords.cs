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
/// <param name="LastCheckedUtc">
/// When this machine last reached the server for it and was answered, or null for never — which
/// is what the Internet Calendars tab's "Last Updated on" column reads. Not the same as
/// <paramref name="Ctag"/> moving: a collection that has not changed in a month is still being
/// checked, and the check is what says the subscription is alive.
/// </param>
public sealed record Collection(
    long Id,
    string Account,
    CollectionKind Kind,
    string DisplayName,
    string Color,
    string? DavUrl,
    string? Ctag,
    string? SyncToken,
    DateTimeOffset? LastCheckedUtc,
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

    /// <summary>
    /// Kept to oneself when the collection is shared — RFC 5545's <c>CLASS:PRIVATE</c>, which the
    /// reference's own Private button sets.
    /// </summary>
    /// <remarks>
    /// A column so a list can draw the mark without parsing the item; the text is still what says
    /// so, and CONFIDENTIAL reads as private here because both mean "not for the reader of a
    /// shared calendar".
    /// </remarks>
    public bool IsPrivate { get; init; }

    /// <summary>
    /// The follow-up flag: when it falls due, and whether it has been dealt with.
    /// </summary>
    /// <remarks>
    /// Beside the item rather than inside its text, because a flag is the reader's own business:
    /// a shared calendar or address book should not learn when somebody meant to ring back.
    /// </remarks>
    public DateTimeOffset? FollowUpDue { get; init; }

    public bool FollowUpComplete { get; init; }

    /// <summary>
    /// The UIDs of the cards linked to this one, mirrored from the vCard's own lines so a list
    /// can group linked people without parsing every card (step 7). Empty for everything that is
    /// not a contact, and for a card saved before the column existed — the text is the truth and
    /// the mirror fills in on the next save.
    /// </summary>
    public IReadOnlyList<string> Links { get; init; } = [];

    public DateTimeOffset LastModified { get; init; }
    public PimSyncState SyncState { get; init; } = PimSyncState.Synced;
    public string? DavHref { get; init; }
    public string? Etag { get; init; }

    // ---- A contact's own columns ---------------------------------------------------------------
    // Empty for everything that is not one. They are here rather than in a table of their own for
    // the reason the calendar's are: a list draws its rows from columns and never parses a card.

    /// <summary>What the contact list orders by, and what its index letters are taken from.</summary>
    public string FileAs { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Company { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;

    /// <summary>A distribution list rather than a person.</summary>
    public bool IsGroup { get; init; }
}

/// <summary>One of a contact's addresses or numbers, indexed so it can be looked up by value.</summary>
/// <param name="Kind">email · phone · im.</param>
/// <param name="Label">Which one it is — business, home, mobile — as the card labels it.</param>
public sealed record ContactField(string Kind, string Value, string Label = "", int Ordinal = 0);
