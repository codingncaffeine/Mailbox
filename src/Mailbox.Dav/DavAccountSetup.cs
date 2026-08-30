using Mailbox.Store.Pim;

namespace Mailbox.Dav;

/// <summary>What adding a DAV account came to: the rows made, and what was already here.</summary>
public sealed record DavAccountOutcome(
    IReadOnlyList<Collection> Added,
    IReadOnlyList<DavCollection> AlreadyHere)
{
    /// <summary>The status sentence: what was added, by kind, and when it fills.</summary>
    public string Said()
    {
        if (Added.Count == 0)
        {
            return AlreadyHere.Count > 0
                ? "Those are already here — nothing was added twice."
                : "Nothing was chosen.";
        }

        var parts = new List<string>();
        Count(CollectionKind.Events, "calendar", "calendars");
        Count(CollectionKind.Contacts, "address book", "address books");
        Count(CollectionKind.Tasks, "task list", "task lists");
        Count(CollectionKind.Journal, "journal", "journals");

        var skipped = AlreadyHere.Count > 0
            ? $" {AlreadyHere.Count} already here."
            : string.Empty;

        return $"{Join(parts)} added. They fill on the next send/receive.{skipped}";

        void Count(CollectionKind kind, string one, string many)
        {
            var n = Added.Count(c => c.Kind == kind);
            if (n > 0) parts.Add($"{n} {(n == 1 ? one : many)}");
        }

        static string Join(IReadOnlyList<string> parts) => parts.Count switch
        {
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }
}

/// <summary>
/// The store half of adding a CalDAV or CardDAV account: the chosen collections written with
/// their addresses and their account, so the engine that already runs picks them up on the next
/// send/receive. The credential is the caller's to file — it belongs to the keyring, which this
/// project does not reach.
/// </summary>
public static class DavAccountSetup
{
    /// <summary>Writes the chosen collections, skipping any address the store already carries.</summary>
    /// <remarks>
    /// The skip is what makes the wizard safe to run twice: a URL already here is reported
    /// rather than inserted again, whichever account it was filed under — a calendar does not
    /// become two calendars because somebody re-ran discovery.
    /// </remarks>
    public static DavAccountOutcome Add(
        PimRepository repository, string userName, IEnumerable<DavCollection> chosen)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var existing = repository.Collections()
            .Where(c => c.DavUrl is { Length: > 0 })
            .Select(c => c.DavUrl!)
            .ToHashSet(StringComparer.Ordinal);

        var added = new List<Collection>();
        var alreadyHere = new List<DavCollection>();

        foreach (var collection in chosen)
        {
            if (!existing.Add(collection.Url.AbsoluteUri))
            {
                alreadyHere.Add(collection);
                continue;
            }

            added.Add(repository.AddCollection(
                collection.Kind,
                collection.DisplayName,
                collection.Colour,
                account: userName.Trim(),
                davUrl: collection.Url.AbsoluteUri,
                readOnly: collection.IsReadOnly));
        }

        return new DavAccountOutcome(added, alreadyHere);
    }
}
