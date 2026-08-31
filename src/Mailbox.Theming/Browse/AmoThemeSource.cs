using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailbox.Theming.Browse;

/// <summary>
/// addons.mozilla.org as a theme source, through its public read API — the same one its own
/// site runs on: no key, static themes only, searched by words, popularity, rating, trending
/// or colour. Every listing carries its licence, which the browser shows before anything is
/// installed: a person should know a theme's terms while they can still decline it.
/// </summary>
/// <remarks>
/// The browser is the one place Mailbox's theming ever touches a network, it talks only to
/// this host, and only when the reader opens it. Fetches are size-capped and time-limited;
/// what a fetch returns is bytes for the import machinery, which trusts nothing regardless
/// of where a file came from.
/// </remarks>
public sealed class AmoThemeSource : IThemeSource, IDisposable
{
    /// <summary>The public API root; a test may point elsewhere.</summary>
    public const string DefaultBaseUrl = "https://addons.mozilla.org/api/v5";

    private const int PageSize = 24;
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public AmoThemeSource(string? baseUrl = null, HttpClient? client = null)
    {
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _client = client ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true });
        _client.Timeout = TimeSpan.FromSeconds(20);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox-Theme-Browser/1.0");
    }

    /// <summary>
    /// The swatches the browser's colour row offers — AMO searches themes by a colour, and
    /// these are the hues worth a button. Here rather than in the dialog so no view names a
    /// colour value.
    /// </summary>
    public static IReadOnlyList<(string Name, string Hex)> SearchColours { get; } =
    [
        ("Red", "#C0392B"), ("Orange", "#D9822B"), ("Yellow", "#D4B106"), ("Green", "#3D8B4F"),
        ("Teal", "#2A8F8F"), ("Blue", "#2E6DA4"), ("Purple", "#7D5BA6"), ("Pink", "#C2589C"),
        ("Grey", "#7F8C8D"), ("Black", "#20232A"),
    ];

    /// <summary>
    /// The categories AMO files its themes under — the shelves its own themes page browses
    /// by, and where the artwork lives. Name-cased for the row; the slug is the API's.
    /// </summary>
    public static IReadOnlyList<(string Name, string Slug)> Categories { get; } =
    [
        ("Abstract", "abstract"), ("Causes", "causes"), ("Fashion", "fashion"),
        ("Film & TV", "film-and-tv"), ("Holiday", "holiday"), ("Music", "music"),
        ("Nature", "nature"), ("Scenery", "scenery"), ("Seasonal", "seasonal"),
        ("Solid", "solid"), ("Sports", "sports"), ("Websites", "websites"), ("Other", "other"),
    ];

    /// <summary>The search URL for one page — separate and static, so a test can hold it still.</summary>
    public static string BuildSearchUrl(string baseUrl, string query, ThemeSort sort, string? colourHex, string? category, int page)
    {
        var sortKey = sort switch
        {
            ThemeSort.TopRated => "rating",
            ThemeSort.Trending => "hotness",
            _ => "users",
        };

        var url = $"{baseUrl.TrimEnd('/')}/addons/search/?type=statictheme&app=firefox"
                  + $"&page_size={PageSize}&page={Math.Max(1, page)}&sort={sortKey}";
        if (sort == ThemeSort.Recommended) url += "&promoted=recommended";
        if (query.Length > 0) url += "&q=" + Uri.EscapeDataString(query);
        if (colourHex is { Length: > 0 }) url += "&color=" + Uri.EscapeDataString(colourHex.TrimStart('#'));
        if (category is { Length: > 0 }) url += "&category=" + Uri.EscapeDataString(category);
        return url;
    }

    public async Task<(IReadOnlyList<ThemeListing> Results, long Total)> SearchAsync(
        string query, ThemeSort sort, string? colourHex, string? category, int page, CancellationToken cancel)
    {
        var url = BuildSearchUrl(_baseUrl, query, sort, colourHex, category, page);

        string json;
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            json = await ReadBounded(response, 4 * 1024 * 1024, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ThemeSourceException("addons.mozilla.org could not be reached.", ex);
        }

        return Parse(json);
    }

    /// <summary>The listing page's JSON to listings — separate and static, so a committed fixture proves the reading.</summary>
    public static (IReadOnlyList<ThemeListing> Results, long Total) Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new ThemeSourceException("The listing is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ThemeSourceException("The listing could not be read as JSON.", ex);
        }

        var results = new List<ThemeListing>();
        foreach (var entry in (root["results"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var file = entry["current_version"]?["file"] as JsonObject;
            var fileUrl = file?["url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(fileUrl)) continue; // nothing to install is nothing to list

            var licence = entry["current_version"]?["license"] as JsonObject;
            var previews = entry["previews"] as JsonArray;
            var thumbnail = previews?.OfType<JsonObject>()
                .Select(p => p["thumbnail_url"]?.GetValue<string>() ?? p["image_url"]?.GetValue<string>())
                .FirstOrDefault(u => u is not null && !u.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));

            results.Add(new ThemeListing(
                entry["slug"]?.GetValue<string>() ?? entry["id"]?.ToString() ?? "theme",
                Localised(entry["name"]) ?? "Untitled theme",
                (entry["authors"] as JsonArray)?.OfType<JsonObject>()
                    .Select(a => a["name"]?.GetValue<string>()).FirstOrDefault(n => n is { Length: > 0 }) ?? "unknown",
                entry["average_daily_users"]?.GetValue<long>() ?? 0,
                entry["ratings"]?["average"]?.GetValue<double>() ?? 0,
                thumbnail,
                fileUrl,
                file?["size"]?.GetValue<long>() ?? 0,
                Localised(licence?["name"]),
                licence?["url"]?.GetValue<string>()));
        }

        return (results, root["count"]?.GetValue<long>() ?? results.Count);
    }

    public async Task<byte[]> FetchAsync(string url, long maxBytes, CancellationToken cancel)
    {
        // Only this source's own host: a listing cannot point a fetch anywhere else.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.EndsWith("mozilla.org", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.EndsWith("mozilla.net", StringComparison.OrdinalIgnoreCase))
        {
            throw new ThemeSourceException($"\"{url}\" is not somewhere this source downloads from.");
        }

        try
        {
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                throw new ThemeSourceException("The file is over the size limit for a theme.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes) throw new ThemeSourceException("The file is over the size limit for a theme.");
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ThemeSourceException("The download could not be completed.", ex);
        }
    }

    private static async Task<string> ReadBounded(HttpResponseMessage response, long maxBytes, CancellationToken cancel)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes) throw new ThemeSourceException("The listing is too large to be one.");
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>AMO localises names as <c>{"en-US": "…"}</c>; a plain string is itself.</summary>
    private static string? Localised(JsonNode? node)
        => node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonObject map => map["en-US"]?.GetValue<string>()
                              ?? map.Select(p => p.Value?.GetValue<string>()).FirstOrDefault(v => v is { Length: > 0 }),
            _ => null,
        };

    public void Dispose() => _client.Dispose();
}
