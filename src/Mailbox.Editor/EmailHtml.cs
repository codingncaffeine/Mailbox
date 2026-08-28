using System.Globalization;
using System.Text;
using Avalonia.Media;
using AvaloniaRichEditor.Documents;
using Mailbox.Theming.Fonts;

namespace Mailbox.Editor;

/// <summary>What the caller can vary about the HTML that goes on the wire.</summary>
public sealed record EmailHtmlOptions
{
    /// <summary>
    /// Turns an embedded image into the <c>src</c> the markup should carry, given its bytes and
    /// media type.
    /// </summary>
    /// <remarks>
    /// The compose window supplies one that adds the bytes to the message as a related part and
    /// returns <c>cid:…</c> — which is how mail carries an image and the only form every client
    /// renders. Without one the bytes are inlined as a <c>data:</c> URI, which is right for a
    /// preview and wrong for a message: several large clients drop <c>data:</c> images outright.
    /// </remarks>
    public Func<byte[], string, string>? RegisterImage { get; init; }

    /// <summary>
    /// Emit only the body's markup rather than a whole document. For a quoted reply spliced
    /// into another message, where a second <c>&lt;html&gt;</c> would be nonsense.
    /// </summary>
    public bool Fragment { get; init; }

    /// <summary>
    /// Whether a font name is expanded into the stack §6 describes. Off produces the name alone,
    /// which is what a test asserting about one thing wants.
    /// </summary>
    public bool SubstituteFonts { get; init; } = true;

    /// <summary>
    /// The face and size the message is written in, stated once on the body.
    /// </summary>
    /// <remarks>
    /// Stated because a message that says nothing renders at whatever the reading client happens
    /// to default to, which is not the size it was written at. Stated <em>once</em> because
    /// repeating it on every run is what makes composed mail several times the size it needs to
    /// be — a run says only what the writer changed. The reference application's default for new
    /// mail is Calibri 11.
    /// </remarks>
    public string BaseFontFamily { get; init; } = "Calibri";

    public double BaseFontPoints { get; init; } = 11;

    /// <summary>The colour the message is written in — <c>#RRGGBB</c> — or null for the reader's own.</summary>
    public string? BaseColour { get; init; }
}

/// <summary>
/// Turns a composed document into the HTML that leaves the machine.
/// </summary>
/// <remarks>
/// This is the half of §7.3 that stayed in-house, and the reason is that it is the half mail
/// fidelity actually rests on. Email HTML is not web HTML: it is read by clients whose engines
/// were current in 2007, and the rules that follow from that are narrow enough to be worth
/// enforcing in one place.
/// <list type="bullet">
///   <item>Inline styles only. No classes and no stylesheet — several large clients strip
///   <c>&lt;style&gt;</c> from a received message entirely, which silently unstyles the lot.</item>
///   <item>Elements that have rendered everywhere for twenty years, and no others.</item>
///   <item>Sizes in points, because that is what the clients and the composers of mail use.</item>
///   <item>Nothing proprietary. The editor stamps a <c>data-are-fg</c> attribute of its own and
///   a redundant <c>text-align:left</c> on every block; neither goes out.</item>
///   <item><b>§6's wire/render split.</b> A message composed in Calibri says
///   <c>font-family: Calibri, Carlito, sans-serif</c>, so a Windows reader gets Calibri, a Linux
///   reader gets the metric-compatible substitute, and both see the same layout. Naming only
///   what was rendered here would reflow the message for everyone else.</item>
/// </list>
/// <para>
/// The editor ships a serializer of its own and it is not used. Its output is close, which is
/// what made the library worth taking; it is not the same, and the difference is the four points
/// above.
/// </para>
/// </remarks>
public static class EmailHtml
{
    /// <summary>Points per device-independent pixel, which is what the document measures in.</summary>
    private const double PointsPerPixel = 0.75;

    /// <summary>
    /// What a run looks like before anyone has touched it.
    /// </summary>
    /// <remarks>
    /// Read off a fresh run rather than written down, so it follows the editor rather than
    /// needing to be kept in step with it. The distinction it buys is the one the whole of this
    /// class turns on: **emit what the writer chose, not what the editor defaulted to.** A run
    /// nobody has restyled says nothing, and the body's own font applies — which is how a
    /// message stays the size of the message rather than the size of its markup.
    /// </remarks>
    private static readonly double UntouchedFontSize = new Run().FontSize;

    private static readonly string? UntouchedFontFamily = new Run().FontFamily;

    public static string Serialize(FlowDocument document, EmailHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new EmailHtmlOptions();

        var html = new StringBuilder();
        var blocks = document.Blocks.ToList();

        for (var i = 0; i < blocks.Count;)
        {
            // A run of list paragraphs is one list, not one list each. Anything else is itself.
            if (blocks[i] is Paragraph { ListType: not ListKind.None } first)
            {
                var run = blocks.Skip(i)
                    .TakeWhile(b => b is Paragraph { ListType: not ListKind.None } p
                                    && p.ListType == first.ListType)
                    .Cast<Paragraph>()
                    .ToList();

                WriteList(html, run, options);
                i += run.Count;
                continue;
            }

            WriteBlock(html, blocks[i], options);
            i++;
        }

        var body = html.ToString();

        if (options.Fragment) return body;

        // The face and size the message was written in, said once. A message that says nothing
        // renders at whatever the reading client defaults to, which is not what the writer saw.
        var baseStyle = new List<string>();

        if (options.BaseFontFamily is { Length: > 0 } face)
        {
            baseStyle.Add($"font-family:{(options.SubstituteFonts ? Stack(face) : face)}");
        }

        if (options.BaseFontPoints > 0)
        {
            baseStyle.Add($"font-size:{Number(options.BaseFontPoints)}pt");
        }

        if (options.BaseColour is { Length: > 0 } colour)
        {
            baseStyle.Add($"color:{colour}");
        }

        var style = baseStyle.Count > 0 ? $" style=\"{string.Join(';', baseStyle)}\"" : string.Empty;

        return $"""
                <html>
                <head><meta charset="utf-8"></head>
                <body{style}>
                {body}</body>
                </html>
                """;
    }

    // ---- Blocks ---------------------------------------------------------------------------

    private static void WriteBlock(StringBuilder html, Block block, EmailHtmlOptions options)
    {
        switch (block)
        {
            case Paragraph paragraph:
                WriteParagraph(html, paragraph, options);
                break;

            case TableBlock table:
                WriteTable(html, table, options);
                break;

            case ImageBlock image:
                html.Append("<p>");
                WriteImage(html, image.RawBytes, image.MimeType, image.Width, image.Height, options);
                html.Append("</p>\n");
                break;

            case DividerBlock:
                html.Append("<hr />\n");
                break;
        }
    }

    private static void WriteParagraph(StringBuilder html, Paragraph paragraph, EmailHtmlOptions options)
    {
        var (open, close) = ParagraphTags(paragraph);
        var style = ParagraphStyle(paragraph);

        html.Append('<').Append(open);
        if (style.Length > 0) html.Append(" style=\"").Append(style).Append('"');
        html.Append('>');

        WriteInlines(html, paragraph, options);

        // An empty paragraph is a blank line the writer typed, and a client that collapses an
        // empty <p> loses it. A non-breaking space is the ancient, universal fix.
        if (paragraph.Inlines.Count == 0) html.Append("&nbsp;");

        html.Append("</").Append(close).Append(">\n");
    }

    private static (string Open, string Close) ParagraphTags(Paragraph paragraph)
    {
        if (paragraph.IsQuote) return ("blockquote", "blockquote");

        return paragraph.HeadingLevel is >= 1 and <= 6
            ? ($"h{paragraph.HeadingLevel}", $"h{paragraph.HeadingLevel}")
            : ("p", "p");
    }

    /// <summary>
    /// A paragraph's own styling, with the defaults left out.
    /// </summary>
    /// <remarks>
    /// Emitting what is already the default is what makes generated mail unreadable in a diff
    /// and enormous on the wire; the editor's own serializer writes <c>text-align:left</c> on
    /// every block ever written.
    /// </remarks>
    private static string ParagraphStyle(Paragraph paragraph)
    {
        var style = new List<string>();

        if (paragraph.TextAlignment is TextAlignment.Center) style.Add("text-align:center");
        else if (paragraph.TextAlignment is TextAlignment.Right) style.Add("text-align:right");
        else if (paragraph.TextAlignment is TextAlignment.Justify) style.Add("text-align:justify");

        if (paragraph.Indent > 0)
        {
            style.Add($"margin-left:{Number(paragraph.Indent * PointsPerPixel)}pt");
        }

        // Line and Paragraph Spacing. NaN is a paragraph nobody has set one on, and 1 is single,
        // which is every client's default and not worth the bytes. Unitless, because a multiplier
        // is what the writer chose and what every client understands — a length here would fix the
        // leading against one font size and break it for a reader whose client picked another.
        if (!double.IsNaN(paragraph.LineSpacing) && paragraph.LineSpacing > 0
            && Math.Abs(paragraph.LineSpacing - 1) > 0.001)
        {
            style.Add($"line-height:{Number(paragraph.LineSpacing)}");
        }

        if (paragraph.Background is ISolidColorBrush { Color.A: > 0 } background)
        {
            style.Add($"background-color:{Hex(background.Color)}");
        }

        // A quote's rule is drawn by the client from the element in most, and by nobody in
        // some, so it is stated. This is the one place a border is worth the bytes.
        if (paragraph.IsQuote)
        {
            style.Add("border-left:2px solid #CCCCCC");
            style.Add("margin:0 0 0 8px");
            style.Add("padding-left:10px");
        }

        return string.Join(';', style);
    }

    private static void WriteList(StringBuilder html, List<Paragraph> items, EmailHtmlOptions options)
    {
        var ordered = items[0].ListType == ListKind.Ordered;
        var tag = ordered ? "ol" : "ul";
        var level = 0;

        foreach (var item in items)
        {
            var wanted = Math.Max(0, item.ListLevel);

            while (level < wanted + 1)
            {
                html.Append('<').Append(tag);

                // Only on the outermost list: a nested one inherits, and repeating it is the
                // kind of noise that makes composed mail three times the size it needs to be.
                if (level == 0 && Marker(items[0]) is { Length: > 0 } marker)
                {
                    html.Append(" style=\"list-style-type:").Append(marker).Append('"');
                }

                html.Append(">\n");
                level++;
            }

            while (level > wanted + 1)
            {
                html.Append("</").Append(tag).Append(">\n");
                level--;
            }

            html.Append("<li>");
            WriteInlines(html, item, options);
            html.Append("</li>\n");
        }

        while (level-- > 0) html.Append("</").Append(tag).Append(">\n");
    }

    private static string Marker(Paragraph item) => item.ListMarker switch
    {
        ListMarkerStyle.Circle => "circle",
        ListMarkerStyle.Square => "square",
        ListMarkerStyle.Decimal => "decimal",
        ListMarkerStyle.LowerAlpha => "lower-alpha",
        ListMarkerStyle.UpperAlpha => "upper-alpha",
        ListMarkerStyle.LowerRoman => "lower-roman",
        ListMarkerStyle.Disc => "disc",

        // Dash and DecimalParen have no CSS counterpart; the nearest is better than a value a
        // client will not understand and will render as a disc anyway.
        ListMarkerStyle.Dash => "square",
        ListMarkerStyle.DecimalParen => "decimal",

        // Default means "whatever the client does", which is the right answer for most mail.
        _ => string.Empty,
    };

    /// <summary>
    /// A table, written the way mail has always written one.
    /// </summary>
    /// <remarks>
    /// Presentational attributes rather than CSS for the border and the spacing, because that is
    /// what the clients this has to survive actually honour. This is the one place where the
    /// old way is still the right way.
    /// </remarks>
    private static void WriteTable(StringBuilder html, TableBlock table, EmailHtmlOptions options)
    {
        html.Append("<table cellpadding=\"4\" cellspacing=\"0\" border=\"1\" ")
            .Append("style=\"border-collapse:collapse\">\n");

        for (var row = 0; row < table.Rows && row < table.Cells.Count; row++)
        {
            html.Append("<tr>\n");

            for (var column = 0; column < table.Columns && column < table.Cells[row].Count; column++)
            {
                // A cell covered by another's merge is not written at all — the merge above or
                // to the left already accounts for it. The table knows which those are; working
                // it out here a second time would be a second thing to get wrong.
                if (table.IsCovered(row, column)) continue;

                var cell = table.Cells[row][column];
                var (columns, rows) = table.SpanOf(row, column);

                html.Append("<td");

                if (columns > 1) html.Append($" colspan=\"{columns}\"");
                if (rows > 1) html.Append($" rowspan=\"{rows}\"");

                if (cell.Background is ISolidColorBrush { Color.A: > 0 } fill)
                {
                    html.Append(" style=\"background-color:").Append(Hex(fill.Color)).Append('"');
                }

                html.Append('>');

                foreach (var block in cell.Blocks) WriteBlock(html, block, options);

                html.Append("</td>\n");
            }

            html.Append("</tr>\n");
        }

        html.Append("</table>\n");
    }

    // ---- Inlines ---------------------------------------------------------------------------

    private static void WriteInlines(StringBuilder html, Paragraph paragraph, EmailHtmlOptions options)
    {
        foreach (var inline in paragraph.Inlines)
        {
            switch (inline)
            {
                case Run run:
                    WriteRun(html, run, options);
                    break;

                case InlineImage image:
                    WriteImage(html, image.RawBytes, image.MimeType, image.Width, image.Height, options);
                    break;

                case InlineTable table:
                    WriteTable(html, table.Table, options);
                    break;
            }
        }
    }

    private static void WriteRun(StringBuilder html, Run run, EmailHtmlOptions options)
    {
        if (string.IsNullOrEmpty(run.Text)) return;

        var closing = new Stack<string>();

        // A link wraps everything else, so that a bold link is a link that is bold rather than
        // two elements that disagree about which of them the pointer is over.
        if (run.NavigateUri is { Length: > 0 } uri && IsSafeLink(uri))
        {
            html.Append("<a href=\"").Append(Escape(uri)).Append("\">");
            closing.Push("a");
        }

        if (run.FontWeight >= FontWeight.Bold) { html.Append("<b>"); closing.Push("b"); }
        if (run.FontStyle is FontStyle.Italic or FontStyle.Oblique) { html.Append("<i>"); closing.Push("i"); }

        foreach (var decoration in run.TextDecorations ?? [])
        {
            switch (decoration.Location)
            {
                case TextDecorationLocation.Underline: html.Append("<u>"); closing.Push("u"); break;
                case TextDecorationLocation.Strikethrough: html.Append("<s>"); closing.Push("s"); break;
            }
        }

        if (RunStyle(run, options) is { Length: > 0 } style)
        {
            html.Append("<span style=\"").Append(style).Append("\">");
            closing.Push("span");
        }

        html.Append(Escape(run.Text));

        while (closing.Count > 0) html.Append("</").Append(closing.Pop()).Append('>');
    }

    private static string RunStyle(Run run, EmailHtmlOptions options)
    {
        var style = new List<string>();

        // Only what the writer changed. An untouched run inherits the body's font, which is
        // stated once; saying it again on every run is the bloat this avoids.
        if (run.FontFamily is { Length: > 0 } family
            && !string.Equals(family, UntouchedFontFamily, StringComparison.Ordinal))
        {
            style.Add($"font-family:{(options.SubstituteFonts ? Stack(family) : family)}");
        }

        if (run.FontSize > 0 && Math.Abs(run.FontSize - UntouchedFontSize) > 0.01)
        {
            style.Add($"font-size:{Number(run.FontSize * PointsPerPixel)}pt");
        }

        if (run.Foreground is ISolidColorBrush { Color.A: > 0 } foreground)
        {
            style.Add($"color:{Hex(foreground.Color)}");
        }

        if (run.Background is ISolidColorBrush { Color.A: > 0 } background)
        {
            style.Add($"background-color:{Hex(background.Color)}");
        }

        return string.Join(';', style);
    }

    /// <summary>
    /// §6's wire/render split, as a CSS font stack.
    /// </summary>
    /// <remarks>
    /// The Microsoft name first, then the metric-compatible substitute, then the generic. A
    /// Windows recipient gets the real font, a Linux recipient gets one that occupies exactly the
    /// same space, and the message lays out the same for both. Naming only what was rendered here
    /// would reflow it for every Windows reader — which is the failure this rule exists for, and
    /// it is invisible from the sending end.
    /// </remarks>
    private static string Stack(string family)
    {
        var trimmed = family.Trim();
        if (trimmed.Length == 0) return trimmed;

        // The Microsoft name, chosen by the writer or pasted in: name it, then what stands in
        // for it, then the generic.
        if (FontSubstitution.Lookup(trimmed) is { } substitute)
        {
            var names = new List<string> { trimmed };
            if (substitute.Substitute is { Length: > 0 } fallback) names.Add(fallback);
            names.Add(substitute.Generic);
            return string.Join(", ", names.Select(Quote));
        }

        // The substitute itself, which is what a run carries when the writer chose a face from
        // the picker: the editor has to be told the family this machine can actually draw, and
        // for the bundled ones — Gelasio, Comic Relief — fontconfig knows no alias, so the run
        // holds "Gelasio" rather than "Georgia". Written the other way round on the wire, or a
        // Windows reader with Georgia installed would get Gelasio's fallback instead of Georgia.
        // This is §6's split, done at the last possible moment.
        if (FontSubstitution.Table.FirstOrDefault(e =>
                string.Equals(e.Substitute, trimmed, StringComparison.OrdinalIgnoreCase))
            is { } stoodInFor)
        {
            return string.Join(", ", new[] { stoodInFor.Original, trimmed, stoodInFor.Generic }.Select(Quote));
        }

        return Quote(trimmed);
    }

    /// <summary>A family name only needs quoting when it has a space in it.</summary>
    private static string Quote(string name)
        => name.Contains(' ', StringComparison.Ordinal) ? $"'{name}'" : name;

    private static void WriteImage(
        StringBuilder html, byte[]? bytes, string? mimeType, double width, double height,
        EmailHtmlOptions options)
    {
        if (bytes is not { Length: > 0 }) return;

        var type = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType;

        var source = options.RegisterImage is { } register
            ? register(bytes, type)
            : $"data:{type};base64,{Convert.ToBase64String(bytes)}";

        if (source.Length == 0) return;

        html.Append("<img src=\"").Append(Escape(source)).Append('"');

        // Stated in the markup as well as in the style: a client that blocks the image still
        // reserves the right amount of room for it, so the message does not reflow when it loads.
        if (width > 0) html.Append($" width=\"{(int)Math.Round(width)}\"");
        if (height > 0) html.Append($" height=\"{(int)Math.Round(height)}\"");

        html.Append(" style=\"border:0\" alt=\"\" />");
    }

    // ---- Small things ------------------------------------------------------------------------

    /// <summary>
    /// Whether a link may go out as one.
    /// </summary>
    /// <remarks>
    /// The mirror of what the reading pane refuses to render. A composer would have to work at
    /// it to produce a <c>javascript:</c> link, but sending one would be sending an attack, and
    /// the check costs nothing.
    /// </remarks>
    private static bool IsSafeLink(string uri)
    {
        var squashed = new string([.. uri.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c))]);

        return !squashed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
               && !squashed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
               && !squashed.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private static string Hex(Color colour)
        => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>A number without a trailing zero, and never in this machine's decimal comma.</summary>
    private static string Number(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

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
                case '"': escaped.Append("&quot;"); break;

                // A non-breaking space the writer typed survives as one; written raw it is
                // indistinguishable from an ordinary space and the client re-wraps where the
                // writer said not to. Spelled as an escape rather than as the character:
                // the character is invisible in a case label, and the next person to read
                // this cannot tell which of the two spaces it is.
                case '\u00A0': escaped.Append("&nbsp;"); break;
                default: escaped.Append(c); break;
            }
        }

        return escaped.ToString();
    }
}
