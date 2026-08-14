using System.Globalization;
using System.Text;

namespace Mailbox.Core.Accounts;

/// <summary>
/// How an account presents itself where there is no photograph: a single letter on a coloured
/// disc, as the title-bar button and the account panel both show.
/// </summary>
public static class AccountIdentity
{
    /// <summary>Shown when an address is missing or begins with nothing usable.</summary>
    public const string Unknown = "?";

    /// <summary>
    /// The letter for an address. Takes the first character that can be written as one — a
    /// leading quote, angle bracket or space is punctuation around the address rather than
    /// part of it, and drawing it would look like a mistake.
    /// </summary>
    public static string Initial(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return Unknown;

        foreach (var rune in address.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune)) continue;

            return Rune.ToUpper(rune, CultureInfo.InvariantCulture).ToString();
        }

        return Unknown;
    }
}
