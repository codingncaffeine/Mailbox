using Mailbox.App.Views;

namespace Mailbox.HeadlessTests;

/// <summary>
/// A picture is recognised by its bytes when its server labels it as something else, and
/// something that is not a picture is not taken for one.
/// </summary>
public class ImageSniffTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0 }, "image/gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' }, "image/avif")]
    public void APictureIsKnownByItsBytes(byte[] bytes, string expected)
        => Assert.Equal(expected, ImageSniff.TypeOf(bytes));

    [Fact]
    public void SvgIsKnownAfterADeclaration()
        => Assert.Equal("image/svg+xml", ImageSniff.TypeOf("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));

    [Theory]
    [InlineData("<!doctype html><html><body>Not found</body></html>")]
    [InlineData("console.log('a script');")]
    [InlineData("")]
    public void APageOrAScriptIsNotAPicture(string text)
        => Assert.Null(ImageSniff.TypeOf(System.Text.Encoding.UTF8.GetBytes(text)));
}
