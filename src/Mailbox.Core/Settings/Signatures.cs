using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Core.Settings;

/// <summary>
/// One signature: a name, and what it says.
/// </summary>
/// <remarks>
/// Both forms are kept. A message goes out as HTML with a plain text alternative beside it, and
/// a signature that exists only as markup would arrive in the text half as angle brackets — so
/// the text is written down rather than derived, and the writer gets to say how it reads when
/// the formatting is gone.
/// </remarks>
public sealed record Signature
{
    public required string Name { get; init; }

    /// <summary>The signature as markup, in the conservative shape §7.3 requires of outgoing mail.</summary>
    public string Html { get; init; } = string.Empty;

    /// <summary>The same words, for the plain text half of the message.</summary>
    public string Text { get; init; } = string.Empty;

    public bool IsEmpty => Html.Trim().Length == 0 && Text.Trim().Length == 0;
}

/// <summary>
/// The signatures, and which account uses which when.
/// </summary>
/// <remarks>
/// Stored as one JSON string under a single key, as the toolbar's command list and the
/// send/receive groups are: the settings file stays something a person can read, and a malformed
/// entry costs a signature rather than the application starting.
/// <para>
/// New messages and replies are chosen separately, and per account, because that is the
/// distinction people actually want — a full block on a new message and a single line on a
/// reply, and a different one entirely from the work address.
/// </para>
/// </remarks>
public sealed class Signatures
{
    public const string Key = "mail.signatures";

    /// <summary>Which signature an account uses, keyed by address.</summary>
    private const string NewKey = "mail.signatures.new";
    private const string ReplyKey = "mail.signatures.reply";

    private readonly SettingsStore _settings;
    private readonly List<Signature> _signatures;

    public Signatures(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        _signatures = settings.Has(Key) ? Parse(settings.GetString(Key)) : [];
    }

    public IReadOnlyList<Signature> All => _signatures;

    public Signature? Find(string name)
        => _signatures.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds one, or replaces the one of that name.</summary>
    public void Save(Signature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (string.IsNullOrWhiteSpace(signature.Name)) return;

        var at = _signatures.FindIndex(
            s => string.Equals(s.Name, signature.Name, StringComparison.OrdinalIgnoreCase));

        if (at >= 0) _signatures[at] = signature;
        else _signatures.Add(signature);

        Write();
    }

    public void Remove(string name)
    {
        if (_signatures.RemoveAll(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Write();
        }
    }

    /// <summary>
    /// The signature this account puts on a new message, or on a reply.
    /// </summary>
    /// <remarks>
    /// Null for "none", which is the default and has to stay a real choice: a great many people
    /// do not want one, and a client that inserts something on the first message is one they
    /// have to go and find the setting for.
    /// </remarks>
    public Signature? ForNew(string address) => Chosen(NewKey, address);

    public Signature? ForReply(string address) => Chosen(ReplyKey, address);

    public void UseForNew(string address, string? name) => Choose(NewKey, address, name);

    public void UseForReply(string address, string? name) => Choose(ReplyKey, address, name);

    private Signature? Chosen(string key, string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (!_settings.Has(key)) return null;

        try
        {
            if (JsonNode.Parse(_settings.GetString(key)) is not JsonObject map) return null;

            // Keyed by address rather than by a row id, for the reason §15 gives about account
            // settings: an id means nothing once a store has been restored or copied.
            var name = map[address.ToLowerInvariant()]?.GetValue<string>();
            return name is { Length: > 0 } ? Find(name) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"The signature choice in '{key}' could not be read.", ex);
            return null;
        }
    }

    private void Choose(string key, string address, string? name)
    {
        if (string.IsNullOrWhiteSpace(address)) return;

        JsonObject map;

        try
        {
            map = _settings.Has(key) && JsonNode.Parse(_settings.GetString(key)) is JsonObject existing
                ? existing
                : [];
        }
        catch (Exception ex)
        {
            // Starting over is the only way forward, but it discards every other account's
            // choice along with the unreadable one — and the line below writes the empty map
            // back, so it is not recoverable on the next run. Said out loud for that reason;
            // its sibling above already says the same about reading.
            Log.Warn($"The signature choices in '{key}' could not be read and were reset.", ex);
            map = [];
        }

        var slot = address.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(name)) map.Remove(slot);
        else map[slot] = name;

        _settings.Set(key, map.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    private void Write()
    {
        var array = new JsonArray();

        foreach (var signature in _signatures)
        {
            array.Add(new JsonObject
            {
                ["name"] = signature.Name,
                ["html"] = signature.Html,
                ["text"] = signature.Text,
            });
        }

        _settings.Set(Key, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    /// <summary>
    /// Reads the stored list, keeping what parses.
    /// </summary>
    /// <remarks>
    /// One bad entry costs that signature rather than all of them, and rather than the
    /// application starting — the settings file is one a person may edit by hand, and this is
    /// what makes that safe to do.
    /// </remarks>
    private static List<Signature> Parse(string json)
    {
        var signatures = new List<Signature>();

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return signatures;

            foreach (var entry in array.OfType<JsonObject>())
            {
                if (entry["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;

                signatures.Add(new Signature
                {
                    Name = name,
                    Html = entry["html"]?.GetValue<string>() ?? string.Empty,
                    Text = entry["text"]?.GetValue<string>() ?? string.Empty,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The signatures could not be read; starting with none.", ex);
        }

        return signatures;
    }
}
