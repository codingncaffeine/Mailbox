using System.Collections.Concurrent;
using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Finds the picture for an article whose feed sent none, by asking the publisher's page.
/// </summary>
/// <remarks>
/// Nearly every article published now has a picture, and most feeds carry it — but not all of
/// them do, and a reader whose one subscription is among the ones that do not sees a column of
/// lettered tiles. The picture is not missing; it is on the page, in the same
/// <c>og:image</c> every social network reads, and this goes and gets it.
/// <para>
/// <b>For the rows on screen, not for the store.</b> A pass over every article a reader owns
/// would be thousands of requests to publishers who did not ask for them, most of them for
/// articles nobody will look at. The article list is virtualised, so asking as a row is drawn
/// means asking for what is actually being read — and the answer is written to the row, so it
/// is asked once ever rather than once per scroll.
/// </para>
/// <para>
/// <b>The store is written on the UI thread.</b> The fetch is not: a request per visible row on
/// the thread drawing them would stall the list. What comes back is posted, because the store is
/// one SQLite file and the poll may be writing to it.
/// </para>
/// </remarks>
public sealed class FeedPictureLookup(Func<OpenAccount?> account, Func<FeedFetch?> fetch)
{
    /// <summary>How many pages are read at once. Enough to fill a screen, few enough to be polite.</summary>
    private readonly SemaphoreSlim _gate = new(3);

    /// <summary>
    /// Articles already asked about, whatever the answer was.
    /// </summary>
    /// <remarks>
    /// Including the ones that came back with nothing: a page with no picture on it will still
    /// have none the next time its row scrolls past, and re-asking on every pass is a request per
    /// keystroke for a reader holding the down arrow.
    /// </remarks>
    private readonly ConcurrentDictionary<long, byte> _asked = new();

    /// <summary>
    /// Which rows are still waiting on each article's lookup.
    /// </summary>
    /// <remarks>
    /// The same trap the thumbnail fetch has: the list is rebuilt whenever anything changes, and
    /// the row that asked is gone by the time the answer arrives. Everyone who asked gets told.
    /// </remarks>
    private readonly Dictionary<long, List<Action<string>>> _waiting = [];

    /// <summary>Whether pictures are looked up at all. Follows the reader's own setting.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Looks up the picture for an article that has none, and hands it back on the UI thread.
    /// </summary>
    /// <param name="found">
    /// Called with the address when one is found, on the UI thread. Not called at all when there
    /// is nothing to find, so the row keeps its lettered tile.
    /// </param>
    public void Want(MessageSummary article, Action<string> found)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(found);

        if (!Enabled) return;
        if (article.FeedImage.Length > 0 || article.FeedLink.Length == 0) return;
        if (fetch() is not { } client) return;

        // Claimed before the work starts, so a row realised twice while the first request is in
        // flight asks once — but the second row still gets the answer.
        lock (_waiting)
        {
            if (_waiting.TryGetValue(article.Id, out var queue))
            {
                queue.Add(found);
                return;
            }

            if (!_asked.TryAdd(article.Id, 0)) return;
            _waiting[article.Id] = [found];
        }

        // A capture waits for the pictures rather than photographing the placeholders, for the
        // same reason the thumbnail fetch holds it: whether they arrive is the claim being made.
        var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;

        _ = LookUpAsync(article, client, hold);
    }

    private async Task LookUpAsync(MessageSummary article, FeedFetch client, IDisposable? hold)
    {
        using var held = hold;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var answer = await client.GetAsync(article.FeedLink).ConfigureAwait(false);
            if (!answer.Ok || answer.Text.Length == 0) return;

            var url = PageCards.Read(answer.Text, article.FeedLink).ImageUrl;
            if (url.Length == 0) return;

            List<Action<string>> waiting;
            lock (_waiting)
            {
                waiting = _waiting.TryGetValue(article.Id, out var queue) ? queue : [];
                _waiting.Remove(article.Id);
            }

            Dispatcher.UIThread.Post(() =>
            {
                // Written to the row so it is there the next time the list is drawn, and after a
                // restart: a lookup that had to happen again on every launch would be a request
                // per article per session.
                account()?.Mail.SetFeedImage(article.Id, url);
                foreach (var waiter in waiting) waiter(url);
            });

            Log.Debug($"Feeds: found a picture for “{article.Subject}” on its own page.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Debug($"Feeds: no picture could be found for “{article.Subject}” — {ex.Message}");
        }
        finally
        {
            _gate.Release();
            lock (_waiting) _waiting.Remove(article.Id);
        }
    }

    /// <summary>Lets everything be asked again, for a reader who has just turned pictures on.</summary>
    public void Forget()
    {
        _asked.Clear();
        lock (_waiting) _waiting.Clear();
    }
}
