using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Theming.Palettes;

/// <summary>
/// One colour scheme a palette can be built from: sixteen slots in the base16 arrangement —
/// base00 the default ground, base01 the raised chrome, base05 the default ink, base08–0F the
/// accents — with its name and its author carried for the attribution the licence asks for.
/// </summary>
/// <param name="Id">A slug; also the tail of the theme file it becomes.</param>
/// <param name="Name">What the picker shows.</param>
/// <param name="Author">The scheme's author, verbatim from the source file.</param>
/// <param name="Dark">Whether the scheme is dark — stated by its file, else read off base00.</param>
/// <param name="Palette">base00 through base0F, as engine hex.</param>
public sealed record ColourScheme(
    string Id, string Name, string Author, bool Dark, IReadOnlyDictionary<string, string> Palette)
{
    public string Slot(string name) => Palette.TryGetValue(name, out var v) ? v : "#000000";
}

/// <summary>
/// Where schemes come from: the curated set vendored from tinted-theming/schemes (MIT, each
/// file carrying its author; the licence text ships beside them), the desktop's own KDE colour
/// scheme, and a scheme derived from an image's pixels.
/// </summary>
public static class ColourSchemes
{
    /// <summary>The curated set, in name order.</summary>
    public static IReadOnlyList<ColourScheme> Curated { get; } = LoadCurated();

    public static ColourScheme? Find(string id)
        => Curated.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    private static List<ColourScheme> LoadCurated()
    {
        var assembly = typeof(ColourSchemes).Assembly;
        var schemes = new List<ColourScheme>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Palettes.schemes.", StringComparison.Ordinal)
                                 && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            if (Parse(reader.ReadToEnd()) is { } scheme) schemes.Add(scheme);
        }

        return [.. schemes.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A scheme from its vendored JSON; null for a file that is not one.</summary>
    public static ColourScheme? Parse(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["palette"] is not JsonObject palette) return null;
        var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in palette)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var hex)) slots[key] = hex.ToUpperInvariant();
        }

        if (!slots.ContainsKey("base00") || !slots.ContainsKey("base05")) return null;

        var id = root["id"]?.GetValue<string>() ?? "scheme";
        var name = root["name"]?.GetValue<string>() ?? id;
        var author = root["author"]?.GetValue<string>() ?? "";
        var dark = root["variant"]?.GetValue<string>() is { } variant
            ? string.Equals(variant, "dark", StringComparison.OrdinalIgnoreCase)
            : Recolour.ReadsDark(slots["base00"]);

        return new ColourScheme(id, name, author, dark, slots);
    }

    // ------------------------------------------------------------------------------------
    // The desktop's colours
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The desktop's own scheme, read from KDE's <c>kdeglobals</c> grammar: the window's
    /// ground and ink, and the selection colour as the accent. Null when the text carries no
    /// window colours — another desktop, or an empty file.
    /// </summary>
    public static ColourScheme? FromKde(string iniText)
    {
        ArgumentNullException.ThrowIfNull(iniText);
        string? section = null;
        var colours = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in iniText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Trim('[', ']'); continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0 || section is null || !section.StartsWith("Colors:", StringComparison.OrdinalIgnoreCase)) continue;

            var key = $"{section}/{line[..eq]}";
            var parts = line[(eq + 1)..].Split(',');
            if (parts.Length >= 3
                && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
            {
                colours[key] = $"#{r:X2}{g:X2}{b:X2}";
            }
        }

        if (!colours.TryGetValue("Colors:Window/BackgroundNormal", out var ground)) return null;
        colours.TryGetValue("Colors:Window/ForegroundNormal", out var ink);
        colours.TryGetValue("Colors:Selection/BackgroundNormal", out var accent);

        return new ColourScheme("desktop", "Desktop colors", "the desktop's color scheme",
            Recolour.ReadsDark(ground),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["base00"] = ground,
                ["base01"] = ground,
                ["base05"] = ink ?? "#000000",
                ["base0D"] = accent ?? ground,
            });
    }

    // ------------------------------------------------------------------------------------
    // A scheme from an image
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// A scheme from an image's pixels: the dominant colour is the ground, the most saturated
    /// distinct cluster is the accent. A small k-means of its own — the point is a palette
    /// that visibly belongs to the picture, not colour science.
    /// </summary>
    public static ColourScheme FromPixels(IReadOnlyList<(byte R, byte G, byte B)> pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Count == 0)
        {
            return new ColourScheme("your-image", "From your image", "your image", Dark: false,
                new Dictionary<string, string> { ["base00"] = "#FFFFFF", ["base05"] = "#000000" });
        }

        // Eight clusters, a dozen rounds, seeded across the pixel list.
        var centroids = Enumerable.Range(0, 8)
            .Select(i => pixels[(int)((long)i * (pixels.Count - 1) / 7)])
            .Select(p => (R: (double)p.R, G: (double)p.G, B: (double)p.B))
            .ToArray();
        var counts = new int[centroids.Length];

        for (var round = 0; round < 12; round++)
        {
            var sums = new (double R, double G, double B, int N)[centroids.Length];
            foreach (var p in pixels)
            {
                var best = 0;
                var bestDistance = double.MaxValue;
                for (var c = 0; c < centroids.Length; c++)
                {
                    var d = Sq(p.R - centroids[c].R) + Sq(p.G - centroids[c].G) + Sq(p.B - centroids[c].B);
                    if (d < bestDistance) { bestDistance = d; best = c; }
                }

                sums[best] = (sums[best].R + p.R, sums[best].G + p.G, sums[best].B + p.B, sums[best].N + 1);
            }

            for (var c = 0; c < centroids.Length; c++)
            {
                counts[c] = sums[c].N;
                if (sums[c].N > 0) centroids[c] = (sums[c].R / sums[c].N, sums[c].G / sums[c].N, sums[c].B / sums[c].N);
            }
        }

        static double Sq(double v) => v * v;
        string Hex(int c) => $"#{(int)Math.Round(centroids[c].R):X2}{(int)Math.Round(centroids[c].G):X2}{(int)Math.Round(centroids[c].B):X2}";

        var dominant = Array.IndexOf(counts, counts.Max());
        var accent = Enumerable.Range(0, centroids.Length)
            .Where(c => counts[c] > pixels.Count / 100)
            .OrderByDescending(c => Oklch.Parse(Hex(c))?.C ?? 0)
            .First();

        var ground = Hex(dominant);
        var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["base00"] = ground,
            ["base01"] = ground,
        };
        if ((Oklch.Parse(Hex(accent))?.C ?? 0) > 0.04) slots["base0D"] = Hex(accent);

        return new ColourScheme("your-image", "From your image", "your image", Recolour.ReadsDark(ground), slots);
    }
}
