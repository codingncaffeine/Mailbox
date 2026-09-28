namespace Mailbox.Core.Weather;

/// <summary>How a layer's pictures are drawn: as the service sends them, or reworked to sit on the map.</summary>
public enum MapLook
{
    AsServed,

    /// <summary>Faint where the rate is slight, so a drizzle over half a country does not paint it solid.</summary>
    Faded,

    /// <summary>The value as transparency, in one colour of the theme's: cloud cover as cloud.</summary>
    Tinted,

    /// <summary>Filled areas as their outlines over a light wash of their colour, the way warnings are drawn on radar.</summary>
    Outlined,

    /// <summary>Black-and-white line work in the map's own ink, with its halo where the white was.</summary>
    Inked,
}

/// <summary>The theme's colours the looks draw in, as 0xAARRGGBB: the map's ink and halo, and its cloud.</summary>
public readonly record struct MapInks(uint Ink, uint Halo, uint Cloud);

/// <summary>
/// The reworkings behind <see cref="MapLook"/>, on a picture's pixels as they are decoded: four
/// bytes each — blue, green, red, alpha — with the colour already multiplied by the alpha.
/// </summary>
public static class MapPixels
{
    /// <summary>How much of a fully covered sky the cloud colour takes.</summary>
    public const double CloudStrength = 0.85;

    /// <summary>How much of a warning's colour its inside keeps.</summary>
    public const double OutlineWash = 0.2;

    /// <summary>The rate from which precipitation is drawn at full strength, in millimetres an hour, and how faint its lowest is.</summary>
    public const double FadeFull = 1;

    public const double FadeFloor = 0.25;

    /// <summary>Reworks a picture of a layer by its look, for a picture asked for at <paramref name="pixelsPerViewPixel"/>.</summary>
    public static void Apply(Span<byte> pixels, int width, int height, MapLayer layer, MapInks inks, double pixelsPerViewPixel)
    {
        ArgumentNullException.ThrowIfNull(layer);
        switch (layer.Look)
        {
            case MapLook.Faded when layer.Legend is { } legend:
                Fade(pixels, legend, FadeFull, FadeFloor);
                break;
            case MapLook.Tinted when layer.Legend is { } legend:
                Tint(pixels, legend, inks.Cloud, CloudStrength);
                break;
            case MapLook.Outlined:
                Outline(pixels, width, height, Math.Max(1, (int)Math.Round(1.5 * pixelsPerViewPixel)), OutlineWash);
                break;
            case MapLook.Inked:
                Ink(pixels, inks.Ink, inks.Halo);
                break;
        }
    }

    /// <summary>Whether a look is drawn in the theme's colours, so its pictures are reworked when the theme changes.</summary>
    public static bool FollowsTheme(MapLook look) => look is MapLook.Tinted or MapLook.Inked;

    /// <summary>
    /// Fades a rate scale's slight end: full strength from <paramref name="full"/> up, falling
    /// along the logarithm of the rate to <paramref name="floor"/> of it at the scale's lowest.
    /// </summary>
    public static void Fade(Span<byte> pixels, MapLegend legend, double full, double floor)
    {
        ArgumentNullException.ThrowIfNull(legend);
        var lowest = legend.Stops[0].Value;
        if (lowest <= 0 || full <= lowest) return;
        var known = new Dictionary<int, double>();

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0) continue;
            var (r, g, b) = Unmultiplied(pixels, i);
            var key = (r << 16) | (g << 8) | b;
            if (!known.TryGetValue(key, out var strength))
            {
                strength = FadeStrength(legend.ValueOf(r, g, b) ?? full, lowest, full, floor);
                known[key] = strength;
            }

            if (strength < 1) Scale(pixels, i, strength);
        }
    }

    /// <summary>How strongly a rate is drawn under <see cref="Fade"/>, from <paramref name="floor"/> at the lowest to 1 at <paramref name="full"/>.</summary>
    public static double FadeStrength(double value, double lowest, double full, double floor)
    {
        if (lowest <= 0 || full <= lowest || value >= full) return 1;
        return floor + (1 - floor) * Math.Clamp(Math.Log(Math.Max(value, lowest) / lowest) / Math.Log(full / lowest), 0, 1);
    }

    /// <summary>
    /// Turns a scale's value into transparency: nothing at its lowest, <paramref name="strength"/>
    /// at its highest, all in <paramref name="tint"/> (0xAARRGGBB; its alpha is not used).
    /// </summary>
    public static void Tint(Span<byte> pixels, MapLegend legend, uint tint, double strength)
    {
        ArgumentNullException.ThrowIfNull(legend);
        var (lowest, highest) = (legend.Stops[0].Value, legend.Stops[^1].Value);
        if (highest <= lowest) return;
        var (tr, tg, tb) = ((byte)(tint >> 16), (byte)(tint >> 8), (byte)tint);
        var known = new Dictionary<int, double>();

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0) continue;
            var (r, g, b) = Unmultiplied(pixels, i);
            var key = (r << 16) | (g << 8) | b;
            if (!known.TryGetValue(key, out var share))
            {
                share = Math.Clamp(((legend.ValueOf(r, g, b) ?? lowest) - lowest) / (highest - lowest), 0, 1);
                known[key] = share;
            }

            var a = alpha / 255.0 * share * strength;
            Write(pixels, i, tr, tg, tb, a);
        }
    }

    /// <summary>Redraws line work in the map's colours: black becomes <paramref name="ink"/>, white <paramref name="halo"/>, greys between them.</summary>
    public static void Ink(Span<byte> pixels, uint ink, uint halo)
    {
        var (ir, ig, ib) = ((byte)(ink >> 16), (byte)(ink >> 8), (byte)ink);
        var (hr, hg, hb) = ((byte)(halo >> 16), (byte)(halo >> 8), (byte)halo);
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0) continue;
            var (r, g, b) = Unmultiplied(pixels, i);
            var light = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
            Write(pixels, i, Mix(ir, hr, light), Mix(ig, hg, light), Mix(ib, hb, light), alpha / 255.0);
        }
    }

    /// <summary>
    /// Redraws filled areas as their outlines — <paramref name="radius"/> pixels in from wherever
    /// one area meets another or the outside — at full strength, over a wash of
    /// <paramref name="wash"/> of their colour. The picture's own border is not an edge: an area
    /// running off the view carries on beyond it.
    /// </summary>
    /// <remarks>
    /// The Weather Service draws each forecast zone with a thin dark border, so a watch over twenty
    /// zones arrives as twenty areas. The picture is therefore rebuilt from its areas: a pixel whose
    /// whole neighbourhood is one colour belongs to an area of that colour, and every other pixel —
    /// the borders and the anti-aliasing along them — joins the nearest area, breadth first. A
    /// border inside a watch is taken into it from both sides and vanishes; one between two
    /// different warnings splits down its middle and becomes the edge between them. Pixels less
    /// than half covered are outside, and outside is left empty; covered pixels are never outside.
    /// </remarks>
    public static void Outline(Span<byte> pixels, int width, int height, int radius, double wash)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4) return;
        const int Tolerance = 24;
        const int Unassigned = int.MinValue;
        var count = width * height;

        var colour = new int[count];
        for (var p = 0; p < count; p++)
        {
            if (pixels[p * 4 + 3] < 128)
            {
                colour[p] = -1;
                continue;
            }

            var (r, g, b) = Unmultiplied(pixels, p * 4);
            colour[p] = (r << 16) | (g << 8) | b;
        }

        // The areas' cores: pixels whose neighbours within the picture are all their colour.
        var area = new int[count];
        Array.Fill(area, Unassigned);
        var queue = new int[count];
        var (head, tail) = (0, 0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = y * width + x;
                var core = true;
                for (var dy = Math.Max(0, y - 1); core && dy <= Math.Min(height - 1, y + 1); dy++)
                {
                    for (var dx = Math.Max(0, x - 1); dx <= Math.Min(width - 1, x + 1); dx++)
                    {
                        if (colour[dy * width + dx] == colour[p]) continue;
                        core = false;
                        break;
                    }
                }

                if (!core) continue;
                area[p] = colour[p];
                queue[tail++] = p;
            }
        }

        // Everything else joins the nearest area — the covered pixels an area, the others the
        // outside — so a zone border reaching the outer edge stays in its warning.
        while (head < tail)
        {
            var p = queue[head++];
            var (x, y) = (p % width, p / width);
            void Join(int q)
            {
                if (area[q] != Unassigned || (area[p] < 0) != (colour[q] < 0)) return;
                area[q] = area[p];
                queue[tail++] = q;
            }

            if (x > 0) Join(p - 1);
            if (x + 1 < width) Join(p + 1);
            if (y > 0) Join(p - width);
            if (y + 1 < height) Join(p + width);
        }

        bool Differ(int a, int b)
        {
            var (u, v) = (area[a], area[b]);
            if (u < 0 || v < 0) return u != v;
            return Math.Abs(((u >> 16) & 0xFF) - ((v >> 16) & 0xFF)) + Math.Abs(((u >> 8) & 0xFF) - ((v >> 8) & 0xFF)) + Math.Abs((u & 0xFF) - (v & 0xFF)) > Tolerance;
        }

        // Where one area meets another, the pixels on both sides are on an edge…
        var edge = new bool[count];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = y * width + x;
                if (x + 1 < width && Differ(p, p + 1)) edge[p] = edge[p + 1] = true;
                if (y + 1 < height && Differ(p, p + width)) edge[p] = edge[p + width] = true;
            }
        }

        // …and the edge is widened to the radius, across and then down.
        var reach = Math.Max(0, radius - 1);
        if (reach > 0)
        {
            var across = new bool[count];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!edge[y * width + x]) continue;
                    for (var dx = Math.Max(0, x - reach); dx <= Math.Min(width - 1, x + reach); dx++) across[y * width + dx] = true;
                }
            }

            Array.Clear(edge);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!across[y * width + x]) continue;
                    for (var dy = Math.Max(0, y - reach); dy <= Math.Min(height - 1, y + reach); dy++) edge[dy * width + x] = true;
                }
            }
        }

        for (var p = 0; p < count; p++)
        {
            var c = area[p] == Unassigned ? -1 : area[p];
            if (c < 0)
            {
                pixels.Slice(p * 4, 4).Clear();
                continue;
            }

            Write(pixels, p * 4, (byte)(c >> 16), (byte)(c >> 8), (byte)c, edge[p] ? 1 : wash);
        }
    }

    private static (byte R, byte G, byte B) Unmultiplied(ReadOnlySpan<byte> pixels, int i)
    {
        var alpha = pixels[i + 3];
        if (alpha == 255) return (pixels[i + 2], pixels[i + 1], pixels[i]);
        byte Un(byte v) => (byte)Math.Min(255, (v * 255 + alpha / 2) / alpha);
        return (Un(pixels[i + 2]), Un(pixels[i + 1]), Un(pixels[i]));
    }

    private static void Write(Span<byte> pixels, int i, byte r, byte g, byte b, double alpha)
    {
        var a = Math.Clamp(alpha, 0, 1);
        pixels[i] = (byte)Math.Round(b * a);
        pixels[i + 1] = (byte)Math.Round(g * a);
        pixels[i + 2] = (byte)Math.Round(r * a);
        pixels[i + 3] = (byte)Math.Round(255 * a);
    }

    private static void Scale(Span<byte> pixels, int i, double by)
    {
        for (var c = 0; c < 4; c++) pixels[i + c] = (byte)Math.Round(pixels[i + c] * by);
    }

    private static byte Mix(byte from, byte to, double at) => (byte)Math.Round(from + (to - from) * at);
}
