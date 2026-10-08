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
/// which is true of a request the engine makes on the sender's markup — the sanitizer's whole point.
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

        // What a browser asks for an image with. Some image servers answer by what is accepted,
        // and a request accepting nothing in particular gets a page, or a refusal.
        client.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        return client;
    }

    /// <summary>How many images are fetched at once: what a browser opens per host, roughly.</summary>
    private const int AtOnce = 6;

    /// <summary>
    /// Fetches what a render blocked, and returns the map the next render inlines from.
    /// </summary>
    /// <remarks>
    /// A resource that fails stays blocked rather than failing the message: an image the
    /// sender's CDN will not serve is their problem, and the rest of the mail is still worth
    /// reading.
    /// <para>
    /// Several at once, as a browser does. One at a time, a newsletter of forty pictures with a
    /// slow host among them took minutes, and a reader who allowed images and saw half of them
    /// was looking at a fetch still under way.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, string>> FetchAsync(
        IEnumerable<BlockedResource> blocked, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(blocked);

        var urls = blocked.Select(b => b.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inlined = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refused = 0;

        await Parallel.ForEachAsync(
            urls,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = cancellation },
            async (url, token) =>
            {
                if (await FetchOneAsync(url, token) is { } uri) inlined[url] = uri;
                else Interlocked.Increment(ref refused);
            });

        Log.Info($"Remote images: {inlined.Count} of {urls.Count} fetched"
                 + (refused > 0 ? $", {refused} not (each says why above)." : "."));

        return new Dictionary<string, string>(inlined, StringComparer.OrdinalIgnoreCase);
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

            // Every refusal is said, by host and reason: an image that silently stays a
            // placeholder is the report "it still isn't pulling in all images" with nothing to
            // say which, or why.
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"Remote image from {uri.Host} not shown: the server answered {(int)response.StatusCode}.");
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                Log.Info($"Remote image from {uri.Host} not shown: larger than {MaxBytes / (1024 * 1024)} MB.");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
            if (bytes.Length is 0 or > MaxBytes)
            {
                Log.Info($"Remote image from {uri.Host} not shown: {(bytes.Length == 0 ? "empty" : "too large")}.");
                return null;
            }

            // The label first, then the bytes, as a browser does: storage services label
            // everything application/octet-stream, and a picture is a picture whatever its
            // server calls it. Something that is neither labelled nor shaped like an image —
            // a page, a script — is refused.
            var declared = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var type = declared.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? declared
                : ImageSniff.TypeOf(bytes);

            if (type is null)
            {
                Log.Info($"Remote image from {uri.Host} not shown: it is {(declared.Length > 0 ? declared : "unlabelled")}, "
                         + "and its bytes are not a picture.");
                return null;
            }

            return $"data:{type};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
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

/// <summary>What kind of picture some bytes are, by their first bytes, as a browser sniffs.</summary>
internal static class ImageSniff
{
    /// <summary>The image media type the bytes are, or null when they are not a picture.</summary>
    public static string? TypeOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G'])) return "image/png";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8)
            && (bytes[8..12].SequenceEqual("avif"u8) || bytes[8..12].SequenceEqual("avis"u8))) return "image/avif";
        if (bytes.StartsWith("BM"u8) && bytes.Length > 14) return "image/bmp";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00])) return "image/x-icon";

        // SVG is text: an <svg element near the top, after an optional XML declaration, comments
        // or white space.
        var head = System.Text.Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 1024)]);
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : null;
    }
}
