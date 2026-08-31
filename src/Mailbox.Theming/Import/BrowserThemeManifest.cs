using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailbox.Theming.Import;

/// <summary>
/// A browser theme after parsing: the colours as engine hex, the images by their in-package
/// paths, the properties that place them, and the honest record of what was seen and not used.
/// </summary>
public sealed record BrowserTheme(
    string Name,
    string Version,
    string SourceId,
    IReadOnlyDictionary<string, string> Colours,
    IReadOnlyDictionary<string, string> DarkColours,
    string? FrameImage,
    IReadOnlyList<string> AdditionalBackgrounds,
    string Alignment,
    string Tiling,
    string? ColorScheme,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Reads a Firefox static theme's <c>manifest.json</c>: <c>theme.colors</c> as strings or
/// integer arrays, <c>theme.images</c>, <c>theme.properties</c>, the optional
/// <c>dark_theme</c>, and the three legacy LWT aliases a lot of old themes are nothing but —
/// <c>accentcolor</c> for <c>frame</c>, <c>textcolor</c> for <c>tab_background_text</c>,
/// <c>headerURL</c> for the frame image. Whatever cannot be used is recorded by name in
/// <see cref="BrowserTheme.Skipped"/> rather than dropped silently: the summary a reader gets
/// is only as honest as this list.
/// </summary>
public static class BrowserThemeManifest
{
    public static BrowserTheme Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new BrowserThemeException("The manifest is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new BrowserThemeException($"The manifest is not JSON: {ex.Message}", ex);
        }

        if (root["theme"] is not JsonObject && root["dark_theme"] is not JsonObject)
        {
            throw new BrowserThemeException("The manifest has no \"theme\" object — this is an extension, not a theme.");
        }

        var skipped = new List<string>();
        if (root["theme_experiment"] is not null) skipped.Add("theme_experiment (Nightly-only, meaningless outside the browser)");

        var name = Text(root["name"]) ?? "Imported theme";
        var version = Text(root["version"]) ?? "0";
        var sourceId = Text(root["browser_specific_settings"]?["gecko"]?["id"])
                       ?? Text(root["applications"]?["gecko"]?["id"])
                       ?? $"{name}-{version}";

        var theme = root["theme"] as JsonObject;
        var dark = root["dark_theme"] as JsonObject;

        var colours = Colours(theme, skipped);
        var darkColours = Colours(dark, skipped);

        var (frameImage, additional) = Images(theme ?? dark, skipped);
        var (alignment, tiling, scheme) = Properties(theme ?? dark);

        return new BrowserTheme(name, version, Sanitise(sourceId), colours, darkColours,
            frameImage, additional, alignment, tiling, scheme, skipped);
    }

    private static Dictionary<string, string> Colours(JsonObject? theme, List<string> skipped)
    {
        var colours = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (theme?["colors"] is not JsonObject declared) return colours;

        foreach (var (rawKey, value) in declared)
        {
            // The alias chain: what an old theme calls it, read as what it means.
            var key = rawKey.ToLowerInvariant() switch
            {
                "accentcolor" => "frame",
                "textcolor" => "tab_background_text",
                "toolbar_text" => "bookmark_text",
                var other => other,
            };

            var parsed = value switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => CssColour.Parse(s),
                JsonArray array when array.All(n => n is JsonValue) =>
                    CssColour.Parse(array.Select(n => n!.GetValue<double>()).ToList()),
                JsonObject => null, // a gradient object under a colour key — not a colour
                _ => null,
            };

            if (parsed is null)
            {
                skipped.Add($"colors.{rawKey} (\"{Describe(value)}\" is not a colour this reads)");
                continue;
            }

            colours.TryAdd(key, parsed);
        }

        return colours;
    }

    private static (string? Frame, IReadOnlyList<string> Additional) Images(JsonObject? theme, List<string> skipped)
    {
        string? frame = null;
        var additional = new List<string>();
        if (theme?["images"] is not JsonObject images)
        {
            return (frame, additional);
        }

        foreach (var (rawKey, value) in images)
        {
            var key = rawKey.Equals("headerURL", StringComparison.OrdinalIgnoreCase) ? "theme_frame" : rawKey.ToLowerInvariant();
            switch (key)
            {
                case "theme_frame" when value is JsonValue v && v.TryGetValue<string>(out var path):
                    frame = path;
                    break;
                case "theme_frame":
                    skipped.Add("images.theme_frame (a gradient object; a later version may sample it)");
                    break;
                case "additional_backgrounds" when value is JsonArray array:
                    foreach (var entry in array)
                    {
                        if (entry is JsonValue e && e.TryGetValue<string>(out var path2)) additional.Add(path2);
                        else skipped.Add("images.additional_backgrounds (a gradient entry; a later version may sample it)");
                    }

                    break;
                default:
                    skipped.Add($"images.{rawKey}");
                    break;
            }
        }

        return (frame, additional);
    }

    private static (string Alignment, string Tiling, string? Scheme) Properties(JsonObject? theme)
    {
        var properties = theme?["properties"] as JsonObject;

        // Arrays cycle against the image list; the first entry is the header's own.
        static string? First(JsonNode? node) => node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray { Count: > 0 } array when array[0] is JsonValue v && v.TryGetValue<string>(out var s) => s,
            _ => null,
        };

        return (
            First(properties?["additional_backgrounds_alignment"]) ?? "right top",
            First(properties?["additional_backgrounds_tiling"]) ?? "no-repeat",
            Text(properties?["color_scheme"])?.ToLowerInvariant());
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    private static string Describe(JsonNode? value)
    {
        var text = value?.ToJsonString() ?? "null";
        return text.Length > 40 ? text[..40] + "…" : text;
    }

    /// <summary>
    /// Provenance lands in token values, where a brace would read as a reference — a gecko id
    /// is very often <c>{guid}</c>-shaped — so braces go, and the value stays inert.
    /// </summary>
    private static string Sanitise(string value)
        => value.Replace("{", "").Replace("}", "");
}
