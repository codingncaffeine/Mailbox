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
    public readonly record struct NameParts(string First, string Middle, string Last);

    public static NameParts Parse(string typed, FullNameOrder order)
    {
        var text = (typed ?? string.Empty).Trim();
        if (text.Length == 0) return new NameParts(string.Empty, string.Empty, string.Empty);

        // The comma form names its own order.
        var comma = text.IndexOf(',');
        if (comma >= 0)
        {
            var last = text[..comma].Trim();
            var rest = text[(comma + 1)..].Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return new NameParts(
                rest.Length > 0 ? rest[0] : string.Empty,
                rest.Length > 1 ? string.Join(' ', rest[1..]) : string.Empty,
                last);
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1) return new NameParts(parts[0], string.Empty, string.Empty);

        return order switch
        {
            // "Vries Anne Marie": the family name leads, everything after it is given names.
            FullNameOrder.LastFirst => new NameParts(
                parts[1],
                parts.Length > 2 ? string.Join(' ', parts[2..]) : string.Empty,
                parts[0]),

            // "Maria Garcia Lopez": one given name, and the rest is a two-part family name —
            // the form Spanish and Portuguese names take, which is what the option exists for.
            FullNameOrder.FirstLastLast => new NameParts(
                parts[0],
                string.Empty,
                string.Join(' ', parts[1..])),

            // "Anne Marie Vries": given name first, family name last, middles between.
            _ => new NameParts(
                parts[0],
                parts.Length > 2 ? string.Join(' ', parts[1..^1]) : string.Empty,
                parts[^1]),
        };
    }
}
