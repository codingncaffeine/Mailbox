using System.Globalization;
using System.Text;

namespace Mailbox.Core.Rules;

/// <summary>
/// The sentence the Rules and Alerts dialog and the wizard show for a rule — "Apply this rule
/// after the message arrives / from A. Person / move it to the Projects folder / and stop
/// processing more rules" — one line per clause, in the reference's wording.
/// </summary>
/// <remarks>
/// Each clause carries the piece of it the reader can click to edit (the reference underlines
/// these), so the dialog can draw them as links without parsing the sentence back.
/// </remarks>
public static class RuleDescription
{
    /// <summary>One line of the description: the words, and the editable value in it if any.</summary>
    public sealed record Clause(string Text, string? Editable = null);

    /// <summary>The whole rule as clauses, in the order the reference writes them.</summary>
    public static IReadOnlyList<Clause> Describe(MailRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var clauses = new List<Clause> { new("Apply this rule after the message arrives") };

        foreach (var condition in rule.Conditions) clauses.Add(ForCondition(condition));
        foreach (var action in rule.Actions) clauses.Add(ForAction(action));

        var exceptions = rule.Exceptions.Select(ForCondition).ToList();
        for (var i = 0; i < exceptions.Count; i++)
        {
            var text = exceptions[i].Text;
            clauses.Add(new Clause(
                (i == 0 ? "except if " : "or ") + text,
                exceptions[i].Editable));
        }

        return clauses;
    }

    /// <summary>The description as one string, clauses joined the way the dialog lays them out.</summary>
    public static string Sentence(MailRule rule)
    {
        var lines = Describe(rule).Select(c => c.Text).ToList();
        var text = new StringBuilder(lines[0]);
        for (var i = 1; i < lines.Count; i++)
        {
            text.Append('\n').Append(i == 1 || lines[i].StartsWith("except", StringComparison.Ordinal) || lines[i].StartsWith("or ", StringComparison.Ordinal)
                ? " " : " and ").Append(lines[i]);
        }

        return text.ToString();
    }

    /// <summary>The label the wizard's checklist gives a condition kind — with the value slot named.</summary>
    public static string Template(RuleConditionKind kind) => kind switch
    {
        RuleConditionKind.From => "from people or public group",
        RuleConditionKind.SubjectContains => "with specific words in the subject",
        RuleConditionKind.BodyContains => "with specific words in the body",
        RuleConditionKind.SubjectOrBodyContains => "with specific words in the subject or body",
        RuleConditionKind.HeaderContains => "with specific words in the message header",
        RuleConditionKind.SenderAddressContains => "with specific words in the sender's address",
        RuleConditionKind.RecipientAddressContains => "with specific words in the recipient's address",
        RuleConditionKind.SentTo => "sent to people or public group",
        RuleConditionKind.SentOnlyToMe => "sent only to me",
        RuleConditionKind.MyNameInTo => "where my name is in the To box",
        RuleConditionKind.MyNameInCc => "where my name is in the Cc box",
        RuleConditionKind.MyNameInToOrCc => "where my name is in the To or Cc box",
        RuleConditionKind.MyNameNotInTo => "where my name is not in the To box",
        RuleConditionKind.HasAttachment => "which has an attachment",
        RuleConditionKind.Importance => "marked as importance",
        RuleConditionKind.Sensitivity => "marked as sensitivity",
        RuleConditionKind.SizeBetween => "with a size in a specific range",
        RuleConditionKind.ReceivedBetween => "received in a specific date span",
        RuleConditionKind.AssignedToCategory => "assigned to category category",
        RuleConditionKind.Flagged => "flagged for action",
        _ => kind.ToString(),
    };

    /// <summary>The label the wizard's checklist gives an action kind.</summary>
    public static string Template(RuleActionKind kind) => kind switch
    {
        RuleActionKind.MoveToFolder => "move it to the specified folder",
        RuleActionKind.CopyToFolder => "move a copy to the specified folder",
        RuleActionKind.Delete => "delete it",
        RuleActionKind.PermanentlyDelete => "permanently delete it",
        RuleActionKind.ForwardTo => "forward it to people or public group",
        RuleActionKind.ForwardAsAttachmentTo => "forward it to people or public group as an attachment",
        RuleActionKind.RedirectTo => "redirect it to people or public group",
        RuleActionKind.MarkAsRead => "mark it as read",
        RuleActionKind.MarkImportance => "mark it as importance",
        RuleActionKind.FlagForFollowUp => "flag message for follow up at this time",
        RuleActionKind.ClearFlag => "clear the message flag",
        RuleActionKind.AssignCategory => "assign it to the category category",
        RuleActionKind.ClearCategories => "clear message's categories",
        RuleActionKind.DisplayAlert => "display a specific message in the New Item Alert window",
        RuleActionKind.DesktopAlert => "display a Desktop Alert",
        RuleActionKind.PlaySound => "play a sound",
        RuleActionKind.Print => "print it",
        RuleActionKind.StopProcessing => "stop processing more rules",
        _ => kind.ToString(),
    };

    private static Clause ForCondition(RuleCondition c) => c.Kind switch
    {
        RuleConditionKind.From => new($"from {People(c.Values)}", People(c.Values)),
        RuleConditionKind.SubjectContains => new($"with {Words(c.Values)} in the subject", Words(c.Values)),
        RuleConditionKind.BodyContains => new($"with {Words(c.Values)} in the body", Words(c.Values)),
        RuleConditionKind.SubjectOrBodyContains => new($"with {Words(c.Values)} in the subject or body", Words(c.Values)),
        RuleConditionKind.HeaderContains => new($"with {Words(c.Values)} in the message header", Words(c.Values)),
        RuleConditionKind.SenderAddressContains => new($"with {Words(c.Values)} in the sender's address", Words(c.Values)),
        RuleConditionKind.RecipientAddressContains => new($"with {Words(c.Values)} in the recipient's address", Words(c.Values)),
        RuleConditionKind.SentTo => new($"sent to {People(c.Values)}", People(c.Values)),
        RuleConditionKind.Importance => new($"marked as {ImportanceName(c.Level)} importance", ImportanceName(c.Level)),
        RuleConditionKind.Sensitivity => new($"marked as {SensitivityName(c.Level)} sensitivity", SensitivityName(c.Level)),
        RuleConditionKind.SizeBetween => new($"with a size {Size(c.Min, c.Max)}", Size(c.Min, c.Max)),
        RuleConditionKind.ReceivedBetween => new($"received {Span(c.After, c.Before)}", Span(c.After, c.Before)),
        RuleConditionKind.AssignedToCategory => new($"assigned to {Names(c.Values, "category")} category", Names(c.Values, "category")),
        _ => new(Template(c.Kind)),
    };

    private static Clause ForAction(RuleAction a) => a.Kind switch
    {
        RuleActionKind.MoveToFolder => new($"move it to the {a.FolderName ?? "specified"} folder", a.FolderName ?? "specified"),
        RuleActionKind.CopyToFolder => new($"move a copy to the {a.FolderName ?? "specified"} folder", a.FolderName ?? "specified"),
        RuleActionKind.ForwardTo => new($"forward it to {People(a.Values)}", People(a.Values)),
        RuleActionKind.ForwardAsAttachmentTo => new($"forward it to {People(a.Values)} as an attachment", People(a.Values)),
        RuleActionKind.RedirectTo => new($"redirect it to {People(a.Values)}", People(a.Values)),
        RuleActionKind.MarkImportance => new($"mark it as {ImportanceName(a.Level)} importance", ImportanceName(a.Level)),
        RuleActionKind.FlagForFollowUp => new($"flag message for follow up {Due(a.Level)}", Due(a.Level)),
        RuleActionKind.AssignCategory => new($"assign it to the {Names(a.Values, "category")} category", Names(a.Values, "category")),
        RuleActionKind.DisplayAlert => new($"display {AlertText(a.Values)} in the New Item Alert window", AlertText(a.Values)),
        RuleActionKind.PlaySound => new($"play {SoundName(a.Values)}", SoundName(a.Values)),
        _ => new(Template(a.Kind)),
    };

    /// <summary>People as the description writes them: the entries joined, or the placeholder to click.</summary>
    private static string People(IReadOnlyList<string> values)
        => values.Count == 0 ? "people or public group" : string.Join(" or ", values);

    private static string Names(IReadOnlyList<string> values, string placeholder)
        => values.Count == 0 ? placeholder : string.Join(" or ", values);

    private static string AlertText(IReadOnlyList<string> values)
        => values.Count == 0 || values[0].Length == 0 ? "a specific message" : Quote(values[0]);

    private static string SoundName(IReadOnlyList<string> values)
        => values.Count == 0 || values[0].Length == 0 ? "a sound" : Quote(Path.GetFileName(values[0]));

    private static string Words(IReadOnlyList<string> values)
        => values.Count == 0 ? "specific words" : string.Join(" or ", values.Select(Quote));

    private static string Quote(string? text) => text is { Length: > 0 } ? $"\"{text}\"" : "\"…\"";

    private static string ImportanceName(int? level) => level switch { 0 => "low", 2 => "high", _ => "normal" };

    private static string SensitivityName(int? level) => level switch { 1 => "personal", 2 => "private", 3 => "confidential", _ => "normal" };

    private static string Size(long? min, long? max) => (min, max) switch
    {
        (null, null) => "in a specific range",
        ({ } a, null) => $"at least {a} KB",
        (null, { } b) => $"at most {b} KB",
        ({ } a, { } b) => $"between {a} and {b} KB",
    };

    private static string Span(DateTimeOffset? after, DateTimeOffset? before) => (after, before) switch
    {
        (null, null) => "in a specific date span",
        ({ } a, null) => $"after {a.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}",
        (null, { } b) => $"before {b.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}",
        ({ } a, { } b) => $"between {a.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)} and {b.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}",
    };

    private static string Due(int? days) => days switch
    {
        null => "with no date",
        0 => "today",
        1 => "tomorrow",
        { } n => $"in {n} days",
    };
}
