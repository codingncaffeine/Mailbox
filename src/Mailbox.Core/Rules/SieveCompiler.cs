using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mailbox.Core.Rules;

/// <summary>What the compiler needs to know about the account and its server to write Sieve.</summary>
public sealed record SieveContext
{
    /// <summary>The reader's own addresses, for the "my name" conditions.</summary>
    public IReadOnlyList<string> OwnAddresses { get; init; } = [];

    /// <summary>A folder's name on the server, by store id — null for a folder that lives only here.</summary>
    public Func<long, string?> FolderPath { get; init; } = _ => null;

    /// <summary>Where "delete it" files a message: the Deleted Items folder's server name, or null.</summary>
    public string? DeletedItemsPath { get; init; }

    /// <summary>The Sieve extensions the server advertised, lower-cased: "fileinto", "body", "copy", …</summary>
    public IReadOnlySet<string> Extensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool Has(string extension) => Extensions.Contains(extension);
}

/// <summary>One rule turned into Sieve, or the reasons it could not be.</summary>
/// <param name="Block">The rule's <c>if … { … }</c>, or null when it does not compile.</param>
/// <param name="Requires">The extensions the block needs, for the script's <c>require</c>.</param>
/// <param name="Reasons">Why the rule stays on this computer — empty when it compiles.</param>
public sealed record SieveRule(string? Block, IReadOnlySet<string> Requires, IReadOnlyList<string> Reasons)
{
    public bool Compiles => Block is not null;
}

/// <summary>
/// Turns the Rules and Alerts wizard's rules into a Sieve script (RFC 5228) for a server that
/// speaks ManageSieve, so the rules a reader marks "run on the server" keep working while
/// Mailbox is closed.
/// </summary>
/// <remarks>
/// A rule compiles whole or not at all: every condition, exception and action has to have a
/// server-side meaning, or the rule stays on this computer and <see cref="Compile"/> says which
/// clause is why, in the wizard's own words. What does not translate is what needs the screen
/// (alerts, sounds, printing), the local store (categories, "has an attachment", the date and
/// flag conditions that exist for Run Rules Now), or a folder the server has never heard of.
/// <para>
/// The translation keeps the evaluator's meaning where Sieve allows it: "from people" is the
/// address when the value has one and the header text otherwise, "with specific words" is
/// <c>:contains</c>, "my name is in the To box" is the reader's own address in To. Rules run in
/// order and a rule that says stop processing stops. Where two rules both move a message,
/// Sieve files a copy into each folder where the client would move it on; the wizard's rules
/// rarely stack moves, and a stop clause settles it when they do.
/// </para>
/// </remarks>
public static class SieveCompiler
{
    /// <summary>The name the script is stored under on the server.</summary>
    public const string ScriptName = "mailbox";

    /// <summary>The Sieve for one rule, or why there is none.</summary>
    public static SieveRule Compile(MailRule rule, SieveContext context)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);

        var requires = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new List<string>();
        var tests = new List<string>();
        var exceptions = new List<string>();
        var actions = new List<string>();

        foreach (var condition in rule.Conditions)
        {
            if (Test(condition, context, requires) is { } compiled) tests.Add(compiled);
            else reasons.Add(Reason(condition, context));
        }

        foreach (var exception in rule.Exceptions)
        {
            if (Test(exception, context, requires) is { } compiled) exceptions.Add(compiled);
            else reasons.Add(Reason(exception, context));
        }

        foreach (var action in rule.Actions)
        {
            if (Action(action, context, requires) is { } text) actions.Add(text);
            else reasons.Add(Reason(action, context));
        }

        if (reasons.Count > 0) return new SieveRule(null, requires, reasons);
        if (actions.Count == 0) return new SieveRule(null, requires, ["the rule has no action"]);

        var test = tests.Count switch
        {
            0 when exceptions.Count == 0 => "true",
            0 => $"not anyof({string.Join(", ", exceptions)})",
            1 when exceptions.Count == 0 => tests[0],
            _ => exceptions.Count == 0
                ? $"allof({string.Join(", ", tests)})"
                : $"allof({string.Join(", ", tests)}, not anyof({string.Join(", ", exceptions)}))",
        };

        var block = new StringBuilder();
        block.Append("# Rule: ").Append(rule.Name.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
        block.Append("if ").Append(test).Append(" {\n");
        foreach (var action in actions) block.Append("    ").Append(action).Append(";\n");
        block.Append("}\n");

        return new SieveRule(block.ToString(), requires, []);
    }

    /// <summary>
    /// The whole script: a header, the <c>require</c> line, an optional include of the script that
    /// was active before, and every rule that compiles, in order. Rules that do not compile are
    /// left out — the caller has already told the reader which.
    /// </summary>
    /// <param name="include">A script to include first — the one that was active before Mailbox's — or null.</param>
    public static string Script(IEnumerable<MailRule> rules, SieveContext context, string? include = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(context);

        var requires = new SortedSet<string>(StringComparer.Ordinal);
        var blocks = new List<string>();

        foreach (var rule in rules)
        {
            var compiled = Compile(rule, context);
            if (!compiled.Compiles) continue;
            requires.UnionWith(compiled.Requires);
            blocks.Add(compiled.Block!);
        }

        if (include is { Length: > 0 }) requires.Add("include");

        var script = new StringBuilder();
        script.Append("# Rules from Mailbox. Edited by its Rules and Alerts dialog; changes made here are replaced.\n");
        if (requires.Count > 0)
        {
            script.Append("require [").Append(string.Join(", ", requires.Select(Quote))).Append("];\n");
        }

        script.Append('\n');
        if (include is { Length: > 0 })
        {
            script.Append("# The script that was active before, kept running first.\n");
            script.Append("include :personal ").Append(Quote(include)).Append(";\n\n");
        }

        foreach (var block in blocks) script.Append(block).Append('\n');
        return script.ToString();
    }

    /// <summary>A short fingerprint of a script, so the store can tell whether the server has this one.</summary>
    public static string Hash(string script)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script ?? string.Empty)))[..16];

    // ---- Conditions --------------------------------------------------------------------------

    private static string? Test(RuleCondition condition, SieveContext context, ISet<string> requires)
    {
        var values = condition.Values.Where(v => v.Trim().Length > 0).Select(v => v.Trim()).ToList();
        var own = context.OwnAddresses.Where(a => a.Length > 0).ToList();

        switch (condition.Kind)
        {
            case RuleConditionKind.From:
                return values.Count == 0 ? null : AnyOf(values.Select(v => AddressTest("\"from\"", v)));

            case RuleConditionKind.SentTo:
                return values.Count == 0 ? null : AnyOf(values.Select(v => AddressTest("[\"to\", \"cc\"]", v)));

            case RuleConditionKind.SubjectContains:
                return values.Count == 0 ? null : $"header :contains \"subject\" {List(values)}";

            case RuleConditionKind.BodyContains:
                if (values.Count == 0 || !context.Has("body")) return null;
                requires.Add("body");
                return $"body :text :contains {List(values)}";

            case RuleConditionKind.SubjectOrBodyContains:
                if (values.Count == 0 || !context.Has("body")) return null;
                requires.Add("body");
                return $"anyof(header :contains \"subject\" {List(values)}, body :text :contains {List(values)})";

            case RuleConditionKind.SenderAddressContains:
                return values.Count == 0 ? null : $"address :all :contains \"from\" {List(values)}";

            case RuleConditionKind.RecipientAddressContains:
                return values.Count == 0 ? null : $"address :all :contains [\"to\", \"cc\"] {List(values)}";

            case RuleConditionKind.SentOnlyToMe:
                if (own.Count == 0 || !context.Has("relational") || !context.Has("comparator-i;ascii-numeric")) return null;
                requires.Add("relational");
                requires.Add("comparator-i;ascii-numeric");
                return $"allof(address :all :is [\"to\", \"cc\"] {List(own)}, address :count \"eq\" :comparator \"i;ascii-numeric\" [\"to\", \"cc\"] \"1\")";

            case RuleConditionKind.MyNameInTo:
                return own.Count == 0 ? null : $"address :all :is \"to\" {List(own)}";

            case RuleConditionKind.MyNameInCc:
                return own.Count == 0 ? null : $"address :all :is \"cc\" {List(own)}";

            case RuleConditionKind.MyNameInToOrCc:
                return own.Count == 0 ? null : $"address :all :is [\"to\", \"cc\"] {List(own)}";

            case RuleConditionKind.MyNameNotInTo:
                return own.Count == 0 ? null : $"not address :all :is \"to\" {List(own)}";

            case RuleConditionKind.Importance:
                return condition.Level switch
                {
                    2 => "anyof(header :is \"importance\" \"high\", header :matches \"x-priority\" [\"1*\", \"2*\"])",
                    0 => "anyof(header :is \"importance\" \"low\", header :matches \"x-priority\" [\"4*\", \"5*\"])",
                    1 => "not anyof(header :is \"importance\" [\"high\", \"low\"], header :matches \"x-priority\" [\"1*\", \"2*\", \"4*\", \"5*\"])",
                    _ => null,
                };

            case RuleConditionKind.Sensitivity:
                return condition.Level switch
                {
                    1 => "header :is \"sensitivity\" \"personal\"",
                    2 => "header :is \"sensitivity\" \"private\"",
                    3 => "header :is \"sensitivity\" [\"company-confidential\", \"confidential\"]",
                    0 => "not header :is \"sensitivity\" [\"personal\", \"private\", \"company-confidential\", \"confidential\"]",
                    _ => null,
                };

            case RuleConditionKind.SizeBetween:
            {
                var parts = new List<string>();
                if (condition.Min is { } min && min > 0) parts.Add($"size :over {Math.Max(0, min * 1024 - 1).ToString(CultureInfo.InvariantCulture)}");
                if (condition.Max is { } max) parts.Add($"size :under {(max * 1024 + 1).ToString(CultureInfo.InvariantCulture)}");
                return parts.Count switch { 0 => "true", 1 => parts[0], _ => $"allof({parts[0]}, {parts[1]})" };
            }

            // What the server cannot see: the whole header block as one text, an attachment,
            // a date span, a category, a flag.
            default:
                return null;
        }
    }

    /// <summary>
    /// "From people" and "sent to people": an address is matched whole, an <c>@domain</c> entry
    /// by domain, and anything else — a name — as text in the header, as the evaluator does.
    /// </summary>
    private static string AddressTest(string headers, string value)
    {
        var wanted = value.Trim();
        if (wanted.StartsWith('@') && wanted.Length > 1)
        {
            return $"address :domain :is {headers} {Quote(wanted[1..])}";
        }

        var open = wanted.IndexOf('<');
        var close = wanted.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            var inner = wanted[(open + 1)..close].Trim();
            var outer = wanted[..open].Trim().Trim('"');
            return outer.Length > 0
                ? $"anyof(address :all :is {headers} {Quote(inner)}, header :contains {headers} {Quote(outer)})"
                : $"address :all :is {headers} {Quote(inner)}";
        }

        return wanted.Contains('@')
            ? $"address :all :is {headers} {Quote(wanted)}"
            : $"header :contains {headers} {Quote(wanted)}";
    }

    // ---- Actions -------------------------------------------------------------------------------

    private static string? Action(RuleAction action, SieveContext context, ISet<string> requires)
    {
        var values = action.Values.Where(v => v.Trim().Length > 0).Select(v => v.Trim()).ToList();

        switch (action.Kind)
        {
            case RuleActionKind.MoveToFolder:
                if (action.FolderId is not { } moveTo || context.FolderPath(moveTo) is not { Length: > 0 } movePath) return null;
                requires.Add("fileinto");
                return $"fileinto {Quote(movePath)}";

            case RuleActionKind.CopyToFolder:
                if (action.FolderId is not { } copyTo || context.FolderPath(copyTo) is not { Length: > 0 } copyPath || !context.Has("copy")) return null;
                requires.Add("fileinto");
                requires.Add("copy");
                return $"fileinto :copy {Quote(copyPath)}";

            case RuleActionKind.Delete:
                if (context.DeletedItemsPath is not { Length: > 0 } deleted) return null;
                requires.Add("fileinto");
                return $"fileinto {Quote(deleted)}";

            case RuleActionKind.PermanentlyDelete:
                return "discard";

            case RuleActionKind.ForwardTo:
            case RuleActionKind.RedirectTo:
                if (values.Count == 0 || !context.Has("copy")) return null;
                requires.Add("copy");
                return string.Join(";\n    ", values.Select(v => $"redirect :copy {Quote(BareAddress(v))}"));

            case RuleActionKind.MarkAsRead:
                if (!context.Has("imap4flags")) return null;
                requires.Add("imap4flags");
                return "addflag \"\\\\Seen\"";

            case RuleActionKind.FlagForFollowUp:
                if (!context.Has("imap4flags")) return null;
                requires.Add("imap4flags");
                return "addflag \"\\\\Flagged\"";

            case RuleActionKind.ClearFlag:
                if (!context.Has("imap4flags")) return null;
                requires.Add("imap4flags");
                return "removeflag \"\\\\Flagged\"";

            case RuleActionKind.StopProcessing:
                return "stop";

            // Alerts, sounds, printing, categories, forwarding as an attachment, importance:
            // this computer's, not the server's.
            default:
                return null;
        }
    }

    // ---- Reasons -------------------------------------------------------------------------------

    private static string Reason(RuleCondition condition, SieveContext context)
    {
        var clause = RuleDescription.Template(condition.Kind);
        return condition.Kind switch
        {
            RuleConditionKind.BodyContains or RuleConditionKind.SubjectOrBodyContains when !context.Has("body")
                => $"the server can't search message text ('{clause}' needs the 'body' extension)",
            RuleConditionKind.SentOnlyToMe when !context.Has("relational") || !context.Has("comparator-i;ascii-numeric")
                => $"the server can't count recipients ('{clause}' needs the 'relational' extension)",
            RuleConditionKind.SentOnlyToMe or RuleConditionKind.MyNameInTo or RuleConditionKind.MyNameInCc
                or RuleConditionKind.MyNameInToOrCc or RuleConditionKind.MyNameNotInTo when context.OwnAddresses.Count == 0
                => $"'{clause}' needs the account's address",
            RuleConditionKind.From or RuleConditionKind.SentTo or RuleConditionKind.SubjectContains
                or RuleConditionKind.BodyContains or RuleConditionKind.SubjectOrBodyContains
                or RuleConditionKind.SenderAddressContains or RuleConditionKind.RecipientAddressContains
                when condition.Values.All(v => v.Trim().Length == 0)
                => $"'{clause}' has no value yet",
            RuleConditionKind.HeaderContains => $"'{clause}' can't be tested on the server",
            RuleConditionKind.HasAttachment => $"'{clause}' can't be tested on the server",
            RuleConditionKind.ReceivedBetween or RuleConditionKind.AssignedToCategory or RuleConditionKind.Flagged
                => $"'{clause}' is checked on this computer",
            RuleConditionKind.Importance or RuleConditionKind.Sensitivity => $"'{clause}' has no level yet",
            _ => $"'{clause}' can't be tested on the server",
        };
    }

    private static string Reason(RuleAction action, SieveContext context)
    {
        var clause = RuleDescription.Template(action.Kind);
        return action.Kind switch
        {
            RuleActionKind.MoveToFolder or RuleActionKind.CopyToFolder when action.FolderId is null
                => $"'{clause}' has no folder yet",
            RuleActionKind.MoveToFolder or RuleActionKind.CopyToFolder when action.FolderId is { } id && context.FolderPath(id) is not { Length: > 0 }
                => $"the folder \"{action.FolderName ?? "?"}\" isn't on the server",
            RuleActionKind.CopyToFolder or RuleActionKind.ForwardTo or RuleActionKind.RedirectTo when !context.Has("copy")
                => $"the server can't keep a copy ('{clause}' needs the 'copy' extension)",
            RuleActionKind.ForwardTo or RuleActionKind.RedirectTo when action.Values.All(v => v.Trim().Length == 0)
                => $"'{clause}' has no address yet",
            RuleActionKind.Delete => "the Deleted Items folder isn't on the server",
            RuleActionKind.MarkAsRead or RuleActionKind.FlagForFollowUp or RuleActionKind.ClearFlag
                => $"the server can't set flags ('{clause}' needs the 'imap4flags' extension)",
            RuleActionKind.DisplayAlert or RuleActionKind.DesktopAlert or RuleActionKind.PlaySound or RuleActionKind.Print
                => $"'{clause}' happens on this computer",
            RuleActionKind.AssignCategory or RuleActionKind.ClearCategories or RuleActionKind.MarkImportance
                or RuleActionKind.ForwardAsAttachmentTo
                => $"'{clause}' is done on this computer",
            _ => $"'{clause}' can't be done on the server",
        };
    }

    // ---- Text ----------------------------------------------------------------------------------

    private static string AnyOf(IEnumerable<string> tests)
    {
        var list = tests.ToList();
        return list.Count == 1 ? list[0] : $"anyof({string.Join(", ", list)})";
    }

    private static string List(IReadOnlyList<string> values)
        => values.Count == 1 ? Quote(values[0]) : $"[{string.Join(", ", values.Select(Quote))}]";

    /// <summary>The address out of "Name &lt;address&gt;", or the text as given.</summary>
    private static string BareAddress(string value)
    {
        var open = value.IndexOf('<');
        var close = value.LastIndexOf('>');
        return open >= 0 && close > open ? value[(open + 1)..close].Trim() : value.Trim();
    }

    /// <summary>A Sieve quoted string: backslash and the quote escaped, line breaks folded to spaces.</summary>
    public static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\r': case '\n': builder.Append(' '); break;
                default: builder.Append(c); break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
