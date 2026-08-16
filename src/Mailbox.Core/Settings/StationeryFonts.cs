using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Core.Settings;

/// <summary>What a message is written in when nothing in it says otherwise: a face, a size, a weight, a slant, a colour.</summary>
/// <param name="Family">The family as it goes on the wire — "Calibri", never the substitute that draws it here.</param>
/// <param name="Points">The size in points, as mail measures.</param>
/// <param name="Colour"><c>#RRGGBB</c>, or null for automatic — the reader's own text colour.</param>
public sealed record MessageFont(string Family, double Points, bool Bold = false, bool Italic = false, string? Colour = null)
{
    /// <summary>The reference's own default for new mail and replies: Calibri 11.</summary>
    public static MessageFont Default { get; } = new("Calibri", 11);

    /// <summary>The reference's default for plain text: Courier New 10.5, a fixed-pitch face.</summary>
    public static MessageFont PlainDefault { get; } = new("Courier New", 10.5);

    /// <summary>The style as the Font dialog names it.</summary>
    public string Style => (Bold, Italic) switch
    {
        (true, true) => "Bold Italic",
        (true, false) => "Bold",
        (false, true) => "Italic",
        _ => "Regular",
    };

    /// <summary>"Calibri 11" — the line beside a Font… button.</summary>
    public string Summary
        => $"{Family} {Points.ToString("0.#", CultureInfo.InvariantCulture)}"
           + (Style == "Regular" ? string.Empty : " " + Style);
}

/// <summary>Which writing a stationery font is for.</summary>
public enum StationeryUse
{
    NewMessages,
    Replies,
    PlainText,
}

/// <summary>
/// The Personal Stationery tab's choices: the font new mail is written in, the one replies and
/// forwards use, and the one for plain text — plus the two switches about comments in a reply.
/// </summary>
/// <remarks>
/// Each font is one JSON object under its own key, readable in the settings file
/// (<c>{"family":"Calibri","points":11,"bold":false,"italic":false,"colour":null}</c>). A key
/// that is missing or unreadable is the reference's default, never an error. The comment
/// switches are kept but not yet acted on: marking a comment inside quoted text needs the
/// editor to know where the quote begins, and it does not yet — the plan's §20 says so.
/// </remarks>
public sealed class StationeryFonts
{
    public const string NewKey = "mail.font.new";
    public const string ReplyKey = "mail.font.reply";
    public const string PlainKey = "mail.font.plain";
    public const string MarkCommentsKey = "mail.reply.markcomments";
    public const string MarkCommentsWithKey = "mail.reply.markcommentswith";
    public const string PickColourOnReplyKey = "mail.reply.pickcolour";

    private readonly SettingsStore _settings;

    public StationeryFonts(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Raised after a font or a switch changes, so an open compose surface can follow.</summary>
    public event EventHandler? Changed;

    public MessageFont Get(StationeryUse use)
    {
        var (key, fallback) = KeyFor(use);
        return _settings.Has(key) ? Parse(_settings.GetString(key), fallback) : fallback;
    }

    public void Set(StationeryUse use, MessageFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var (key, _) = KeyFor(use);
        var node = new JsonObject
        {
            ["family"] = font.Family,
            ["points"] = font.Points,
            ["bold"] = font.Bold,
            ["italic"] = font.Italic,
            ["colour"] = font.Colour,
        };
        _settings.Set(key, node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back to the reference's default for that use.</summary>
    public void Reset(StationeryUse use)
    {
        var (_, fallback) = KeyFor(use);
        Set(use, fallback);
    }

    public bool MarkComments
    {
        get => _settings.GetBool(MarkCommentsKey, false);
        set { _settings.Set(MarkCommentsKey, value); Changed?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>The label a marked comment carries — the reader's name by default, which the caller supplies.</summary>
    public string MarkCommentsWith(string fallback)
        => _settings.GetString(MarkCommentsWithKey) is { Length: > 0 } text ? text : fallback;

    public void SetMarkCommentsWith(string text)
    {
        _settings.Set(MarkCommentsWithKey, text ?? string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool PickColourOnReply
    {
        get => _settings.GetBool(PickColourOnReplyKey, false);
        set { _settings.Set(PickColourOnReplyKey, value); Changed?.Invoke(this, EventArgs.Empty); }
    }

    private static (string Key, MessageFont Fallback) KeyFor(StationeryUse use) => use switch
    {
        StationeryUse.Replies => (ReplyKey, MessageFont.Default),
        StationeryUse.PlainText => (PlainKey, MessageFont.PlainDefault),
        _ => (NewKey, MessageFont.Default),
    };

    private static MessageFont Parse(string stored, MessageFont fallback)
    {
        try
        {
            if (JsonNode.Parse(stored) is not JsonObject node) return fallback;
            var family = node["family"]?.GetValue<string>();
            var points = node["points"] is { } p ? p.GetValue<double>() : fallback.Points;
            if (string.IsNullOrWhiteSpace(family) || points is < 4 or > 144) return fallback;
            var colour = node["colour"]?.GetValue<string>();
            return new MessageFont(
                family.Trim(),
                points,
                node["bold"]?.GetValue<bool>() ?? false,
                node["italic"]?.GetValue<bool>() ?? false,
                string.IsNullOrWhiteSpace(colour) ? null : colour.Trim());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            Log.Warn("A stationery font setting could not be read; using the default.", ex);
            return fallback;
        }
    }
}
