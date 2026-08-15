namespace Mailbox.Core.Compose;

/// <summary>
/// A parsed <c>mailto:</c> link — what the desktop hands Mailbox when it is the system mail
/// client and something asks to write an email.
/// </summary>
/// <param name="To">The recipients, from the path and any <c>to=</c> parameters.</param>
/// <param name="Cc">Carbon copies.</param>
/// <param name="Bcc">Blind carbon copies.</param>
/// <param name="Subject">The subject, or empty.</param>
/// <param name="Body">The body, or empty.</param>
/// <remarks>
/// A link arrives from outside the application — a link on a web page, another program's command
/// line — and is not to be trusted, so <see cref="Parse"/> handles it carefully. Two rules from
/// §19 are enforced there rather than remembered later:
/// <list type="bullet">
/// <item><c>attach=</c> is dropped entirely. A link that could make the mail client attach an
/// arbitrary local file — <c>/etc/passwd</c>, an ssh key — is an exfiltration primitive, and no
/// header a stranger writes gets to name a file on this machine.</item>
/// <item>headers other than to/cc/bcc/subject/body are ignored, so a link cannot inject a
/// <c>Bcc</c> the writer cannot see by spelling it <c>X-Bcc</c>, or set arbitrary headers.</item>
/// </list>
/// </remarks>
public sealed record MailtoLink(
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string Body)
{
    /// <summary>The headers a link is allowed to set. Everything else, <c>attach</c> included, is ignored.</summary>
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "to", "cc", "bcc", "subject", "body" };

    /// <summary>
    /// Parses a link, or returns null if it is not a <c>mailto:</c> at all. A well-formed link
    /// with nothing in it — bare <c>mailto:</c> — parses to an empty draft, which is a valid
    /// "compose a new message" request.
    /// </summary>
    public static MailtoLink? Parse(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;

        var text = link.Trim();
        const string scheme = "mailto:";
        if (!text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = text[scheme.Length..];

        var queryStart = rest.IndexOf('?');
        var pathPart = queryStart < 0 ? rest : rest[..queryStart];
        var queryPart = queryStart < 0 ? string.Empty : rest[(queryStart + 1)..];

        var to = new List<string>();
        var cc = new List<string>();
        var bcc = new List<string>();
        var subject = string.Empty;
        var body = string.Empty;

        // The path is a comma-separated recipient list, percent-decoded, possibly empty.
        AddAddresses(to, Decode(pathPart));

        foreach (var pair in queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);

            if (!Allowed.Contains(key)) continue;

            switch (key.ToLowerInvariant())
            {
                case "to": AddAddresses(to, value); break;
                case "cc": AddAddresses(cc, value); break;
                case "bcc": AddAddresses(bcc, value); break;
                case "subject": subject = OneLine(value); break;
                case "body": body = value; break;
            }
        }

        return new MailtoLink(to, cc, bcc, subject, body);
    }

    /// <summary>Splits a comma-separated recipient value and adds each non-empty one, de-duplicated.</summary>
    private static void AddAddresses(List<string> into, string value)
    {
        foreach (var address in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!into.Contains(address, StringComparer.OrdinalIgnoreCase)) into.Add(address);
        }
    }

    /// <summary>
    /// Percent-decoding, with <c>+</c> left as a plus. RFC 6068 does not use form encoding, so a
    /// <c>+</c> in a mailto is a literal plus — a tag address like <c>you+list@example.com</c> —
    /// not a space, and decoding it to a space would corrupt the address.
    /// </summary>
    private static string Decode(string value)
    {
        if (!value.Contains('%')) return value;

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (Exception)
        {
            // A malformed escape from a hostile link: hand back what was written rather than throw.
            return value;
        }
    }

    /// <summary>A header value is one line: a newline in a subject would be header injection.</summary>
    private static string OneLine(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
