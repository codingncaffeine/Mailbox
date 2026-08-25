using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Dav;

/// <summary>What a publish came to.</summary>
/// <param name="Written">How many events went up.</param>
/// <param name="Refused">Set when the server would not take it, with what it said.</param>
public sealed record PublishResult(int Written, string? Refused = null)
{
    public bool Ok => Refused is null;
}

/// <summary>
/// Publishing a calendar: everything on it, written to one address as one document.
/// </summary>
/// <remarks>
/// The mirror of a subscription, and deliberately so — what this writes is exactly what
/// <see cref="DavSync.FetchDocumentAsync"/> reads, so a calendar published from one machine can
/// be subscribed to from another with nothing in between but a web server that takes a PUT.
/// <para>
/// A whole document each time rather than a resource per event: a subscriber fetches one file, so
/// that is the shape the far end has to hold. It also keeps the operation atomic — a reader
/// fetching halfway through a publish gets the old calendar or the new one, never a calendar with
/// half its meetings.
/// </para>
/// <para>
/// <b>METHOD:PUBLISH</b>, per RFC 5546 — this is a calendar being made available to read, not an
/// invitation and not a request for anything back. Overrides ride with their masters, since a
/// series is only correct with them.
/// </para>
/// </remarks>
public static class CalendarPublisher
{
    /// <summary>
    /// Writes a calendar's events to an address.
    /// </summary>
    /// <remarks>
    /// No <c>If-Match</c>: this machine is the author of what is there, and a precondition would
    /// turn "somebody else has written here" into a silent failure to publish rather than the
    /// overwrite that is meant. What the far end holds is a copy, and this is where the copy
    /// comes from.
    /// </remarks>
    public static async Task<PublishResult> PublishAsync(
        DavClient client,
        PimRepository repository,
        Collection calendar,
        Uri url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(calendar);

        var events = repository.Items(calendar.Id)
            .Where(i => i.SyncState != PimSyncState.Deleted)
            .Select(PimEventCodec.FromItem)
            .ToList();

        var document = ICalendarCodec.SerializeCalendar(events, "PUBLISH");
        var written = await client.PutAsync(url, document, cancellationToken: cancellationToken).ConfigureAwait(false);

        return written.Ok
            ? new PublishResult(events.Count)
            : new PublishResult(0, $"{(int)written.Status} {written.Status}");
    }
}
