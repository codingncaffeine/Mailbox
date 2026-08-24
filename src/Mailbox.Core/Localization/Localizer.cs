using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailbox.Core.Localization;

/// <summary>
/// §16's internationalization scaffolding: the lookup every surface will eventually call, built
/// so adoption is incremental and absence is harmless.
/// </summary>
/// <remarks>
/// Keys are the English strings themselves, which is the property that makes gradual adoption
/// safe: an untranslated key, an unadopted surface and a missing locale file all render exactly
/// what they render today. A locale is one flat JSON file of "English": "translation" pairs in
/// <c>assets/locales/&lt;culture&gt;.json</c>, resolved parent-first — <c>de-AT</c> reads
/// <c>de-AT.json</c> over <c>de.json</c> over nothing.
/// <para>
/// The culture ships as <c>en-US</c>, the reference's own; the deliberate two-Englishes mix in
/// the UI (Favourites beside Categorize) is a recorded decision still owed to the owner, and a
/// locale file is now the mechanism that will carry whichever way it goes.
/// </para>
/// </remarks>
public sealed class Localizer
{
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    /// <summary>The culture the strings were loaded for.</summary>
    public string Culture { get; private set; } = "en-US";

    /// <summary>An empty localizer: every lookup answers its own key. The shipped default.</summary>
    public static Localizer Passthrough { get; } = new();

    /// <summary>
    /// Loads a culture from a locales directory, parent first so the child's entries win.
    /// Missing files are the ordinary case, not an error.
    /// </summary>
    public static Localizer Load(string directory, string culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);

        var localizer = new Localizer { Culture = culture };
        var dash = culture.IndexOf('-');

        foreach (var candidate in dash > 0 ? new[] { culture[..dash], culture } : [culture])
        {
            var path = Path.Combine(directory, candidate + ".json");
            if (!File.Exists(path)) continue;

            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject entries) continue;
                foreach (var (key, value) in entries)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
                    {
                        localizer._strings[key] = text;
                    }
                }
            }
            catch (JsonException)
            {
                // A locale file that will not parse costs its translations and nothing else.
            }
        }

        return localizer;
    }

    /// <summary>The translation, or the English itself — absence is always harmless.</summary>
    public string T(string english)
        => english.Length > 0 && _strings.TryGetValue(english, out var translated) ? translated : english;

    /// <summary>How many strings this culture carries, for the log line that says so.</summary>
    public int Count => _strings.Count;
}
