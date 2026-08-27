using System.Text;

namespace Mailbox.Core.Feeds;

/// <summary>What a message says about being a newsletter.</summary>
/// <param name="IsNewsletter">True when it carries the marks of bulk mail somebody signed up for.</param>
/// <param name="Identity">
/// What tells this newsletter from every other one: its List-ID where it has one, else the
/// sender's address. Stable across issues, which is what makes it a subscription rather than a
/// pile of messages.
/// </param>
/// <param name="Name">What to call it: the sender's display name, or the list's.</param>
public sealed record NewsletterMarks(bool IsNewsletter, string Identity = "", string Name = "");

/// <summary>
/// Newsletters, read as feeds.
/// </summary>
/// <remarks>
/// <b>This is the one thing a hosted reader cannot do better than we can.</b> Feedly's version of
/// it hands you an <c>@feedly.com</c> address, you re-subscribe every newsletter to that address,
/// and their servers turn the mail into articles. They have to: a website has no mailbox. We are
/// a mail client — the mailbox is already here, the newsletters are already arriving in it, and
/// nothing has to be re-subscribed, forwarded, or routed through a third party who then holds
/// your mail.
/// <para>
/// A newsletter is therefore a subscription like any other, with its transport being the inbox
/// rather than HTTP: it gets a folder under the feeds root, an unread count, a place in the
/// article list, and everything else the module does. What is different is only where the issues
/// come from.
/// </para>
/// <para>
/// <b>Detection is a suggestion, never an action.</b> These marks are what bulk mail carries, and
/// plenty of things carry them that nobody thinks of as a newsletter — a receipt, a password
/// reset, a calendar invitation from a service. So nothing is moved because it looks like a
/// newsletter; the reader is shown what was found and picks. What is routed is routed by
/// identity, which is a decision they made once.
/// </para>
/// </remarks>
public static class Newsletters
{
    /// <summary>The scheme a newsletter subscription's address carries instead of http.</summary>
    /// <remarks>
    /// So a newsletter is a <see cref="FeedSubscription"/> and reuses the pane, the counts, the
    /// layouts and the mute filters — while the poll, which only ever asks for http addresses,
    /// leaves it alone without needing to know it exists.
    /// </remarks>
    public const string Scheme = "newsletter:";

    /// <summary>The address a newsletter subscription is filed under.</summary>
    public static string AddressFor(string identity) => Scheme + identity.Trim().ToLowerInvariant();

    /// <summary>True for a subscription whose issues arrive as mail rather than over HTTP.</summary>
    public static bool IsNewsletter(this FeedSubscription feed)
        => feed.Url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a message's headers say about being a newsletter.
    /// </summary>
    /// <param name="raw">The message as it was received. Only its headers are read.</param>
    /// <remarks>
    /// Off the raw bytes rather than a parsed message, and only as far as the blank line, because
    /// this runs over every message in a folder when a reader asks what their newsletters are.
    /// Parsing a thousand messages to read one header of each is the difference between a
    /// question that answers itself and one that spins.
    /// </remarks>
    public static NewsletterMarks Marks(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var headers = Headers(raw);
        if (headers.Count == 0) return new NewsletterMarks(false);

        // RFC 2369's List-Unsubscribe is the one mark that means "somebody subscribed to this",
        // and it is the mark every mailing list and every marketing platform sets. A List-ID
        // (RFC 2919) says the same and additionally names the list.
        var unsubscribe = Value(headers, "list-unsubscribe");
        var listId = Value(headers, "list-id");
        var post = Value(headers, "list-post");
        var precedence = Value(headers, "precedence");

        var bulk = unsubscribe.Length > 0
                   || listId.Length > 0
                   || post.Length > 0
                   || precedence.Equals("bulk", StringComparison.OrdinalIgnoreCase)
                   || precedence.Equals("list", StringComparison.OrdinalIgnoreCase);

        if (!bulk) return new NewsletterMarks(false);

        var (name, address) = Sender(Value(headers, "from"));

        // The List-ID is the stable one: a publication that changes which server sends it keeps
        // its list, and routing on the sending address alone would lose the subscription the day
        // they move.
        var identity = ListIdentity(listId) is { Length: > 0 } list ? list : address;
        if (identity.Length == 0) return new NewsletterMarks(false);

        return new NewsletterMarks(true, identity, name.Length > 0 ? name : identity);
    }

    /// <summary>The bare list identifier out of a List-ID header, without its description or brackets.</summary>
    private static string ListIdentity(string listId)
    {
        if (listId.Length == 0) return string.Empty;

        var open = listId.LastIndexOf('<');
        var close = listId.LastIndexOf('>');

        return open >= 0 && close > open ? listId[(open + 1)..close].Trim() : listId.Trim();
    }

    /// <summary>
    /// The display name and address out of a From header, without a MIME parse.
    /// </summary>
    /// <remarks>
    /// An encoded-word display name is left as it is rather than decoded here: what it is used
    /// for is a folder name the reader can rename, and a name that arrives looking like
    /// <c>=?utf-8?…</c> is a sign to rename it rather than a fault. The address, which is the
    /// part that has to be right, is ASCII by definition.
    /// </remarks>
    private static (string Name, string Address) Sender(string from)
    {
        if (from.Length == 0) return (string.Empty, string.Empty);

        var open = from.LastIndexOf('<');
        var close = from.LastIndexOf('>');

        if (open >= 0 && close > open)
        {
            var name = from[..open].Trim().Trim('"').Trim();
            return (name, from[(open + 1)..close].Trim().ToLowerInvariant());
        }

        var bare = from.Trim();
        return (string.Empty, bare.Contains('@') ? bare.ToLowerInvariant() : string.Empty);
    }

    /// <summary>
    /// The header block, unfolded, as name/value pairs. Stops at the blank line.
    /// </summary>
    private static List<(string Name, string Value)> Headers(byte[] raw)
    {
        var found = new List<(string, string)>();
        var text = Ascii(raw);

        string? name = null;
        var value = new StringBuilder();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) break;

            // A continuation line belongs to the header above it.
            if (trimmed[0] is ' ' or '\t')
            {
                if (name is not null) value.Append(' ').Append(trimmed.Trim());
                continue;
            }

            if (name is not null) found.Add((name, value.ToString().Trim()));

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                name = null;
                continue;
            }

            name = trimmed[..colon].Trim().ToLowerInvariant();
            value.Clear();
            value.Append(trimmed[(colon + 1)..].Trim());
        }

        if (name is not null) found.Add((name, value.ToString().Trim()));
        return found;
    }

    /// <summary>
    /// The header block as text, read as ASCII.
    /// </summary>
    /// <remarks>
    /// Safe whatever the body turns out to be in: a header's own syntax is ASCII by the
    /// specification, and anything outside it is already encoded as an encoded-word. Reading
    /// 16KB rather than the whole message keeps this cheap on a folder of large messages.
    /// </remarks>
    private static string Ascii(byte[] raw)
    {
        var end = Math.Min(raw.Length, 16 * 1024);
        return Encoding.ASCII.GetString(raw, 0, end);
    }

    private static string Value(List<(string Name, string Value)> headers, string name)
        => headers.FirstOrDefault(h => h.Name == name).Value ?? string.Empty;
}
