using System.Buffers.Binary;
using System.Text;
using Mailbox.Import;
using Mailbox.Pst;
using Mailbox.Pst.Messaging;

namespace Mailbox.Tests;

/// <summary>
/// The RTF body of last resort: compressed-RTF blobs built in the format's own uncompressed
/// framing (MELA — [MS-OXRTFCP] allows a writer to store the bytes raw), so the fixtures test
/// the de-encapsulation and the stripping rather than MimeKit's decompressor.
/// </summary>
public class RtfBodyTests
{
    /// <summary>Wraps raw RTF in the compressed-RTF container's uncompressed form.</summary>
    private static byte[] Mela(string rtf)
    {
        var raw = Encoding.Latin1.GetBytes(rtf);
        var blob = new byte[16 + raw.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, (uint)(blob.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), (uint)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), 0x414C454D); // "MELA"
        raw.CopyTo(blob, 16);
        return blob;
    }

    [Fact]
    public void EncapsulatedHtmlComesBackOutAsHtml()
    {
        // The shape Outlook writes for HTML mail: markup in \*\htmltag groups, the RTF's own
        // rendering fenced behind \htmlrtf toggles, the visible text shared between both.
        var rtf = @"{\rtf1\ansi\fromhtml1{\fonttbl{\f0 Arial;}}" +
                  @"{\*\htmltag2 <html>}{\*\htmltag50 <body>}" +
                  @"\htmlrtf \f0\fs24 \htmlrtf0 " +
                  @"{\*\htmltag96 <p>}Hello, \'e9taged {\*\htmltag8 <b>}world{\*\htmltag9 </b>}{\*\htmltag104 </p>}" +
                  @"{\*\htmltag58 </body>}{\*\htmltag3 </html>}}";

        var (html, text) = RtfBody.FromCompressed(Mela(rtf));

        Assert.Null(text);
        Assert.NotNull(html);
        Assert.Contains("<body>", html);
        Assert.Contains("<p>Hello, étaged <b>world</b></p>", html);
    }

    [Fact]
    public void RealRtfStripsToItsTextWithParagraphsKept()
    {
        var rtf = @"{\rtf1\ansi{\fonttbl{\f0 Times;}}{\colortbl;\red0\green0\blue0;}" +
                  @"\f0\fs20 First paragraph.\par Second\tab indented.\par\'a9 2026}";

        var (html, text) = RtfBody.FromCompressed(Mela(rtf));

        Assert.Null(html);
        Assert.Equal("First paragraph.\nSecond\tindented.\n© 2026", text);
    }

    [Fact]
    public void UnicodeEscapesCarryTheirCharactersAndEatTheirFallbacks()
    {
        var rtf = @"{\rtf1\ansi A \u26085?\u26412? day}";

        var (_, text) = RtfBody.FromCompressed(Mela(rtf));

        Assert.Equal("A 日本 day", text);
    }

    [Fact]
    public void BytesThatAreNotCompressedRtfAnswerNothing()
    {
        var (html, text) = RtfBody.FromCompressed([1, 2, 3, 4, 5]);
        Assert.Null(html);
        Assert.Null(text);
    }

    private sealed class RtfOnlyMessage : IStoredMessage
    {
        public required byte[] Rtf { get; init; }

        public string MessageClass => "IPM.Note";

        public string Subject => "Only an RTF body";

        public string TransportHeaders => string.Empty;

        public string SenderName => string.Empty;

        public string SenderAddress => string.Empty;

        public string InternetMessageId => string.Empty;

        public DateTimeOffset? Delivered => null;

        public DateTimeOffset? Submitted => null;

        public string BodyText => string.Empty;

        public byte[] HtmlBody => [];

        public bool IsRead => true;

        public bool IsFlagged => false;

        public PstProperty? Property(ushort id) =>
            id == 0x1009 ? new PstProperty(0x1009, PstPropertyType.Binary, Rtf) : null;

        public PstProperty? Named(PstNamedProperties names, Guid set, uint numericId) => null;

        public IEnumerable<PstRecipient> Recipients() => [];

        public IEnumerable<IStoredAttachment> Attachments() => [];
    }

    [Fact]
    public void AMessageWhoseOnlyBodyIsRtfAssemblesWithABody()
    {
        var message = PstMime.Assemble(new RtfOnlyMessage
        {
            Rtf = Mela(@"{\rtf1\ansi The words survive.\par}"),
        }, "fixture@rtf.test.invalid");

        Assert.Equal("The words survive.", message.TextBody?.Trim());
    }
}
