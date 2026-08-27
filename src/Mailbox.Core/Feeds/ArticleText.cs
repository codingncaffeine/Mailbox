using System.Text;

namespace Mailbox.Core.Feeds;

/// <summary>What reading a publisher's own page got us.</summary>
/// <param name="Html">The article as markup, or empty when nothing article-shaped was found.</param>
public sealed record ArticleBody(string Html)
{
    /// <summary>The same thing as plain text, for the preview line and the search index.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>How many characters of article this is, which is what "worth using" is judged on.</summary>
    public int Length => Text.Length;

    public bool Found => Html.Length > 0;

    public static readonly ArticleBody Nothing = new(string.Empty);
}

/// <summary>
/// Pulls the article out of a publisher's page.
/// </summary>
/// <remarks>
/// The single commonest complaint about reading by RSS, and the reason a great many people gave
/// it up: a feed that publishes one paragraph and a "read more" link. TechCrunch's feed carries
/// a hundred and thirty characters an entry — not a summary, a teaser — and a reader who
/// subscribes to it in any reader without this gets a list of headlines they cannot read.
/// <para>
/// <b>How it finds the article, and why this way.</b> The signal is the paragraphs. Every
/// approach that starts from the markup's structure — a class called "content", an
/// <c>&lt;article&gt;</c> element — is guessing at a convention the publisher had no reason to
/// follow, and modern pages are a thousand nested divs with machine-generated class names. But
/// the article is always the one place on the page with a long run of long paragraphs that are
/// not mostly links. Navigation is short and link-dense; a sidebar is short and link-dense;
/// comments are short. So: find the paragraphs worth reading, find the longest run of them that
/// sit near each other, and take everything between the first and the last.
/// <para>
/// It needs no tree, which matters: this runs over pages nobody validated, and a tolerant scan
/// that gets a wrong answer costs a missed article rather than an exception.
/// </para>
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It does not try to be right about paywalls, infinite
/// scroll, or a page whose text arrives by script — there is nothing in the markup to find, and
/// the caller keeps whatever the feed itself said rather than replacing it with a worse answer.
/// </para>
/// </remarks>
public static class ArticleText
{
    /// <summary>A paragraph shorter than this is a caption, a byline or a button.</summary>
    private const int WorthReading = 40;

    /// <summary>Above this share of a paragraph being link text, it is navigation.</summary>
    private const double TooManyLinks = 0.4;

    /// <summary>
    /// How much markup may sit between two paragraphs of the same article.
    /// </summary>
    /// <remarks>
    /// Generous on purpose: a modern page puts an advertisement, a related-articles rail and
    /// three tracking divs between two paragraphs, and all of that is markup rather than text.
    /// Too small a gap splits one article into several runs and takes only part of it.
    /// </remarks>
    private const int LongestGap = 6000;

    /// <summary>Longer than any article; a page bigger than this is not one.</summary>
    private const int LongestPage = 4 * 1024 * 1024;

    /// <summary>Everything inside one of these is not the article, whatever is in it.</summary>
    private static readonly string[] Furniture =
    [
        "script", "style", "noscript", "svg", "iframe", "form", "template", "nav", "aside",
        "header", "footer", "button", "select", "textarea", "video", "audio", "canvas",
    ];

    /// <summary>The elements the article is made of, in the order a reader meets them.</summary>
    private static readonly string[] Blocks =
    [
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "li", "figcaption",
    ];

    /// <summary>Tags kept inside a paragraph. Everything else becomes its own text.</summary>
    private static readonly HashSet<string> Inline = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "b", "strong", "i", "em", "u", "code", "kbd", "samp", "var", "sub", "sup", "mark",
        "small", "cite", "q", "abbr", "time", "br", "img",
    };

    /// <summary>
    /// Reads the article out of a page, or gives back nothing when there is no article in it.
    /// </summary>
    /// <param name="html">The page as fetched.</param>
    /// <param name="pageUrl">Where it came from, so relative links and pictures resolve.</param>
    public static ArticleBody Extract(string html, string pageUrl = "")
    {
        if (string.IsNullOrEmpty(html) || html.Length > LongestPage) return ArticleBody.Nothing;

        var page = Strip(html);
        var blocks = Read(page);
        if (blocks.Count == 0) return ArticleBody.Nothing;

        var span = Densest(blocks);
        if (span is not var (from, to)) return ArticleBody.Nothing;

        to = Trailing(blocks, to);

        var kept = blocks.Where(b => b.Start >= from && b.Start <= to).ToList();
        if (kept.Count == 0) return ArticleBody.Nothing;

        var markup = new StringBuilder();
        var text = new StringBuilder();

        foreach (var block in kept)
        {
            var inner = Clean(block.Inner, pageUrl);
            if (inner.Length == 0) continue;

            var tag = block.Tag.Equals("li", StringComparison.OrdinalIgnoreCase) ? "li" : block.Tag;

            markup.Append('<').Append(tag).Append('>').Append(inner).Append("</").Append(tag).Append(">\n");

            if (text.Length > 0) text.Append("\n\n");
            text.Append(block.Text);
        }

        return markup.Length == 0
            ? ArticleBody.Nothing
            : new ArticleBody(markup.ToString()) { Text = text.ToString() };
    }

    // ---- Finding it ------------------------------------------------------------------------------

    private readonly record struct Block(string Tag, int Start, int End, string Inner, string Text, int LinkChars)
    {
        /// <summary>A paragraph long enough, and not mostly link text, to be part of an article.</summary>
        public bool Reads => Tag.Equals("p", StringComparison.OrdinalIgnoreCase)
                             && Text.Length >= WorthReading
                             && (double)LinkChars / Math.Max(1, Text.Length) < TooManyLinks;
    }

    /// <summary>
    /// Blanks out everything that is not the article, keeping every other offset where it was.
    /// </summary>
    /// <remarks>
    /// Overwritten with spaces rather than removed, so every index taken afterwards still points
    /// at the same character of the original — which is what lets the gap between two paragraphs
    /// be measured in the page's own coordinates.
    /// </remarks>
    private static string Strip(string html)
    {
        var page = html.ToCharArray();

        // Comments first: a commented-out block can hold an unbalanced tag that would otherwise
        // send the element scan looking for a close that never comes.
        Blank(page, "<!--", "-->");

        foreach (var tag in Furniture)
        {
            var at = 0;
            while (at < page.Length)
            {
                var open = IndexOfTag(page, tag, at);
                if (open < 0) break;

                var close = IndexOfClose(page, tag, open);
                if (close < 0)
                {
                    // No end tag. Blank the opening tag alone rather than the rest of the page:
                    // one stray <nav> must not swallow the article under it.
                    var end = new ReadOnlySpan<char>(page)[open..].IndexOf('>');
                    Fill(page, open, end < 0 ? page.Length : open + end + 1);
                    at = open + 1;
                    continue;
                }

                Fill(page, open, close);
                at = close;
            }
        }

        return new string(page);
    }

    private static void Blank(char[] page, string from, string to)
    {
        var at = 0;
        while (at < page.Length)
        {
            var open = new ReadOnlySpan<char>(page)[at..].IndexOf(from, StringComparison.Ordinal);
            if (open < 0) break;
            open += at;

            var after = open + from.Length;
            var close = after >= page.Length
                ? -1
                : new ReadOnlySpan<char>(page)[after..].IndexOf(to, StringComparison.Ordinal);

            var end = close < 0 ? page.Length : after + close + to.Length;

            Fill(page, open, end);
            at = end;
        }
    }

    /// <summary>
    /// Overwrites a stretch with spaces.
    /// </summary>
    /// <remarks>
    /// Spaces rather than a removal, so every index taken afterwards still points at the same
    /// character of the page — which is what lets the gap between two paragraphs be measured in
    /// the page's own coordinates. Newlines are left alone so the scan's line structure survives.
    /// </remarks>
    private static void Fill(char[] page, int from, int to)
    {
        for (var at = Math.Max(0, from); at < to && at < page.Length; at++)
        {
            if (page[at] is not ('\n' or '\r')) page[at] = ' ';
        }
    }

    /// <summary>Every block element on the page, in the order it appears.</summary>
    private static List<Block> Read(string page)
    {
        var found = new List<Block>();

        foreach (var tag in Blocks)
        {
            var at = 0;
            while (at < page.Length)
            {
                var open = IndexOfTag(page, tag, at);
                if (open < 0) break;

                var bodyStart = page.IndexOf('>', open);
                if (bodyStart < 0) break;

                var close = IndexOfClose(page, tag, open);
                if (close < 0)
                {
                    at = bodyStart + 1;
                    continue;
                }

                var innerEnd = page.LastIndexOf('<', close - 1, close - 1 - bodyStart);
                if (innerEnd <= bodyStart)
                {
                    at = close;
                    continue;
                }

                var inner = page[(bodyStart + 1)..innerEnd];
                found.Add(new Block(tag, open, close, inner, PlainText(inner), LinkChars(inner)));
                at = close;
            }
        }

        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>
    /// The stretch of the page holding the longest run of paragraphs worth reading.
    /// </summary>
    /// <remarks>
    /// Measured by how much text is in the run rather than by how many paragraphs, so an article
    /// of four long paragraphs beats a sidebar of nine short ones.
    /// </remarks>
    private static (int From, int To)? Densest(List<Block> blocks)
    {
        var reading = blocks.Where(b => b.Reads).ToList();
        if (reading.Count == 0) return null;

        var bestFrom = reading[0].Start;
        var bestTo = reading[0].End;
        var best = reading[0].Text.Length;

        var from = reading[0].Start;
        var to = reading[0].End;
        var run = reading[0].Text.Length;

        for (var at = 1; at < reading.Count; at++)
        {
            var here = reading[at];

            if (here.Start - to <= LongestGap)
            {
                to = here.End;
                run += here.Text.Length;
            }
            else
            {
                from = here.Start;
                to = here.End;
                run = here.Text.Length;
            }

            if (run <= best) continue;

            best = run;
            bestFrom = from;
            bestTo = to;
        }

        return (bestFrom, bestTo);
    }

    /// <summary>
    /// Extends the run over the headings, lists and quotes that finish the article.
    /// </summary>
    /// <remarks>
    /// The run is found from paragraphs, and an article that ends with a subheading and a list —
    /// which a great many do — would otherwise stop at its last paragraph and lose them.
    /// <para>
    /// Only over things that are not paragraphs, and it stops at the first paragraph that is not
    /// part of the run. That is what keeps the comments out: a comment is a paragraph, and a
    /// section of thirty short ones sits directly under the article.
    /// </para>
    /// </remarks>
    private static int Trailing(List<Block> blocks, int to)
    {
        foreach (var block in blocks.Where(b => b.Start > to).OrderBy(b => b.Start))
        {
            if (block.Start - to > LongestGap) break;
            if (block.Tag.Equals("p", StringComparison.OrdinalIgnoreCase)) break;

            to = block.End;
        }

        return to;
    }

    // ---- Cleaning it -----------------------------------------------------------------------------

    /// <summary>
    /// One block's markup with everything but a few inline tags taken out.
    /// </summary>
    /// <remarks>
    /// An allowlist, and the same reasoning the message sanitizer gives: what is not named here
    /// is dropped, so an element nobody thought of is dropped rather than passed on. This is not
    /// the security boundary — what comes out of here is a message body and goes through that
    /// sanitizer like every other — but there is no reason to carry a page's tracking pixels and
    /// its layout divs into the store either.
    /// </remarks>
    private static string Clean(string inner, string pageUrl)
    {
        var kept = new StringBuilder(inner.Length);
        var at = 0;

        while (at < inner.Length)
        {
            var open = inner.IndexOf('<', at);
            if (open < 0)
            {
                kept.Append(inner[at..]);
                break;
            }

            kept.Append(inner[at..open]);

            var close = inner.IndexOf('>', open);
            if (close < 0) break;

            var tag = inner[(open + 1)..close];
            var name = NameOf(tag);

            if (Inline.Contains(name)) kept.Append(Rewrite(tag, name, pageUrl));

            at = close + 1;
        }

        // Kept when there are words in it — or when there is a picture, which is a paragraph with
        // nothing to say and everything to show. Judging on text alone dropped every photograph
        // in an article.
        return kept.ToString().Trim() is { Length: > 0 } text
               && (PlainText(text).Length > 0 || text.Contains("<img", StringComparison.OrdinalIgnoreCase))
            ? text
            : string.Empty;
    }

    /// <summary>An inline tag with only the attributes worth keeping, and its addresses absolute.</summary>
    private static string Rewrite(string tag, string name, string pageUrl)
    {
        var closing = tag.StartsWith('/');
        if (closing) return $"</{name}>";

        var selfClosing = tag.EndsWith('/');

        var attributes = FeedLinks.Attributes(tag);
        var written = new StringBuilder("<").Append(name);

        if (name.Equals("a", StringComparison.OrdinalIgnoreCase)
            && Absolute(attributes.GetValueOrDefault("href") ?? string.Empty, pageUrl) is { Length: > 0 } href)
        {
            written.Append(" href=\"").Append(System.Net.WebUtility.HtmlEncode(href)).Append('"');
        }

        if (name.Equals("img", StringComparison.OrdinalIgnoreCase))
        {
            // A lazy-loaded picture keeps its real address in data-src and a placeholder in src,
            // which is what the whole web does now and what a naive read gets wrong.
            var source = new[] { "src", "data-src", "data-original", "data-lazy-src" }
                .Select(k => attributes.GetValueOrDefault(k) ?? string.Empty)
                .FirstOrDefault(v => v.Length > 0 && !v.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;

            if (Absolute(source, pageUrl) is not { Length: > 0 } src) return string.Empty;

            written.Append(" src=\"").Append(System.Net.WebUtility.HtmlEncode(src)).Append('"');

            if (attributes.GetValueOrDefault("alt") is { Length: > 0 } alt)
            {
                written.Append(" alt=\"").Append(System.Net.WebUtility.HtmlEncode(alt)).Append('"');
            }
        }

        return written.Append(selfClosing ? " />" : ">").ToString();
    }

    private static string Absolute(string candidate, string pageUrl)
    {
        if (candidate.Trim() is not { Length: > 0 } trimmed) return string.Empty;

        var resolved = Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
                       && Uri.TryCreate(page, trimmed, out var absolute)
            ? absolute
            : Uri.TryCreate(trimmed, UriKind.Absolute, out var alone) ? alone : null;

        return resolved is { Scheme: "http" or "https" } ? resolved.AbsoluteUri : string.Empty;
    }

    // ---- The small scanner underneath ---------------------------------------------------------------

    /// <summary>The tag's name, lower-cased, without its slash or its attributes.</summary>
    private static string NameOf(string tag)
    {
        var at = tag.StartsWith('/') ? 1 : 0;
        var end = at;

        while (end < tag.Length && (char.IsAsciiLetterOrDigit(tag[end]) || tag[end] is '-')) end++;

        return tag[at..end].ToLowerInvariant();
    }

    /// <summary>Where the next opening tag of this name starts, or -1.</summary>
    private static int IndexOfTag(ReadOnlySpan<char> page, string name, int from)
    {
        var at = from;

        while (at < page.Length)
        {
            var open = page[at..].IndexOf('<');
            if (open < 0) return -1;
            open += at;
            if (open + 1 >= page.Length) return -1;

            if (Is(page, open + 1, name)) return open;
            at = open + 1;
        }

        return -1;
    }

    /// <summary>
    /// Where this element's own end tag finishes, allowing for the same tag nested inside it.
    /// </summary>
    private static int IndexOfClose(ReadOnlySpan<char> page, string name, int open)
    {
        var depth = 0;
        var at = open;

        while (at < page.Length)
        {
            var found = page[at..].IndexOf('<');
            if (found < 0) return -1;

            var next = at + found;
            if (next + 1 >= page.Length) return -1;

            if (page[next + 1] == '/' && Is(page, next + 2, name))
            {
                depth--;
                var shut = page[next..].IndexOf('>');
                if (shut < 0) return -1;

                var end = next + shut;
                if (depth <= 0) return end + 1;
                at = end + 1;
                continue;
            }

            if (Is(page, next + 1, name))
            {
                var shut = page[next..].IndexOf('>');
                if (shut < 0) return -1;

                var end = next + shut;

                // <br/> and friends never nest, and neither does a tag written self-closing.
                if (end > next && page[end - 1] != '/') depth++;
                at = end + 1;
                continue;
            }

            at = next + 1;
        }

        return -1;
    }

    private static bool Is(ReadOnlySpan<char> page, int at, string name)
        => at + name.Length <= page.Length
           && page.Slice(at, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)
           && (at + name.Length == page.Length || !char.IsAsciiLetterOrDigit(page[at + name.Length]));

    /// <summary>How many characters of a block are inside a link, which is what marks navigation.</summary>
    private static int LinkChars(string inner)
    {
        var total = 0;
        var at = 0;

        while (at < inner.Length)
        {
            var open = IndexOfTag(inner, "a", at);
            if (open < 0) break;

            var close = IndexOfClose(inner, "a", open);
            if (close < 0) break;

            total += PlainText(inner[open..close]).Length;
            at = close;
        }

        return total;
    }

    /// <summary>Markup as the words in it. The parser's own, so one rule for both.</summary>
    private static string PlainText(string markup) => FeedParser.PlainText(markup).Trim();
}
