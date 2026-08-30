using Mailbox.Core.Settings;

namespace Mailbox.Core.People;

/// <summary>
/// How a typed full name splits into its parts, under the People page's "Default Full Name
/// order" — the row that decides what "Anne Marie Vries" means, there being nothing in the
/// string itself to say.
/// </summary>
/// <remarks>
/// A comma overrules the option, as the reference's own parsing does: "Vries, Anne" is
/// last-comma-first whatever the default, because the writer just said so. One word is a first
/// name alone — filing a person under the only word they have would make "Cher" a surname.
/// </remarks>
public static class FullNames
{
    public readonly record struct NameParts(string Prefix, string First, string Middle, string Last, string Suffix)
    {
        /// <summary>The parts back as one line, in speaking order, skipping what is empty.</summary>
        public string Joined()
            => string.Join(' ', new[] { Prefix, First, Middle, Last, Suffix }.Where(p => p.Length > 0));
    }

    /// <summary>The titles the reference's Check Full Name offers, recognised typed too.</summary>
    public static readonly string[] Prefixes = ["Dr.", "Miss", "Mr.", "Mrs.", "Ms.", "Prof."];

    /// <summary>Its suffixes, likewise.</summary>
    public static readonly string[] Suffixes = ["I", "II", "III", "IV", "Jr.", "Sr."];

    private static bool IsPrefix(string word)
        => Prefixes.Any(p => p.TrimEnd('.').Equals(word.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

    private static bool IsSuffix(string word)
        => Suffixes.Any(s => s.TrimEnd('.').Equals(word.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

    public static NameParts Parse(string typed, FullNameOrder order)
    {
        var text = (typed ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            // Explicit empties, never `default`: the struct's default is five nulls, and a
            // caller asking an empty card's parsed.Last.Length would fall over the first one.
            return new NameParts(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        // The title and the suffix come off the ends first — "Dr. Anne Smith Jr." is Anne Smith
        // with a Dr. in front and a Jr. behind, in every order the option can choose — so the
        // order rule below is applied to the name itself and never files a person under "Jr.".
        var prefix = string.Empty;
        var suffix = string.Empty;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length > 1 && IsPrefix(words[0]))
        {
            prefix = words[0];
            words = words[1..];
        }

        if (words.Length > 1 && IsSuffix(words[^1].TrimEnd(',')))
        {
            suffix = words[^1];
            words = words[..^1];
            if (words.Length > 0) words[^1] = words[^1].TrimEnd(',');
        }

        text = string.Join(' ', words);
        if (text.Length == 0) return new NameParts(prefix, string.Empty, string.Empty, string.Empty, suffix);

        // The comma form names its own order.
        var comma = text.IndexOf(',');
        if (comma >= 0)
        {
            var last = text[..comma].Trim();
            var rest = text[(comma + 1)..].Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return new NameParts(
                prefix,
                rest.Length > 0 ? rest[0] : string.Empty,
                rest.Length > 1 ? string.Join(' ', rest[1..]) : string.Empty,
                last,
                suffix);
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1) return new NameParts(prefix, parts[0], string.Empty, string.Empty, suffix);

        return order switch
        {
            // "Vries Anne Marie": the family name leads, everything after it is given names.
            FullNameOrder.LastFirst => new NameParts(
                prefix,
                parts[1],
                parts.Length > 2 ? string.Join(' ', parts[2..]) : string.Empty,
                parts[0],
                suffix),

            // "Maria Garcia Lopez": one given name, and the rest is a two-part family name —
            // the form Spanish and Portuguese names take, which is what the option exists for.
            FullNameOrder.FirstLastLast => new NameParts(
                prefix,
                parts[0],
                string.Empty,
                string.Join(' ', parts[1..]),
                suffix),

            // "Anne Marie Vries": given name first, family name last, middles between.
            _ => new NameParts(
                prefix,
                parts[0],
                parts.Length > 2 ? string.Join(' ', parts[1..^1]) : string.Empty,
                parts[^1],
                suffix),
        };
    }
}
