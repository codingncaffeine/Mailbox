using System.Globalization;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Rules;

/// <summary>
/// The automatic reply an account sends while its reader is away — what the reference calls
/// Automatic Replies (Out of Office).
/// </summary>
/// <remarks>
/// Held by the mail server rather than by this application, as RFC 5230's <c>vacation</c> action,
/// so it answers while the machine is off — which is the only way an out-of-office reply is worth
/// having. That is also why it is kept beside the account's server-side rules and published with
/// them: one script, one place the server is told about, one thing to take down again.
/// <para>
/// Per account, in the settings store rather than the mailbox database: it is a setting about how
/// an account behaves, not an item in it, and it has to be readable before any store is opened.
/// </para>
/// </remarks>
public sealed record AwayMessage
{
    /// <summary>Whether the server should be answering at all.</summary>
    public bool Enabled { get; init; }

    /// <summary>The first day it answers, or null to start as soon as it is switched on.</summary>
    public DateOnly? From { get; init; }

    /// <summary>The last day it answers, or null to keep answering until it is switched off.</summary>
    public DateOnly? Until { get; init; }

    /// <summary>The reply's subject. Empty leaves it to the server, which echoes the original's.</summary>
    public string Subject { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// How long before the same person is answered a second time. RFC 5230 asks for at least a
    /// day and servers commonly refuse less; seven is the specification's own default.
    /// </summary>
    public int Days { get; init; } = 7;

    /// <summary>
    /// Further addresses of the reader's that count as theirs — an alias, a role address, a
    /// forwarding domain. The account's own address is always one and is not kept here.
    /// </summary>
    /// <remarks>
    /// This is what stops the reply going to a mailing list: <c>vacation</c> answers only a
    /// message addressed to one of the addresses it knows about, so a list posting, which is
    /// addressed to the list, is left alone.
    /// </remarks>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>Whether it should be answering on a given day, dates and all.</summary>
    public bool ActiveOn(DateOnly day)
        => Enabled && (From is not { } from || day >= from) && (Until is not { } until || day <= until);

    /// <summary>True when a date range was asked for — which is what the server needs an extension to hold.</summary>
    public bool HasDates => From is not null || Until is not null;

    private static string Key(string address, string field) => $"account.{address}.away.{field}";

    private static readonly string[] Fields =
        ["on", "from", "until", "subject", "body", "days", "addresses"];

    public static AwayMessage Load(SettingsStore settings, string address)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new AwayMessage
        {
            Enabled = settings.GetBool(Key(address, "on"), false),
            From = Date(settings.GetString(Key(address, "from"))),
            Until = Date(settings.GetString(Key(address, "until"))),
            Subject = settings.GetString(Key(address, "subject")),
            Body = settings.GetString(Key(address, "body")),
            Days = (int)settings.GetNumber(Key(address, "days"), 7),
            Addresses = settings.GetString(Key(address, "addresses"))
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }

    public void Save(SettingsStore settings, string address)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Set(Key(address, "on"), Enabled);
        settings.Set(Key(address, "from"), From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
        settings.Set(Key(address, "until"), Until?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
        settings.Set(Key(address, "subject"), Subject);
        settings.Set(Key(address, "body"), Body);
        settings.Set(Key(address, "days"), Days);
        settings.Set(Key(address, "addresses"), string.Join(", ", Addresses));
    }

    /// <summary>Forgets an account's automatic reply, for an account being removed.</summary>
    public static void Forget(SettingsStore settings, string address)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var field in Fields) settings.Remove(Key(address, field));
    }

    private static DateOnly? Date(string text)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
}
