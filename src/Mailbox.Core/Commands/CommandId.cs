namespace Mailbox.Core.Commands;

/// <summary>
/// A stable identifier for a command. These are persisted in ribbon layouts, keyboard
/// bindings and plugin manifests, so an id is API: once shipped it does not change.
/// </summary>
/// <remarks>
/// Format is <c>area.verb</c> or <c>area.noun.verb</c>, lowercase, dot separated —
/// <c>mail.reply</c>, <c>mail.reply.all</c>, <c>calendar.appointment.new</c>. Plugin
/// commands are namespaced by the plugin id: <c>plugin.&lt;pluginId&gt;.&lt;command&gt;</c>.
/// </remarks>
public readonly record struct CommandId
{
    public string Value { get; }

    public CommandId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!IsWellFormed(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid command id. Expected lowercase dot-separated " +
                "segments, e.g. 'mail.reply.all'.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>The leading segment, used to group commands in the customization gallery.</summary>
    public string Area => Value[..Value.IndexOf('.')];

    public bool IsPluginCommand => Value.StartsWith("plugin.", StringComparison.Ordinal);

    private static bool IsWellFormed(string value)
    {
        if (value.Length == 0 || value[0] == '.' || value[^1] == '.') return false;
        if (!value.Contains('.')) return false;

        var previousWasDot = false;
        foreach (var c in value)
        {
            if (c == '.')
            {
                if (previousWasDot) return false;
                previousWasDot = true;
                continue;
            }

            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)) return false;
            previousWasDot = false;
        }

        return true;
    }

    public override string ToString() => Value;

    public static implicit operator string(CommandId id) => id.Value;
}
