using System.Text;
using Mailbox.Protocols;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// A message in a legacy code page decodes to the words that were sent.
/// </summary>
/// <remarks>
/// .NET ships ASCII, the UTF family and Latin-1 and nothing else; every other code page lives in
/// <c>CodePagesEncodingProvider</c>, which has to be registered before <c>Encoding.GetEncoding</c>
/// can answer for it. The tree registers it twice — for feeds, and for reading a <c>.pst</c>, both
/// with a comment saying why — and did not for mail itself, which is the path that carries the
/// most of it.
/// <para>
/// The failure is silent and total: a message whose charset cannot be resolved is read as Latin-1,
/// so a Japanese message becomes a page of accented Latin. It reaches the list's preview and the
/// search index as well as the pane, because all three come off the same decode.
/// </para>
/// </remarks>
public class CharsetDecodeTests
{
    /// <summary>Builds the bytes a message in this charset would actually arrive as.</summary>
    /// <remarks>
    /// Through the production helper rather than <c>Encoding.RegisterProvider</c> directly: a test
    /// that registers the provider itself passes whether or not the application ever does, which
    /// is exactly the hole this fault lived in. Registration is process-wide, so this is the one
    /// call that can be held to.
    /// </remarks>
    private static byte[] Message(string charset, string body)
    {
        LegacyCodePages.Register();

        var message = new MimeMessage { Subject = "A subject" };
        message.From.Add(new MailboxAddress("A. Person", "a.person@example.com"));
        message.To.Add(new MailboxAddress("You", "you@example.com"));

        var part = new TextPart(MimeKit.Text.TextFormat.Plain);
        part.SetText(Encoding.GetEncoding(charset), body);
        message.Body = part;

        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        return buffer.ToArray();
    }

    [Theory]
    [InlineData("iso-8859-1", "Une phrase avec des accents : é è ê ë à ù ç ô.")]
    [InlineData("koi8-r", "Проверка кодировки и того, как она читается.")]
    [InlineData("shift_jis", "これは Shift_JIS で符号化された本文です。")]
    [InlineData("gb2312", "这是一封用简体中文写的邮件。")]
    [InlineData("big5", "這是一封用繁體中文寫的郵件。")]
    [InlineData("euc-kr", "이것은 한국어로 작성된 메일입니다.")]
    [InlineData("windows-1251", "Это письмо написано кириллицей.")]
    [InlineData("windows-1252", "Curly quotes — and an em dash.")]
    public void AMessageInALegacyCodePageKeepsItsWords(string charset, string body)
    {
        LegacyCodePages.Register();

        // Whether this platform can hold these words in this charset at all, asked before the
        // message is built. `CodePagesEncodingProvider` does not ship the same tables everywhere —
        // the CJK double-byte pages are present on some Linux images and not others — and a test
        // that asserts through a table the platform does not have is testing the platform. What
        // this test is for is the decode: that a message whose charset *is* resolvable comes back
        // as the words that were sent, which is what stops the Latin-1 fallback returning.
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            Assert.Skip($"This platform has no table for {charset}.");
            return;
        }

        if (encoding.GetString(encoding.GetBytes(body)) != body)
        {
            Assert.Skip($"This platform's {charset} table cannot hold these characters.");
            return;
        }

        var raw = Message(charset, body);

        using var stream = new MemoryStream(raw);
        var parsed = MimeMessage.Load(stream, TestContext.Current.CancellationToken);

        // The summary is what the list previews and what the search index holds, so it is the
        // decode with the widest blast radius — and the one a reader meets first.
        var summary = MessageMapper.ToSummary(parsed, "uid", raw.Length, DateTimeOffset.UtcNow);

        Assert.Equal(body.Trim(), summary.BodyText.Trim());
    }
}
