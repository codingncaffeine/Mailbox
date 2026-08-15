namespace Mailbox.Junk;

/// <summary>
/// Scores a message's tokens as the probability it is junk, from a trained corpus.
/// </summary>
/// <remarks>
/// Naive Bayes, in the form Paul Graham described and every content filter since has built on.
/// Two deliberate biases, both toward leaving good mail alone — the error that costs a person a
/// message they wanted is worse than the one that leaves spam in the inbox:
/// <list type="bullet">
/// <item>the not-junk count of each token is <b>doubled</b>, so a word has to be markedly more
/// spammy than hammy before it tips;</item>
/// <item>a token never seen before is scored 0.4, slightly on the good side of even, rather than
/// treated as neutral.</item>
/// </list>
/// Only the fifteen most decisive tokens — those furthest from even — are combined, so a long
/// message is judged on its telling words rather than diluted by its ordinary ones.
/// </remarks>
public sealed class BayesianClassifier
{
    /// <summary>How many of the most decisive tokens are combined into the verdict.</summary>
    private const int Decisive = 15;

    /// <summary>The score for a token the corpus has never seen — slightly hammy, as Graham has it.</summary>
    private const double UnknownTokenScore = 0.4;

    /// <summary>Scores are clamped away from 0 and 1, so one certain token cannot swamp the rest.</summary>
    private const double Floor = 0.01;
    private const double Ceiling = 0.99;

    /// <summary>
    /// The probability, 0 to 1, that a message of these tokens is junk. Returns 0.5 — undecided —
    /// when the corpus has not been trained on both junk and not-junk, because a filter with
    /// nothing to go on should decide nothing.
    /// </summary>
    public double SpamProbability(IReadOnlyCollection<string> tokens, IJunkCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(corpus);

        if (corpus.SpamMessages == 0 || corpus.HamMessages == 0) return 0.5;

        var distinct = tokens.Distinct().ToArray();
        if (distinct.Length == 0) return 0.5;

        var counts = corpus.CountsFor(distinct);

        var scored = distinct
            .Select(token => TokenScore(token, counts.GetValueOrDefault(token), corpus))
            .OrderByDescending(p => Math.Abs(p - 0.5))
            .Take(Decisive)
            .ToArray();

        // Combine: the product of the spam scores against the product of their complements.
        // Clamped scores and at most fifteen terms keep this well away from underflow.
        var spam = 1.0;
        var ham = 1.0;
        foreach (var p in scored)
        {
            spam *= p;
            ham *= 1 - p;
        }

        return spam / (spam + ham);
    }

    private static double TokenScore(string token, (long Spam, long Ham) count, IJunkCorpus corpus)
    {
        if (count.Spam == 0 && count.Ham == 0) return UnknownTokenScore;

        var spamFreq = Math.Min(1.0, (double)count.Spam / corpus.SpamMessages);

        // The good count doubled: the thumb on the scale toward keeping mail.
        var hamFreq = Math.Min(1.0, 2.0 * count.Ham / corpus.HamMessages);

        var probability = spamFreq / (spamFreq + hamFreq);
        return Math.Clamp(probability, Floor, Ceiling);
    }
}
