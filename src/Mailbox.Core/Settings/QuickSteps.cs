using System.Text.Json;
using System.Text.Json.Serialization;
using Mailbox.Core.Commands;

namespace Mailbox.Core.Settings;

/// <summary>What one step of a Quick Step does. The reference's list, less what needs a module not yet built.</summary>
public enum QuickStepKind
{
    // Filing
    MoveToFolder,
    CopyToFolder,
    Delete,
    PermanentlyDelete,

    // Change status
    MarkAsRead,
    MarkAsUnread,
    SetImportance,

    // Categories and flags
    Categorize,
    ClearCategories,
    FlagMessage,
    ClearFlags,
    MarkComplete,

    // Respond
    NewMessage,
    Forward,
    Reply,
    ReplyAll,
    ForwardAsAttachment,

    // Conversation
    AlwaysMoveFromSender,

    // Commands. Appended after everything the reference's own picker offers: any catalogue
    // command as an action, which is how a plugin's command becomes part of a step —
    // and how Snooze or Message Source could, additions being ordinary commands too.
    RunCommand,
}

/// <summary>One action in a Quick Step, with the value it carries.</summary>
public sealed record QuickStepAction(QuickStepKind Kind)
{
    /// <summary>The folder a move or copy goes to, by store id in the account it was chosen from.</summary>
    public long? FolderId { get; init; }

    /// <summary>The folder's name, for the description and for finding it by name in another account.</summary>
    public string? FolderName { get; init; }

    /// <summary>Addresses for a message or forward, or category names.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>The importance (0 low, 1 normal, 2 high), or the flag's due-in days (null for no date).</summary>
    public int? Level { get; init; }

    /// <summary>The subject a New Message or Forward opens with, if any.</summary>
    public string? Subject { get; init; }

    /// <summary>Whether a step that asks — a folder or address left blank — is still to be set up.</summary>
    public bool NeedsSetup => Kind switch
    {
        QuickStepKind.MoveToFolder or QuickStepKind.CopyToFolder => FolderId is null && string.IsNullOrEmpty(FolderName),
        QuickStepKind.NewMessage or QuickStepKind.Forward or QuickStepKind.ForwardAsAttachment => Values.Count == 0,
        QuickStepKind.RunCommand => Values.Count == 0,
        _ => false,
    };

    /// <summary>The line the Manage dialog writes for the action.</summary>
    public string Describe() => Kind switch
    {
        QuickStepKind.MoveToFolder => $"Move to folder: {FolderName ?? "(choose on first use)"}",
        QuickStepKind.CopyToFolder => $"Copy to folder: {FolderName ?? "(choose on first use)"}",
        QuickStepKind.Delete => "Delete message",
        QuickStepKind.PermanentlyDelete => "Permanently delete message",
        QuickStepKind.MarkAsRead => "Mark as read",
        QuickStepKind.MarkAsUnread => "Mark as unread",
        QuickStepKind.SetImportance => $"Set importance: {Level switch { 0 => "Low", 2 => "High", _ => "Normal" }}",
        QuickStepKind.Categorize => $"Categorize message: {(Values.Count == 0 ? "(choose)" : string.Join(", ", Values))}",
        QuickStepKind.ClearCategories => "Clear categories",
        QuickStepKind.FlagMessage => $"Flag message: {Level switch { null => "no date", 0 => "today", 1 => "tomorrow", { } n => $"in {n} days" }}",
        QuickStepKind.ClearFlags => "Clear flags on message",
        QuickStepKind.MarkComplete => "Mark complete",
        QuickStepKind.NewMessage => $"New message to: {(Values.Count == 0 ? "(choose on first use)" : string.Join("; ", Values))}",
        QuickStepKind.Forward => $"Forward to: {(Values.Count == 0 ? "(choose on first use)" : string.Join("; ", Values))}",
        QuickStepKind.Reply => "Reply",
        QuickStepKind.ReplyAll => "Reply All",
        QuickStepKind.ForwardAsAttachment => $"Forward as attachment to: {(Values.Count == 0 ? "(choose on first use)" : string.Join("; ", Values))}",
        QuickStepKind.AlwaysMoveFromSender => "Always move messages from sender",

        // Values carries the id and then the label, so the dialog's line reads like a person
        // and the runner still has the id whatever the command is renamed to.
        QuickStepKind.RunCommand => $"Run: {(Values.Count > 1 ? Values[1] : Values.FirstOrDefault() ?? "(choose on first use)")}",
        _ => Kind.ToString(),
    };

    /// <summary>The label the Edit dialog's action picker gives a kind.</summary>
    public static string Label(QuickStepKind kind) => kind switch
    {
        QuickStepKind.MoveToFolder => "Move to folder",
        QuickStepKind.CopyToFolder => "Copy to folder",
        QuickStepKind.Delete => "Delete message",
        QuickStepKind.PermanentlyDelete => "Permanently delete message",
        QuickStepKind.MarkAsRead => "Mark as read",
        QuickStepKind.MarkAsUnread => "Mark as unread",
        QuickStepKind.SetImportance => "Set importance",
        QuickStepKind.Categorize => "Categorize message",
        QuickStepKind.ClearCategories => "Clear Categories",
        QuickStepKind.FlagMessage => "Flag Message",
        QuickStepKind.ClearFlags => "Clear flags on message",
        QuickStepKind.MarkComplete => "Mark complete",
        QuickStepKind.NewMessage => "New Message",
        QuickStepKind.Forward => "Forward",
        QuickStepKind.Reply => "Reply",
        QuickStepKind.ReplyAll => "Reply All",
        QuickStepKind.ForwardAsAttachment => "Forward message as an attachment",
        QuickStepKind.AlwaysMoveFromSender => "Always Move Messages from Sender",
        QuickStepKind.RunCommand => "Run a command",
        _ => kind.ToString(),
    };

    /// <summary>The group heading a kind sits under in the picker, as the reference groups them.</summary>
    public static string Group(QuickStepKind kind) => kind switch
    {
        QuickStepKind.MoveToFolder or QuickStepKind.CopyToFolder or QuickStepKind.Delete or QuickStepKind.PermanentlyDelete => "Filing",
        QuickStepKind.MarkAsRead or QuickStepKind.MarkAsUnread or QuickStepKind.SetImportance => "Change Status",
        QuickStepKind.Categorize or QuickStepKind.ClearCategories or QuickStepKind.FlagMessage or QuickStepKind.ClearFlags or QuickStepKind.MarkComplete
            => "Categories, Tasks and Flags",
        QuickStepKind.AlwaysMoveFromSender => "Conversation",
        QuickStepKind.RunCommand => "Commands",
        _ => "Respond",
    };
}

/// <summary>
/// A Quick Step: a name, an icon, a shortcut, a tooltip and the actions it runs over the
/// selection, in order — the reference's gallery entries.
/// </summary>
public sealed record QuickStep
{
    /// <summary>A stable id, and the command the step is reached by.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>An icon name from the glyph map.</summary>
    public string Icon { get; init; } = "quicksteps";

    /// <summary>1 to 9 for Ctrl+Shift+N, or null for no shortcut.</summary>
    public int? Shortcut { get; init; }

    public string Tooltip { get; init; } = string.Empty;

    public IReadOnlyList<QuickStepAction> Actions { get; init; } = [];

    /// <summary>The command id the step is placed and pressed by.</summary>
    public CommandId CommandId => new(Id.StartsWith("mail.", StringComparison.Ordinal) ? Id : "quickstep." + Id);

    /// <summary>The step as a catalogue command: what the ribbon, the QAT and the shortcut editor see.</summary>
    public MailboxCommand ToCommand() => new()
    {
        Id = CommandId,
        Label = Name,
        Description = Tooltip.Length > 0 ? Tooltip : "Quick Step: " + string.Join("; ", Actions.Select(a => a.Describe())),
        Icon = Icon,
        Category = "Quick Steps",
        Scope = ModuleScope.Mail,
        RequiresSelection = Actions.Any(a => a.Kind is not (QuickStepKind.NewMessage or QuickStepKind.RunCommand)),
        DefaultGesture = Shortcut is { } n ? $"Ctrl+Shift+{n}" : null,
        InDefaultLayout = false,
    };

    /// <summary>Whether any action still needs the reader to choose a folder or an address.</summary>
    public bool NeedsSetup => Actions.Any(a => a.NeedsSetup);
}

/// <summary>
/// The Quick Steps, persisted with the rest of the preferences as one JSON value — a plain,
/// diffable file like the ribbon's, as every customization is kept.
/// </summary>
public sealed class QuickSteps
{
    public const string Key = "quicksteps";

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// What ships: the reference's five, in its order. The first three carry the ids of the
    /// commands the shipped ribbon layout already places, so first run is unchanged; the folder
    /// and the addresses are chosen on first use, as the reference asks.
    /// </summary>
    public static IReadOnlyList<QuickStep> Defaults { get; } =
    [
        new()
        {
            Id = ViewCommands.MoveToQuick.Id.Value,
            Name = "Move to: ?",
            Icon = "move",
            Tooltip = "Moves selected email to a folder after marking the email as read.",
            Actions = [new(QuickStepKind.MoveToFolder), new(QuickStepKind.MarkAsRead)],
        },
        new()
        {
            Id = ViewCommands.ToManager.Id.Value,
            Name = "To Manager",
            Icon = "forward",
            Tooltip = "Forwards selected email to your manager.",
            Actions = [new(QuickStepKind.Forward)],
        },
        new()
        {
            Id = ViewCommands.TeamEmail.Id.Value,
            Name = "Team Email",
            Icon = "mail",
            Tooltip = "Creates a new email to your team.",
            Actions = [new(QuickStepKind.NewMessage)],
        },
        new()
        {
            Id = "done",
            Name = "Done",
            Icon = "mark-complete",
            Tooltip = "Marks selected email as complete and read, and moves it to a folder.",
            Actions = [new(QuickStepKind.MoveToFolder), new(QuickStepKind.MarkComplete), new(QuickStepKind.MarkAsRead)],
        },
        new()
        {
            Id = "replydelete",
            Name = "Reply & Delete",
            Icon = "reply",
            Tooltip = "Replies to the sender and deletes the original message.",
            Actions = [new(QuickStepKind.Reply), new(QuickStepKind.Delete)],
        },
    ];

    private readonly SettingsStore _settings;
    private List<QuickStep> _steps;

    public QuickSteps(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _steps = settings.Has(Key) ? Parse(settings.GetString(Key)) : [.. Defaults];
    }

    /// <summary>Raised after the list changes, so the ribbon and the catalogue can follow.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<QuickStep> All => _steps;

    public QuickStep? Find(string id) => _steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public QuickStep? FindByCommand(CommandId command) => _steps.FirstOrDefault(s => s.CommandId == command);

    /// <summary>A new step's id: short, unique, and safe in a command id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("n")[..8];

    public void Replace(IEnumerable<QuickStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = [.. steps];
        Save();
    }

    /// <summary>Puts one step in, in place of the one with its id or at the end.</summary>
    public void Upsert(QuickStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var index = _steps.FindIndex(s => s.Id == step.Id);
        if (index >= 0) _steps[index] = step;
        else _steps.Add(step);
        Save();
    }

    public void Remove(string id)
    {
        if (_steps.RemoveAll(s => s.Id == id) > 0) Save();
    }

    public void Reset() => Replace(Defaults);

    private void Save()
    {
        _settings.Set(Key, JsonSerializer.Serialize(_steps, Json));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<QuickStep> Parse(string stored)
    {
        try
        {
            return JsonSerializer.Deserialize<List<QuickStep>>(stored, Json)?.Where(s => s.Id.Length > 0).ToList() ?? [.. Defaults];
        }
        catch (JsonException)
        {
            return [.. Defaults];
        }
    }
}
