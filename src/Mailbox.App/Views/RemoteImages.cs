using System.Net;
using System.Net.Http;
using Mailbox.Core.Diagnostics;
using Mailbox.Rendering;

namespace Mailbox.App.Views;

/// <summary>What the reader has decided about a message's remote images.</summary>
public enum RemoteImagePolicy
{
    /// <summary>The default. Placeholders stay and nothing is fetched.</summary>
    Block,

    /// <summary>Fetch and inline for this message only. Not remembered.</summary>
    AllowOnce,
}

/// <summary>
/// Fetches the images a reader has asked for, on our terms rather than the engine's.
/// </summary>
/// <remarks>
/// This is the whole of Mailbox's outbound HTTP for a message, and it exists so that the
/// rendering engine never has any. A client we own means no cookies, no referer, no
/// authentication, a timeout, a size cap, and one place to point at a proxy later — none of
/// which is true of a request the engine makes on the sender's markup. See §11.
/// <para>
/// The bytes come back as <c>data:</c> URIs and go through the same inliner a <c>cid:</c> part
/// does, so the document still reaches the engine with nothing left in it to request.
/// </para>
/// </remarks>
public sealed class RemoteImages
{
    /// <summary>Anything larger is not an image worth waiting for in a reading pane.</summary>
    private const int MaxBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Client = Build();

    private static HttpClient Build()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.All,
        };

        // The cap is on the client as well as checked afterwards. A server that declares no
        // length, or lies about it, would otherwise be buffered in full before the check that
        // rejects it — and the reader chose to fetch from a host precisely because a stranger
        // asked them to.
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = MaxBytes,
        };

        // Says what it is. A user agent naming a browser would be a small lie told to every
        // tracking server the reader ever allows.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox/0.1");
        return client;
    }

    /// <summary>
    /// Fetches what a render blocked, and returns the map the next render inlines from.
    /// </summary>
    /// <remarks>
    /// A resource that fails stays blocked rather than failing the message: an image the
    /// sender's CDN will not serve is their problem, and the rest of the mail is still worth
    /// reading.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, string>> FetchAsync(
        IEnumerable<BlockedResource> blocked, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(blocked);

        var inlined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in blocked.DistinctBy(b => b.Url, StringComparer.OrdinalIgnoreCase))
        {
            if (await FetchOneAsync(resource.Url, cancellation) is { } uri) inlined[resource.Url] = uri;
        }

        return inlined;
    }

    private static async Task<string?> FetchOneAsync(string url, CancellationToken cancellation)
    {
        var absolute = url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;

        if (!Uri.TryCreate(absolute, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        try
        {
            using var response = await Client.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, cancellation);

            if (!response.IsSuccessStatusCode) return null;

            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

            if (response.Content.Headers.ContentLength is > MaxBytes) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
            if (bytes.Length is 0 or > MaxBytes) return null;

            return $"data:{type};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            // One image failing is not worth a dialog, and is worth a line in the log: a
            // reader who allowed images and saw nothing appear deserves an explanation
            // somewhere.
            Log.Warn($"Could not fetch a remote image from {uri.Host}.", ex);
            return null;
        }
    }
}
