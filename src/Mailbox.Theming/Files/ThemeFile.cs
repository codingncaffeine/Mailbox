using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mailbox.Theming.Tokens;

namespace Mailbox.Theming.Files;

/// <summary>
/// A theme as a file a reader can write: an id, a name, the theme it starts from, whether it is
/// dark, and the tokens it sets — as many or as few as it likes.
/// </summary>
/// <param name="Id">A slug: letters, digits, hyphens. What <c>MAILBOX_THEME</c> and the settings file name it by.</param>
/// <param name="Name">What the theme picker shows.</param>
/// <param name="Base">The theme this one starts from — a built-in's id or another file's — or null to start from nothing, which only a complete file can do.</param>
/// <param name="IsDark">Whether the theme is dark, for the surfaces that ask; null inherits the base's answer.</param>
/// <param name="Tokens">The tokens the file sets, in any of the three layers, references and all.</param>
/// <param name="Path">Where it was read from, for the log; empty for a theme built in memory.</param>
public sealed record ThemeFile(string Id, string Name, string? Base, bool? IsDark, TokenSet Tokens, string Path = "");

/// <summary>Thrown for a file that is not a theme: not JSON, no id, an id that is not a slug, a token that is not a string.</summary>
public sealed class ThemeFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The on-disk format, both ways.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "id": "midnight",
///   "name": "Midnight",
///   "base": "black",
///   "dark": true,
///   "tokens": {
///     "palette.brand.primary": "#4FA3E0",
///     "accent.rest": "{palette.brand.primary}"
///   }
/// }
/// </code>
/// Progressive disclosure is the point of <c>base</c>: a file that sets three palette entries
/// and nothing else is a complete theme, because everything the base derives from those entries
/// follows them (§8). A file with no base has to say everything the coverage gate requires, and
/// exporting a built-in is how to get one of those to start from. Keys are the engine's own
/// (<c>ribbon.background</c>, <c>text.primary</c>…); a key the engine does not know is kept and
/// ignored rather than refused, so a file written for a later version still loads here.
/// </remarks>
public static partial class ThemeFileFormat
{
    /// <summary>What a theme file's name ends in, so a directory of them is unmistakable.</summary>
    public const string Extension = ".mailbox-theme.json";

    public static ThemeFile Parse(string json, string path = "")
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new ThemeFileException($"{Describe(path)} is not JSON: {ex.Message}", ex);
        }

        if (root is not JsonObject obj) throw new ThemeFileException($"{Describe(path)} is not a JSON object.");

        var id = obj["id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(id)) throw new ThemeFileException($"{Describe(path)} has no \"id\".");
        if (!SlugPattern().IsMatch(id)) throw new ThemeFileException($"{Describe(path)}: the id \"{id}\" must be letters, digits and hyphens.");

        var name = obj["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(name)) name = id;

        var baseId = obj["base"]?.GetValue<string>()?.Trim();
        if (baseId is { Length: 0 }) baseId = null;

        bool? dark = obj["dark"] is { } d ? d.GetValue<bool>() : null;

        var tokens = new TokenSet();
        if (obj["tokens"] is JsonObject set)
        {
            foreach (var (key, value) in set)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                var text = value switch
                {
                    null => throw new ThemeFileException($"{Describe(path)}: token \"{key}\" is null."),
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonValue v when v.TryGetValue<double>(out var n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
                    _ => throw new ThemeFileException($"{Describe(path)}: token \"{key}\" must be a string."),
                };
                tokens.Set(key.Trim(), text);
            }
        }
        else if (obj["tokens"] is not null)
        {
            throw new ThemeFileException($"{Describe(path)}: \"tokens\" must be an object of key: value.");
        }

        return new ThemeFile(id, name, baseId, dark, tokens, path);
    }

    /// <summary>The file for a theme, indented, its tokens grouped by layer and sorted within each.</summary>
    public static string Write(ThemeFile theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var tokens = new JsonObject();
        foreach (var layer in new[] { TokenLayer.Primitive, TokenLayer.Semantic, TokenLayer.Component })
        {
            foreach (var key in theme.Tokens.KeysInLayer(layer).OrderBy(k => k, StringComparer.Ordinal))
            {
                tokens[key] = theme.Tokens[key];
            }
        }

        var root = new JsonObject
        {
            ["id"] = theme.Id,
            ["name"] = theme.Name,
        };
        if (theme.Base is not null) root["base"] = theme.Base;
        if (theme.IsDark is { } dark) root["dark"] = dark;
        root["tokens"] = tokens;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    private static string Describe(string path) => path.Length == 0 ? "The theme" : $"\"{path}\"";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*$")]
    private static partial Regex SlugPattern();
}
