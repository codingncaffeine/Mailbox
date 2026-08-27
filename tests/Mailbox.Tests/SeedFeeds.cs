using Mailbox.Core.Feeds;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Fills a scratch profile with real subscriptions and real articles, so the Feeds module can be
/// photographed with something in it.
/// </summary>
/// <remarks>
/// Real feeds rather than invented ones: what the article list has to survive is the shape of
/// actual publishing — headlines of wildly different lengths, entries with pictures and entries
/// without, a feed that publishes fifty items and one that publishes five. Invented rows agree
/// with the layout by construction and prove nothing.
/// <para>
/// Runs only when <c>MAILBOX_SEED_FEEDS</c> names a directory, as the message seed does, so an
/// ordinary test run neither touches the network nor writes anything.
/// </para>
/// </remarks>
public class SeedFeeds
{
    [Fact]
    public async Task SeedFeedsOnRequest()
    {
        var target = Environment.GetEnvironmentVariable("MAILBOX_SEED_FEEDS");
        if (string.IsNullOrWhiteSpace(target)) return;

        // Laid out so the profile can be handed to a run as XDG_CONFIG_HOME: the settings file
        // is where SettingsStore.DefaultPath would look for it, which is what a capture's scratch
        // copy reads. Put it anywhere else and the run starts with no subscriptions at all.
        var config = Path.Combine(target, "mailbox");
        Directory.CreateDirectory(config);

        var settings = new SettingsStore(Path.Combine(config, "settings.json"));
        var feeds = new FeedSubscriptions(settings);

        (string Url, string Name, string Category)[] wanted =
        [
            ("https://feeds.arstechnica.com/arstechnica/index", "Ars Technica", "Technology"),
            ("http://www.theverge.com/rss/full.xml", "The Verge", "Technology"),
            ("https://www.techradar.com/rss", "TechRadar", "Technology"),
            ("https://feeds.bbci.co.uk/news/rss.xml", "BBC News", "News"),
            ("https://www.theguardian.com/world/rss", "The Guardian", "News"),
            ("https://lwn.net/headlines/rss", "LWN.net", "Linux"),
            ("https://blog.rust-lang.org/feed.xml", "Rust Blog", "Linux"),
            ("https://xkcd.com/rss.xml", "xkcd", string.Empty),
        ];

        using (feeds.Batch())
        {
            foreach (var (url, name, category) in wanted) feeds.Add(url, name, category);
        }

        var order = new SettingsAccountOrder(settings);
        using var stores = new AccountStores(Path.Combine(target, "accounts"), order);

        // Add makes the standard folders; calling for them again would make a second set.
        var account = stores.All.FirstOrDefault() ?? stores.Add("you@example.com", "You", MailProtocol.Pop3);

        using var receiver = new FeedReceiver(feeds);
        var report = await receiver.PollAsync(account, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var folders = account.Mail.Folders(account.Account.Id);
        Console.WriteLine($"Seeded {report.Delivered} articles across {folders.Count} folders into {target}.");

        Assert.True(report.Delivered > 0, "no articles were delivered from any live feed");
    }
}
