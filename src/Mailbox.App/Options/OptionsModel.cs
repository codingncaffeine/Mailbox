namespace Mailbox.App.Options;

/// <summary>
/// A row on an Options page.
/// </summary>
/// <remarks>
/// Pages are described rather than hand-built. Thirteen sections of checkboxes, dropdowns and
/// spinners authored as markup would be thousands of lines that all look the same, and every
/// one would need its own token bindings. Describing them once and rendering them once keeps
/// the pages honest to the reference and makes adding the next one nearly free — the same
/// reasoning as the ribbon layout document.
/// </remarks>
public abstract record OptionRow
{
    /// <summary>Indent level. the reference application nests sub-options one or two steps under their parent.</summary>
    public int Indent { get; init; }

    /// <summary>Renders the ⓘ affordance the reference application puts beside options that need explaining.</summary>
    public bool HasInfo { get; init; }

    /// <summary>Greyed and unclickable.</summary>
    public bool IsDisabled { get; init; }

    /// <summary>
    /// Where this row's value is kept, or null for rows that carry no value.
    /// </summary>
    /// <remarks>
    /// Derived from the label unless given, so declaring a setting stays one line: the label is
    /// already unique within its page, and a key nobody has to invent is a key nobody can
    /// mistype. Set it explicitly when the label is likely to be reworded — a renamed setting
    /// should not silently forget what the user chose.
    /// </remarks>
    public string? Key { get; init; }
}

public sealed record CheckRow(string Label, bool IsChecked = false) : OptionRow;

public sealed record RadioRow(string Group, string Label, bool IsChecked = false) : OptionRow;

public sealed record ComboRow(
    string Label,
    IReadOnlyList<string> Items,
    int Selected = 0,
    double Width = 260,
    double LabelWidth = 200) : OptionRow
{
    /// <summary>
    /// What each entry stands for, when what is stored has to outlive the list's order.
    /// </summary>
    /// <remarks>
    /// A combo keeps its index, which is right for a list this application writes — the reminder
    /// times, the colours. It is wrong for a list the machine supplies: the zone database is
    /// hundreds of entries long and is not the same list on the next machine or after the next
    /// update, so an index into it means a different zone by then. With values, the row keeps the
    /// entry's own text instead.
    /// </remarks>
    public IReadOnlyList<string>? Values { get; init; }
}

public sealed record TextRow(
    string Label,
    string Value = "",
    double Width = 210,
    double LabelWidth = 200,
    string? Placeholder = null) : OptionRow;

public sealed record SpinnerRow(
    string Label,
    int Value,
    int Minimum = 0,
    int Maximum = 999,
    double LabelWidth = 380) : OptionRow;

/// <summary>Plain explanatory text, no control attached.</summary>
public sealed record NoteRow(string Text) : OptionRow;

/// <summary>A bold line introducing the options beneath it, inside a section.</summary>
public sealed record SubHeadingRow(string Text) : OptionRow;

/// <summary>
/// the reference's signature Options row: an icon on the left, a sentence of description, and a
/// button pushed to the right edge that opens a sub-dialog.
/// </summary>
public sealed record ActionRow(
    string Icon,
    string Description,
    string ButtonLabel,
    IReadOnlyList<OptionRow>? Children = null) : OptionRow;

/// <summary>A labelled field with a Browse button beside it.</summary>
public sealed record BrowseRow(string Label, string Value = "", double LabelWidth = 240) : OptionRow;

/// <summary>
/// A placeholder the window fills with a live control. Used where an option has to drive real
/// state rather than sit inert — the theme picker, for one. Named rather than positional so
/// inserting a row above it cannot silently move it.
/// </summary>
public sealed record SlotRow(string SlotId) : OptionRow;

/// <summary>Bold heading followed by a rule running to the right edge.</summary>
public sealed record OptionSection(string Heading, IReadOnlyList<OptionRow> Rows);

/// <summary>One page in the Options rail.</summary>
public sealed record OptionsPage(
    string Id,
    string Title,
    string Icon,
    string Description,
    IReadOnlyList<OptionSection> Sections)
{
    /// <summary>True when the page is described here rather than still to be transcribed.</summary>
    public bool IsAuthored => Sections.Count > 0;
}
