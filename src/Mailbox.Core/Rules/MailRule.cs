using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailbox.Core.Rules;

/// <summary>What a condition tests. The reference's list, less what needs a module not yet built.</summary>
public enum RuleConditionKind
{
    /// <summary>From people or public group: the sender is one of <see cref="RuleCondition.Values"/>.</summary>
    From,

    /// <summary>With specific words in the subject.</summary>
    SubjectContains,

    /// <summary>With specific words in the body.</summary>
    BodyContains,

    /// <summary>With specific words in the subject or body.</summary>
    SubjectOrBodyContains,

    /// <summary>With specific words in the message header — any header line.</summary>
    HeaderContains,

    /// <summary>With specific words in the sender's address.</summary>
    SenderAddressContains,

    /// <summary>With specific words in the recipient's address — To or Cc.</summary>
    RecipientAddressContains,

    /// <summary>Sent to people or public group: one of <see cref="RuleCondition.Values"/> is in To or Cc.</summary>
    SentTo,

    /// <summary>Sent only to me: I am the only recipient.</summary>
    SentOnlyToMe,

    /// <summary>Where my name is in the To box.</summary>
    MyNameInTo,

    /// <summary>Where my name is in the Cc box.</summary>
    MyNameInCc,

    /// <summary>Where my name is in the To or Cc box.</summary>
    MyNameInToOrCc,

    /// <summary>Where my name is not in the To box.</summary>
    MyNameNotInTo,

    /// <summary>Which has an attachment.</summary>
    HasAttachment,

    /// <summary>Marked as importance: <see cref="RuleCondition.Level"/> is 0 low, 1 normal, 2 high.</summary>
    Importance,

    /// <summary>Marked as sensitivity: <see cref="RuleCondition.Level"/> is 0 normal, 1 personal, 2 private, 3 confidential.</summary>
    Sensitivity,

    /// <summary>With a size in a specific range, in kilobytes, <see cref="RuleCondition.Min"/> to <see cref="RuleCondition.Max"/>.</summary>
    SizeBetween,

    /// <summary>Received in a specific date span: <see cref="RuleCondition.After"/> to <see cref="RuleCondition.Before"/>.</summary>
    ReceivedBetween,

    /// <summary>Assigned to a category — for Run Rules Now, since an arriving message has none.</summary>
    AssignedToCategory,

    /// <summary>Flagged for action — likewise.</summary>
    Flagged,

    /// <summary>
    /// From a specific RSS feed: <see cref="RuleCondition.Values"/> hold feed addresses.
    /// </summary>
    /// <remarks>
    /// A feed item is a message like any other by the time a rule sees it, and nothing in its
    /// headers said which feed it came from — so the receiver stamps one (<c>X-Mailbox-Feed</c>)
    /// and this reads it. Matching on the sender instead would have caught every feed on a host
    /// at once, every one of them being <c>rss@&lt;host&gt;</c>.
    /// </remarks>
    FromFeed,
}

/// <summary>What an action does.</summary>
public enum RuleActionKind
{
    /// <summary>Move it to the specified folder.</summary>
    MoveToFolder,

    /// <summary>Move a copy to the specified folder.</summary>
    CopyToFolder,

    /// <summary>Delete it — to Deleted Items.</summary>
    Delete,

    /// <summary>Permanently delete it.</summary>
    PermanentlyDelete,

    /// <summary>Forward it to people or public group.</summary>
    ForwardTo,

    /// <summary>Forward it to people or public group as an attachment.</summary>
    ForwardAsAttachmentTo,

    /// <summary>Redirect it to people or public group — resent, headers kept.</summary>
    RedirectTo,

    /// <summary>Mark it as read.</summary>
    MarkAsRead,

    /// <summary>Mark it as importance: <see cref="RuleAction.Level"/> is 0 low, 1 normal, 2 high.</summary>
    MarkImportance,

    /// <summary>Flag message for follow up, due in <see cref="RuleAction.Level"/> days (0 for today, null for no date).</summary>
    FlagForFollowUp,

    /// <summary>Clear the message flag.</summary>
    ClearFlag,

    /// <summary>Assign it to the category: <see cref="RuleAction.Values"/> are category names.</summary>
    AssignCategory,

    /// <summary>Clear message's categories.</summary>
    ClearCategories,

    /// <summary>Display a specific message in the New Item Alert window: <see cref="RuleAction.Values"/>[0].</summary>
    DisplayAlert,

    /// <summary>Display a Desktop Alert.</summary>
    DesktopAlert,

    /// <summary>Play a sound: <see cref="RuleAction.Values"/>[0] is the file, or empty for the default.</summary>
    PlaySound,

    /// <summary>Print it.</summary>
    Print,

    /// <summary>Stop processing more rules.</summary>
    StopProcessing,
}

/// <summary>One condition, or one exception — an exception is a condition that must not hold.</summary>
public sealed record RuleCondition(RuleConditionKind Kind)
{
    /// <summary>The words, addresses or names the condition looks for, where it looks for any.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>The importance or sensitivity level, for the kinds that carry one.</summary>
    public int? Level { get; init; }

    /// <summary>The lower bound in kilobytes, for a size condition.</summary>
    public long? Min { get; init; }

    /// <summary>The upper bound in kilobytes, for a size condition.</summary>
    public long? Max { get; init; }

    /// <summary>The start of a date span.</summary>
    public DateTimeOffset? After { get; init; }

    /// <summary>The end of a date span.</summary>
    public DateTimeOffset? Before { get; init; }
}

/// <summary>One action.</summary>
public sealed record RuleAction(RuleActionKind Kind)
{
    /// <summary>Addresses, category names, or the one text an alert or sound carries.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>The folder a move or copy goes to, by store id.</summary>
    public long? FolderId { get; init; }

    /// <summary>The folder's name when the rule was written, for the description and for a folder that has gone.</summary>
    public string? FolderName { get; init; }

    /// <summary>The importance level, or the follow-up's due-in days.</summary>
    public int? Level { get; init; }
}

/// <summary>
/// A rule: conditions that must all hold, exceptions none of which may, and the actions then
/// taken, in order. Applied to messages as they arrive, and by Run Rules Now to a folder.
/// </summary>
public sealed record MailRule
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>The order rules run in, lowest first. What Move Up and Move Down change.</summary>
    public int Ordinal { get; init; }

    /// <summary>
    /// Whether the rule runs on the server — compiled to Sieve and put there by ManageSieve —
    /// rather than here as mail arrives. Only a rule that <see cref="SieveCompiler"/> can express
    /// is ever marked so; the wizard's checkbox says why when it cannot be.
    /// </summary>
    public bool ServerSide { get; init; }

    public IReadOnlyList<RuleCondition> Conditions { get; init; } = [];

    public IReadOnlyList<RuleAction> Actions { get; init; } = [];

    public IReadOnlyList<RuleCondition> Exceptions { get; init; } = [];

    /// <summary>
    /// Whether this rule runs over messages being sent rather than messages arriving.
    /// </summary>
    /// <remarks>
    /// The reference's wizard starts a blank rule one of two ways — on messages I receive, or on
    /// messages I send — and they are not the same rule run twice: a send rule sees the copy
    /// being filed in Sent Items, after the message has gone. The two sets never mix, so a rule
    /// written for one is never evaluated by the other.
    /// </remarks>
    public bool AppliesToSent { get; init; }

    /// <summary>Whether the rule stops the ones after it once it has fired.</summary>
    public bool StopsProcessing => Actions.Any(a => a.Kind == RuleActionKind.StopProcessing);

    // ---- The document ---------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>The conditions, actions and exceptions as one JSON document, for the store.</summary>
    /// <remarks>
    /// <see cref="AppliesToSent"/> rides in here rather than in a column of its own: the shape of
    /// a rule belongs in the document, a reader that predates the field sees a rule that applies
    /// to arriving mail — which is what every rule written before it was — and the store needs no
    /// migration for it.
    /// </remarks>
    public string DefinitionJson() => JsonSerializer.Serialize(
        new Definition(Conditions, Actions, Exceptions, AppliesToSent), Json);

    /// <summary>Reads a definition back. A document that will not parse yields an empty rule, which matches nothing.</summary>
    public static MailRule FromDefinition(long id, string name, bool enabled, int ordinal, string json, bool serverSide = false)
    {
        Definition? definition = null;
        try
        {
            definition = JsonSerializer.Deserialize<Definition>(json, Json);
        }
        catch (JsonException)
        {
            // Left null: an unreadable rule is a rule that never fires, and says so in the list.
        }

        return new MailRule
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            Ordinal = ordinal,
            ServerSide = serverSide,
            Conditions = definition?.Conditions ?? [],
            Actions = definition?.Actions ?? [],
            Exceptions = definition?.Exceptions ?? [],
            AppliesToSent = definition?.AppliesToSent ?? false,
        };
    }

    private sealed record Definition(
        IReadOnlyList<RuleCondition> Conditions,
        IReadOnlyList<RuleAction> Actions,
        IReadOnlyList<RuleCondition> Exceptions,
        bool AppliesToSent = false);
}
