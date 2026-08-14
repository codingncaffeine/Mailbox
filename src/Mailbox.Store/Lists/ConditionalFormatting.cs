namespace Mailbox.Store.Lists;

/// <summary>How a row should be drawn when a rule matches it.</summary>
public sealed record RowFormat(
    string Name,
    bool Bold = false,
    bool Italic = false,
    string? ColourToken = null)
{
    /// <summary>Nothing applied. The row draws as the theme says.</summary>
    public static readonly RowFormat None = new("Default");
}

/// <summary>One rule: a name, a condition, and what to do when it matches.</summary>
public sealed record FormattingRule(string Name, Func<IArrangeable, bool> Matches, RowFormat Format)
{
    /// <summary>Rules can be switched off without being deleted, as the reference allows.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Per-row conditional formatting.
/// </summary>
/// <remarks>
/// First match wins, in list order, which is what makes the order meaningful and what the
/// reference's Move Up and Move Down buttons are for. A rule that matched everything and sat at
/// the top would make every rule under it dead, so order is the user's to control rather than
/// something to be clever about.
/// <para>
/// Colours are named as tokens rather than as values. A rule that stored <c>#FF0000</c> would
/// be unreadable in the Black theme and there would be nothing to do about it.
/// </para>
/// </remarks>
public sealed class ConditionalFormatting
{
    private readonly List<FormattingRule> _rules;

    public ConditionalFormatting(IEnumerable<FormattingRule>? rules = null)
        => _rules = [.. rules ?? Defaults()];

    public IReadOnlyList<FormattingRule> Rules => _rules;

    /// <summary>
    /// The reference's own two, which between them cover what most people would write by hand.
    /// Unread is bold and blue; anything overdue is the only other thing it ships with.
    /// </summary>
    public static IReadOnlyList<FormattingRule> Defaults() =>
    [
        new("Unread messages",
            row => row is IThreadable { IsUnread: true },
            new RowFormat("Unread", Bold: true, ColourToken: "list.row.unread.text")),
    ];

    /// <summary>The first rule that matches, or nothing.</summary>
    public RowFormat For(IArrangeable row)
    {
        foreach (var rule in _rules)
        {
            if (rule.IsEnabled && rule.Matches(row)) return rule.Format;
        }

        return RowFormat.None;
    }

    public void Add(FormattingRule rule) => _rules.Add(rule);

    public bool Remove(string name) => _rules.RemoveAll(r => r.Name == name) > 0;

    /// <summary>
    /// Moves a rule, which changes which one wins. Removed and reinserted rather than swapped,
    /// so a move of more than one place means what it says.
    /// </summary>
    public void Move(string name, int direction)
    {
        var index = _rules.FindIndex(r => r.Name == name);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _rules.Count) return;

        var rule = _rules[index];
        _rules.RemoveAt(index);
        _rules.Insert(target, rule);
    }
}
