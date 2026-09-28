using Avalonia.Platform;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Weather;

/// <summary>
/// The Weather map's own base map: land, lakes, borders, states and provinces, US counties, major
/// highways and places, read from the file <c>tools/weather-basemap.mjs</c> builds.
/// </summary>
/// <remarks>
/// Coordinates are Web Mercator on [0, 1), stored as integers in <see cref="Quantum"/> steps per
/// world, which is finer than any zoom the map goes to. Each layer carries up to two levels of
/// detail, the coarse one for the whole world and the fine one from a regional zoom on, so a
/// continent is never drawn from the county-level data or a county from the continent-level.
/// <para>
/// Read once, on the pool, the first time a map asks: the file is a few megabytes of varints and
/// a single pass decodes it.
/// </para>
/// </remarks>
internal sealed class BaseMap
{
    public const double Quantum = 1 << 26;

    /// <summary>One feature: its parts' points, interleaved x then y, and its bounds.</summary>
    public sealed class Shape(int[] points, int[] partStarts, int minX, int minY, int maxX, int maxY)
    {
        public int[] Points { get; } = points;
        public int[] PartStarts { get; } = partStarts;
        public int MinX { get; } = minX;
        public int MinY { get; } = minY;
        public int MaxX { get; } = maxX;
        public int MaxY { get; } = maxY;

        public int PartLength(int part) => ((part + 1 < PartStarts.Length ? PartStarts[part + 1] : Points.Length) - PartStarts[part]) / 2;
    }

    public sealed record Detail(float MinZoom, Shape[] Shapes);

    public sealed record Layer(string Name, int Kind, Detail[] Details)
    {
        /// <summary>The level of detail for a zoom, or null below the layer's first one.</summary>
        public Detail? For(double zoom)
        {
            Detail? chosen = null;
            foreach (var detail in Details)
            {
                if (zoom >= detail.MinZoom) chosen = detail;
            }

            return chosen;
        }
    }

    /// <summary>A named place and the zoom from which its name is worth writing.</summary>
    public sealed record Place(int X, int Y, float MinZoom, string Name);

    private BaseMap(Dictionary<string, Layer> layers, Place[] places)
    {
        Layers = layers;
        Places = places;
    }

    public IReadOnlyDictionary<string, Layer> Layers { get; }

    /// <summary>Every place, the most important first.</summary>
    public IReadOnlyList<Place> Places { get; }

    private static readonly Lazy<Task<BaseMap?>> Loaded = new(() => Task.Run(Load));

    /// <summary>The base map, read on the pool the first time it is asked for; null when it cannot be read.</summary>
    public static Task<BaseMap?> LoadAsync() => Loaded.Value;

    private static BaseMap? Load()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://mailbox/Assets/Weather/map/basemap.bin"));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Decode(memory.GetBuffer().AsSpan(0, (int)memory.Length));
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            Log.Warn("The weather map's base map could not be read.", ex);
            return null;
        }
    }

    internal static BaseMap Decode(ReadOnlySpan<byte> data)
    {
        var at = 0;
        if (data.Length < 9 || !data[..8].SequenceEqual("MBXMAP01"u8)) throw new InvalidDataException("Not a weather base map.");
        at = 8;

        byte U8(ReadOnlySpan<byte> d) => d[at++];
        long VarUInt(ReadOnlySpan<byte> d)
        {
            long value = 0;
            var shift = 0;
            while (true)
            {
                var b = d[at++];
                value |= (long)(b & 0x7F) << shift;
                if (b < 0x80) return value;
                shift += 7;
            }
        }

        long VarInt(ReadOnlySpan<byte> d)
        {
            var z = VarUInt(d);
            return (z & 1) == 0 ? z >> 1 : -((z + 1) >> 1);
        }

        float F32(ReadOnlySpan<byte> d)
        {
            var value = BitConverter.ToSingle(d.Slice(at, 4));
            at += 4;
            return value;
        }

        string Text(ReadOnlySpan<byte> d, bool isLong)
        {
            var length = isLong ? (int)VarUInt(d) : U8(d);
            var text = System.Text.Encoding.UTF8.GetString(d.Slice(at, length));
            at += length;
            return text;
        }

        var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
        Place[] places = [];
        var count = U8(data);
        for (var l = 0; l < count; l++)
        {
            var name = Text(data, isLong: false);
            var kind = U8(data);
            var lods = U8(data);

            if (kind == 2)
            {
                F32(data);
                var n = (int)VarUInt(data);
                places = new Place[n];
                for (var i = 0; i < n; i++)
                {
                    var x = (int)VarUInt(data);
                    var y = (int)VarUInt(data);
                    var zoom = F32(data);
                    places[i] = new Place(x, y, zoom, Text(data, isLong: true));
                }

                continue;
            }

            var details = new Detail[lods];
            for (var d = 0; d < lods; d++)
            {
                var minZoom = F32(data);
                var features = (int)VarUInt(data);
                var shapes = new Shape[features];
                for (var f = 0; f < features; f++)
                {
                    var parts = (int)VarUInt(data);
                    var starts = new int[parts];
                    var points = new List<int>();
                    int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                    for (var p = 0; p < parts; p++)
                    {
                        starts[p] = points.Count;
                        var length = (int)VarUInt(data);
                        long x = VarUInt(data), y = VarUInt(data);
                        for (var i = 0; i < length; i++)
                        {
                            if (i > 0)
                            {
                                x += VarInt(data);
                                y += VarInt(data);
                            }

                            points.Add((int)x);
                            points.Add((int)y);
                            minX = Math.Min(minX, (int)x);
                            maxX = Math.Max(maxX, (int)x);
                            minY = Math.Min(minY, (int)y);
                            maxY = Math.Max(maxY, (int)y);
                        }
                    }

                    shapes[f] = new Shape([.. points], starts, minX, minY, maxX, maxY);
                }

                details[d] = new Detail(minZoom, shapes);
            }

            layers[name] = new Layer(name, kind, details);
        }

        return new BaseMap(layers, places);
    }
}
