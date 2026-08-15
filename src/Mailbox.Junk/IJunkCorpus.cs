namespace Mailbox.Junk;

/// <summary>
/// The trained corpus the classifier weighs a message against: how often each token has been
/// seen in mail marked junk versus mail marked not junk, and how many messages of each there
/// have been.
/// </summary>
/// <remarks>
/// An interface so the classifier is pure — it can be tested against an in-memory corpus, and
/// the real one is a table in the mail store. Nothing here reaches the network: §7.8's whole
/// point is that the corpus is the user's own and never leaves the machine.
/// </remarks>
public interface IJunkCorpus
{
    /// <summary>How many junk messages have been trained in.</summary>
    long SpamMessages { get; }

    /// <summary>How many not-junk messages have been trained in.</summary>
    long HamMessages { get; }

    /// <summary>How often a token has appeared in junk, and in not-junk.</summary>
    (long Spam, long Ham) Counts(string token);

    /// <summary>The counts for many tokens at once, so scoring is one read rather than one per word.</summary>
    IReadOnlyDictionary<string, (long Spam, long Ham)> CountsFor(IReadOnlyCollection<string> tokens);

    /// <summary>Records a message's tokens as junk or not junk, adding one to the message total.</summary>
    void Train(IReadOnlyCollection<string> tokens, bool spam);

    /// <summary>
    /// Undoes a training, for a message re-marked the other way — a false positive rescued from
    /// Junk should not still be counted as spam. Counts never go below zero.
    /// </summary>
    void Untrain(IReadOnlyCollection<string> tokens, bool spam);
}
