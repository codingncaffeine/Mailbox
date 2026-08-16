using System.Xml.Linq;

namespace Mailbox.Dav;

/// <summary>
/// The XML half of WebDAV: the namespaces, the request bodies, and what a 207 comes back as.
/// </summary>
/// <remarks>
/// Hand-built rather than serialized from types. DAV responses vary considerably between servers
/// — a property may be absent, empty, or carry a different child than the RFC's example — and a
/// reader that asks for what it wants and ignores the rest survives that, where a strict
/// deserializer breaks on the first server that adds a namespace.
/// </remarks>
public static class DavXml
{
    public static readonly XNamespace Dav = "DAV:";
    public static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";

    /// <summary>Apple's CTag, which is what a server without sync-collection offers instead.</summary>
    public static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";

    /// <summary>Apple's calendar colour, which every server that has one uses.</summary>
    public static readonly XNamespace AppleIcal = "http://apple.com/ns/ical/";

    // ---- Requests --------------------------------------------------------------------------

    /// <summary>PROPFIND for the principal this request is authenticated as.</summary>
    public static string CurrentUserPrincipal() => Propfind(Dav + "current-user-principal");

    /// <summary>PROPFIND for where that principal's calendars live.</summary>
    public static string CalendarHomeSet() => Propfind(CalDav + "calendar-home-set");

    /// <summary>PROPFIND for where that principal's address books live.</summary>
    public static string AddressBookHomeSet() => Propfind(CardDav + "addressbook-home-set");

    /// <summary>
    /// PROPFIND Depth:1 over a home collection: everything needed to list what is in it and to
    /// decide how to sync each one.
    /// </summary>
    public static string CollectionProperties() => Propfind(
        Dav + "resourcetype",
        Dav + "displayname",
        Dav + "sync-token",
        Dav + "current-user-privilege-set",
        CalDav + "supported-calendar-component-set",
        CalendarServer + "getctag",
        AppleIcal + "calendar-color");

    /// <summary>PROPFIND Depth:1 over one collection: the ETag of everything in it.</summary>
    public static string ItemEtags() => Propfind(Dav + "getetag", Dav + "resourcetype");

    /// <summary>
    /// <c>sync-collection</c> (RFC 6578): what changed since a token. An empty token asks for
    /// everything, which is how a first sync starts.
    /// </summary>
    public static string SyncCollection(string? token) =>
        Document(new XElement(
            Dav + "sync-collection",
            new XElement(Dav + "sync-token", token ?? string.Empty),
            new XElement(Dav + "sync-level", "1"),
            new XElement(Dav + "prop", new XElement(Dav + "getetag"))));

    /// <summary><c>calendar-query</c> over a span of time.</summary>
    public static string CalendarQuery(DateTimeOffset fromUtc, DateTimeOffset toUtc, string component = "VEVENT") =>
        Document(new XElement(
            CalDav + "calendar-query",
            new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
            new XElement(Dav + "prop", new XElement(Dav + "getetag"), new XElement(CalDav + "calendar-data")),
            new XElement(
                CalDav + "filter",
                new XElement(
                    CalDav + "comp-filter",
                    new XAttribute("name", "VCALENDAR"),
                    new XElement(
                        CalDav + "comp-filter",
                        new XAttribute("name", component),
                        new XElement(
                            CalDav + "time-range",
                            new XAttribute("start", Stamp(fromUtc)),
                            new XAttribute("end", Stamp(toUtc))))))));

    /// <summary><c>calendar-multiget</c>: the payloads of the hrefs given.</summary>
    public static string CalendarMultiget(IEnumerable<string> hrefs) =>
        Document(new XElement(
            CalDav + "calendar-multiget",
            new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
            new XElement(Dav + "prop", new XElement(Dav + "getetag"), new XElement(CalDav + "calendar-data")),
            hrefs.Select(h => new XElement(Dav + "href", h))));

    /// <summary><c>addressbook-multiget</c>, which is the same request in the other namespace.</summary>
    public static string AddressBookMultiget(IEnumerable<string> hrefs) =>
        Document(new XElement(
            CardDav + "addressbook-multiget",
            new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
            new XElement(Dav + "prop", new XElement(Dav + "getetag"), new XElement(CardDav + "address-data")),
            hrefs.Select(h => new XElement(Dav + "href", h))));

    /// <summary>UTC as RFC 5545 writes it, which is what a time-range filter wants.</summary>
    public static string Stamp(DateTimeOffset when)
        => when.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string Propfind(params XName[] properties) =>
        Document(new XElement(
            Dav + "propfind",
            new XElement(Dav + "prop", properties.Select(p => new XElement(p)))));

    private static string Document(XElement root)
        => new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting);

    // ---- Responses -------------------------------------------------------------------------

    /// <summary>One <c>&lt;response&gt;</c> of a 207: its href, what came back for it, and its status.</summary>
    public sealed record DavResource(string Href, int Status, IReadOnlyDictionary<XName, XElement> Properties)
    {
        public string? Etag => Text(Dav + "getetag")?.Trim('"');

        public string? DisplayName => Text(Dav + "displayname");

        public string? SyncToken => Text(Dav + "sync-token");

        public string? Ctag => Text(CalendarServer + "getctag");

        public string? Colour => Text(AppleIcal + "calendar-color") is { Length: >= 7 } colour ? colour[..7] : null;

        /// <summary>The iCalendar or vCard payload, when the request asked for it.</summary>
        public string? Data => Text(CalDav + "calendar-data") ?? Text(CardDav + "address-data");

        /// <summary>True when the resource is a collection at all.</summary>
        public bool IsCollection => Properties.TryGetValue(Dav + "resourcetype", out var kind)
                                    && kind.Element(Dav + "collection") is not null;

        /// <summary>The component kinds a calendar accepts, which is what tells events from tasks.</summary>
        public IReadOnlyList<string> Components =>
            Properties.TryGetValue(CalDav + "supported-calendar-component-set", out var set)
                ? [.. set.Elements(CalDav + "comp").Select(c => (string?)c.Attribute("name") ?? string.Empty).Where(n => n.Length > 0)]
                : [];

        /// <summary>True for a calendar rather than a plain collection or an address book.</summary>
        public bool IsCalendar => Properties.TryGetValue(Dav + "resourcetype", out var kind)
                                  && kind.Element(CalDav + "calendar") is not null;

        public bool IsAddressBook => Properties.TryGetValue(Dav + "resourcetype", out var kind)
                                     && kind.Element(CardDav + "addressbook") is not null;

        /// <summary>
        /// Whether this account may write to it. A server that says nothing about privileges is
        /// taken as writable, which is what every server that omits the property means.
        /// </summary>
        public bool IsReadOnly =>
            Properties.TryGetValue(Dav + "current-user-privilege-set", out var privileges)
            && !privileges.Descendants(Dav + "write").Any()
            && !privileges.Descendants(Dav + "write-content").Any();

        public string? Text(XName name) => Properties.TryGetValue(name, out var element) ? element.Value : null;

        /// <summary>The first href inside a property — how the two home-set properties answer.</summary>
        public string? HrefIn(XName name)
            => Properties.TryGetValue(name, out var element) ? element.Element(Dav + "href")?.Value : null;
    }

    /// <summary>A 207 read into its responses, plus the sync token a sync-collection came back with.</summary>
    public sealed record MultiStatus(IReadOnlyList<DavResource> Resources, string? SyncToken)
    {
        /// <summary>Hrefs the server says are gone — a 404 inside the multistatus.</summary>
        public IReadOnlyList<string> Removed => [.. Resources.Where(r => r.Status == 404).Select(r => r.Href)];

        /// <summary>Everything the server actually answered for.</summary>
        public IReadOnlyList<DavResource> Found => [.. Resources.Where(r => r.Status is >= 200 and < 300)];
    }

    /// <summary>Reads a 207 Multi-Status body. A body that is not one comes back empty rather than throwing.</summary>
    public static MultiStatus ReadMultiStatus(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return new MultiStatus([], null);

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return new MultiStatus([], null);
        }

        var root = document.Root;
        if (root is null) return new MultiStatus([], null);

        var resources = new List<DavResource>();

        foreach (var response in root.Elements(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value ?? string.Empty;
            var properties = new Dictionary<XName, XElement>();
            var status = StatusOf(response.Element(Dav + "status")?.Value) ?? 200;

            foreach (var propstat in response.Elements(Dav + "propstat"))
            {
                var code = StatusOf(propstat.Element(Dav + "status")?.Value) ?? 200;
                if (code is < 200 or >= 300) continue;

                foreach (var property in propstat.Element(Dav + "prop")?.Elements() ?? [])
                {
                    properties[property.Name] = property;
                }
            }

            // A response whose only propstat was a 404 is a resource that has gone; one with a
            // 404 status on the response itself is the same thing said the other way.
            if (properties.Count == 0 && response.Elements(Dav + "propstat")
                    .All(p => StatusOf(p.Element(Dav + "status")?.Value) is { } code && code >= 400)
                && response.Elements(Dav + "propstat").Any())
            {
                status = 404;
            }

            resources.Add(new DavResource(href, status, properties));
        }

        return new MultiStatus(resources, root.Element(Dav + "sync-token")?.Value);
    }

    /// <summary>"HTTP/1.1 207 Multi-Status" → 207.</summary>
    internal static int? StatusOf(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (int.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var code) && code is >= 100 and < 600)
            {
                return code;
            }
        }

        return null;
    }
}
