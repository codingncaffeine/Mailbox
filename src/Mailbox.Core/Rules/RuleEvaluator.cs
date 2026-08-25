namespace Mailbox.Core.Rules;

/// <summary>
/// What a rule can see of a message: the facts, with none of the MIME. Built by whoever holds
/// the message, so the evaluation itself is pure and tested without a parser.
/// </summary>
public sealed record RuleFacts
{
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = string.Empty;
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;

    /// <summary>The raw header block, one line per header, for "specific words in the header".</summary>
    public string Headers { get; init; } = string.Empty;

    public long SizeBytes { get; init; }
    public bool HasAttachment { get; init; }

    /// <summary>0 low, 1 normal, 2 high.</summary>
    public int Importance { get; init; } = 1;

    /// <summary>0 normal, 1 personal, 2 private, 3 confidential.</summary>
    public int Sensitivity { get; init; }

    public DateTimeOffset Received { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public bool IsFlagged { get; init; }

    /// <summary>The reader's own addresses, for the "my name" conditions.</summary>
    public IReadOnlyList<string> OwnAddresses { get; init; } = [];

    /// <summary>The feed this arrived from, when it arrived from one — the receiver's own stamp.</summary>
    public string FeedUrl { get; init; } = string.Empty;
}

/// <summary>Decides whether a rule applies to a message. Pure.</summary>
public static class RuleEvaluator
{
    /// <summary>
    /// True when every condition holds and no exception does. A rule with no conditions
    /// matches everything, as the reference's does — the wizard warns before saving one.
    /// </summary>
    public static bool Matches(MailRule rule, RuleFacts facts)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);

        return rule.Conditions.All(c => Holds(c, facts)) && !rule.Exceptions.Any(e => Holds(e, facts));
    }

    /// <summary>Whether one condition holds.</summary>
    public static bool Holds(RuleCondition condition, RuleFacts facts)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(facts);

        var recipients = facts.To.Concat(facts.Cc).ToList();

        return condition.Kind switch
        {
            RuleConditionKind.From => condition.Values.Any(v => AddressMatches(facts.FromAddress, facts.FromName, v)),
            RuleConditionKind.SubjectContains => AnyWord(facts.Subject, condition.Values),
            RuleConditionKind.BodyContains => AnyWord(facts.Body, condition.Values),
            RuleConditionKind.SubjectOrBodyContains => AnyWord(facts.Subject, condition.Values) || AnyWord(facts.Body, condition.Values),
            RuleConditionKind.HeaderContains => AnyWord(facts.Headers, condition.Values),
            RuleConditionKind.SenderAddressContains => AnyWord(facts.FromAddress, condition.Values),
            RuleConditionKind.RecipientAddressContains => recipients.Any(r => AnyWord(r, condition.Values)),
            RuleConditionKind.SentTo => condition.Values.Any(v => recipients.Any(r => AddressMatches(r, string.Empty, v))),
            RuleConditionKind.SentOnlyToMe => recipients.Count > 0 && recipients.All(r => IsMine(r, facts)),
            RuleConditionKind.MyNameInTo => facts.To.Any(r => IsMine(r, facts)),
            RuleConditionKind.MyNameInCc => facts.Cc.Any(r => IsMine(r, facts)),
            RuleConditionKind.MyNameInToOrCc => recipients.Any(r => IsMine(r, facts)),
            RuleConditionKind.MyNameNotInTo => !facts.To.Any(r => IsMine(r, facts)),
            RuleConditionKind.HasAttachment => facts.HasAttachment,
            RuleConditionKind.Importance => condition.Level == facts.Importance,
            RuleConditionKind.Sensitivity => condition.Level == facts.Sensitivity,
            RuleConditionKind.SizeBetween => InSize(facts.SizeBytes, condition.Min, condition.Max),
            RuleConditionKind.ReceivedBetween =>
                (condition.After is not { } after || facts.Received >= after)
                && (condition.Before is not { } before || facts.Received <= before),
            RuleConditionKind.AssignedToCategory => condition.Values.Any(v => facts.Categories.Contains(v, StringComparer.OrdinalIgnoreCase)),
            RuleConditionKind.Flagged => facts.IsFlagged,

            // A feed is named by its address, and a reader picks it from the subscribed list, so
            // an exact match is the whole of it — no substring, or one feed on a site would
            // catch its siblings.
            RuleConditionKind.FromFeed => facts.FeedUrl.Length > 0
                && condition.Values.Any(v => string.Equals(v.Trim(), facts.FeedUrl, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    /// <summary>
    /// Whether an address matches an entry from a rule: the whole address, or a display name,
    /// or a domain written as "@example.com" — the forms the wizard's people picker writes.
    /// </summary>
    internal static bool AddressMatches(string address, string name, string entry)
    {
        var wanted = entry.Trim();
        if (wanted.Length == 0) return false;

        if (wanted.StartsWith('@'))
        {
            return address.EndsWith(wanted, StringComparison.OrdinalIgnoreCase);
        }

        // "Name <address>" as the picker writes it: either half is enough.
        var open = wanted.IndexOf('<');
        var close = wanted.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            var inner = wanted[(open + 1)..close].Trim();
            var outer = wanted[..open].Trim().Trim('"');
            return string.Equals(address, inner, StringComparison.OrdinalIgnoreCase)
                   || (outer.Length > 0 && string.Equals(name, outer, StringComparison.OrdinalIgnoreCase));
        }

        return string.Equals(address, wanted, StringComparison.OrdinalIgnoreCase)
               || (name.Length > 0 && string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
               || (wanted.Contains('@') is false && address.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMine(string address, RuleFacts facts)
        => facts.OwnAddresses.Any(own => string.Equals(own, address, StringComparison.OrdinalIgnoreCase));

    private static bool AnyWord(string text, IReadOnlyList<string> words)
        => words.Any(w => w.Length > 0 && text.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static bool InSize(long bytes, long? minKb, long? maxKb)
    {
        var kb = bytes / 1024.0;
        return (minKb is not { } min || kb >= min) && (maxKb is not { } max || kb <= max);
    }
}
