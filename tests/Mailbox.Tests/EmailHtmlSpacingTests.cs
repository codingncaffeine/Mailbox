using AvaloniaRichEditor.Documents;
using Mailbox.Editor;

namespace Mailbox.Tests;

/// <summary>
/// Line and Paragraph Spacing, on the wire.
/// </summary>
/// <remarks>
/// The audit found this button applying its choice to the document and to nothing else: the
/// spacing was set, the screen changed, and the serializer wrote no <c>line-height</c>, so every
/// recipient got single. That is the worst shape a fault can take — it looks right where it was
/// made and is wrong only where nobody can see it — which is why it is held here rather than
/// left to the next capture.
/// </remarks>
public class EmailHtmlSpacingTests
{
    private static string Body(Paragraph paragraph)
    {
        var document = new FlowDocument();
        document.Blocks.Add(paragraph);
        return EmailHtml.Serialize(document, new EmailHtmlOptions { Fragment = true });
    }

    private static Paragraph Spaced(double spacing)
    {
        var paragraph = new Paragraph { LineSpacing = spacing };
        paragraph.Inlines.Add(new Run { Text = "Hello" });
        return paragraph;
    }

    [Theory]
    [InlineData(1.15)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void SpacingReachesTheWire(double spacing)
        => Assert.Contains($"line-height:{spacing.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            Body(Spaced(spacing)), StringComparison.Ordinal);

    /// <summary>Single is every client's default, and saying it again is bytes for nothing.</summary>
    [Fact]
    public void SingleSpacingIsNotWrittenDown()
        => Assert.DoesNotContain("line-height", Body(Spaced(1.0)), StringComparison.Ordinal);

    /// <summary>A paragraph nobody has set a spacing on carries NaN, which is not a length.</summary>
    [Fact]
    public void AParagraphWithNoSpacingSaysNothing()
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = "Hello" });

        Assert.DoesNotContain("line-height", Body(paragraph), StringComparison.Ordinal);
    }

    /// <summary>
    /// Unitless, so the leading follows whatever size the reader's client settled on. A length
    /// here would be measured against the size this machine happened to compose at.
    /// </summary>
    [Fact]
    public void SpacingIsAMultiplierRatherThanALength()
    {
        var html = Body(Spaced(2.0));

        Assert.Contains("line-height:2", html, StringComparison.Ordinal);
        Assert.DoesNotContain("line-height:2pt", html, StringComparison.Ordinal);
        Assert.DoesNotContain("line-height:2px", html, StringComparison.Ordinal);
    }
}
