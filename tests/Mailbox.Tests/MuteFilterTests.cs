using Mailbox.Core.Feeds;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Muting: what is kept out, what is deliberately not, and the word-boundary rule that is the
/// difference between a useful filter and one that empties the whole reader.
/// </summary>
public class MuteFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static MuteFilters Empty() => new(SettingsStore.Transient());

    [Fact]
    public void AFilterMatchesWholeWordsRatherThanAnySubstring()
    {
        // The case that decides whether this feature is usable: muting "AI" must not mute
        // Ukraine, rain, maintenance, said, and every article with the letters in it.
        var mutes = Empty();
        mutes.Add(new MuteFilter("AI"));

        Assert.NotNull(mutes.Matching("AI is everywhere", string.Empty, string.Empty, "u", Now));
        Assert.NotNull(mutes.Matching("The trouble with ai.", string.Empty, string.Empty, "u", Now));

        Assert.Null(mutes.Matching("Rain in Ukraine", string.Empty, string.Empty, "u", Now));
        Assert.Null(mutes.Matching("Maintenance window", string.Empty, string.Empty, "u", Now));
        Assert.Null(mutes.Matching("She said so", string.Empty, string.Empty, "u", Now));
    }

    [Fact]
    public void APhraseIsMatchedAsAPhrase()
    {
        var mutes = Empty();
        mutes.Add(new MuteFilter("world cup"));

        Assert.NotNull(mutes.Matching("The World Cup draw", string.Empty, string.Empty, "u", Now));
        Assert.Null(mutes.Matching("A world of cups", string.Empty, string.Empty, "u", Now));
    }

    [Fact]
    public void AFilterCanBeKeptToTheHeadline()
    {
        var mutes = Empty();
        mutes.Add(new MuteFilter("crypto", TitleOnly: true));

        Assert.NotNull(mutes.Matching("Crypto falls again", "Nothing to see", string.Empty, "u", Now));

        // Mentioned in passing in the body is not what the reader asked to be rid of.
        Assert.Null(mutes.Matching("Markets today", "Also crypto fell.", string.Empty, "u", Now));
    }

    [Fact]
    public void AFilterCanBeKeptToOneHeadingOrOneFeed()
    {
        var mutes = Empty();
        mutes.Add(new MuteFilter("transfer", MuteScope.Heading, "Sport"));
        mutes.Add(new MuteFilter("rumour", MuteScope.Feed, "https://a.example/feed"));

        Assert.NotNull(mutes.Matching("Transfer news", string.Empty, "Sport", "https://x.example/f", Now));
        Assert.Null(mutes.Matching("Transfer news", string.Empty, "Technology", "https://x.example/f", Now));

        Assert.NotNull(mutes.Matching("A rumour", string.Empty, "Any", "https://a.example/feed", Now));
        Assert.Null(mutes.Matching("A rumour", string.Empty, "Any", "https://b.example/feed", Now));
    }

    [Fact]
    public void AFilterWithATimeOnItStopsApplyingAndIsTidiedAway()
    {
        // Muting a story for a week is the ordinary case; a permanent rule for something that
        // ends is one the reader has to remember to come back and delete.
        var settings = SettingsStore.Transient();
        var mutes = new MuteFilters(settings);
        mutes.Add(new MuteFilter("election", ExpiresUtc: Now.AddDays(7)));

        Assert.NotNull(mutes.Matching("The election", string.Empty, string.Empty, "u", Now));
        Assert.Null(mutes.Matching("The election", string.Empty, string.Empty, "u", Now.AddDays(8)));

        Assert.Equal(1, mutes.Expire(Now.AddDays(8)));
        Assert.Empty(mutes.All);
        Assert.Empty(new MuteFilters(settings).All);
    }

    [Fact]
    public void APatternIsAvailableAndABrokenOneIsRefusedRatherThanThrown()
    {
        var mutes = Empty();
        mutes.Add(new MuteFilter(@"episode \d+", IsRegex: true));

        Assert.NotNull(mutes.Matching("Episode 42 is out", string.Empty, string.Empty, "u", Now));
        Assert.Null(mutes.Matching("Episode notes", string.Empty, string.Empty, "u", Now));

        // The dialog will not offer to add one that does not compile, and one that reached the
        // store anyway matches nothing rather than taking a delivery down.
        Assert.False(MuteFilters.IsValidPattern("([unclosed"));
        mutes.Add(new MuteFilter("([unclosed", IsRegex: true));
        Assert.Null(mutes.Matching("anything at all", string.Empty, string.Empty, "u", Now));
    }

    [Fact]
    public async Task AMutedArticleIsNeverFiled()
    {
        const string url = "https://example.com/feed.xml";
        var body = """
            <rss version="2.0"><channel><title>Example</title>
            <item><guid>1</guid><title>The election result</title><description>Votes.</description></item>
            <item><guid>2</guid><title>Something else entirely</title><description>Words.</description></item>
            </channel></rss>
            """;

        var server = new FakeFeedServer().Serve(url, body);
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(url, "Example");

        var mutes = Empty();
        mutes.Add(new MuteFilter("election"));

        using var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var account = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        var open = new OpenAccount(account, store, mail);

        using var receiver = new FeedReceiver(feeds, server) { Mutes = mutes };
        var report = await receiver.PollAsync(open, Now, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Delivered);
        Assert.Equal(1, report.Muted);

        // Not filed and hidden, not filed and marked read: not filed. It costs no space and
        // turns up in no count and no search.
        var folder = mail.Folders(account.Id).Single(f => f.Name == "Example");
        var kept = Assert.Single(mail.Messages(folder.Id));
        Assert.Equal("Something else entirely", kept.Subject);

        // And the filter says how much work it has done, so a reader can judge whether to keep it.
        Assert.Equal(1, mutes.All[0].Muted);
    }

    [Fact]
    public async Task MutingSomethingDoesNotDeleteWhatHasAlreadyArrived()
    {
        // A filter is not a licence to delete somebody's messages, and the dialog says so.
        const string url = "https://example.com/feed.xml";
        var body = """
            <rss version="2.0"><channel><title>Example</title>
            <item><guid>1</guid><title>The election result</title><description>Votes.</description></item>
            </channel></rss>
            """;

        var server = new FakeFeedServer().Serve(url, body);
        var feeds = new FeedSubscriptions(SettingsStore.Transient());
        feeds.Add(url, "Example");

        var mutes = Empty();

        using var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var account = mail.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        var open = new OpenAccount(account, store, mail);

        using var receiver = new FeedReceiver(feeds, server) { Mutes = mutes };
        await receiver.PollAsync(open, Now, TestContext.Current.CancellationToken);

        var folder = mail.Folders(account.Id).Single(f => f.Name == "Example");
        Assert.Single(mail.Messages(folder.Id));

        mutes.Add(new MuteFilter("election"));
        await receiver.PollAsync(open, Now, TestContext.Current.CancellationToken, force: true);

        Assert.Single(mail.Messages(folder.Id));
    }

    [Fact]
    public void FiltersSurviveARestart()
    {
        var settings = SettingsStore.Transient();
        var mutes = new MuteFilters(settings);
        mutes.Add(new MuteFilter("world cup", MuteScope.Heading, "Sport", TitleOnly: true, ExpiresUtc: Now.AddDays(7)));

        var again = Assert.Single(new MuteFilters(settings).All);

        Assert.Equal("world cup", again.Text);
        Assert.Equal(MuteScope.Heading, again.Scope);
        Assert.Equal("Sport", again.Target);
        Assert.True(again.TitleOnly);
        Assert.Equal(Now.AddDays(7), again.ExpiresUtc);
    }
}
