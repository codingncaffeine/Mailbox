using AvaloniaRichEditor.Documents;
using AvaloniaRichEditor.Formatters;
using Mailbox.Editor;

namespace Mailbox.Tests;

/// <summary>
/// Changing the sending account changes the signature — replaced in the place the old one holds,
/// around whatever has been written, and never touching a word of the writer's.
/// </summary>
public sealed class SignatureSwapTests
{
    private static FlowDocument Doc(string html) => HtmlDocumentFormatter.ParseHtml(html, false, false);

    private static string Text(FlowDocument document)
        => string.Join("/", document.Blocks.OfType<Paragraph>()
            .Select(p => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text)).Trim()));

    [Fact]
    public void SwitchingAccountsReplacesTheSignatureWhereItStands()
    {
        var document = Doc("<p>Dear Ada,</p><p>the words</p><p>—</p><p>Work Me</p><blockquote>the quote</blockquote>");
        var tracked = new[] { document.Blocks[2], document.Blocks[3] };

        var (now, removed) = SignatureBlocks.Swap(document, tracked, insertBefore: null,
            SignatureBlocks.Parse("<p>Home Me</p>"));

        Assert.False(removed);
        Assert.Equal("Dear Ada,/the words/Home Me/the quote", Text(document));
        Assert.Single(now);
        Assert.Same(document.Blocks[2], now[0]);
    }

    [Fact]
    public void TheWritersIdenticalParagraphIsNotTheSignature()
    {
        // The writer typed the very words the signature carries, above it. Only the tracked
        // instance may go — by identity, whatever the blocks think of equality.
        var document = Doc("<p>Work Me</p><p>Work Me</p>");
        var tracked = new[] { document.Blocks[1] };

        var (_, removed) = SignatureBlocks.Swap(document, tracked, null, SignatureBlocks.Parse("<p>Home Me</p>"));

        Assert.False(removed);
        Assert.Equal("Work Me/Home Me", Text(document));
    }

    [Fact]
    public void ADeletedSignatureStaysDeleted()
    {
        var document = Doc("<p>just my words</p>");
        var gone = Doc("<p>Work Me</p>").Blocks.ToList();

        var (now, removed) = SignatureBlocks.Swap(document, gone, null, SignatureBlocks.Parse("<p>Home Me</p>"));

        Assert.True(removed);
        Assert.Empty(now);
        Assert.Equal("just my words", Text(document));
    }

    [Fact]
    public void AnAccountWithNoSignatureTakesTheOldOneOutAndTheNextPutsItsOwnBack()
    {
        var document = Doc("<p>&nbsp;</p><p>&nbsp;</p><p>Work Me</p><blockquote>the quote</blockquote>");
        var quote = document.Blocks[3];

        var (afterNone, _) = SignatureBlocks.Swap(document, [document.Blocks[2]], quote, []);
        Assert.Empty(afterNone);
        Assert.Equal("//the quote", Text(document));

        // Back to an account that signs: the reply's signature returns above the quote.
        var (afterSome, _) = SignatureBlocks.Swap(document, afterNone, quote, SignatureBlocks.Parse("<p>Home Me</p>"));
        Assert.Single(afterSome);
        Assert.Equal("//Home Me/the quote", Text(document));
    }

    [Fact]
    public void WithNothingTrackedAndNoAnchorTheSignatureGoesAtTheEnd()
    {
        var document = Doc("<p>a new message</p>");

        var (now, _) = SignatureBlocks.Swap(document, [], null, SignatureBlocks.Parse("<p>Home Me</p>"));

        Assert.Single(now);
        Assert.Equal("a new message/Home Me", Text(document));
    }

    [Fact]
    public void PartsParseToTheSameBlocksJoinedOrApart()
    {
        // Prefill counts the signature's blocks by parsing it alone, then takes that many from
        // the loaded document — which only works if block-level fragments parse additively.
        string[] signatures = ["<p>—</p><p>Work Me</p>", "Cheers, Me", "<p>one</p>plain tail"];
        string[] quotes = ["<blockquote><p>the original</p></blockquote>", "<p>On Friday, Ada wrote:</p><p>words</p>"];

        foreach (var signature in signatures)
        {
            foreach (var quote in quotes)
            {
                var apart = 2 + Doc(signature).Blocks.Count + Doc(quote).Blocks.Count;
                var joined = Doc("<p>&nbsp;</p><p>&nbsp;</p>" + signature + quote).Blocks.Count;
                Assert.Equal(apart, joined);
            }
        }
    }
}
