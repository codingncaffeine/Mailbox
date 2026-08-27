using System.Collections.Concurrent;
using System.Net;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// The pictures the article list draws: fetched once, kept in memory, and never asked for twice.
/// </summary>
/// <remarks>
/// On the same terms as <see cref="RemoteImages"/> — our own client, no cookies, no referer, a
/// timeout and a size cap — because the reasoning there holds here too: this is outbound HTTP on
/// somebody else's markup, and it should carry nothing about the reader.
/// <para>
/// <b>Where this differs from a message's images, and why.</b> A stranger's mail has its pictures
/// blocked until the reader asks, because the request itself tells the sender they were read. A
/// feed is not a stranger: the reader went and subscribed to it, the publisher already knows the
/// feed was fetched, and a list of articles with no pictures in it is not the thing anybody means
/// by a feed reader. So these load by default — and there is a setting, and it is honoured, for
/// the reader who would rather they did not.
/// </para>
/// <para>
/// Bounded in two ways that matter for a list: at most a few requests at once, so scrolling
/// quickly does not open fifty connections; and a cap on how many decoded pictures are kept, so a
/// reader who scrolls through a thousand articles does not end up holding all thousand.
/// </para>
/// </remarks>
public sealed class FeedThumbnails
{
    /// <summary>Wider than any row draws one; enough that a high-density screen is not soft.</summary>
    private const int DecodeWidth = 320;

    /// <summary>Anything larger is not a thumbnail.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>How many decoded pictures are kept before the oldest are let go.</summary>
    private const int MostKept = 400;

    private static readonly HttpClient Client = Build();

    private readonly ConcurrentDictionary<string, Bitmap?> _kept = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly SemaphoreSlim _gate = new(4);

    /// <summary>
    /// Who is still waiting for each picture being fetched.
    /// </summary>
    /// <remarks>
    /// A list rather than a single caller, and this is load-bearing. The list is virtualised and
    /// rebuilt whenever anything changes — a poll finishing, a row being marked read, a view being
    /// switched — and each rebuild makes new Image controls for the same articles. With one caller
    /// remembered, the second row to ask for a picture already in flight was dropped and the
    /// callback went to a control no longer on screen, so the row stayed blank until it was
    /// scrolled out of view and back.
    /// </remarks>
    private readonly Dictionary<string, List<Action<Bitmap>>> _waiting = new(StringComparer.Ordinal);

    /// <summary>Whether pictures are fetched at all. Read from the reader's setting.</summary>
    public bool Enabled { get; set; } = true;

    private static HttpClient Build()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxBytes,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox/1.0 (+feeds)");
        return client;
    }

    /// <summary>The picture if it is already here, without asking for it.</summary>
    public Bitmap? Ready(string url)
        => url.Length > 0 && _kept.TryGetValue(url, out var bitmap) ? bitmap : null;

    /// <summary>
    /// Fetches a picture and hands it back on the UI thread, or does nothing at all if it is
    /// already here, already being fetched, or already known not to be a picture.
    /// </summary>
    /// <param name="onLoaded">
    /// Called on the UI thread when the picture is ready. Not called when there is nothing to
    /// show — a row with no picture draws its placeholder and stays as it is.
    /// </param>
    public void Want(string url, Action<Bitmap> onLoaded)
    {
        ArgumentNullException.ThrowIfNull(onLoaded);

        if (!Enabled || url.Length == 0) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) return;

        // Already here, or already known not to be a picture — a broken address is not
        // re-requested every time its row scrolls back into view.
        if (_kept.TryGetValue(url, out var already))
        {
            if (already is not null) Dispatcher.UIThread.Post(() => onLoaded(already));
            return;
        }

        lock (_waiting)
        {
            // Being fetched already. Join the queue rather than being dropped.
            if (_waiting.TryGetValue(url, out var queue))
            {
                queue.Add(onLoaded);
                return;
            }

            _waiting[url] = [onLoaded];
        }

        // A capture waits for the pictures rather than photographing the placeholders: whether
        // the thumbnails arrive is exactly the claim a screenshot of this list is making.
        //
        // Taken here rather than inside the task, and that is the whole point: the task starts
        // asynchronously, so a hold taken inside it can be too late — the capture settles in the
        // gap and photographs the placeholder it was meant to wait for.
        var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;

        _ = FetchAsync(url, address, hold);
    }

    private async Task FetchAsync(string url, Uri address, IDisposable? hold)
    {
        using var held = hold;

        Bitmap? bitmap = null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await Client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength <= MaxBytes)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length is > 0 and <= MaxBytes)
                {
                    using var stream = new MemoryStream(bytes);
                    bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or NotSupportedException)
        {
            // A picture that will not load is not worth a message to the reader. The row keeps
            // its placeholder, and the failure is remembered so it is not retried on every scroll.
            Log.Debug($"Feeds: the picture at {url} could not be loaded — {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }

        // Everyone who asked while it was in flight — and a null, which is what says not to ask
        // for this one again.
        List<Action<Bitmap>> waiting;
        lock (_waiting)
        {
            waiting = _waiting.TryGetValue(url, out var queue) ? queue : [];
            _waiting.Remove(url);
        }

        _kept[url] = bitmap;
        if (bitmap is not { } ready) return;

        Remember(url);
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var waiter in waiting) waiter(ready);
        });
    }

    /// <summary>Lets the oldest pictures go once there are too many to be holding.</summary>
    private void Remember(string url)
    {
        _order.Enqueue(url);

        while (_order.Count > MostKept && _order.TryDequeue(out var oldest))
        {
            if (!_kept.TryRemove(oldest, out var bitmap)) continue;
            bitmap?.Dispose();
        }
    }

    /// <summary>Drops everything, for a reader who has just turned pictures off.</summary>
    public void Forget()
    {
        foreach (var url in _kept.Keys)
        {
            if (_kept.TryRemove(url, out var bitmap)) bitmap?.Dispose();
        }

        while (_order.TryDequeue(out _)) { }

        lock (_waiting) _waiting.Clear();
    }
}
