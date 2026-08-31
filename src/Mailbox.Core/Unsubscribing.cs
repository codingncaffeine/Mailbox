namespace Mailbox.Core;

/// <summary>
/// What a mailing-list message offers as its way out, read from <c>List-Unsubscribe</c>
/// (RFC 2369) and <c>List-Unsubscribe-Post</c> (RFC 8058).
/// </summary>
/// <param name="Mailto">The unsubscribe addresses, ready to open as a pre-addressed message.</param>
/// <param name="Web">The unsubscribe pages, for a browser when nothing better is offered.</param>
/// <param name="OneClick">
/// The HTTPS endpoint that takes the one-click POST, when the list declares RFC 8058 support.
/// HTTPS only, as the RFC requires — a plain-http entry can still be a <see cref="Web"/> page,
/// but an unsubscribe request does not go over a line anyone can rewrite.
/// </param>
public sealed record UnsubscribeOffer(
    IReadOnlyList<Uri> Mailto,
    IReadOnlyList<Uri> Web,
    Uri? OneClick)
{
    /// <summary>The offer the two headers make, or null when the message makes none.</summary>
    /// <remarks>
    /// The header is a comma-separated list of angle-bracketed URIs. Anything outside brackets
    /// is a comment and ignored; anything inside that does not parse as an absolute mailto or
    /// http(s) URI is somebody's typo and skipped rather than fatal — one bad entry should not
    /// cost the reader the good one beside it.
    /// </remarks>
    public static UnsubscribeOffer? Parse(string? listUnsubscribe, string? listUnsubscribePost)
    {
        if (string.IsNullOrWhiteSpace(listUnsubscribe)) return null;

        var mailto = new List<Uri>();
        var web = new List<Uri>();

        var at = 0;
        while ((at = listUnsubscribe.IndexOf('<', at)) >= 0)
        {
            var end = listUnsubscribe.IndexOf('>', at + 1);
            if (end < 0) break;

            var entry = listUnsubscribe[(at + 1)..end].Trim();
            at = end + 1;

            if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri)) continue;

            if (uri.Scheme == Uri.UriSchemeMailto) mailto.Add(uri);
            else if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) web.Add(uri);
        }

        if (mailto.Count == 0 && web.Count == 0) return null;

        // RFC 8058 §3.1: the post header's value is exactly this token, and the target is the
        // first HTTPS entry. A list that says one-click and offers only http gets no POST.
        var oneClick = listUnsubscribePost?.Contains("List-Unsubscribe=One-Click", StringComparison.OrdinalIgnoreCase) == true
            ? web.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttps)
            : null;

        return new UnsubscribeOffer(mailto, web, oneClick);
    }
}
