using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailbox.Core.Views;

/// <summary>The three shapes a message list comes in — the reference's Change View gallery.</summary>
public enum ViewLayout
{
    /// <summary>A three-line card in a narrow list; the column line with a preview beneath in a wide one.</summary>
    Compact,

    /// <summary>One line per message, in columns.</summary>
    Single,

    /// <summary>The column line with the preview beneath, whatever the width.</summary>
    Preview,
}

/// <summary>When Compact draws the card and when the line — Other Settings' three radios.</summary>
public enum CompactMode
{
    /// <summary>The card below the width threshold, the line above it.</summary>
    Auto,
    AlwaysCompact,
    AlwaysSingleLine,
}

/// <summary>How a date column writes its dates.</summary>
public enum DateFormat
{
    /// <summary>A time today, a weekday this week, else the date — the reference's default.</summary>
    BestFit,
    Short,
    Long,
    TimeOnly,
}

/// <summary>One column of the line layout: which field, how wide.</summary>
public sealed record ViewColumn(string Id, double Width);

/// <summary>What Format Columns says about one column: a label of the reader's own, a date format.</summary>
public sealed record ColumnFormat
{
    public string? Label { get; init; }
    public DateFormat DateFormat { get; init; } = DateFormat.BestFit;
}

/// <summary>
/// One conditional-formatting rule: a name, a switch, a font, and the condition — in the search
/// box's own syntax — a message must meet for the row to be drawn that way.
/// </summary>
public sealed record ConditionalFormat(string Name)
{
    public bool Enabled { get; init; } = true;
    public bool Bold { get; init; }
    public bool Italic { get; init; }

    /// <summary>A theme token for the row's ink, never a colour: a rule has to read in every theme.</summary>
    public string? ColourToken { get; init; }

    /// <summary>The condition, as <see cref="Search.SearchQuery"/> reads it: <c>read:no</c>, <c>from:alice flagged:yes</c>.</summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>The rules the reference ships with, which cannot be deleted — only switched off.</summary>
    public bool BuiltIn { get; init; }
}

/// <summary>
/// The fields a column can show. Ids are stable — they are what a stored view names.
/// </summary>
public static class ViewFields
{
    public const string Importance = "importance";
    public const string Reminder = "reminder";
    public const string Icon = "icon";
    public const string Flag = "flag";
    public const string Attachment = "attachment";
    public const string From = "from";
    public const string To = "to";
    public const string Subject = "subject";
    public const string Received = "received";
    public const string Sent = "sent";
    public const string Size = "size";
    public const string Categories = "categories";
    public const string Mention = "mention";
    public const string Folder = "folder";

    /// <summary>Every field, in the reference's Show Columns order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Importance, Reminder, Icon, Flag, Attachment, From, To, Subject, Received, Sent, Size, Categories, Mention, Folder,
    ];

    /// <summary>The columns that show a glyph rather than words, and are drawn narrow and unlabelled.</summary>
    public static bool IsGlyph(string id) => id is Importance or Reminder or Icon or Flag or Attachment;

    public static bool IsDate(string id) => id is Received or Sent;

    /// <summary>What the header calls the column.</summary>
    public static string Label(string id) => id switch
    {
        Importance => "Importance",
        Reminder => "Reminder",
        Icon => "Icon",
        Flag => "Flag Status",
        Attachment => "Attachment",
        From => "From",
        To => "To",
        Subject => "Subject",
        Received => "Received",
        Sent => "Sent",
        Size => "Size",
        Categories => "Categories",
        Mention => "Mention",
        Folder => "In Folder",
        _ => id,
    };

    /// <summary>The header's glyph for a glyph column, or its label.</summary>
    public static string HeaderText(string id) => id switch
    {
        Importance => "!",
        Reminder => "⌂",
        Icon => "▤",
        Flag => "⚑",
        Attachment => "◯",
        _ => Label(id),
    };

    /// <summary>The width a column has until Format Columns says otherwise.</summary>
    public static double DefaultWidth(string id) => id switch
    {
        _ when IsGlyph(id) => 18,
        From or To => 150,
        Subject => 300,
        Received or Sent => 100,
        Size => 55,
        Categories => 90,
        Mention => 70,
        Folder => 110,
        _ => 100,
    };

    /// <summary>The Arrange By arrangement a column sorts by, or null for one that does not sort.</summary>
    public static string? SortField(string id) => id switch
    {
        Importance => "Importance",
        Flag => "Flag",
        Attachment => "Attachments",
        From => "From",
        To => "To",
        Subject => "Subject",
        Received or Sent => "Date",
        Size => "Size",
        Categories => "Categories",
        _ => null,
    };
}

/// <summary>
/// A message-list view: what the reference's Change View picks and Advanced View Settings
/// edits — the layout, the columns, how the list is grouped and sorted, a filter, the other
/// settings, the conditional formatting and the column formats. Kept per folder as one JSON
/// document, and by name for the views a reader saves.
/// </summary>
/// <remarks>
/// Pure. Sort and group fields are arrangement names ("Date", "From", …) rather than an enum
/// this assembly does not own; the shell maps them. Filters and conditions are the search
/// box's syntax, so one grammar serves the box, the Filter dialog and Conditional Formatting.
/// </remarks>
public sealed record MailView
{
    public const string CompactName = "Compact";
    public const string SingleName = "Single";
    public const string PreviewName = "Preview";

    public string Name { get; init; } = CompactName;

    public ViewLayout Layout { get; init; } = ViewLayout.Compact;

    /// <summary>The line layout's columns, in order.</summary>
    public IReadOnlyList<ViewColumn> Columns { get; init; } = DefaultColumns();

    /// <summary>
    /// The arrangement the list is grouped by, or null to group by the sort — "Automatically
    /// group according to arrangement", the reference's default.
    /// </summary>
    public string? GroupBy { get; init; }

    public bool GroupAscending { get; init; }

    /// <summary>Other Settings' "Show items in Groups".</summary>
    public bool ShowInGroups { get; init; } = true;

    /// <summary>Group By's "Expand/collapse defaults": as last viewed (null), all expanded (true), all collapsed (false).</summary>
    public bool? GroupsExpanded { get; init; }

    /// <summary>The arrangement the list is sorted by — Arrange By's choice.</summary>
    public string SortField { get; init; } = "Date";

    public bool SortDescending { get; init; } = true;

    /// <summary>The Filter dialog's outcome, in the search box's syntax; empty for Off.</summary>
    public string Filter { get; init; } = string.Empty;

    /// <summary>Preview lines under the line — Message Preview's Off / 1 / 2 / 3.</summary>
    public int PreviewLines { get; init; } = 1;

    public CompactMode CompactMode { get; init; } = CompactMode.Auto;

    /// <summary>"Use compact layout in widths smaller than N characters" — the reference's default is 125.</summary>
    public int CompactBelowChars { get; init; } = 125;

    public IReadOnlyList<ConditionalFormat> Formats { get; init; } = DefaultFormats();

    /// <summary>Format Columns' choices, by column id.</summary>
    public IReadOnlyDictionary<string, ColumnFormat> ColumnFormats { get; init; } = new Dictionary<string, ColumnFormat>();

    /// <summary>True for the three that ship — Compact, Single, Preview — which Reset restores to as they came.</summary>
    public bool IsBuiltIn => Name is CompactName or SingleName or PreviewName;

    // ---- The three that ship ---------------------------------------------------------------

    public static MailView Compact => new() { Name = CompactName, Layout = ViewLayout.Compact };

    public static MailView Single => new() { Name = SingleName, Layout = ViewLayout.Single, PreviewLines = 0 };

    public static MailView Preview => new() { Name = PreviewName, Layout = ViewLayout.Preview };

    /// <summary>The shipped view of that name, or null.</summary>
    public static MailView? BuiltIn(string name) => name switch
    {
        CompactName => Compact,
        SingleName => Single,
        PreviewName => Preview,
        _ => null,
    };

    /// <summary>The columns the reference shows until told otherwise.</summary>
    public static IReadOnlyList<ViewColumn> DefaultColumns() =>
    [
        new(ViewFields.Importance, 18), new(ViewFields.Reminder, 18), new(ViewFields.Icon, 18),
        new(ViewFields.Attachment, 18), new(ViewFields.From, 150), new(ViewFields.Subject, 300),
        new(ViewFields.Received, 100), new(ViewFields.Size, 55), new(ViewFields.Categories, 90),
        new(ViewFields.Mention, 70), new(ViewFields.Flag, 24),
    ];

    /// <summary>The reference's own two rules: unread is bold and blue; overdue is red.</summary>
    public static IReadOnlyList<ConditionalFormat> DefaultFormats() =>
    [
        new("Unread messages") { Bold = true, ColourToken = "list.row.unread.text", Condition = "read:no", BuiltIn = true },
        new("Overdue messages") { ColourToken = "status.danger", Condition = "flagged:yes due:<today", BuiltIn = true },
    ];

    // ---- The document ---------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a view back; a document that will not parse yields the shipped Compact view.</summary>
    public static MailView FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MailView>(json, Json) ?? Compact;
        }
        catch (JsonException)
        {
            return Compact;
        }
    }

    /// <summary>The line layout's columns as the header draws them: id, header text, width, sort field.</summary>
    public IEnumerable<(string Id, string Header, double Width, bool IsGlyph, string? SortField)> HeaderColumns()
        => Columns.Select(c => (c.Id, ColumnFormats.TryGetValue(c.Id, out var f) && f.Label is { Length: > 0 } ? f.Label : ViewFields.HeaderText(c.Id),
            c.Width, ViewFields.IsGlyph(c.Id), ViewFields.SortField(c.Id)));
}
