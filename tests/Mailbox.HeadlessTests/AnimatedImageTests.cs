using Mailbox.App.Theming;
using SkiaSharp;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The animated decoder over committed fixtures of our own making: a three-band GIF and the
/// same bands as APNG — each frame a different solid colour, so a wrong composite is a wrong
/// pixel, not a judgement call.
/// </summary>
public class AnimatedImageTests
{
    private static string Fixture(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "tests", "fixtures", "animated", name)))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? ".", "tests", "fixtures", "animated", name);
    }

    private static SKColor Pixel(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
    }

    [Theory]
    [InlineData("bands.gif")]
    [InlineData("bands.png")]
    public void ThreeBandsComeBackAsThreeFramesInOrder(string name)
    {
        var frames = AnimatedImageDecoder.Decode(File.ReadAllBytes(Fixture(name)));

        Assert.NotNull(frames);
        Assert.Equal(3, frames!.Count);
        Assert.All(frames, f => Assert.True(f.DelayMs >= 20, $"delay {f.DelayMs}"));

        // Red, then green, then blue — the order the fixture was made in.
        var colours = frames.Select(f => Pixel(f.Png)).ToList();
        Assert.True(colours[0].Red > 128 && colours[0].Green < 96, $"frame 0 is {colours[0]}");
        Assert.True(colours[1].Green > 128 && colours[1].Red < 96, $"frame 1 is {colours[1]}");
        Assert.True(colours[2].Blue > 128 && colours[2].Green < 96, $"frame 2 is {colours[2]}");
    }

    [Fact]
    public void FrameCountAnswersWithoutDecodingPixels()
    {
        Assert.Equal(3, AnimatedImageDecoder.FrameCount(File.ReadAllBytes(Fixture("bands.gif"))));
        Assert.Equal(3, AnimatedImageDecoder.FrameCount(File.ReadAllBytes(Fixture("bands.png"))));
    }

    [Fact]
    public void AStillImageIsOneFrameAndGarbageIsNull()
    {
        // A still PNG through the same door: one frame, no delay.
        var still = AnimatedImageDecoder.Decode(File.ReadAllBytes(
            Fixture("bands.gif").Replace(Path.Combine("animated", "bands.gif"), Path.Combine("theme-import", "header.png"))));
        Assert.NotNull(still);
        Assert.Single(still!);

        Assert.Null(AnimatedImageDecoder.Decode([1, 2, 3, 4, 5]));
    }
}
