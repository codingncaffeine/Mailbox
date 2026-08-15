using Mailbox.Junk;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The junk filter, against an in-memory corpus. What is tested is the behaviour §7.8 asks for:
/// it learns from what it is told, it leans toward keeping mail, lists always win, and the four
/// levels mean what they say. Nothing here touches a store or a network — the classifier is pure.
/// </summary>
public class JunkFilterTests
{
    /// <summary>A corpus in a dictionary, which is all the classifier needs.</summary>
    private sealed class MemoryCorpus : IJunkCorpus
    {
        private readonly Dictionary<string, (long Spam, long Ham)> _tokens = new(StringComparer.Ordinal);
        public long SpamMessages { get; private set; }
        public long HamMessages { get; private set; }

        public (long Spam, long Ham) Counts(string token) => _tokens.GetValueOrDefault(token);

        public IReadOnlyDictionary<string, (long Spam, long Ham)> CountsFor(IReadOnlyCollection<string> tokens)
            => tokens.Where(_tokens.ContainsKey).ToDictionary(t => t, t => _tokens[t]);

        public void Train(IReadOnlyCollection<string> tokens, bool spam)
        {
            if (spam) SpamMessages++; else HamMessages++;
            foreach (var t in tokens.Distinct())
            {
                var (s, h) = _tokens.GetValueOrDefault(t);
                _tokens[t] = spam ? (s + 1, h) : (s, h + 1);
            }
        }

        public void Untrain(IReadOnlyCollection<string> tokens, bool spam)
        {
            if (spam) SpamMessages = Math.Max(0, SpamMessages - 1);
            else HamMessages = Math.Max(0, HamMessages - 1);
            foreach (var t in tokens.Distinct())
            {
                var (s, h) = _tokens.GetValueOrDefault(t);
                _tokens[t] = spam ? (Math.Max(0, s - 1), h) : (s, Math.Max(0, h - 1));
            }
        }
    }

    private static List<string> Spam(string subject, string body = "buy now cheap pills viagra offer")
        => [.. JunkTokenizer.Tokenize("deals@spammer.example", subject, body)];

    private static List<string> Ham(string subject, string body = "meeting agenda notes attached please review")
        => [.. JunkTokenizer.Tokenize("priya@example.net", subject, body)];

    private static void TrainSpread(IJunkCorpus corpus)
    {
        // A small but real corpus: some obvious junk and some ordinary mail.
        for (var i = 0; i < 20; i++)
        {
            corpus.Train(Spam($"CHEAP PILLS {i} act now"), spam: true);
            corpus.Train(Ham($"Re: project update {i}"), spam: false);
        }
    }

    // ---- The classifier -------------------------------------------------------------------

    [Fact]
    public void AnUntrainedFilterDecidesNothing()
    {
        var corpus = new MemoryCorpus();
        var filter = new JunkFilter();

        var decision = filter.Judge(FilterLevel.High, Spam("free money"), corpus, isSafe: false, isBlocked: false);

        Assert.False(decision.IsJunk);
        Assert.Equal(0.5, decision.Score, 3);
    }

    [Fact]
    public void ItCatchesJunkItHasBeenTrainedOn()
    {
        var corpus = new MemoryCorpus();
        TrainSpread(corpus);
        var filter = new JunkFilter();

        var junk = filter.Judge(FilterLevel.High,
            [.. JunkTokenizer.Tokenize("deals@spammer.example", "CHEAP PILLS act now", "buy cheap pills viagra offer")],
            corpus, isSafe: false, isBlocked: false);

        Assert.True(junk.IsJunk);
        Assert.Equal(JunkReason.Classifier, junk.Reason);
        Assert.True(junk.Score > 0.75);
    }

    [Fact]
    public void ItLeavesOrdinaryMailAlone()
    {
        var corpus = new MemoryCorpus();
        TrainSpread(corpus);
        var filter = new JunkFilter();

        var good = filter.Judge(FilterLevel.High,
            [.. JunkTokenizer.Tokenize("priya@example.net", "Re: project update", "meeting agenda notes attached")],
            corpus, isSafe: false, isBlocked: false);

        Assert.False(good.IsJunk);
        Assert.True(good.Score < 0.5);
    }

    [Fact]
    public void LowIsAHigherBarThanHigh()
    {
        var corpus = new MemoryCorpus();
        TrainSpread(corpus);

        // A message that is spammy but not overwhelmingly so: some junk words, some ordinary.
        var borderline = new List<string>();
        borderline.AddRange(JunkTokenizer.Tokenize("stranger@unknown.example",
            "cheap offer about the project", "act now on this update please"));

        var filter = new JunkFilter();
        var atHigh = filter.Judge(FilterLevel.High, borderline, corpus, false, false);
        var atLow = filter.Judge(FilterLevel.Low, borderline, corpus, false, false);

        // Same score, different thresholds: Low never junks something High leaves alone.
        Assert.Equal(atHigh.Score, atLow.Score, 6);
        if (atLow.IsJunk) Assert.True(atHigh.IsJunk);
    }

    // ---- Levels and lists -----------------------------------------------------------------

    [Fact]
    public void OffNeverJunksOnAScore()
    {
        var corpus = new MemoryCorpus();
        TrainSpread(corpus);
        var filter = new JunkFilter();

        var decision = filter.Judge(FilterLevel.Off, Spam("cheap pills act now"), corpus, false, false);
        Assert.False(decision.IsJunk);
        Assert.Equal(JunkReason.NotJunk, decision.Reason);
    }

    [Fact]
    public void ASafeSenderIsNeverJunkAndABlockedSenderAlwaysIs()
    {
        var corpus = new MemoryCorpus();
        TrainSpread(corpus);
        var filter = new JunkFilter();

        // The spammiest possible message, but from a safe sender: kept.
        var safe = filter.Judge(FilterLevel.High, Spam("cheap pills act now"), corpus, isSafe: true, isBlocked: false);
        Assert.False(safe.IsJunk);
        Assert.Equal(JunkReason.SafeSender, safe.Reason);

        // The most ordinary message, but from a blocked sender: junked.
        var blocked = filter.Judge(FilterLevel.High, Ham("Re: lunch"), corpus, isSafe: false, isBlocked: true);
        Assert.True(blocked.IsJunk);
        Assert.Equal(JunkReason.BlockedSender, blocked.Reason);
    }

    [Fact]
    public void SafeListsOnlyJunksEverythingNotFromASafeSender()
    {
        var corpus = new MemoryCorpus();
        var filter = new JunkFilter();

        // No training at all, and a perfectly ordinary message: still junk, because it is not safe.
        var stranger = filter.Judge(FilterLevel.SafeListsOnly, Ham("Re: lunch"), corpus, isSafe: false, isBlocked: false);
        Assert.True(stranger.IsJunk);
        Assert.Equal(JunkReason.NotOnSafeList, stranger.Reason);

        var friend = filter.Judge(FilterLevel.SafeListsOnly, Ham("Re: lunch"), corpus, isSafe: true, isBlocked: false);
        Assert.False(friend.IsJunk);
    }

    // ---- The tokenizer --------------------------------------------------------------------

    [Fact]
    public void TheSenderAndItsDomainAreTokens()
    {
        var tokens = JunkTokenizer.Tokenize("Deals@Spammer.Example", "hello", "world");

        Assert.Contains("from:deals@spammer.example", tokens);
        Assert.Contains("fromdomain:spammer.example", tokens);
    }

    [Fact]
    public void SubjectWordsAreDistinctFeaturesFromBodyWords()
    {
        var tokens = JunkTokenizer.Tokenize("a@b.com", "invoice", "invoice");

        Assert.Contains("subject:invoice", tokens);
        Assert.Contains("invoice", tokens);
    }

    [Fact]
    public void NoiseIsDropped()
    {
        var tokens = JunkTokenizer.Tokenize("a@b.com", "", "the 2026 a bc meeting");

        // Bare numbers and sub-three-letter scraps carry nothing.
        Assert.DoesNotContain("2026", tokens);
        Assert.DoesNotContain("bc", tokens);
        Assert.Contains("the", tokens);
        Assert.Contains("meeting", tokens);
    }

    [Fact]
    public void ARepeatedWordCountsOnce()
    {
        var tokens = JunkTokenizer.Tokenize("a@b.com", "", "free free free free free money");
        Assert.Single(tokens, t => t == "free");
    }

    // ---- Training and untraining ----------------------------------------------------------

    [Fact]
    public void UntrainingUndoesATrainingAndCannotGoNegative()
    {
        var corpus = new MemoryCorpus();
        var tokens = Spam("cheap pills");

        corpus.Train(tokens, spam: true);
        Assert.Equal(1, corpus.SpamMessages);

        corpus.Untrain(tokens, spam: true);
        Assert.Equal(0, corpus.SpamMessages);
        Assert.Equal((0, 0), corpus.Counts("subject:cheap"));

        // Once more past zero: it floors rather than going negative.
        corpus.Untrain(tokens, spam: true);
        Assert.Equal(0, corpus.SpamMessages);
    }

    // ---- The store-backed corpus ----------------------------------------------------------

    /// <summary>
    /// Training through the real store persists, and the same classifier that works on an
    /// in-memory corpus works on the one on disk — the adapter carries no logic of its own.
    /// </summary>
    [Fact]
    public void TheStoreBackedCorpusTrainsAndClassifies()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var corpus = new JunkCorpus(repo);

        for (var i = 0; i < 20; i++)
        {
            corpus.Train(Spam($"CHEAP PILLS {i} act now"), spam: true);
            corpus.Train(Ham($"Re: project update {i}"), spam: false);
        }

        Assert.Equal(20, corpus.SpamMessages);
        Assert.Equal(20, corpus.HamMessages);

        var filter = new JunkFilter();
        var junk = filter.Judge(FilterLevel.High,
            [.. JunkTokenizer.Tokenize("deals@spammer.example", "CHEAP PILLS act now", "buy cheap pills viagra offer")],
            corpus, isSafe: false, isBlocked: false);
        Assert.True(junk.IsJunk);

        // Untraining a message takes its counts back out.
        corpus.Untrain(Spam("CHEAP PILLS 0 act now"), spam: true);
        Assert.Equal(19, corpus.SpamMessages);
    }

    [Fact]
    public void TheBlockedListRoundTrips()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var now = DateTimeOffset.UnixEpoch;

        repo.AddBlockedSender("Spammer@Bad.Example", now);
        Assert.True(repo.IsBlockedSender("spammer@bad.example"));
        Assert.Equal("spammer@bad.example", Assert.Single(repo.BlockedSenders()));

        repo.RemoveBlockedSender("spammer@bad.example");
        Assert.False(repo.IsBlockedSender("spammer@bad.example"));
    }
}
