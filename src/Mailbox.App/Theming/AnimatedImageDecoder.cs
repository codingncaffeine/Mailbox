using System.Buffers.Binary;
using Mailbox.Theming.Import;
using SkiaSharp;

namespace Mailbox.App.Theming;

/// <summary>
/// Decodes an image — still or animated — into laundered PNG frames. GIF and animated WebP go
/// through Skia's own codec, which composites partial frames itself; APNG, which Skia does not
/// read, is walked chunk by chunk here and composited to the specification: each frame's
/// region, offset, blend and dispose honoured. Everything comes out as whole, freshly encoded
/// full-size frames, so nothing downstream ever meets a stranger's bytes or a partial region.
/// </summary>
/// <remarks>
/// Animation is bounded like everything imported: at most <see cref="FrameCap"/> frames and
/// <see cref="PixelBudget"/> decoded pixels in all — a theme, not a film. Anything over the
/// budget keeps its first frames and the note says so; anything that does not decode is null,
/// and the caller refuses it.
/// </remarks>
internal static class AnimatedImageDecoder
{
    internal const int FrameCap = 90;
    internal const long PixelBudget = 40_000_000;

    /// <summary>The slowest a browser treats a near-zero delay: the classic 100ms.</summary>
    private const int DefaultDelayMs = 100;
    private const int MinimumDelayMs = 20;

    /// <summary>Frames from whatever arrived, or null when it does not decode as an image.</summary>
    internal static IReadOnlyList<ReencodedFrame>? Decode(byte[] source)
    {
        try
        {
            if (IsApng(source) && DecodeApng(source) is { Count: > 0 } apng) return apng;

            // Skia reads every still format this accepts, so what its codec refuses is
            // refused — a lenient fallback here would wave garbage through.
            return DecodeWithSkia(source) is { Count: > 0 } skia ? skia : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException
                                       or NullReferenceException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>How many frames the file carries, without decoding pixels — the preview's "Animated" badge.</summary>
    internal static int FrameCount(byte[] source)
    {
        try
        {
            if (IsApng(source))
            {
                // acTL's first field is the frame count.
                var at = IndexOfChunk(source, "acTL");
                return at >= 0 ? BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(at + 8, 4)) : 1;
            }

            using var codec = SKCodec.Create(SKData.CreateCopy(source));
            return codec is null ? 1 : Math.Max(1, codec.FrameCount);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return 1;
        }
    }

    // ------------------------------------------------------------------------------------
    // GIF and animated WebP — Skia's codec does the compositing
    // ------------------------------------------------------------------------------------

    private static List<ReencodedFrame>? DecodeWithSkia(byte[] source)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(source));
        if (codec is null) return null;

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (info.Width <= 0 || info.Height <= 0) return null;

        var frameCount = Math.Max(1, codec.FrameCount);
        var budgetFrames = Bounded(frameCount, info.Width, info.Height);
        var frameInfos = codec.FrameInfo;

        var frames = new List<ReencodedFrame>();
        using var bitmap = new SKBitmap(info);
        for (var i = 0; i < budgetFrames; i++)
        {
            var options = new SKCodecOptions(i);
            if (i > 0 && frameInfos.Length > i && frameInfos[i].RequiredFrame != -1)
            {
                // The buffer still holds the previous composite, which is what Skia asks for.
                options = new SKCodecOptions(i) { PriorFrame = i - 1 };
            }

            var result = codec.GetPixels(info, bitmap.GetPixels(), options);
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput) return null;

            var delay = frameInfos.Length > i ? frameInfos[i].Duration : 0;
            frames.Add(new ReencodedFrame(EncodePng(bitmap), NormalDelay(delay)));
        }

        return frames;
    }

    // ------------------------------------------------------------------------------------
    // APNG — walked and composited here, to the specification
    // ------------------------------------------------------------------------------------

    private static bool IsApng(byte[] source)
        => source.Length > 40
           && source[0] == 0x89 && source[1] == 0x50 && source[2] == 0x4E && source[3] == 0x47
           && IndexOfChunk(source, "acTL") >= 0;

    private sealed record ApngFrame(int Width, int Height, int X, int Y, int DelayMs, byte Dispose, byte Blend, List<byte[]> Data);

    private static List<ReencodedFrame>? DecodeApng(byte[] source)
    {
        // One walk collects the canvas IHDR, the ancillary chunks every frame's sub-PNG
        // needs (palette, transparency, gamma), and each frame's control and data.
        byte[]? ihdr = null;
        var ancillary = new List<(string Type, byte[] Data)>();
        var frames = new List<ApngFrame>();
        ApngFrame? current = null;
        var defaultImageIsFrame = false;
        var sawIdat = false;

        var at = 8;
        while (at + 12 <= source.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(at, 4));
            if (length < 0 || at + 12 + length > source.Length) return null;
            var type = System.Text.Encoding.ASCII.GetString(source, at + 4, 4);
            var data = source.AsSpan(at + 8, length).ToArray();

            switch (type)
            {
                case "IHDR":
                    ihdr = data;
                    break;
                case "PLTE" or "tRNS" or "gAMA" or "cHRM" or "sRGB" or "iCCP" or "sBIT":
                    ancillary.Add((type, data));
                    break;
                case "fcTL":
                    current = new ApngFrame(
                        BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4)),
                        BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8, 4)),
                        BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12, 4)),
                        BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)),
                        FctlDelay(data), data[24], data[25], []);
                    frames.Add(current);
                    if (!sawIdat) defaultImageIsFrame = true;
                    break;
                case "IDAT":
                    sawIdat = true;
                    if (defaultImageIsFrame && current is not null) current.Data.Add(data);
                    break;
                case "fdAT":
                    current?.Data.Add(data[4..]); // past the sequence number is IDAT data
                    break;
                case "IEND":
                    at = source.Length;
                    continue;
            }

            at += 12 + length;
        }

        if (ihdr is null || frames.Count == 0) return null;
        var canvasWidth = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(0, 4));
        var canvasHeight = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4, 4));
        if (canvasWidth <= 0 || canvasHeight <= 0) return null;

        var budgetFrames = Bounded(frames.Count, canvasWidth, canvasHeight);
        var output = new List<ReencodedFrame>();
        using var canvasBitmap = new SKBitmap(new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(canvasBitmap);
        canvas.Clear(SKColors.Transparent);
        SKBitmap? held = null;

        try
        {
            for (var i = 0; i < budgetFrames; i++)
            {
                var frame = frames[i];
                if (frame.Data.Count == 0) return null;
                using var region = SKBitmap.Decode(BuildSubPng(ihdr, ancillary, frame));
                if (region is null) return null;

                if (frame.Dispose == 2)
                {
                    held?.Dispose();
                    held = canvasBitmap.Copy();
                }

                var target = SKRect.Create(frame.X, frame.Y, frame.Width, frame.Height);
                if (frame.Blend == 0)
                {
                    // Blend "source": the region replaces what is under it, alpha included.
                    canvas.Save();
                    canvas.ClipRect(target);
                    canvas.Clear(SKColors.Transparent);
                    canvas.Restore();
                }

                canvas.DrawBitmap(region, target.Left, target.Top);
                canvas.Flush();
                output.Add(new ReencodedFrame(EncodePng(canvasBitmap), frame.DelayMs));

                // Dispose, for whoever composites on top next.
                if (frame.Dispose == 1)
                {
                    canvas.Save();
                    canvas.ClipRect(target);
                    canvas.Clear(SKColors.Transparent);
                    canvas.Restore();
                }
                else if (frame.Dispose == 2 && held is not null)
                {
                    canvas.DrawBitmap(held, 0, 0);
                    canvas.Flush();
                }
            }
        }
        finally
        {
            held?.Dispose();
        }

        return output;
    }

    private static int FctlDelay(byte[] fctl)
    {
        var numerator = BinaryPrimitives.ReadUInt16BigEndian(fctl.AsSpan(20, 2));
        var denominator = BinaryPrimitives.ReadUInt16BigEndian(fctl.AsSpan(22, 2));
        return NormalDelay((int)(numerator * 1000.0 / (denominator == 0 ? 100 : denominator)));
    }

    /// <summary>A standalone PNG for one frame's region, from the canvas's own header chunks.</summary>
    private static byte[] BuildSubPng(byte[] ihdr, List<(string Type, byte[] Data)> ancillary, ApngFrame frame)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var frameIhdr = (byte[])ihdr.Clone();
        BinaryPrimitives.WriteInt32BigEndian(frameIhdr.AsSpan(0, 4), frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(frameIhdr.AsSpan(4, 4), frame.Height);
        WriteChunk(png, "IHDR", frameIhdr);
        foreach (var (type, data) in ancillary) WriteChunk(png, type, data);
        foreach (var data in frame.Data) WriteChunk(png, "IDAT", data);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        stream.Write(crc);
    }

    // ------------------------------------------------------------------------------------
    // Small tools
    // ------------------------------------------------------------------------------------

    private static int IndexOfChunk(byte[] source, string type)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(type);
        var at = 8;
        while (at + 12 <= source.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(at, 4));
            if (length < 0 || at + 12 + length > source.Length) return -1;
            if (source.AsSpan(at + 4, 4).SequenceEqual(needle)) return at;
            if (source.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8)) return -1; // acTL must precede IDAT
            at += 12 + length;
        }

        return -1;
    }

    private static int Bounded(int frames, int width, int height)
    {
        var byBudget = (int)Math.Max(1, PixelBudget / Math.Max(1L, (long)width * height));
        return Math.Min(frames, Math.Min(FrameCap, byBudget));
    }

    private static int NormalDelay(int delayMs)
        => delayMs < MinimumDelayMs ? DefaultDelayMs : delayMs;

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) == 1 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
