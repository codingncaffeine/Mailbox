using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace Mailbox.Rendering;

/// <summary>
/// Turns a received message into the document the reading pane shows.
/// </summary>
/// <remarks>
/// One pass does the lot: choose the body, sanitize it, resolve what the message carries, block
/// what it does not, and count what was blocked. Splitting detection from blocking would be two
/// walks that can disagree, and the one that disagreed quietly would be the blocker.
/// <para>
/// Nothing here touches the network, by construction rather than by policy — the project has no
/// HTTP dependency. See §11.
/// </para>
/// </remarks>
public static partial class MessageRenderer
{
    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl { get; }

    /// <summary>Renders a whole message, picking its best body part.</summary>
    public static RenderedMessage Render(MimeMessage message, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= new RenderOptions();

        var resources = ResourceMap.From(message);

        if (message.HtmlBody is { Length: > 0 } html)
        {
            var sanitizer = new HtmlSanitizer(resources, options);
            var body = sanitizer.Sanitize(html);
            return new RenderedMessage(Document(body, options), sanitizer.Blocked, WasHtml: true);
        }

        var text = message.TextBody ?? string.Empty;
        return new RenderedMessage(Document(FromPlainText(text), options), [], WasHtml: false);
    }

    /// <summary>
    /// Sanitizes a fragment on its own, for callers that already have the markup.
    /// </summary>
    /// <remarks>
    /// Used by the tests, and by anything that renders a quoted reply before there is an editor
    /// to put it in.
    /// </remarks>
    public static RenderedMessage RenderHtml(
        string html, MimeMessage? carrier = null, RenderOptions? options = null)
    {
        options ??= new RenderOptions();
        var resources = carrier is null ? ResourceMap.From(new MimeMessage()) : ResourceMap.From(carrier);

        var sanitizer = new HtmlSanitizer(resources, options);
        var body = sanitizer.Sanitize(html ?? string.Empty);

        return new RenderedMessage(Document(body, options), sanitizer.Blocked, WasHtml: true);
    }

    /// <summary>
    /// A plain-text body as markup: escaped, kept as written, with its links made clickable.
    /// </summary>
    /// <remarks>
    /// Plain text is laid out by its author — signatures, quoting, tables of figures — so it is
    /// wrapped rather than reflowed. Linkifying is the one liberty taken, because a URL nobody
    /// can click is one that gets copied out by hand.
    /// </remarks>
    private static string FromPlainText(string text)
    {
        var escaped = Escape(text);

        var linked = BareUrl.Replace(escaped, match =>
        {
            var url = match.Value;
            var href = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;

            return $"<a href=\"{href}\" target=\"_blank\" rel=\"noopener noreferrer\">{url}</a>";
        });

        return $"<div class=\"plain\">{linked}</div>";
    }

    /// <summary>The Memo header, as a definition list the print stylesheet lays out.</summary>
    private static string PrintBlock(PrintHeader? header)
    {
        if (header is null) return string.Empty;

        var rows = new StringBuilder("<dl class=\"memo\">");
        rows.Append(CultureInfo.InvariantCulture, $"<dt>From:</dt><dd>{Escape(header.From)}</dd>");
        rows.Append(CultureInfo.InvariantCulture, $"<dt>Sent:</dt><dd>{Escape(header.Sent)}</dd>");
        rows.Append(CultureInfo.InvariantCulture, $"<dt>To:</dt><dd>{Escape(header.To)}</dd>");

        if (header.Cc is { Length: > 0 } cc)
        {
            rows.Append(CultureInfo.InvariantCulture, $"<dt>Cc:</dt><dd>{Escape(cc)}</dd>");
        }

        rows.Append(CultureInfo.InvariantCulture, $"<dt>Subject:</dt><dd>{Escape(header.Subject)}</dd>");
        return rows.Append("</dl>").ToString();
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length + 16);

        foreach (var c in text)
        {
            switch (c)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                default: escaped.Append(c); break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>
    /// The document the body is placed in.
    /// </summary>
    /// <remarks>
    /// Its own rules come first so the message's can override them, which is the right way
    /// round: the frame decides what an unstyled message looks like, and a styled one is
    /// allowed to look like itself. The values are the theme's, passed in.
    /// <para>
    /// The policy is belt and braces. Nothing that could act on it should have survived
    /// sanitizing — there is no script left, and no remote URL — so it exists to be wrong
    /// twice before anything leaks: <c>default-src 'none'</c> denies every fetch, images are
    /// allowed only from the <c>data:</c> URIs we produced, and a form has nowhere to post to.
    /// </para>
    /// </remarks>
    private static string Document(string body, RenderOptions options)
    {
        if (options.Fragment) return body;

        var style = options.Style;
        var size = style.FontSize.ToString("0.##", CultureInfo.InvariantCulture);

        // Double-dollar so a CSS brace is a brace and the interpolations are the doubled ones.
        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8">
            <meta name="referrer" content="no-referrer">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'">
            <style>
            html,body{margin:0;padding:0;background:{{style.Background}};color:{{style.Foreground}};
            font-family:{{style.FontFamily}};font-size:{{size}}px;line-height:1.45;
            word-wrap:break-word;overflow-wrap:break-word;}
            body{padding:16px 20px;}
            a{color:{{style.Link}};}
            img{max-width:100%;height:auto;}
            table{max-width:100%;}
            blockquote{margin:0 0 0 8px;padding-left:10px;border-left:2px solid {{style.Quote}};
            color:{{style.Quote}};}
            .plain{white-space:pre-wrap;font-family:{{style.FontFamily}};}

            /* The reference's Memo style: who sent it, when, and to whom, above the message.
               Only on paper — on screen the pane's own header says all of this already. */
            .memo{display:none;}
            @media print{
              html,body{background:#FFFFFF;color:#000000;}
              a{color:#000000;}
              .memo{display:block;border-bottom:1px solid #000000;margin:0 0 12px;padding:0 0 8px;}
              .memo dt{float:left;width:70px;clear:left;font-weight:bold;}
              .memo dd{margin:0 0 2px 70px;}
            }
            </style></head><body>{{PrintBlock(options.PrintHeader)}}{{body}}</body></html>
            """;
    }
}
