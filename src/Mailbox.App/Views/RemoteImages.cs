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
/// The bytes are handed to the engine by the pane under addresses of its own, so the document
/// still reaches the engine with nothing left in it to request — and nothing written into it: a
/// picture written into the page as text costs several times its size and had to be capped.
/// </para>
/// </remarks>
public sealed class RemoteImages
{
    /// <summary>
    /// Anything larger is not an image worth waiting for in a reading pane.
    /// </summary>
    /// <remarks>
    /// A browser has no cap at all, and a newsletter's animated header can run to tens of
    /// megabytes; four turned away real pictures every day, and a message whose only picture was
    /// large showed nothing however often its sender was trusted. The bytes go to the engine as
    /// they are rather than written into the page, so the ceiling costs memory only, and
    /// sixty-four is a ceiling on what a stranger can make the reader download rather than one
    /// that refuses what senders actually send.
    /// </remarks>
    private const int MaxBytes = 64 * 1024 * 1024;

    private static readonly HttpClient Client = Build();

    /// <summary>The client as built, for a test to read its request shape.</summary>
    internal static HttpClient ClientForTests => Client;

    private static HttpClient Build()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            // A browser follows twenty. Mailing services put a click-tracker and a CDN or two in
            // front of a picture, and at three the chain ran out before the picture arrived.
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
        };

        // The cap is on the client as well as checked afterwards. A server that declares no
        // length, or lies about it, would otherwise be buffered in full before the check that
        // rejects it — and the reader chose to fetch from a host precisely because a stranger
        // asked them to.
        // Shaped as a browser's request, without claiming to be one: HTTP/2 where the server
        // speaks it, and the language header every browser sends. The CDNs in front of the
        // larger retailers' and carriers' pictures score each request, and refuse with 403 one
        // that asks for HTTP/1.1, as .NET does unless told otherwise, or that names no language
        // — so every picture in an order confirmation from such a store was refused, however
        // many times the sender was trusted. A browser's agent string makes it worse: they
        // compare the string against the connection and refuse a mismatch.
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = MaxBytes,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        // Says what it is. A user agent naming a browser would be a small lie told to every
        // tracking server the reader ever allows.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox/0.1");

        var language = System.Globalization.CultureInfo.CurrentUICulture.Name;
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(
            language.Length > 0 && !language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? $"{language},en;q=0.8"
                : "en-US,en;q=0.9");

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
    public static async Task<IReadOnlyDictionary<string, FetchedPicture>> FetchAsync(
        IEnumerable<BlockedResource> blocked, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(blocked);

        var urls = blocked.Select(b => b.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inlined = new System.Collections.Concurrent.ConcurrentDictionary<string, FetchedPicture>(StringComparer.OrdinalIgnoreCase);
        var refused = 0;

        // What is already in memory is taken from there, and only the rest goes to the network.
        var wanted = new List<string>();
        foreach (var url in urls)
        {
            if (Memory.TryGet(url) is { } kept) inlined[url] = kept;
            else wanted.Add(url);
        }

        var remembered = inlined.Count;

        await Parallel.ForEachAsync(
            wanted,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = cancellation },
            async (url, token) =>
            {
                if (await SharedFetchAsync(url).WaitAsync(token) is { } picture) inlined[url] = picture;
                else Interlocked.Increment(ref refused);
            });

        Log.Info($"Remote images: {inlined.Count} of {urls.Count} fetched"
                 + (remembered > 0 ? $" ({remembered} already in memory)" : string.Empty)
                 + (refused > 0 ? $", {refused} not (each says why above)." : "."));

        return new Dictionary<string, FetchedPicture>(inlined, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pictures fetched this session, so a message read again shows them without asking the
    /// network a second time.
    /// </summary>
    /// <remarks>
    /// In memory only, and gone when Mailbox closes: nothing a sender's server sent is written to
    /// disk. Consulted only where pictures are allowed, so a picture fetched for one message
    /// never appears in another whose pictures are held back. Refusals are not kept, so Try
    /// Again asks the network again.
    /// </remarks>
    internal static readonly PictureMemory Memory = new(PictureMemory.DefaultBudget);

    /// <summary>Fetches under way, by address, so a second request for one joins the first.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<FetchedPicture?>>> InFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// One fetch per address however many ask for it, and its result kept when it succeeds.
    /// </summary>
    /// <remarks>
    /// The fetch is not cancelled with the message that started it. A reader who moves on while
    /// a slow picture arrives and then comes back finds it ready, or joins the fetch still on its
    /// way, rather than starting it again.
    /// </remarks>
    private static Task<FetchedPicture?> SharedFetchAsync(string url)
    {
        var started = InFlight.GetOrAdd(url, key => new Lazy<Task<FetchedPicture?>>(() => FetchAndKeepAsync(key)));
        return started.Value;
    }

    private static async Task<FetchedPicture?> FetchAndKeepAsync(string url)
    {
        try
        {
            var picture = await FetchOneAsync(url, CancellationToken.None);
            if (picture is not null) Memory.Add(url, picture);
            return picture;
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }

    private static async Task<FetchedPicture?> FetchOneAsync(string url, CancellationToken cancellation)
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

            return new FetchedPicture(bytes, type);
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

/// <summary>A picture as fetched: its bytes and the image type they are.</summary>
public sealed record FetchedPicture(byte[] Bytes, string Type);

/// <summary>
/// Fetched pictures kept in memory, the least recently used given up first once they pass a budget.
/// </summary>
/// <remarks>
/// Measured in bytes as fetched. The default holds 128 MB of pictures — several hundred
/// newsletters' worth — and a single picture too large to leave room for others is not kept.
/// </remarks>
internal sealed class PictureMemory(long budgetBytes)
{
    /// <summary>128 MB.</summary>
    public const long DefaultBudget = 128L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Url, FetchedPicture Picture)>> _byUrl = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Url, FetchedPicture Picture)> _order = new();
    private long _used;

    /// <summary>The bytes held now, for a test to read.</summary>
    public long Used
    {
        get { lock (_gate) return _used; }
    }

    public int Count
    {
        get { lock (_gate) return _byUrl.Count; }
    }

    public FetchedPicture? TryGet(string url)
    {
        lock (_gate)
        {
            if (!_byUrl.TryGetValue(url, out var node)) return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Picture;
        }
    }

    public void Add(string url, FetchedPicture picture)
    {
        if (picture.Bytes.Length > budgetBytes / 4) return;

        lock (_gate)
        {
            if (_byUrl.Remove(url, out var old))
            {
                _order.Remove(old);
                _used -= old.Value.Picture.Bytes.Length;
            }

            _byUrl[url] = _order.AddFirst((url, picture));
            _used += picture.Bytes.Length;

            while (_used > budgetBytes && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _byUrl.Remove(oldest.Value.Url);
                _used -= oldest.Value.Picture.Bytes.Length;
            }
        }
    }
}
