using System.Text;

namespace Mailbox.Core.Conversations;

/// <summary>What Clean Up needs to know about one message in a conversation.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Received">When it arrived, for the order.</param>
/// <param name="Text">Its plain text — the body as the reader would read it, quotes and all.</param>
public sealed record CleanUpCandidate(long Id, DateTimeOffset Received, string Text)
{
    public bool IsUnread { get; init; }
    public bool IsCategorized { get; init; }
    public bool IsFlagged { get; init; }
    public bool IsSigned { get; init; }
}

/// <summary>The Options page's Conversation Clean Up switches: which messages are never moved.</summary>
public sealed record CleanUpPolicy
{
    public bool KeepUnread { get; init; }
    public bool KeepCategorized { get; init; } = true;
    public bool KeepFlagged { get; init; } = true;
    public bool KeepSigned { get; init; } = true;

    /// <summary>"When a reply modifies a message, don't move the original": only a wholly quoted message is redundant.</summary>
    public bool KeepIfModified { get; init; } = true;
}

/// <summary>
/// Conversation Clean Up: which messages of a conversation are redundant — their whole text is
/// quoted in a later message — and can go, leaving the conversation readable from what remains.
/// </summary>
/// <remarks>
/// Pure, over the texts. A message is redundant when a later message in the same conversation
/// contains it: its text, quoting marks stripped and whitespace folded, appears inside the later
/// message's text folded the same way. Short messages are never redundant — "ok" is inside
/// nearly everything — and the policy's switches keep unread, categorized, flagged and signed
/// messages regardless. The newest message is never moved: it is what the conversation is read
/// from.
/// </remarks>
public static class CleanUp
{
    /// <summary>A text shorter than this, once folded, is never counted as contained.</summary>
    private const int MinimumLength = 40;

    /// <summary>The ids that can go, in the order they were given.</summary>
    public static IReadOnlyList<long> Redundant(IReadOnlyList<CleanUpCandidate> conversation, CleanUpPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        policy ??= new CleanUpPolicy();

        var ordered = conversation.OrderBy(c => c.Received).ThenBy(c => c.Id).ToList();
        var folded = ordered.Select(c => Fold(c.Text)).ToList();
        var redundant = new List<long>();

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var candidate = ordered[i];
            if (policy.KeepUnread && candidate.IsUnread) continue;
            if (policy.KeepCategorized && candidate.IsCategorized) continue;
            if (policy.KeepFlagged && candidate.IsFlagged) continue;
            if (policy.KeepSigned && candidate.IsSigned) continue;

            var text = folded[i];
            if (text.Length < MinimumLength) continue;

            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (folded[j].Contains(text, StringComparison.Ordinal))
                {
                    redundant.Add(candidate.Id);
                    break;
                }
            }
        }

        return redundant;
    }

    /// <summary>
    /// A text as it compares: quoting marks off the start of lines, the reference's own header
    /// block ("From: … Sent: … To: … Subject: …") and signature rules dropped, whitespace
    /// folded, case folded.
    /// </summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart();
            while (line.StartsWith('>')) line = line[1..].TrimStart();
            if (line.Length == 0) continue;
            if (line.StartsWith("--", StringComparison.Ordinal) && line.Trim() is "--" or "-- ") continue;
            if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Sent:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("To:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("On ", StringComparison.Ordinal) && line.EndsWith("wrote:", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(word.ToLowerInvariant());
            }
        }

        return builder.ToString();
    }
}
