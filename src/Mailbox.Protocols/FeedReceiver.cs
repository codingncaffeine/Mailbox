using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Protocols;

/// <summary>What one pass over the subscriptions did.</summary>
/// <param name="Delivered">New items filed as messages.</param>
/// <param name="Failed">Feeds that could not be read, with what went wrong.</param>
public sealed record FeedReport(int Delivered, IReadOnlyList<(string Url, string Error)> Failed)
{
    public static readonly FeedReport Nothing = new(0, []);

    public string Summary => Failed.Count == 0
        ? $"{Delivered} new item(s)"
        : $"{Delivered} new item(s), {Failed.Count} feed(s) failed";
}

/// <summary>
/// The RSS reader (§15, Phase 14): feeds delivered into mail folders as messages.
/// </summary>
/// <remarks>
/// <b>A feed item is a message.</b> It is written into a folder as MIME, with the entry's own id
/// as its server id, so everything the mail module already does works on it without being told it
/// is a feed: the list draws it, the reading pane renders it through the same sanitizer, Delete
/// deletes it, Categorize tags it, search finds it, AutoArchive files it. That is what §15 means
/// by reusing the list and the reading pane wholesale, and it is why there is no feed module.
/// <para>
/// The entry's id is what stops a second download delivering it twice — the same job a POP3
/// UIDL does, filed in the same column.
/// </para>
/// <para>
/// The handler is injectable, as the DAV client's is, so the whole of this is testable against a
/// fake server rather than against somebody's real feed.
/// </para>
/// </remarks>
public sealed class FeedReceiver
{
    /// <summary>The folder the reference keeps feeds under, and this does too.</summary>
    public const string RootFolder = "RSS Feeds";

    private readonly FeedSubscriptions _feeds;
    private readonly HttpClient _client;

    public FeedReceiver(FeedSubscriptions feeds, HttpMessageHandler? handler = null)
    {
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(30);

        // Some publishers refuse a request with no user agent, and one that lies about being a
        // browser would be a worse citizen than one that says what it is.
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mailbox/1.0 (+feeds)");
    }

    /// <summary>Reads every subscription and files what is new into <paramref name="account"/>.</summary>
    public async Task<FeedReport> PollAsync(OpenAccount account, DateTimeOffset now, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (_feeds.All.Count == 0) return FeedReport.Nothing;

        var delivered = 0;
        var failed = new List<(string Url, string Error)>();

        foreach (var feed in _feeds.All.ToList())
        {
            try
            {
                var text = await _client.GetStringAsync(feed.Url, cancellation).ConfigureAwait(false);
                delivered += Deliver(account, feed, FeedParser.Parse(text), now);
                _feeds.Checked(feed.Url, now);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or UriFormatException)
            {
                failed.Add((feed.Url, ex.Message));
                Log.Warn($"The feed at {feed.Url} could not be read: {ex.Message}");
            }
        }

        return new FeedReport(delivered, failed);
    }

    /// <summary>
    /// Files a feed's new entries. Returns how many were new.
    /// </summary>
    /// <remarks>
    /// Public so the harness can pose a feed without a network: what has to be provable is that an
    /// entry becomes a message in the right folder exactly once, not that HTTP works.
    /// </remarks>
    /// <param name="arrival">
    /// What to run over each item as it is filed, or null to file it and nothing more. Null is
    /// the default and the reference's: its "Enable rules on all messages downloaded from RSS
    /// Feeds" is off out of the box, and a folder-per-feed is already a kind of filing.
    /// </param>
    public static int Deliver(OpenAccount account, FeedSubscription feed, FeedChannel channel, DateTimeOffset now,
        IArrivalHandler? arrival = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(channel);

        var folder = EnsureFolder(account, feed.Name is { Length: > 0 } named ? named : channel.Title);
        var already = account.Mail.MessageIdsByServerUid(folder.Id);

        var delivered = 0;
        foreach (var item in channel.Items)
        {
            if (item.Id.Length == 0 || already.ContainsKey(item.Id)) continue;

            var message = Compose(channel, item, feed);
            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();

            var summary = MessageMapper.ToSummary(message, item.Id, raw.Length, item.Published ?? now);
            var id = account.Mail.AddMessage(folder.Id, summary, raw);
            delivered++;

            // Rules over feed items are off unless asked for, and a rule that moves one has to
            // see it where it was filed — so the pipeline runs after the message exists, not on
            // the way in. A null id is a message this store already had, which is nothing new to
            // run anything over.
            if (arrival is not null && id is { } stored) arrival.Handle(account.Mail, folder, stored, message);
        }

        return delivered;
    }

    /// <summary>The feed's folder, under the RSS Feeds heading, made if it is not there.</summary>
    private static Folder EnsureFolder(OpenAccount account, string name)
    {
        var folders = account.Mail.Folders(account.Account.Id);

        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == RootFolder)
                   ?? account.Mail.AddFolder(account.Account.Id, RootFolder);

        return folders.FirstOrDefault(f => f.ParentId == root.Id && f.Name == name)
               ?? account.Mail.AddFolder(account.Account.Id, name, parentId: root.Id);
    }

    /// <summary>
    /// One entry as a message.
    /// </summary>
    /// <remarks>
    /// The feed is the sender, because that is who a reader is hearing from; the entry's own link
    /// goes at the foot of the body, where the reference puts its "View article" line, so a reader
    /// can always reach the original. The address is invented from the feed's host and marked as
    /// such — nothing here should ever be replied to by accident.
    /// </remarks>
    private static MimeMessage Compose(FeedChannel channel, FeedItem item, FeedSubscription feed)
    {
        // An address needs a host, and a feed's address may not have one — a local path parses as
        // a file URI whose host is empty, and "rss@" is not an address MimeKit will take.
        var host = Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 } hosted
            ? hosted
            : "feed.invalid";
        var who = item.Author is { Length: > 0 } author ? author : channel.Title;

        var message = new MimeMessage
        {
            Subject = item.Title is { Length: > 0 } title ? title : "(no subject)",
            Date = item.Published ?? DateTimeOffset.UtcNow,
        };

        // Which feed this came from. Nothing else in the message says: every feed on a host
        // sends as rss@<host>, so a rule matching the sender would catch a site's whole set.
        message.Headers.Add("X-Mailbox-Feed", feed.Url);

        message.From.Add(new MailboxAddress(who, $"rss@{host}"));
        message.To.Add(new MailboxAddress(channel.Title is { Length: > 0 } named ? named : feed.Name, $"subscriber@{host}"));

        var body = new BodyBuilder
        {
            HtmlBody = item.Html.Length > 0
                ? item.Html + Footer(item)
                : $"<p>{System.Net.WebUtility.HtmlEncode(item.Title)}</p>{Footer(item)}",
            TextBody = item.Link.Length > 0 ? $"{item.Title}\n\n{item.Link}" : item.Title,
        };

        message.Body = body.ToMessageBody();
        return message;
    }

    private static string Footer(FeedItem item)
        => item.Link.Length > 0
            ? $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(item.Link)}\">View article</a></p>"
            : string.Empty;
}
