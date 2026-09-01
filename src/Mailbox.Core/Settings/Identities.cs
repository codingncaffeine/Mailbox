using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Core.Settings;

/// <summary>
/// One address a mailbox sends as: a name, an address, and what a reply to it should do.
/// </summary>
/// <remarks>
/// An account is one connection to one server. An identity is one <c>From</c> line, and a
/// mailbox commonly has several — a work alias, a role address, a domain that forwards into the
/// same inbox. All of them are read in one place and sent through one server; only the header
/// differs, which is why this is a setting about an account rather than a second account.
/// <para>
/// The account's own address and display name are always an identity and are never stored here:
/// they belong to the <c>Account</c> row and are edited where the account is. What this holds is
/// the extras. <see cref="Identities.Of"/> is what puts the two together, and every reader
/// should ask it rather than assembling the list itself.
/// </para>
/// </remarks>
public sealed record Identity
{
    /// <summary>The address messages go out as.</summary>
    public required string Address { get; init; }

    /// <summary>The name beside it, or empty for the address alone.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Where replies should go, if not to <see cref="Address"/>.
    /// </summary>
    /// <remarks>
    /// The reason half of these identities exist. A role address that nobody reads wants replies
    /// at a person's own; a forwarding domain wants them at the mailbox behind it.
    /// </remarks>
    public string ReplyTo { get; init; } = string.Empty;

    /// <summary>The <c>Organization</c> header, which many corporate identities carry and none of ours did.</summary>
    public string Organization { get; init; } = string.Empty;

    /// <summary>True for the identity that stands for the account itself, which cannot be removed.</summary>
    public bool IsAccountDefault { get; init; }

    /// <summary>How the From menu and the identity list name it: the name and the address, or the address alone.</summary>
    public string Label
        => string.IsNullOrWhiteSpace(DisplayName)
           || string.Equals(DisplayName, Address, StringComparison.OrdinalIgnoreCase)
            ? Address
            : $"{DisplayName}  ({Address})";
}

/// <summary>
/// The identities of every account, and which account each belongs to.
/// </summary>
/// <remarks>
/// Stored as one JSON string under a single key, as the signatures and the send/receive groups
/// are: the settings file stays something a person can read, and a malformed entry costs an
/// identity rather than the application starting.
/// <para>
/// Keyed by address throughout, never by a row id — the same reasoning the signature choices
/// carry: an id means nothing once a store has been restored or copied, and an address is what
/// the message itself is written with.
/// </para>
/// </remarks>
public sealed class Identities
{
    public const string Key = "mail.identities";

    private readonly SettingsStore _settings;

    /// <summary>The stored extras, in order, each carrying the account it belongs to.</summary>
    private readonly List<(string Account, Identity Identity)> _extras;

    public Identities(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        _extras = settings.Has(Key) ? Parse(settings.GetString(Key)) : [];
    }

    /// <summary>
    /// Every identity of an account: its own first, then the extras in the order they were given.
    /// </summary>
    /// <remarks>
    /// The account's own is synthesized rather than stored, so an account whose name or address
    /// is changed keeps one identity rather than gaining a stale second.
    /// </remarks>
    public IReadOnlyList<Identity> Of(string accountAddress, string accountDisplayName)
    {
        if (string.IsNullOrWhiteSpace(accountAddress)) return [];

        var own = new Identity
        {
            Address = accountAddress,
            DisplayName = accountDisplayName,
            IsAccountDefault = true,
        };

        return [own, .. Extras(accountAddress)];
    }

    /// <summary>The stored identities of one account, without the account's own.</summary>
    public IReadOnlyList<Identity> Extras(string accountAddress)
        => [.. _extras
            .Where(e => string.Equals(e.Account, accountAddress, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Identity)];

    /// <summary>
    /// Which account an address sends through, or null when no identity claims it.
    /// </summary>
    /// <remarks>
    /// What lets a <c>From</c> address that is not an account's own still find a server to go
    /// out by — a reopened draft, or a message the writer picked an alias for.
    /// </remarks>
    public string? AccountFor(string address)
        => string.IsNullOrWhiteSpace(address)
            ? null
            : _extras.FirstOrDefault(
                e => string.Equals(e.Identity.Address, address, StringComparison.OrdinalIgnoreCase)).Account;

    /// <summary>The stored identity with this address, or null — the account's own is not one of these.</summary>
    public Identity? Find(string address)
        => string.IsNullOrWhiteSpace(address)
            ? null
            : _extras.FirstOrDefault(
                e => string.Equals(e.Identity.Address, address, StringComparison.OrdinalIgnoreCase)).Identity;

    /// <summary>
    /// Replaces an account's extra identities with these, in this order.
    /// </summary>
    /// <remarks>
    /// The whole list at once rather than one at a time, because the dialog that writes it edits
    /// the whole list — and because ordering is part of what is being said.
    /// </remarks>
    public void Save(string accountAddress, IEnumerable<Identity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (string.IsNullOrWhiteSpace(accountAddress)) return;

        _extras.RemoveAll(e => string.Equals(e.Account, accountAddress, StringComparison.OrdinalIgnoreCase));

        foreach (var identity in identities)
        {
            // An identity with no address is not one, and the account's own is never stored.
            if (string.IsNullOrWhiteSpace(identity.Address)) continue;
            if (string.Equals(identity.Address, accountAddress, StringComparison.OrdinalIgnoreCase)) continue;

            _extras.Add((accountAddress, identity with { IsAccountDefault = false }));
        }

        Write();
    }

    /// <summary>Forgets an account's identities, for an account being removed.</summary>
    public void Forget(string accountAddress)
    {
        if (_extras.RemoveAll(
                e => string.Equals(e.Account, accountAddress, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Write();
        }
    }

    private void Write()
    {
        var array = new JsonArray();

        foreach (var (account, identity) in _extras)
        {
            array.Add(new JsonObject
            {
                ["account"] = account,
                ["address"] = identity.Address,
                ["name"] = identity.DisplayName,
                ["replyTo"] = identity.ReplyTo,
                ["organization"] = identity.Organization,
            });
        }

        _settings.Set(Key, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    /// <summary>
    /// Reads the stored list, keeping what parses.
    /// </summary>
    /// <remarks>
    /// One bad entry costs that identity rather than all of them, and rather than the
    /// application starting — the settings file is one a person may edit by hand, and this is
    /// what makes that safe to do.
    /// </remarks>
    private static List<(string Account, Identity Identity)> Parse(string json)
    {
        var identities = new List<(string, Identity)>();

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return identities;

            foreach (var entry in array.OfType<JsonObject>())
            {
                if (entry["account"]?.GetValue<string>() is not { Length: > 0 } account) continue;
                if (entry["address"]?.GetValue<string>() is not { Length: > 0 } address) continue;

                identities.Add((account, new Identity
                {
                    Address = address,
                    DisplayName = entry["name"]?.GetValue<string>() ?? string.Empty,
                    ReplyTo = entry["replyTo"]?.GetValue<string>() ?? string.Empty,
                    Organization = entry["organization"]?.GetValue<string>() ?? string.Empty,
                }));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The identities could not be read; every account keeps its own address.", ex);
        }

        return identities;
    }
}
