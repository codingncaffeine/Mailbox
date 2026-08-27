using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Feeds;

/// <summary>What a mute filter is allowed to act on.</summary>
public enum MuteScope
{
    /// <summary>Every feed.</summary>
    Everywhere,

    /// <summary>Every feed filed under one heading.</summary>
    Heading,

    /// <summary>One feed.</summary>
    Feed,
}

/// <summary>
/// One rule for articles a reader does not want to see.
/// </summary>
/// <param name="Text">The word or phrase, or the pattern when <paramref name="IsRegex"/>.</param>
/// <param name="Target">
/// The heading or the feed address the scope names; empty for <see cref="MuteScope.Everywhere"/>.
/// </param>
/// <param name="ExpiresUtc">
/// When the filter stops applying, or null for one that does not. A story that will be over in a
/// week is the ordinary case for muting, and a permanent rule for it is one the reader has to
/// remember to come back and delete.
/// </param>
public sealed record MuteFilter(
    string Text,
    MuteScope Scope = MuteScope.Everywhere,
    string Target = "",
    bool TitleOnly = false,
    bool IsRegex = false,
    DateTimeOffset? ExpiresUtc = null)
{
    /// <summary>How many articles this filter has kept out. Shown so a reader can judge it.</summary>
    public int Muted { get; init; }

    /// <summary>Whether the filter still applies at the given moment.</summary>
    public bool IsLive(DateTimeOffset now) => ExpiresUtc is not { } expires || expires > now;

    /// <summary>What the filter covers, in a phrase.</summary>
    public string Where => Scope switch
    {
        MuteScope.Heading => $"in {Target}",
        MuteScope.Feed => $"in one feed",
        _ => "everywhere",
    };

    /// <summary>How long is left, in a phrase.</summary>
    public string Until(DateTimeOffset now) => ExpiresUtc switch
    {
        null => "forever",
        { } expires when expires <= now => "expired",
        { } expires when expires - now < TimeSpan.FromDays(1) => "today",
        { } expires => $"until {expires.ToLocalTime():d MMM}",
    };
}

/// <summary>
/// The articles a reader has asked not to be shown.
/// </summary>
/// <remarks>
/// A subscription is to a publication, not to everything a publication says, and the gap between
/// those two is what makes people abandon feed readers: one story they do not care about runs for
/// three weeks and every feed they have is full of it. Feedly charges for this. It costs us
/// nothing, because a keyword match over text we already hold needs no service and no model.
/// <para>
/// <b>What is muted is never filed.</b> Not filed and hidden, not filed and marked read —
/// a muted article does not arrive, so it costs no space and turns up in no count and no search.
/// The consequence, which the dialog says out loud, is that a filter added today does not clear
/// out what came in yesterday; removing those is a separate thing a reader asks for explicitly,
/// because it deletes messages.
/// </para>
/// <para>
/// <b>The semantic half is not here and cannot be.</b> Feedly's version can mute "articles about
/// layoffs" without being given a word, because it ships a thousand trained topic models. This
/// matches words and patterns, and says so, rather than pretending to understand.
/// </para>
/// </remarks>
public sealed class MuteFilters
{
    public const string Key = "rss.mutes";

    /// <summary>How long a pattern is given before it is treated as a runaway.</summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(50);

    private readonly SettingsStore _settings;
    private readonly List<MuteFilter> _filters = [];
    private readonly Dictionary<string, Regex?> _compiled = new(StringComparer.Ordinal);

    public MuteFilters(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        if (settings.Has(Key)) _filters.AddRange(Parse(settings.GetString(Key)));
    }

    /// <summary>Raised after the list changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<MuteFilter> All => _filters;

    /// <summary>The filters that still apply, which is what a delivery consults.</summary>
    public IReadOnlyList<MuteFilter> Live(DateTimeOffset now) => [.. _filters.Where(f => f.IsLive(now))];

    public MuteFilter Add(MuteFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Text.Trim().Length == 0) throw new ArgumentException("A filter needs a word to match.", nameof(filter));

        var trimmed = filter with { Text = filter.Text.Trim() };
        _filters.Add(trimmed);
        Save();
        return trimmed;
    }

    public bool Remove(MuteFilter filter)
    {
        var removed = _filters.RemoveAll(f =>
            f.Text == filter.Text && f.Scope == filter.Scope && f.Target == filter.Target) > 0;

        if (removed) Save();
        return removed;
    }

    /// <summary>Drops the ones whose time is up, so the list stays what the reader meant.</summary>
    public int Expire(DateTimeOffset now)
    {
        var gone = _filters.RemoveAll(f => !f.IsLive(now));
        if (gone > 0) Save();
        return gone;
    }

    /// <summary>
    /// Whether an article should be kept out, and by which filter.
    /// </summary>
    /// <param name="title">The headline.</param>
    /// <param name="body">The rest of what the entry says, as plain text.</param>
    /// <param name="heading">The heading the feed is filed under, or empty.</param>
    /// <param name="feedUrl">The feed's address.</param>
    public MuteFilter? Matching(string title, string body, string heading, string feedUrl, DateTimeOffset now)
    {
        foreach (var filter in _filters)
        {
            if (!filter.IsLive(now)) continue;
            if (!Covers(filter, heading, feedUrl)) continue;

            if (Hits(filter, title)) return filter;
            if (!filter.TitleOnly && Hits(filter, body)) return filter;
        }

        return null;
    }

    /// <summary>Records that a filter kept something out, for the count the dashboard shows.</summary>
    public void Counted(MuteFilter filter, int by = 1)
    {
        var at = _filters.FindIndex(f =>
            f.Text == filter.Text && f.Scope == filter.Scope && f.Target == filter.Target);
        if (at < 0) return;

        _filters[at] = _filters[at] with { Muted = _filters[at].Muted + by };
        Save();
    }

    private static bool Covers(MuteFilter filter, string heading, string feedUrl) => filter.Scope switch
    {
        MuteScope.Heading => string.Equals(filter.Target, heading, StringComparison.OrdinalIgnoreCase),
        MuteScope.Feed => string.Equals(filter.Target, feedUrl, StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    /// <summary>
    /// Whether the text matches. A plain filter matches on whole words; a pattern matches as it
    /// is written.
    /// </summary>
    /// <remarks>
    /// Whole words rather than any substring, which is the difference between muting "AI" and
    /// muting every article about Ukraine, rain, and maintenance. A phrase with a space in it is
    /// matched as a phrase, for the same reason.
    /// </remarks>
    private bool Hits(MuteFilter filter, string text)
    {
        if (text.Length == 0) return false;

        if (!filter.IsRegex)
        {
            return WholeWord(text, filter.Text);
        }

        if (Pattern(filter.Text) is not { } pattern) return false;

        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pattern that cannot finish is a pattern that mutes nothing, rather than one that
            // stops a delivery. It is the reader's own pattern, but it is still not worth a hang.
            Log.Warn($"Feeds: the mute pattern “{filter.Text}” took too long and was skipped.");
            return false;
        }
    }

    /// <summary>
    /// True when the phrase appears in the text on word boundaries.
    /// </summary>
    /// <remarks>
    /// Written out rather than done with a pattern per filter: this runs over every entry of
    /// every feed on every poll, and building a regular expression for "Ukraine" to do what
    /// <see cref="string.IndexOf(string, StringComparison)"/> plus two character tests do is a
    /// cost paid thousands of times for nothing.
    /// </remarks>
    internal static bool WholeWord(string text, string phrase)
    {
        if (phrase.Length == 0) return false;

        var at = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            var before = at == 0 || !IsWordCharacter(text[at - 1]);
            var afterAt = at + phrase.Length;
            var after = afterAt >= text.Length || !IsWordCharacter(text[afterAt]);

            if (before && after) return true;

            at = text.IndexOf(phrase, at + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>A reader's pattern, compiled once and kept, or null when it does not compile.</summary>
    private Regex? Pattern(string text)
    {
        if (_compiled.TryGetValue(text, out var already)) return already;

        Regex? compiled = null;
        try
        {
            compiled = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (ArgumentException ex)
        {
            Log.Warn($"Feeds: the mute pattern “{text}” is not a pattern: {ex.Message}");
        }

        _compiled[text] = compiled;
        return compiled;
    }

    /// <summary>True when the text would compile as a pattern, for the dialog to say so.</summary>
    public static bool IsValidPattern(string text)
    {
        try
        {
            _ = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void Save()
    {
        _compiled.Clear();

        var array = new JsonArray();
        foreach (var filter in _filters)
        {
            var entry = new JsonObject { ["text"] = filter.Text };

            if (filter.Scope != MuteScope.Everywhere) entry["scope"] = filter.Scope.ToString();
            if (filter.Target.Length > 0) entry["target"] = filter.Target;
            if (filter.TitleOnly) entry["title"] = true;
            if (filter.IsRegex) entry["regex"] = true;
            if (filter.ExpiresUtc is { } expires) entry["until"] = expires.ToUnixTimeSeconds();
            if (filter.Muted > 0) entry["muted"] = filter.Muted;

            array.Add(entry);
        }

        _settings.Set(Key, array.ToJsonString());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<MuteFilter> Parse(string json)
    {
        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            Log.Warn($"The mute filters could not be read: {ex.Message}");
        }

        if (node is not JsonArray array) yield break;

        foreach (var entry in array.OfType<JsonObject>())
        {
            if (entry["text"]?.GetValue<string>() is not { Length: > 0 } text) continue;

            yield return new MuteFilter(
                text,
                Enum.TryParse<MuteScope>(entry["scope"]?.GetValue<string>(), out var scope) ? scope : MuteScope.Everywhere,
                entry["target"]?.GetValue<string>() ?? string.Empty,
                entry["title"]?.GetValue<bool?>() ?? false,
                entry["regex"]?.GetValue<bool?>() ?? false,
                entry["until"]?.GetValue<long?>() is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null)
            {
                Muted = (int)(entry["muted"]?.GetValue<long?>() ?? 0),
            };
        }
    }
}
