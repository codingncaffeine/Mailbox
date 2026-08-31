using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailbox.Theming.Browse;

/// <summary>One theme a source offers: what the list shows, where its file is, and its terms.</summary>
public sealed record ThemeListing(
    string Slug,
    string Name,
    string Author,
    long Users,
    double Rating,
    string? ThumbnailUrl,
    string FileUrl,
    long FileSize,
    string? LicenceName,
    string? LicenceUrl);

/// <summary>How a search is ordered. The names are ours; each source maps them to its own.</summary>
public enum ThemeSort
{
    /// <summary>The source's own showcase — AMO's curated recommended shelf, artwork first.</summary>
    Recommended,
    Popular,
    TopRated,
    Trending,
}

/// <summary>
/// Somewhere themes can be browsed from. The dialog runs over this seam, so the community
/// gallery of Mailbox packs can join the browser later without the browser changing.
/// </summary>
public interface IThemeSource
{
    /// <summary>A page of results and how many there are in all.</summary>
    Task<(IReadOnlyList<ThemeListing> Results, long Total)> SearchAsync(
        string query, ThemeSort sort, string? colourHex, string? category, int page, CancellationToken cancel);

    /// <summary>One file by the URL a listing gave, bounded — a byte past the cap is a refusal, not a truncation.</summary>
    Task<byte[]> FetchAsync(string url, long maxBytes, CancellationToken cancel);
}

/// <summary>Thrown when a source cannot answer, with the sentence the status line shows.</summary>
public sealed class ThemeSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A directory as a theme source: a <c>listing.json</c> of entries whose files sit beside it.
/// The harness's source — poses and tests browse committed fixtures, offline and
/// deterministic — and the shape a hand-rolled local gallery could take.
/// </summary>
public sealed class DirectoryThemeSource(string directory) : IThemeSource
{
    public Task<(IReadOnlyList<ThemeListing> Results, long Total)> SearchAsync(
        string query, ThemeSort sort, string? colourHex, string? category, int page, CancellationToken cancel)
    {
        var path = Path.Combine(directory, "listing.json");
        if (!File.Exists(path)) throw new ThemeSourceException($"\"{directory}\" has no listing.json.");

        var results = new List<ThemeListing>();
        if (JsonNode.Parse(File.ReadAllText(path)) is JsonArray entries)
        {
            foreach (var entry in entries.OfType<JsonObject>())
            {
                string Text(string key) => entry[key]?.GetValue<string>() ?? string.Empty;
                var listing = new ThemeListing(
                    Text("slug"), Text("name"), Text("author"),
                    entry["users"]?.GetValue<long>() ?? 0,
                    entry["rating"]?.GetValue<double>() ?? 0,
                    entry["thumbnail"]?.GetValue<string>(),
                    Text("file"),
                    entry["size"]?.GetValue<long>() ?? 0,
                    entry["licenceName"]?.GetValue<string>(),
                    entry["licenceUrl"]?.GetValue<string>());

                if (query.Length == 0
                    || listing.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || listing.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(listing);
                }
            }
        }

        return Task.FromResult(((IReadOnlyList<ThemeListing>)results, (long)results.Count));
    }

    public Task<byte[]> FetchAsync(string url, long maxBytes, CancellationToken cancel)
    {
        var full = Path.GetFullPath(Path.Combine(directory, url));
        var root = Path.GetFullPath(directory);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ThemeSourceException($"\"{url}\" climbs out of the source.");
        }

        if (!File.Exists(full)) throw new ThemeSourceException($"\"{url}\" is not in the source.");
        if (new FileInfo(full).Length > maxBytes) throw new ThemeSourceException($"\"{url}\" is over the size limit.");
        return Task.FromResult(File.ReadAllBytes(full));
    }
}
