using Avalonia.Media;
using AvaloniaRichEditor.Documents;
using Mailbox.Editor;

namespace Mailbox.Tests;

/// <summary>
/// What leaves the machine.
/// </summary>
/// <remarks>
/// The half of the editor that stayed in-house, and the half mail fidelity rests on: everything here
/// is about a message rendering correctly in a client we will never see. The rules are narrow
/// and each one exists because breaking it is invisible from the sending end.
/// </remarks>
public class EmailHtmlTests
{
    private static string Body(params Block[] blocks)
    {
        var document = new FlowDocument();
        foreach (var block in blocks) document.Blocks.Add(block);

        return EmailHtml.Serialize(document, new EmailHtmlOptions { Fragment = true });
    }

    private static Paragraph Para(params Inline[] inlines)
    {
        var paragraph = new Paragraph();
        foreach (var inline in inlines) paragraph.Inlines.Add(inline);
        return paragraph;
    }

    private static Run Text(string text) => new() { Text = text };

    // ---- The shape of it ---------------------------------------------------------------------

    [Fact]
    public void AParagraphIsAParagraph()
        => Assert.Contains("<p>Hello</p>", Body(Para(Text("Hello"))), StringComparison.Ordinal);

    /// <summary>
    /// A blank line survives however it is held: an empty paragraph, and equally one whose only
    /// content is the plain space a reloaded draft's <c>&amp;nbsp;</c> comes back as — clients
    /// collapse a whitespace-only paragraph exactly as they do an empty one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\u00A0")]
    public void ABlankLineIsHeldOpenByANonBreakingSpace(string content)
    {
        var paragraph = content.Length == 0 ? Para() : Para(Text(content));

        Assert.Contains("<p>&nbsp;</p>", Body(paragraph), StringComparison.Ordinal);
    }

    /// <summary>
    /// The editor's own serializer writes text-align:left on every block ever written. Emitting
    /// the default is what makes generated mail enormous and unreadable in a diff.
    /// </summary>
    [Fact]
    public void TheDefaultAlignmentIsNotWrittenOut()
    {
        var body = Body(Para(Text("Hello")));

        Assert.DoesNotContain("text-align", body, StringComparison.Ordinal);
        Assert.DoesNotContain("style=\"\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TextAlignment.Center, "text-align:center")]
    [InlineData(TextAlignment.Right, "text-align:right")]
    [InlineData(TextAlignment.Justify, "text-align:justify")]
    public void AnAlignmentThatIsNotTheDefaultIs(TextAlignment alignment, string expected)
    {
        var paragraph = Para(Text("Hello"));
        paragraph.TextAlignment = alignment;

        Assert.Contains(expected, Body(paragraph), StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadingIsAHeading()
    {
        var paragraph = Para(Text("Title"));
        paragraph.HeadingLevel = 2;

        Assert.Contains("<h2>Title</h2>", Body(paragraph), StringComparison.Ordinal);
    }

    /// <summary>
    /// A quote states its own rule. Most clients draw one from the element and some draw
    /// nothing, and a reply whose quoted half is indistinguishable from the new half is the
    /// thing this is for.
    /// </summary>
    [Fact]
    public void AQuoteCarriesItsOwnRule()
    {
        var paragraph = Para(Text("They wrote this."));
        paragraph.IsQuote = true;

        var body = Body(paragraph);

        Assert.Contains("<blockquote", body, StringComparison.Ordinal);
        Assert.Contains("border-left:2px solid", body, StringComparison.Ordinal);
    }

    /// <summary>A blank line the writer typed is a blank line the reader sees.</summary>
    [Fact]
    public void AnEmptyParagraphSurvives()
        => Assert.Contains("<p>&nbsp;</p>", Body(new Paragraph()), StringComparison.Ordinal);

    [Fact]
    public void ADividerIsARule()
        => Assert.Contains("<hr />", Body(new DividerBlock()), StringComparison.Ordinal);

    // ---- Character formatting ------------------------------------------------------------------

    [Fact]
    public void BoldAndItalicUseTheElementsEveryClientKnows()
    {
        var body = Body(Para(
            new Run { Text = "bold", FontWeight = FontWeight.Bold },
            new Run { Text = "italic", FontStyle = FontStyle.Italic }));

        Assert.Contains("<b>bold</b>", body, StringComparison.Ordinal);
        Assert.Contains("<i>italic</i>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void UnderlineAndStrikethroughDoToo()
    {
        var body = Body(Para(
            new Run { Text = "under", TextDecorations = TextDecorations.Underline },
            new Run { Text = "struck", TextDecorations = TextDecorations.Strikethrough }));

        Assert.Contains("<u>under</u>", body, StringComparison.Ordinal);
        Assert.Contains("<s>struck</s>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ColourIsAHexValueOnASpan()
    {
        var body = Body(Para(new Run
        {
            Text = "red",
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x00, 0x00)),
        }));

        Assert.Contains("color:#C00000", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Points, not pixels. It is what the clients use and what the people writing mail into
    /// them expect, and a size in px is a size some of them ignore.
    /// </summary>
    [Fact]
    public void SizesAreInPoints()
    {
        var body = Body(Para(new Run { Text = "big", FontSize = 16 }));

        Assert.Contains("font-size:12pt", body, StringComparison.Ordinal);
        Assert.DoesNotContain("px", body, StringComparison.Ordinal);
    }

    /// <summary>A link wraps its formatting rather than the other way round.</summary>
    [Fact]
    public void ABoldLinkIsOneElementInsideTheOther()
    {
        var body = Body(Para(new Run
        {
            Text = "click",
            NavigateUri = "https://example.com/",
            FontWeight = FontWeight.Bold,
        }));

        Assert.Contains("<a href=\"https://example.com/\"><b>click</b></a>", body,
            StringComparison.Ordinal);
    }

    // ---- the design's wire/render split ----------------------------------------------------------------

    /// <summary>
    /// The rule the whole font subsystem exists for. A message composed in Calibri must name
    /// Calibri first, so a Windows reader gets the real font; the metric-compatible substitute
    /// second, so a Linux reader gets one that occupies the same space; and a generic last.
    /// Naming only what was rendered here reflows the message for every Windows reader, and it
    /// is invisible from this end.
    /// </summary>
    [Fact]
    public void AFontNamesTheOriginalFirstThenItsSubstitute()
    {
        var body = Body(Para(new Run { Text = "x", FontFamily = "Calibri" }));

        Assert.Contains("font-family:Calibri, Carlito, sans-serif", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Working the requested name back from the substitution table alone is many-to-one —
    /// Georgia and Times New Roman can both render in Liberation Serif — so the picker's own
    /// record of what was asked for outranks the table's guess.
    /// </summary>
    [Fact]
    public void TheFamilyTheWriterChoseOutranksTheTablesGuess()
    {
        var document = new FlowDocument();
        document.Blocks.Add(Para(new Run { Text = "x", FontFamily = "Liberation Serif" }));

        var body = EmailHtml.Serialize(document, new EmailHtmlOptions
        {
            Fragment = true,
            RequestedFamily = rendered =>
                string.Equals(rendered, "Liberation Serif", StringComparison.OrdinalIgnoreCase) ? "Georgia" : null,
        });

        Assert.Contains("font-family:Georgia, Gelasio, serif", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Times New Roman", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Said once on the body, not on every run. A run nobody restyled says nothing about its
    /// font — the editor's own default is not a choice the writer made, and repeating it on
    /// every run is what makes composed mail several times the size of the message in it.
    /// </summary>
    [Fact]
    public void TheStationeryFontAndColourAreTheBodysBaseStyle()
    {
        var document = new FlowDocument();
        document.Blocks.Add(Para(Text("One")));

        var html = EmailHtml.Serialize(document, new EmailHtmlOptions
        {
            BaseFontFamily = "Georgia",
            BaseFontPoints = 12,
            BaseColour = "#1F3864",
        });

        Assert.Contains("<body style=\"font-family:Georgia, Gelasio, serif;font-size:12pt;color:#1F3864\">",
            html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFontIsStatedOnceAndAnUntouchedRunSaysNothing()
    {
        var document = new FlowDocument();
        document.Blocks.Add(Para(Text("One"), Text("Two"), Text("Three")));

        var html = EmailHtml.Serialize(document);

        Assert.Contains("<body style=\"font-family:Calibri, Carlito, sans-serif;font-size:11pt\">",
            html, StringComparison.Ordinal);

        // Exactly one, and it is the body's.
        Assert.Equal(1, Count(html, "font-size:"));
        Assert.Equal(1, Count(html, "font-family:"));
        Assert.DoesNotContain("<span", html, StringComparison.Ordinal);
    }

    /// <summary>And a run the writer did restyle says exactly what changed, and no more.</summary>
    [Fact]
    public void ARestyledRunSaysOnlyWhatChanged()
    {
        var document = new FlowDocument();
        document.Blocks.Add(Para(
            Text("plain "),
            new Run { Text = "bigger", FontSize = 20 }));

        var html = EmailHtml.Serialize(document);

        Assert.Contains("<span style=\"font-size:15pt\">bigger</span>", html, StringComparison.Ordinal);
        Assert.Contains("<p>plain <span", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AFamilyWithASpaceInItIsQuoted()
    {
        var body = Body(Para(new Run { Text = "x", FontFamily = "Times New Roman" }));

        Assert.Contains("'Times New Roman'", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The picker hands the editor the family this machine can draw — Liberation Serif, or the
    /// bundled Gelasio — because that is what the editor needs to render it. On the wire that
    /// has to turn back into the name the writer chose, or a Windows reader who has Times New
    /// Roman gets Liberation Serif's fallback instead. The split, done at the last moment.
    /// </summary>
    [Theory]
    [InlineData("Liberation Serif", "font-family:'Times New Roman', 'Liberation Serif', serif")]
    [InlineData("Carlito", "font-family:Calibri, Carlito, sans-serif")]
    [InlineData("Gelasio", "font-family:Georgia, Gelasio, serif")]
    public void ASubstituteOnARunGoesOutAsTheFontItStandsInFor(string rendered, string expected)
    {
        var body = Body(Para(new Run { Text = "x", FontFamily = rendered }));

        Assert.Contains(expected, body, StringComparison.Ordinal);
    }

    /// <summary>A font nothing substitutes still goes out under its own name.</summary>
    [Fact]
    public void AFontWithNoSubstituteIsNamedAnyway()
    {
        var body = Body(Para(new Run { Text = "x", FontFamily = "Some Unknown Face" }));

        Assert.Contains("font-family:'Some Unknown Face'", body, StringComparison.Ordinal);
    }

    // ---- Lists -----------------------------------------------------------------------------------

    /// <summary>
    /// A run of list paragraphs is one list. One list each is what a naive walk produces, and it
    /// renders as a column of single-item lists with a gap between every line.
    /// </summary>
    [Fact]
    public void ConsecutiveItemsAreOneList()
    {
        var first = Para(Text("One"));
        first.ListType = ListKind.Bullet;
        var second = Para(Text("Two"));
        second.ListType = ListKind.Bullet;

        var body = Body(first, second);

        Assert.Equal(1, Count(body, "<ul"));
        Assert.Equal(2, Count(body, "<li>"));
    }

    [Fact]
    public void AnOrderedListIsOrdered()
    {
        var item = Para(Text("One"));
        item.ListType = ListKind.Ordered;

        Assert.Contains("<ol", Body(item), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoListsOfDifferentKindsStaySeparate()
    {
        var bullet = Para(Text("One"));
        bullet.ListType = ListKind.Bullet;
        var numbered = Para(Text("Two"));
        numbered.ListType = ListKind.Ordered;

        var body = Body(bullet, numbered);

        Assert.Equal(1, Count(body, "<ul"));
        Assert.Equal(1, Count(body, "<ol"));
    }

    // ---- Tables ------------------------------------------------------------------------------------

    /// <summary>
    /// Presentational attributes, deliberately. This is the one place the old way is still the
    /// right way: the clients a table has to survive honour border and cellpadding and disagree
    /// about the CSS.
    /// </summary>
    [Fact]
    public void ATableIsWrittenTheWayMailWritesOne()
    {
        var body = Body(Table(2, 2));

        Assert.Contains("cellpadding=\"4\"", body, StringComparison.Ordinal);
        Assert.Contains("border=\"1\"", body, StringComparison.Ordinal);
        Assert.Equal(2, Count(body, "<tr>"));
        Assert.Equal(4, Count(body, "<td"));
    }

    /// <summary>A merged cell is written once, with its span, and the covered ones not at all.</summary>
    [Fact]
    public void AMergedCellIsWrittenOnce()
    {
        var table = Table(2, 2);
        table.MergeCells(0, 0, 0, 1);

        var body = Body(table);

        Assert.Contains("colspan=\"2\"", body, StringComparison.Ordinal);
        Assert.Equal(3, Count(body, "<td"));
    }

    private static TableBlock Table(int rows, int columns)
    {
        var table = new TableBlock { Rows = rows, Columns = columns };

        for (var r = 0; r < rows; r++)
        {
            var row = new List<TableCell>();
            var spans = new List<int>();

            for (var c = 0; c < columns; c++)
            {
                var cell = new TableCell();
                cell.Blocks.Add(Para(Text($"r{r}c{c}")));
                row.Add(cell);
                spans.Add(1);
            }

            table.Cells.Add(row);
            table.ColSpans.Add([.. spans]);
            table.RowSpans.Add([.. spans]);
        }

        return table;
    }

    // ---- What must not go out -------------------------------------------------------------------

    /// <summary>
    /// Several large clients strip a stylesheet out of a received message entirely, which
    /// silently unstyles the lot. Everything is inline for that reason.
    /// </summary>
    [Fact]
    public void ThereIsNoStylesheetAndNoClasses()
    {
        var paragraph = Para(new Run { Text = "x", FontFamily = "Calibri", FontSize = 14 });
        paragraph.TextAlignment = TextAlignment.Center;

        var body = Body(paragraph);

        Assert.DoesNotContain("<style", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("style=\"", body, StringComparison.Ordinal);
    }

    /// <summary>The editor stamps an attribute of its own on a coloured run. It does not go out.</summary>
    [Fact]
    public void NothingProprietaryGoesOut()
    {
        var body = Body(Para(new Run
        {
            Text = "x",
            Foreground = new SolidColorBrush(Colors.Blue),
            NavigateUri = "https://example.com/",
        }));

        Assert.DoesNotContain("data-are", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("vbscript:msgbox")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void ALinkThatIsAnAttackIsNotSentAsOne(string uri)
    {
        var body = Body(Para(new Run { Text = "click", NavigateUri = uri }));

        Assert.DoesNotContain("<a href", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("click", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TextIsEscaped()
    {
        var body = Body(Para(Text("a < b & c > d \"quoted\"")));

        Assert.Contains("&lt;", body, StringComparison.Ordinal);
        Assert.Contains("&amp;", body, StringComparison.Ordinal);
        Assert.Contains("&gt;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("< b", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-breaking space the writer typed is one the reader gets — and an ordinary space
    /// stays ordinary. Escaping every space would be the worse bug of the two: the paragraph
    /// would become one unbreakable line and no client would wrap it. Both are spelled as
    /// escapes here, because the two are indistinguishable on the page.
    /// </summary>
    [Fact]
    public void OnlyANonBreakingSpaceBecomesOne()
    {
        var body = Body(Para(Text("a\u00A0b c")));

        Assert.Contains("a&nbsp;b c", body, StringComparison.Ordinal);
        Assert.Equal(1, Count(body, "&nbsp;"));
    }

    // ---- Images ------------------------------------------------------------------------------------

    /// <summary>
    /// A composed image becomes a <c>cid:</c> reference and a related part, because that is how
    /// mail carries one. Several large clients drop a <c>data:</c> image outright.
    /// </summary>
    [Fact]
    public void AnImageGoesOutThroughTheCallerSoItCanBecomeAPart()
    {
        var registered = new List<string>();

        var image = new ImageBlock { Width = 120, Height = 32 };
        image.SetImageData([1, 2, 3], "image/png", null!);

        var document = new FlowDocument();
        document.Blocks.Add(image);

        var html = EmailHtml.Serialize(document, new EmailHtmlOptions
        {
            Fragment = true,
            RegisterImage = (bytes, type) =>
            {
                registered.Add($"{type}:{bytes.Length}");
                return "cid:image-1";
            },
        });

        Assert.Equal(["image/png:3"], registered);
        Assert.Contains("src=\"cid:image-1\"", html, StringComparison.Ordinal);

        // Stated so a client that blocks the image still leaves room for it, and the message
        // does not reflow when it loads.
        Assert.Contains("width=\"120\"", html, StringComparison.Ordinal);
        Assert.Contains("height=\"32\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoCallerAnImageIsInlinedWhichIsRightForAPreviewOnly()
    {
        var image = new ImageBlock();
        image.SetImageData([1, 2, 3], "image/png", null!);

        var document = new FlowDocument();
        document.Blocks.Add(image);

        Assert.Contains("src=\"data:image/png;base64,",
            EmailHtml.Serialize(document, new EmailHtmlOptions { Fragment = true }),
            StringComparison.Ordinal);
    }

    // ---- The document around it ---------------------------------------------------------------------

    [Fact]
    public void AWholeDocumentSaysWhatItIsEncodedIn()
    {
        var document = new FlowDocument();
        document.Blocks.Add(Para(Text("Hello")));

        var html = EmailHtml.Serialize(document);

        Assert.StartsWith("<html>", html, StringComparison.Ordinal);
        Assert.Contains("charset=\"utf-8\"", html, StringComparison.Ordinal);
        Assert.Contains("</html>", html, StringComparison.Ordinal);
    }

    /// <summary>A quoted reply spliced into another message must not carry a second html element.</summary>
    [Fact]
    public void AFragmentIsJustTheMarkup()
    {
        var document = new FlowDocument();
        document.Blocks.Add(Para(Text("Hello")));

        var html = EmailHtml.Serialize(document, new EmailHtmlOptions { Fragment = true });

        Assert.DoesNotContain("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<body", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The output has to survive the thing that reads received mail, because a reply quotes what
    /// it is replying to and that goes back through the sanitizer on the way to the screen.
    /// </summary>
    [Fact]
    public void WhatGoesOutSurvivesWhatComesIn()
    {
        var paragraph = Para(
            new Run { Text = "Bold ", FontWeight = FontWeight.Bold },
            new Run { Text = "and a link", NavigateUri = "https://example.com/" });

        var document = new FlowDocument();
        document.Blocks.Add(paragraph);

        var rendered = Mailbox.Rendering.MessageRenderer
            .RenderHtml(EmailHtml.Serialize(document)).Html;

        Assert.Contains("<b>", rendered, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com/\"", rendered, StringComparison.Ordinal);
        Assert.Contains("Bold", rendered, StringComparison.Ordinal);
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
