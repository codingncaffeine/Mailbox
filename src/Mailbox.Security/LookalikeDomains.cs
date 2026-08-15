using System.Globalization;
using System.Text;

namespace Mailbox.Security;

/// <summary>
/// Whether a sending domain is pretending to be one the reader knows.
/// </summary>
/// <remarks>
/// Two families, and they need different tests. A <em>homograph</em> uses characters that look
/// like other characters — Cyrillic а for Latin a — and is caught by looking at the domain
/// itself. A <em>typosquat</em> is spelled in plain ASCII and is one slip away from a real
/// domain, so it can only be caught by comparing against domains this reader actually deals
/// with. Guessing a list of famous brands would flag mail from anyone whose domain happens to
/// resemble one and miss the attack that matters, which is on the domains they correspond with.
/// </remarks>
public static class LookalikeDomains
{
    /// <summary>Pairs that read alike in a typeface nobody chose. "rn" is the classic.</summary>
    private static readonly (string From, string To)[] Confusables =
    [
        ("rn", "m"), ("vv", "w"), ("cl", "d"), ("0", "o"), ("1", "l"), ("5", "s"), ("8", "b"),
    ];

    /// <summary>
    /// True when the domain is written in a script the reader is unlikely to be able to
    /// distinguish from Latin, or is an encoded name hiding that fact.
    /// </summary>
    public static bool IsHomograph(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;

        // Punycode. The name on screen is not the name in the message, which is the whole
        // trick — and a legitimate internationalised domain is rare enough in mail that
        // saying so is worth the interruption.
        foreach (var label in domain.Split('.'))
        {
            if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)) return true;
        }

        var scripts = new HashSet<string>(StringComparer.Ordinal);
        var hasLatin = false;

        foreach (var c in domain)
        {
            if (!char.IsLetter(c)) continue;

            if (c < 128)
            {
                hasLatin = true;
                continue;
            }

            scripts.Add(char.GetUnicodeCategory(c).ToString());
        }

        // Mixed scripts in one name: Latin letters beside letters that are not.
        return hasLatin && scripts.Count > 0;
    }

    /// <summary>
    /// The familiar domain this one is imitating, or null.
    /// </summary>
    /// <param name="domain">The sender's domain.</param>
    /// <param name="familiar">
    /// Domains the reader is known to deal with — their own accounts, and everyone they have
    /// exchanged mail with.
    /// </param>
    public static string? Imitates(string domain, IEnumerable<string> familiar)
    {
        ArgumentNullException.ThrowIfNull(familiar);
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var candidate = domain.Trim().ToLowerInvariant();
        var folded = Fold(candidate);

        foreach (var known in familiar)
        {
            if (string.IsNullOrWhiteSpace(known)) continue;

            var other = known.Trim().ToLowerInvariant();
            if (other == candidate) return null;

            // Short names are too easy to be within one edit of by accident.
            if (other.Length < 6) continue;

            // Reads the same once confusable runs are folded together.
            if (Fold(other) == folded) return known;

            if (Distance(candidate, other) == 1) return known;
        }

        return null;
    }

    /// <summary>Collapses the pairs that look alike, so "paypa1" and "paypal" meet.</summary>
    private static string Fold(string value)
    {
        var folded = new StringBuilder(value);

        foreach (var (from, to) in Confusables)
        {
            folded.Replace(from, to);
        }

        return folded.ToString().Normalize(NormalizationForm.FormKC);
    }

    /// <summary>
    /// Levenshtein distance, stopped once it is past caring.
    /// </summary>
    /// <remarks>
    /// Two rows rather than a full matrix: this runs per message against every domain the
    /// reader knows, and the reading pane opens on a keystroke.
    /// </remarks>
    internal static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 2;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLower(a[i - 1], CultureInfo.InvariantCulture)
                           == char.ToLower(b[j - 1], CultureInfo.InvariantCulture) ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);

                best = Math.Min(best, current[j]);
            }

            if (best > 1) return 2;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
