using Mailbox.Contacts;
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
/// Publishing a calendar or an address book: everything in it, written to one address as one
/// document.
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
/// A calendar goes up with <b>METHOD:PUBLISH</b>, per RFC 5546 — this is a calendar being made
/// available to read, not an invitation and not a request for anything back. Overrides ride with
/// their masters, since a series is only correct with them. An address book is the same idea in
/// vCard: every card in the book, one after another, in the version the store keeps.
/// </para>
/// </remarks>
public static class CollectionPublisher
{
    /// <summary>
    /// Writes a collection's contents to an address, in whichever text its kind is written in.
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
        Collection collection,
        Uri url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(collection);

        var rows = repository.Items(collection.Id)
            .Where(i => i.SyncState != PimSyncState.Deleted)
            .ToList();

        // A deleted row is one this machine has said goodbye to and not yet told a server about.
        // Publishing it would put it in front of every subscriber as though it were still there.
        var (document, type) = collection.Kind == CollectionKind.Contacts
            ? (VCardCodec.SerializeMany([.. rows.Select(PimContactCodec.FromItem)], PimContactCodec.StoredVersion),
               "text/vcard; charset=utf-8")
            : (ICalendarCodec.SerializeCalendar([.. rows.Select(PimEventCodec.FromItem)], "PUBLISH"),
               "text/calendar; charset=utf-8");

        var written = await client
            .PutAsync(url, document, contentType: type, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return written.Ok
            ? new PublishResult(rows.Count)
            : new PublishResult(0, $"{(int)written.Status} {written.Status}");
    }
}
