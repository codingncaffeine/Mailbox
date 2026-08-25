using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailbox.Core.Rules;

/// <summary>
/// Rules as a file: what the Rules and Alerts dialog's Options button writes and reads.
/// </summary>
/// <remarks>
/// A set of rules somebody has spent years on is the thing they would most want to carry to
/// another machine, and nothing else here can move one. The reference's own format is an
/// undocumented binary, so this is our own JSON — readable, diffable, and something a person can
/// fix by hand when a folder name changed.
/// <para>
/// <b>Nothing about one store travels.</b> Ids are dropped, ordinals are the order in the file,
/// and <c>ServerSide</c> is dropped too: whether a rule can run on the server is a fact about the
/// account it lands in, not about the rule, and carrying a true into an account whose server
/// cannot express it would leave a rule that claims to run somewhere it does not.
/// </para>
/// </remarks>
public static class RuleTransfer
{
    /// <summary>Bumped only if the shape changes; a reader refuses a version it does not know.</summary>
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The rules as a document, in the order given.</summary>
    public static string Write(IReadOnlyList<MailRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var document = new Document(
            CurrentVersion,
            [.. rules.Select(r => new Entry(r.Name, r.Enabled, r.AppliesToSent, r.Conditions, r.Actions, r.Exceptions))]);

        return JsonSerializer.Serialize(document, Json);
    }

    /// <summary>
    /// The rules a document holds, ready to be added to an account.
    /// </summary>
    /// <remarks>
    /// Throws on anything it cannot read, rather than returning what it managed: half a rule set
    /// imported silently is worse than none, because the half that is missing is the half nobody
    /// notices until the mail it was filing piles up.
    /// </remarks>
    public static IReadOnlyList<MailRule> Read(string document)
    {
        var read = JsonSerializer.Deserialize<Document>(document, Json)
                   ?? throw new JsonException("The file holds no rules.");

        if (read.Version > CurrentVersion)
        {
            throw new JsonException($"These rules were written by a newer version of Mailbox (format {read.Version}).");
        }

        return
        [
            .. read.Rules.Select((entry, index) => new MailRule
            {
                Name = entry.Name is { Length: > 0 } named ? named : $"Rule {index + 1}",
                Enabled = entry.Enabled,
                AppliesToSent = entry.AppliesToSent,
                Ordinal = index,
                Conditions = entry.Conditions ?? [],
                Actions = entry.Actions ?? [],
                Exceptions = entry.Exceptions ?? [],
            }),
        ];
    }

    private sealed record Document(int Version, IReadOnlyList<Entry> Rules);

    private sealed record Entry(
        string Name,
        bool Enabled,
        bool AppliesToSent,
        IReadOnlyList<RuleCondition>? Conditions,
        IReadOnlyList<RuleAction>? Actions,
        IReadOnlyList<RuleCondition>? Exceptions);
}
