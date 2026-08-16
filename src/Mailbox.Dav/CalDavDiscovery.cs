using Mailbox.Store.Pim;

namespace Mailbox.Dav;

/// <summary>A collection found on a server, before anything has been stored about it.</summary>
/// <param name="Url">Its absolute URL.</param>
/// <param name="Kind">What it holds, from its supported component set or its resource type.</param>
public sealed record DavCollection(
    Uri Url,
    CollectionKind Kind,
    string DisplayName,
    string Colour,
    string? Ctag,
    string? SyncToken,
    bool IsReadOnly);

/// <summary>
/// Finding a server's collections from an address and a password, in the order RFC 6764 lays
/// down: the well-known path, then the principal, then the home set, then what is in it.
/// </summary>
/// <remarks>
/// Every step is allowed to fail into the next. Servers differ over which of them they answer —
/// some serve <c>/.well-known/caldav</c>, some redirect it, some expect the calendar URL
/// directly — and a discovery that gives up at the first missing step finds nothing on half of
/// them. Handing in a URL that is already a collection is therefore also a valid start.
/// </remarks>
public static class CalDavDiscovery
{
    /// <summary>The collections a server offers this account, calendars first.</summary>
    public static async Task<IReadOnlyList<DavCollection>> FindAsync(
        DavClient client,
        Uri server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(server);

        var home = await FindHomeAsync(client, server, cancellationToken).ConfigureAwait(false);
        if (home is null) return [];

        var listing = await client.PropFindAsync(home, DavXml.CollectionProperties(), depth: 1, cancellationToken)
            .ConfigureAwait(false);
        if (!listing.Ok && listing.Status != System.Net.HttpStatusCode.MultiStatus) return [];

        var found = new List<DavCollection>();
        foreach (var resource in listing.MultiStatus.Found)
        {
            if (!resource.IsCalendar && !resource.IsAddressBook) continue;

            var url = Absolute(home, resource.Href);
            if (url is null) continue;

            // A calendar that says which components it takes is trusted; one that says nothing
            // is taken as events, which is what every server means by an unqualified calendar.
            var kind = resource.IsAddressBook
                ? CollectionKind.Contacts
                : resource.Components switch
                {
                    { Count: > 0 } components when components.Contains("VTODO") && !components.Contains("VEVENT") => CollectionKind.Tasks,
                    { Count: > 0 } components when components.Contains("VJOURNAL") && !components.Contains("VEVENT") => CollectionKind.Journal,
                    _ => CollectionKind.Events,
                };

            found.Add(new DavCollection(
                url,
                kind,
                resource.DisplayName is { Length: > 0 } name ? name : LastSegment(url),
                resource.Colour ?? string.Empty,
                resource.Ctag,
                resource.SyncToken,
                resource.IsReadOnly));
        }

        return found;
    }

    /// <summary>
    /// Where this account's calendars live, from whatever the caller had: a bare domain, a
    /// principal URL, or the home itself.
    /// </summary>
    public static async Task<Uri?> FindHomeAsync(DavClient client, Uri server, CancellationToken cancellationToken = default)
    {
        foreach (var start in Starts(server))
        {
            var principalResponse = await client
                .PropFindAsync(start, DavXml.CurrentUserPrincipal(), depth: 0, cancellationToken)
                .ConfigureAwait(false);

            var principalHref = principalResponse.MultiStatus.Found
                .Select(r => r.HrefIn(DavXml.Dav + "current-user-principal"))
                .FirstOrDefault(h => h is { Length: > 0 });

            var principal = principalHref is null ? start : Absolute(start, principalHref) ?? start;

            var homeResponse = await client
                .PropFindAsync(principal, DavXml.CalendarHomeSet(), depth: 0, cancellationToken)
                .ConfigureAwait(false);

            var homeHref = homeResponse.MultiStatus.Found
                .Select(r => r.HrefIn(DavXml.CalDav + "calendar-home-set"))
                .FirstOrDefault(h => h is { Length: > 0 });

            if (homeHref is { Length: > 0 } && Absolute(principal, homeHref) is { } home) return home;

            // No home set: the URL handed in may already be one, which is what a server that
            // only publishes a calendar address gives you.
            var listing = await client.PropFindAsync(start, DavXml.CollectionProperties(), depth: 1, cancellationToken)
                .ConfigureAwait(false);
            if (listing.MultiStatus.Found.Any(r => r.IsCalendar || r.IsAddressBook)) return start;
        }

        return null;
    }

    /// <summary>The URLs to try, in order: what was given, then the two well-known paths.</summary>
    private static IEnumerable<Uri> Starts(Uri server)
    {
        yield return server;

        var root = new Uri(server.GetLeftPart(UriPartial.Authority));
        if (!server.AbsolutePath.Contains("/.well-known/", StringComparison.Ordinal))
        {
            yield return new Uri(root, "/.well-known/caldav");
            yield return new Uri(root, "/.well-known/carddav");
        }

        if (server.AbsolutePath.Length > 1) yield return root;
    }

    /// <summary>An href from a response, which may be relative, against the URL it came from.</summary>
    internal static Uri? Absolute(Uri baseUrl, string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        return Uri.TryCreate(baseUrl, href.Trim(), out var absolute) ? absolute : null;
    }

    private static string LastSegment(Uri url)
    {
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? Uri.UnescapeDataString(segments[^1]) : url.Host;
    }
}
