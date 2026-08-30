namespace Mailbox.Core.Focus;

/// <summary>What the classifier can see of a message: enough to tell a person from a machine.</summary>
public sealed record FocusFacts
{
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = string.Empty;

    /// <summary>The header names present, lower-cased — List-Id, List-Unsubscribe, Precedence and the like.</summary>
    public IReadOnlyCollection<string> HeaderNames { get; init; } = [];

    /// <summary>The Precedence header's value, if any: "bulk", "list", "junk".</summary>
    public string? Precedence { get; init; }

    /// <summary>The Auto-Submitted header's value, if any: "auto-generated", "auto-replied".</summary>
    public string? AutoSubmitted { get; init; }

    /// <summary>Whether the reader has ever written to the sender — the Auto-Complete List knows.</summary>
    public bool KnownCorrespondent { get; init; }

    /// <summary>Whether the reader is in To (rather than Cc or a list).</summary>
    public bool AddressedToMe { get; init; }

    /// <summary>What the reader has said about this sender: true Focused, false Other, null nothing.</summary>
    public bool? Override { get; init; }
}

/// <summary>
/// Decides whether a message belongs in Focused or Other. Local, and explainable.
/// </summary>
/// <remarks>
/// The reference does this with a model trained on a mailbox; this is the same decision made by
/// rules a reader can predict. Their word wins: an "always" override decides outright. Then the
/// tells of machine mail — a list header, a bulk precedence, an auto-submitted marker, a
/// no-reply sender — put a message in Other; someone the reader has written to is Focused; and a
/// message addressed to the reader personally from anyone else is Focused too. What is left —
/// mail from a stranger not addressed to the reader — is Other.
/// </remarks>
public static class FocusedInbox
{
    private static readonly string[] MachineSenders =
    [
        "noreply", "no-reply", "no_reply", "donotreply", "do-not-reply", "do_not_reply",
        "notification", "notifications", "newsletter", "mailer-daemon", "postmaster", "bounce",
        "alerts", "alert@", "digest", "marketing", "info@", "news@", "updates@", "offers@",
    ];

    /// <summary>True for Focused, false for Other.</summary>
    public static bool IsFocused(FocusFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Override is { } said) return said;

        var address = facts.FromAddress.Trim().ToLowerInvariant();

        // The reader has written to them: a person, whatever their headers say.
        if (facts.KnownCorrespondent) return true;

        if (facts.HeaderNames.Any(h => h is "list-id" or "list-unsubscribe" or "list-post" or "x-mailchimp-id" or "x-campaign" or "x-mailer-lid"))
        {
            return false;
        }

        if (facts.Precedence is { } precedence && precedence.Trim().ToLowerInvariant() is "bulk" or "list" or "junk")
        {
            return false;
        }

        if (facts.AutoSubmitted is { } auto && !auto.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var local = address.Contains('@') ? address[..address.IndexOf('@')] : address;
        if (MachineSenders.Any(m => m.EndsWith('@') ? address.StartsWith(m, StringComparison.Ordinal) : local.Contains(m, StringComparison.Ordinal)))
        {
            return false;
        }

        // A stranger who wrote to the reader by name is Focused; one who wrote to a list or a
        // Cc is not, until the reader writes back.
        return facts.AddressedToMe;
    }
}
