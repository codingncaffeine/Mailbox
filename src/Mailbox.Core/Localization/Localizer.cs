using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailbox.Core.Localization;

/// <summary>
/// The lookup every surface calls, built so adoption is incremental and absence is harmless.
/// </summary>
/// <remarks>
/// Keys are the English strings themselves, which is the property that makes gradual adoption
/// safe: an untranslated string, an unadopted surface and a missing catalogue all render exactly
/// what they render today. That is also <c>msgid</c>'s own meaning, which is why the catalogues
/// are gettext's <c>.po</c> — see <see cref="PoCatalog"/> for why a format nobody else speaks
/// would have been the wrong kind of in-house.
/// <para>
/// Three things a flat table of English-to-translation cannot do, and all three are the difference
/// between a translated interface and an embarrassing one:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Plurals.</b> "1 message" and "5 messages" are two forms in English, one in Japanese and
/// three in Polish. <see cref="Plural"/> asks the language's own rule, which arrives in the
/// catalogue rather than being decided here.
/// </description></item>
/// <item><description>
/// <b>Context.</b> English "Open" is a verb on a button and a noun in a status; a single key
/// forces one translation onto both. <see cref="T(string, string)"/> takes the disambiguation
/// gettext calls <c>msgctxt</c>.
/// </description></item>
/// <item><description>
/// <b>Falling back by language.</b> <c>de-AT</c> reads its own catalogue over <c>de</c>'s, so a
/// regional translation only has to carry what it changes.
/// </description></item>
/// </list>
/// <para>
/// The culture ships as <c>en-US</c>, the reference's own; the two-Englishes mix in the interface
/// (Favourites beside Categorize) is a recorded decision still owed to the owner, and a catalogue
/// is the mechanism that will carry whichever way it goes.
/// </para>
/// </remarks>
public sealed class Localizer
{
    private readonly Dictionary<string, PoEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>The culture the strings were loaded for.</summary>
    public string Culture { get; private set; } = "en-US";

    /// <summary>Which plural form a count takes in this language.</summary>
    public PluralRule Plurals { get; private set; } = PluralRule.English;

    /// <summary>An empty localizer: every lookup answers its own key. The shipped default.</summary>
    public static Localizer Passthrough { get; } = new();

    /// <summary>
    /// Loads a culture from a locales directory, parent first so the child's entries win.
    /// Missing files are the ordinary case, not an error.
    /// </summary>
    /// <remarks>
    /// Both shapes are read: <c>&lt;culture&gt;.po</c>, which is what a translator's tool writes
    /// and what everything new should be, and <c>&lt;culture&gt;.json</c>, the flat table this
    /// started as — kept so a catalogue written against the older shape is not silently dropped.
    /// The <c>.po</c> is read second and wins, being the one with plurals and context in it.
    /// </remarks>
    public static Localizer Load(string directory, string culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);

        var localizer = new Localizer { Culture = culture };
        var dash = culture.IndexOf('-', StringComparison.Ordinal);

        foreach (var candidate in dash > 0 ? new[] { culture[..dash], culture } : [culture])
        {
            localizer.ReadJson(Path.Combine(directory, candidate + ".json"));
            localizer.ReadPo(Path.Combine(directory, candidate + ".po"));
        }

        return localizer;
    }

    private void ReadPo(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            using var reader = new StreamReader(path);
            var (entries, header) = PoCatalog.Read(reader);

            foreach (var (key, entry) in entries) _entries[key] = entry;

            // The rule travels with the translations it governs: a catalogue that carries plural
            // forms without a header for them would pick between them by English's rule.
            if (Header(header, "Plural-Forms") is { Length: > 0 } forms)
            {
                Plurals = PluralRule.Read(forms);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A catalogue that cannot be read costs its translations and nothing else.
        }
    }

    private void ReadJson(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject entries) return;
            foreach (var (key, value) in entries)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
                {
                    _entries[key] = new PoEntry([text]);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // As above: one unreadable file, not one broken application.
        }
    }

    /// <summary>One field out of a catalogue's header block.</summary>
    private static string? Header(string? header, string field)
    {
        if (header is null) return null;

        foreach (var line in header.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            if (string.Equals(line[..colon].Trim(), field, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>The translation, or the English itself — absence is always harmless.</summary>
    public string T(string english)
        => english.Length > 0 && _entries.TryGetValue(english, out var entry)
            ? entry.Form(0)
            : english;

    /// <summary>
    /// The translation of a string that means two things, told apart by a context.
    /// </summary>
    /// <param name="context">
    /// What this use of the words means — "the Open button", "a folder's state". Never shown; it
    /// exists so a translator can give one English word two translations, and so they can see
    /// which is which.
    /// </param>
    public string T(string context, string english)
    {
        if (english.Length == 0) return english;

        // The contextual entry, then the plain one: a string somebody translated before anybody
        // thought it needed a context still applies.
        if (_entries.TryGetValue(PoCatalog.Key(context, english), out var entry)) return entry.Form(0);
        return _entries.TryGetValue(english, out var plain) ? plain.Form(0) : english;
    }

    /// <summary>
    /// The translation for a count, in whichever form this language uses for that number.
    /// </summary>
    /// <remarks>
    /// The English singular and plural are both passed because they are both keys: gettext's
    /// <c>msgid</c> and <c>msgid_plural</c>, and the pair is what a translator's tool shows
    /// together. With no translation the English is chosen by English's own rule, which is what
    /// the interface does today.
    /// </remarks>
    /// <param name="count">How many, which chooses the form rather than being formatted in.</param>
    public string Plural(string english, string englishPlural, long count, string? context = null)
    {
        if (_entries.TryGetValue(PoCatalog.Key(context, english), out var entry)
            || (context is not null && _entries.TryGetValue(english, out entry)))
        {
            return entry.Form(Plurals.Form(count));
        }

        return count == 1 ? english : englishPlural;
    }

    /// <summary>
    /// The translation for a count with the number put into it, in this culture's own digits and
    /// grouping.
    /// </summary>
    /// <remarks>
    /// The formatting belongs here rather than at the call site: a count written with the
    /// invariant culture's separators inside a translated sentence is the small wrongness that
    /// makes a translation look machine-made. <c>{0}</c> in the string is where it goes, and it
    /// arrives already written out — grouped by this culture's own rules, since "1234 messages"
    /// reads wrong in every language including English, and a translator should not have to know
    /// a format specifier to get a thousands separator.
    /// <para>
    /// A translation whose placeholders are malformed — <c>{0</c>, or a <c>{1}</c> that was never
    /// passed — falls back to the English rather than throwing. The promise everywhere else here
    /// is that a bad catalogue costs its own strings; a surface taken down by somebody's typo
    /// would break it.
    /// </para>
    /// </remarks>
    public string Counted(string english, string englishPlural, long count, string? context = null)
    {
        var culture = CultureFor();
        var written = count.ToString("N0", culture);
        var text = Plural(english, englishPlural, count, context);

        try
        {
            return string.Format(culture, text, written);
        }
        catch (FormatException)
        {
            var fallback = count == 1 ? english : englishPlural;
            try
            {
                return string.Format(culture, fallback, written);
            }
            catch (FormatException)
            {
                return fallback;
            }
        }
    }

    /// <summary>The culture the strings are in, for formatting numbers and dates inside them.</summary>
    private CultureInfo CultureFor()
    {
        try
        {
            return CultureInfo.GetCultureInfo(Culture);
        }
        catch (CultureNotFoundException)
        {
            // A catalogue named for something the runtime has never heard of still translates;
            // only its number formatting falls back.
            return CultureInfo.CurrentCulture;
        }
    }

    /// <summary>How many strings this culture carries, for the log line that says so.</summary>
    public int Count => _entries.Count;
}
