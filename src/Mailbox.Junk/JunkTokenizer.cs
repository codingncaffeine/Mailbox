using System.Globalization;
using System.Text;

namespace Mailbox.Junk;

/// <summary>
/// Turns a message's text into the tokens the classifier weighs.
/// </summary>
/// <remarks>
/// Naive Bayes over words, with two refinements that matter for mail: the sender's address is a
/// token in its own right (a domain that only ever sends spam is the strongest signal there is),
/// and tokens are namespaced by where they were found — a word in the subject is a different
/// feature from the same word in the body, because spam shouts in the subject. Case is folded;
/// pure numbers and one-character scraps are dropped, being noise rather than signal.
/// </remarks>
public static class JunkTokenizer
{
    /// <summary>
    /// The tokens of a message: its sender, and the words of its subject and body, each
    /// namespaced by where it came from.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string fromAddress, string subject, string body)
    {
        var tokens = new List<string>();

        if (!string.IsNullOrWhiteSpace(fromAddress))
        {
            var address = fromAddress.Trim().ToLowerInvariant();
            tokens.Add("from:" + address);

            // The domain alone, so a new address at a spamming domain is already suspect.
            var at = address.IndexOf('@');
            if (at >= 0 && at < address.Length - 1)
            {
                tokens.Add("fromdomain:" + address[(at + 1)..]);
            }
        }

        foreach (var word in Words(subject)) tokens.Add("subject:" + word);
        foreach (var word in Words(body)) tokens.Add(word);

        return tokens;
    }

    /// <summary>
    /// The words worth weighing: lowercased, three characters or more, not a bare number. A cap
    /// keeps a pathological message — a megabyte of one repeated word — from dominating the
    /// corpus; the classifier only wants to know a word appeared, not how many thousand times.
    /// </summary>
    private static IEnumerable<string> Words(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder();

        foreach (var character in text)
        {
            // Letters, digits and a few in-word marks hold a token together; anything else ends
            // it. `$` and `%` are kept because "$$$" and "100%" are the language of spam.
            if (char.IsLetterOrDigit(character) || character is '\'' or '-' or '$' or '%')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                if (Emit(builder, seen) is { } word) yield return word;
                builder.Clear();
            }
        }

        if (Emit(builder, seen) is { } last) yield return last;
    }

    private static string? Emit(StringBuilder builder, HashSet<string> seen)
    {
        if (builder.Length < 3) return null;

        var word = builder.ToString();

        // A bare number carries nothing — a date, a quantity — so it is dropped. A number with a
        // symbol ("$500", "50%") is kept, because that is the signal.
        if (word.All(c => char.IsDigit(c))) return null;

        // Once per message: presence, not frequency, is what the classifier is built on, and a
        // repeated word would otherwise let one message stuff the corpus.
        return seen.Add(word) ? word : null;
    }
}
