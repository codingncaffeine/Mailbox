using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Mailbox.Core.Commands;

/// <summary>
/// The registry every command lives in. Ribbon layouts, the QAT, keyboard bindings, context
/// menus and plugin contributions all resolve through here by <see cref="CommandId"/>, so
/// enablement and labelling have exactly one source of truth.
/// </summary>
public sealed class CommandCatalog
{
    private readonly Dictionary<CommandId, MailboxCommand> _commands = [];
    private FrozenDictionary<CommandId, MailboxCommand>? _frozen;

    public void Register(MailboxCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_commands.TryAdd(command.Id, command))
        {
            throw new InvalidOperationException(
                $"Command '{command.Id}' is already registered. Ids must be unique across " +
                "built-ins and plugins.");
        }

        _frozen = null;
    }

    public void RegisterRange(IEnumerable<MailboxCommand> commands)
    {
        foreach (var command in commands) Register(command);
    }

    /// <summary>Removes every command owned by a plugin. Used when a plugin is disabled.</summary>
    public int UnregisterPlugin(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var doomed = _commands.Values
            .Where(c => string.Equals(c.OwningPluginId, pluginId, StringComparison.Ordinal))
            .Select(c => c.Id)
            .ToList();

        foreach (var id in doomed) _commands.Remove(id);
        if (doomed.Count > 0) _frozen = null;
        return doomed.Count;
    }

    private FrozenDictionary<CommandId, MailboxCommand> Lookup =>
        _frozen ??= _commands.ToFrozenDictionary();

    public bool TryGet(CommandId id, [NotNullWhen(true)] out MailboxCommand? command) =>
        Lookup.TryGetValue(id, out command);

    public MailboxCommand Get(CommandId id) =>
        TryGet(id, out var command)
            ? command
            : throw new KeyNotFoundException($"No command registered with id '{id}'.");

    public IReadOnlyCollection<MailboxCommand> All => Lookup.Values;

    public int Count => _commands.Count;

    public IEnumerable<MailboxCommand> ForModule(MailboxModule module)
    {
        var scope = module.AsScope();
        return Lookup.Values.Where(c => (c.Scope & scope) != 0);
    }

    /// <summary>
    /// Commands absent from the shipped ribbon layout. These are the additions beyond the reference application
    /// parity — present, searchable and placeable, just not on screen at first run.
    /// </summary>
    public IEnumerable<MailboxCommand> BeyondDefaultLayout =>
        Lookup.Values.Where(c => !c.InDefaultLayout);

    /// <summary>
    /// Substring search over label, description and id, for the Customize Ribbon gallery.
    /// Ordered so label matches beat description matches.
    /// </summary>
    public IReadOnlyList<MailboxCommand> Search(string term, MailboxModule? module = null)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];

        var candidates = module is { } m ? ForModule(m) : Lookup.Values.AsEnumerable();

        return candidates
            .Select(c => (Command: c, Rank: RankMatch(c, term)))
            .Where(x => x.Rank > 0)
            .OrderByDescending(x => x.Rank)
            .ThenBy(x => x.Command.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Command)
            .ToList();
    }

    private static int RankMatch(MailboxCommand command, string term)
    {
        const StringComparison Ci = StringComparison.CurrentCultureIgnoreCase;

        if (command.Label.Equals(term, Ci)) return 4;
        if (command.Label.StartsWith(term, Ci)) return 3;
        if (command.Label.Contains(term, Ci)) return 2;
        if (command.Description.Contains(term, Ci)) return 1;
        if (command.Id.Value.Contains(term, StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

}
