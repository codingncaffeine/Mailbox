using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Core.Ribbon;

/// <summary>Where the Quick Access Toolbar sits, which the reference makes a user choice.</summary>
public enum QuickAccessPlacement
{
    /// <summary>In the title bar, beside the application icon. The shipped default.</summary>
    AboveRibbon,

    /// <summary>On its own strip under the ribbon, nearer the content it acts on.</summary>
    BelowRibbon,
}

/// <summary>
/// The Quick Access Toolbar as customization state: which commands, in what order, where the
/// bar sits, and whether it is shown at all.
/// </summary>
/// <remarks>
/// Customization state is a plain file, like a theme — portable, diffable, restorable from a
/// backup. The commands are stored as their stable ids in a single comma-separated string, so
/// the settings file stays something a person can read and edit.
/// <para>
/// An empty toolbar is a real choice and is not the same as never having customized one, so the
/// key's presence is what distinguishes them; removing every command does not silently restore
/// the shipped set on the next launch.
/// </para>
/// </remarks>
public sealed class QuickAccessLayout
{
    public const string CommandsKey = "ribbon.qat.commands";
    public const string PlacementKey = "ribbon.qat.placement";
    public const string VisibleKey = "ribbon.qat.visible";

    private readonly SettingsStore _settings;
    private readonly List<CommandId> _commands;

    private QuickAccessPlacement _placement;
    private bool _visible;

    public QuickAccessLayout(SettingsStore settings, IReadOnlyList<CommandId> shipped)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(shipped);

        _settings = settings;
        Shipped = [.. shipped];

        _commands = settings.Has(CommandsKey)
            ? Parse(settings.GetString(CommandsKey))
            : [.. shipped];

        _placement = string.Equals(settings.GetString(PlacementKey), "below", StringComparison.Ordinal)
            ? QuickAccessPlacement.BelowRibbon
            : QuickAccessPlacement.AboveRibbon;

        _visible = settings.GetBool(VisibleKey, fallback: true);
    }

    /// <summary>
    /// Poses the toolbar without writing to the settings file, for the fidelity harness.
    /// </summary>
    /// <remarks>
    /// A capture that persisted its own arrangement would leave the next run in whatever state
    /// the last photograph wanted, which is a nasty thing to debug.
    /// </remarks>
    public void Pose(QuickAccessPlacement? placement = null, bool? visible = null)
    {
        if (placement is { } wanted) _placement = wanted;
        if (visible is { } shown) _visible = shown;
    }

    /// <summary>The layout document's own toolbar, for Reset.</summary>
    public IReadOnlyList<CommandId> Shipped { get; }

    public IReadOnlyList<CommandId> Commands => _commands;

    public QuickAccessPlacement Placement
    {
        get => _placement;
        set
        {
            _placement = value;
            _settings.Set(
                PlacementKey, value == QuickAccessPlacement.BelowRibbon ? "below" : "above");
        }
    }

    public bool IsVisible
    {
        get => _visible;
        set
        {
            _visible = value;
            _settings.Set(VisibleKey, value);
        }
    }

    public bool Contains(CommandId id) => _commands.Contains(id);

    /// <summary>
    /// Adds a command or removes it, which is what a tick in the customize flyout means.
    /// Added commands go on the end, because the reference appends rather than restoring a
    /// command to some remembered position.
    /// </summary>
    public void Toggle(CommandId id)
    {
        if (!_commands.Remove(id)) _commands.Add(id);
        Save();
    }

    public void Add(CommandId id)
    {
        if (_commands.Contains(id)) return;
        _commands.Add(id);
        Save();
    }

    public void Remove(CommandId id)
    {
        if (_commands.Remove(id)) Save();
    }

    /// <summary>
    /// Appends a rule. Unlike a command, several may sit on one toolbar, so this does not go
    /// through <see cref="Add"/> and its "already placed" check.
    /// </summary>
    public void AddSeparator()
    {
        _commands.Add(RibbonItem.SeparatorId);
        Save();
    }

    /// <summary>
    /// Removes whatever is at <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// By position rather than by id, because rules repeat: removing the third one by id would
    /// take the first.
    /// </remarks>
    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= _commands.Count) return false;

        _commands.RemoveAt(index);
        Save();
        return true;
    }

    /// <summary>Moves whatever is at <paramref name="index"/> by <paramref name="delta"/>.</summary>
    public bool MoveAt(int index, int delta)
    {
        var to = index + delta;
        if (index < 0 || index >= _commands.Count || to < 0 || to >= _commands.Count) return false;

        var moved = _commands[index];
        _commands.RemoveAt(index);
        _commands.Insert(to, moved);
        Save();
        return true;
    }

    /// <summary>Moves a command by <paramref name="delta"/> places, clamped to the ends.</summary>
    public bool Move(CommandId id, int delta)
    {
        var from = _commands.IndexOf(id);
        if (from < 0 || delta == 0) return false;

        var to = Math.Clamp(from + delta, 0, _commands.Count - 1);
        if (to == from) return false;

        _commands.RemoveAt(from);
        _commands.Insert(to, id);
        Save();
        return true;
    }

    public void Replace(IEnumerable<CommandId> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands.Clear();
        _commands.AddRange(commands);
        Save();
    }

    /// <summary>Back to the shipped toolbar, placement and visibility included.</summary>
    public void Reset()
    {
        _commands.Clear();
        _commands.AddRange(Shipped);
        Save();
        Placement = QuickAccessPlacement.AboveRibbon;
        IsVisible = true;
    }

    private void Save()
        => _settings.Set(CommandsKey, string.Join(",", _commands.Select(c => c.Value)));

    /// <summary>
    /// Reads the stored ids, dropping any the id format rejects.
    /// </summary>
    /// <remarks>
    /// The settings file is meant to be editable by hand, so it is allowed to be wrong. A
    /// malformed id is a note in the log and one missing toolbar button, never a reason the
    /// application will not start.
    /// </remarks>
    private static List<CommandId> Parse(string stored)
    {
        var parsed = new List<CommandId>();

        foreach (var value in stored.Split(
                     ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                parsed.Add(new CommandId(value));
            }
            catch (ArgumentException)
            {
                Log.Warn($"{CommandsKey} lists '{value}', which is not a valid command id.");
            }
        }

        return parsed;
    }
}
