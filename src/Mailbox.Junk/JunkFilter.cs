namespace Mailbox.Junk;

/// <summary>
/// How hard the junk filter works, in the reference's own four settings.
/// </summary>
public enum FilterLevel
{
    /// <summary>No automatic filtering — only the blocked-senders list moves anything to Junk.</summary>
    Off,

    /// <summary>The reference's default: catch only the most obvious junk. A high bar.</summary>
    Low,

    /// <summary>Catch more, at the cost of the occasional wanted message going to Junk.</summary>
    High,

    /// <summary>Only mail from a safe sender reaches the inbox; everything else is junk.</summary>
    SafeListsOnly,
}

/// <summary>Why a message was judged junk or not, so the decision can be explained and tested.</summary>
public enum JunkReason
{
    /// <summary>Filtering is off, or the message was below the level's threshold.</summary>
    NotJunk,

    /// <summary>The sender is on the safe list — lists always win.</summary>
    SafeSender,

    /// <summary>The sender is on the blocked list — lists always win.</summary>
    BlockedSender,

    /// <summary>The classifier scored it above the level's threshold.</summary>
    Classifier,

    /// <summary>Safe Lists Only, and the sender is not safe.</summary>
    NotOnSafeList,
}

/// <summary>What the filter decided about a message.</summary>
public sealed record JunkDecision(bool IsJunk, JunkReason Reason, double Score);

/// <summary>
/// The junk filter: the blocked and safe lists, the four filter levels, and the classifier
/// behind them.
/// </summary>
/// <remarks>
/// Lists always win, in both directions and before the classifier is consulted — a sender the
/// user has marked safe is never junked on a score, and one they have blocked is always junked.
/// Only then does the level decide: Off does nothing, Low and High compare the classifier's score
/// against a threshold, and Safe Lists Only junks anything not from a safe sender without asking
/// the classifier at all. The corpus is local and the whole of it stays on the machine (§7.8).
/// </remarks>
public sealed class JunkFilter(BayesianClassifier classifier)
{
    private readonly BayesianClassifier _classifier = classifier;

    public JunkFilter() : this(new BayesianClassifier())
    {
    }

    /// <summary>The score above which Low treats a message as junk — a high bar, few false positives.</summary>
    public double LowThreshold { get; init; } = 0.95;

    /// <summary>The score above which High treats a message as junk — more aggressive.</summary>
    public double HighThreshold { get; init; } = 0.75;

    /// <summary>
    /// Judges a message. <paramref name="isSafe"/> and <paramref name="isBlocked"/> are the
    /// list checks, done by the caller because the lists live in the store.
    /// </summary>
    public JunkDecision Judge(
        FilterLevel level,
        IReadOnlyCollection<string> tokens,
        IJunkCorpus corpus,
        bool isSafe,
        bool isBlocked)
    {
        // Lists first, and they are final.
        if (isSafe) return new JunkDecision(false, JunkReason.SafeSender, 0);
        if (isBlocked) return new JunkDecision(true, JunkReason.BlockedSender, 1);

        switch (level)
        {
            case FilterLevel.Off:
                return new JunkDecision(false, JunkReason.NotJunk, 0);

            case FilterLevel.SafeListsOnly:
                // Not safe (checked above), so junk. The classifier is not consulted.
                return new JunkDecision(true, JunkReason.NotOnSafeList, 1);

            case FilterLevel.Low:
            case FilterLevel.High:
                var score = _classifier.SpamProbability(tokens, corpus);
                var threshold = level == FilterLevel.High ? HighThreshold : LowThreshold;
                return score >= threshold
                    ? new JunkDecision(true, JunkReason.Classifier, score)
                    : new JunkDecision(false, JunkReason.NotJunk, score);

            default:
                return new JunkDecision(false, JunkReason.NotJunk, 0);
        }
    }
}
