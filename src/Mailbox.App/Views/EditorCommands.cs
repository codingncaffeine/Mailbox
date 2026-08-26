using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaRichEditor.Documents;
using Mailbox.Core.Commands;
using Mailbox.Core.Settings;
using Mailbox.Editor;

namespace Mailbox.App.Views;

/// <summary>
/// The Format Text tab, over whichever editor is asking.
/// </summary>
/// <remarks>
/// <b>Why this is not in the compose window any more.</b> Two windows now write rich text: a
/// message, and a contact's notes — the reference's contact notes are rich text and its Insert
/// and Format Text tabs act on them, so ours are too. Bold is bold in both, and a second copy of
/// these two hundred lines would be a second copy to fix.
/// <para>
/// What stays with the host is what is the host's: whether a message goes as plain text, what a
/// signature is, what an attachment means. This is the part that is only ever about a document
/// and a selection.
/// </para>
/// <para>
/// The choosers are the compromise the compose window already made: a ribbon control reports
/// which command was pressed and never a value with it, so a font, a size, a colour or an
/// alignment has to be asked for before the command can act. The reference uses live-previewing
/// galleries; replacing these with those is ribbon work, written down rather than pretended away.
/// </para>
/// </remarks>
internal sealed class EditorCommands(
    ComposeEditor editor,
    Func<Window> host,
    Action<string>? report = null,
    Action? changed = null)
{
    /// <summary>Points per device-independent pixel, which is what the document measures in.</summary>
    private const double PointsPerPixel = 0.75;

    private readonly ComposeEditor _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    private readonly Func<Window> _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>The family and size a caret with no explicit font of its own falls back to.</summary>
    public Func<MessageFont> BaseFont { get; init; } = () => MessageFont.Default;

    /// <summary>
    /// Runs one of the formatting commands, or says it is none of them.
    /// </summary>
    public bool Handle(CommandId id)
    {
        if (id == MailCommands.Undo.Id) { _editor.Undo(); _editor.Focus(); return true; }
        if (id == ViewCommands.Redo.Id) { _editor.Redo(); _editor.Focus(); return true; }

        if (id == ComposeCommands.Bold.Id) return Format(_editor.ToggleBold);
        if (id == ComposeCommands.Italic.Id) return Format(_editor.ToggleItalic);
        if (id == ComposeCommands.Underline.Id) return Format(_editor.ToggleUnderline);
        if (id == ComposeCommands.Strikethrough.Id) return Format(_editor.ToggleStrikethrough);

        if (id == ComposeCommands.GrowFont.Id) return Format(_editor.IncreaseFontSize);
        if (id == ComposeCommands.ShrinkFont.Id) return Format(_editor.DecreaseFontSize);

        if (id == ComposeCommands.Bullets.Id) return Format(_editor.ToggleBullet);
        if (id == ComposeCommands.Numbering.Id) return Format(_editor.ToggleNumbering);

        if (id == ComposeCommands.IncreaseIndent.Id) return Format(() => _editor.Indent(24));
        if (id == ComposeCommands.DecreaseIndent.Id) return Format(() => _editor.Indent(-24));

        if (id == ComposeCommands.FormatPainter.Id)
        {
            if (_editor.IsFormatPainterActive)
            {
                _editor.CancelFormatPainter();
                report?.Invoke("Format painter off.");
            }
            else
            {
                _editor.StartFormatPainter();
                report?.Invoke("Format painter on — select the text to paint.");
            }

            _editor.Focus();
            return true;
        }

        if (id == ComposeCommands.Font.Id) { _ = ChooseFontAsync(); return true; }
        if (id == ComposeCommands.FontSize.Id) { _ = ChooseFontSizeAsync(); return true; }
        if (id == ComposeCommands.FontColor.Id) { _ = ChooseColourAsync(highlight: false); return true; }
        if (id == ComposeCommands.Highlight.Id) { _ = ChooseColourAsync(highlight: true); return true; }
        if (id == ComposeCommands.Align.Id) { _ = ChooseAlignmentAsync(); return true; }
        if (id == ComposeCommands.LineSpacing.Id) { _ = ChooseLineSpacingAsync(); return true; }
        if (id == ComposeCommands.MultilevelList.Id) { _ = ChooseListStyleAsync(); return true; }

        return false;
    }

    /// <summary>
    /// The Insert tab's two entries that are only about the document: a table and a link.
    /// </summary>
    /// <remarks>
    /// A picture is not here: a message carries one as a related part, and a contact's note
    /// would have to carry it inside the card. What each host does with the rest of that tab is
    /// the host's business, which is why this answers only these two.
    /// </remarks>
    public bool HandleInsert(CommandId id)
    {
        if (id == ComposeCommands.Table.Id) { _ = InsertTableAsync(); return true; }
        if (id == ComposeCommands.Link.Id) { _ = InsertLinkAsync(); return true; }

        return false;
    }

    private async Task InsertTableAsync()
    {
        var size = await Prompt.AskAsync(_host(), "Table", "Rows and columns, as 3x4:", string.Empty);
        if (string.IsNullOrWhiteSpace(size)) return;

        var parts = size.Split(['x', 'X', '*', ',', ' '], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2
            || !int.TryParse(parts[0], out var rows)
            || !int.TryParse(parts[1], out var columns))
        {
            report?.Invoke("A table size reads as rows by columns, like 3x4.");
            return;
        }

        // A bound, because the number came from a text box and a table of ten thousand cells is
        // a hang rather than a table.
        if (rows is < 1 or > 50 || columns is < 1 or > 20)
        {
            report?.Invoke("A table can be up to 50 rows by 20 columns.");
            return;
        }

        _editor.InsertTable(rows, columns);
        _editor.Focus();
        report?.Invoke($"Inserted a {rows}x{columns} table.");
        changed?.Invoke();
    }

    private async Task InsertLinkAsync()
    {
        var address = await Prompt.AskAsync(_host(), "Link", "Address:", string.Empty);
        if (string.IsNullOrWhiteSpace(address)) return;

        var escaped = address.Trim()
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

        _editor.InsertHtml($"<a href=\"{escaped}\">{escaped}</a>");
        _editor.Focus();
        changed?.Invoke();
    }

    /// <summary>Applies something to the selection, then puts the caret back where it was.</summary>
    private bool Format(Action apply)
    {
        apply();
        _editor.Focus();
        changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The face at the caret as the writer knows it, so the picker opens on it.
    /// </summary>
    /// <remarks>
    /// The run holds the family this machine draws — Liberation Serif, Gelasio — because that
    /// is what the editor needs. The picker lists the Microsoft names, so the substitute is
    /// mapped back to what it stands in for. Same split the serializer does on the wire.
    /// </remarks>
    private string? CaretFont()
    {
        var rendered = _editor.GetCaretFormat().FontFamily;
        if (string.IsNullOrEmpty(rendered)) return null;

        return Mailbox.Theming.Fonts.FontSubstitution.Table
                   .FirstOrDefault(e => string.Equals(e.Substitute, rendered, StringComparison.OrdinalIgnoreCase))
                   ?.Original
               ?? rendered;
    }

    private async Task ChooseFontAsync()
    {
        // FontResolver already builds this list, in the reference's own order and carrying each
        // entry's resolution — it exists for this picker.
        var choices = App.Fonts.PickerFamilies()
            .Where(f => !string.Equals(f.Requested, "Segoe UI", StringComparison.Ordinal))
            .Select(f => new Choice(f.Requested, f.Requested, Describe(f)))
            .ToList();

        if (await Chooser.AskAsync(_host(), "Font", "Font:", choices, CaretFont()) is not { } family)
        {
            return;
        }

        // The wire name and the substitute both: §6's split, and what keeps a document the size
        // it was written at on a machine that has neither font.
        _editor.SetFontFamily(App.Fonts.Resolve(family).Rendered);
        _editor.Focus();
        report?.Invoke($"Font {family}.");
        changed?.Invoke();
    }

    /// <summary>What choosing this face actually gets the reader, and their recipient.</summary>
    private static string Describe(Mailbox.Theming.Fonts.FontResolution font) => font switch
    {
        { IsSubstituted: false } => "installed here, and named in the message",

        { Quality: Mailbox.Theming.Fonts.SubstitutionQuality.MetricCompatible } =>
            $"shown in {font.Rendered}, which lays out identically",

        { MayReflow: true } =>
            $"shown in {font.Rendered} — similar, but the message will reflow",

        _ => "not installed here; a recipient who has it will see it correctly",
    };

    private async Task ChooseFontSizeAsync()
    {
        // The reference's own list.
        var sizes = new[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72 };

        var choices = sizes
            .Select(p => new Choice(p.ToString(CultureInfo.InvariantCulture),
                                    p.ToString(CultureInfo.InvariantCulture)))
            .ToList();

        var caret = _editor.GetCaretFormat().FontSize;
        var current = (caret > 0 ? caret * PointsPerPixel : BaseFont().Points)
            .ToString("0", CultureInfo.InvariantCulture);

        if (await Chooser.AskAsync(_host(), "Font Size", "Size:", choices, current) is not { } chosen) return;
        if (!double.TryParse(chosen, CultureInfo.InvariantCulture, out var points)) return;

        _editor.SetFontSize(points / PointsPerPixel);
        _editor.Focus();
        changed?.Invoke();
    }

    private async Task ChooseColourAsync(bool highlight)
    {
        var colours = highlight
            ? new[]
            {
                new Choice("None", "none"),
                new Choice("Yellow", "#FFFF00"), new Choice("Bright green", "#00FF00"),
                new Choice("Turquoise", "#00FFFF"), new Choice("Pink", "#FF00FF"),
                new Choice("Blue", "#0000FF"), new Choice("Red", "#FF0000"),
                new Choice("Grey", "#C0C0C0"),
            }
            : new[]
            {
                new Choice("Automatic", "none"),
                new Choice("Black", "#000000"), new Choice("Dark red", "#C00000"),
                new Choice("Red", "#FF0000"), new Choice("Orange", "#FFC000"),
                new Choice("Green", "#008000"), new Choice("Blue", "#0070C0"),
                new Choice("Dark blue", "#002060"), new Choice("Purple", "#7030A0"),
                new Choice("Grey", "#808080"),
            };

        var title = highlight ? "Text Highlight Colour" : "Font Colour";

        if (await Chooser.AskAsync(_host(), title, "Colour:", colours) is not { } value) return;

        var brush = string.Equals(value, "none", StringComparison.Ordinal)
            ? null
            : new SolidColorBrush(Color.Parse(value));

        if (highlight) _editor.SetHighlight(brush!);
        else _editor.SetForeground(brush!);

        _editor.Focus();
        report?.Invoke(title + " set.");
        changed?.Invoke();
    }

    private async Task ChooseAlignmentAsync()
    {
        var choices = new[]
        {
            new Choice("Left", "left"), new Choice("Centre", "center"),
            new Choice("Right", "right"), new Choice("Justified", "justify"),
        };

        if (await Chooser.AskAsync(_host(), "Align", "Alignment:", choices) is not { } value) return;

        _editor.SetTextAlignment(value switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            "justify" => TextAlignment.Justify,
            _ => TextAlignment.Left,
        });

        _editor.Focus();
    }

    private async Task ChooseLineSpacingAsync()
    {
        var choices = new[]
        {
            new Choice("Single", "1.0"), new Choice("1.15", "1.15"),
            new Choice("1.5 lines", "1.5"), new Choice("Double", "2.0"),
        };

        if (await Chooser.AskAsync(_host(), "Line Spacing", "Spacing:", choices) is not { } value)
        {
            return;
        }

        if (double.TryParse(value, CultureInfo.InvariantCulture, out var multiplier))
        {
            _editor.SetLineSpacing(multiplier);
            _editor.Focus();
        }
    }

    private async Task ChooseListStyleAsync()
    {
        var choices = new[]
        {
            new Choice("Bullet — disc", "disc"), new Choice("Bullet — circle", "circle"),
            new Choice("Bullet — square", "square"), new Choice("Bullet — dash", "dash"),
            new Choice("Numbered — 1.", "decimal"), new Choice("Numbered — 1)", "decimalparen"),
            new Choice("Lettered — a.", "loweralpha"), new Choice("Lettered — A.", "upperalpha"),
            new Choice("Roman — i.", "lowerroman"),
        };

        if (await Chooser.AskAsync(_host(), "List Style", "Marker:", choices) is not { } value)
        {
            return;
        }

        _editor.SetListStyle(value switch
        {
            "circle" => ListMarkerStyle.Circle,
            "square" => ListMarkerStyle.Square,
            "dash" => ListMarkerStyle.Dash,
            "decimal" => ListMarkerStyle.Decimal,
            "decimalparen" => ListMarkerStyle.DecimalParen,
            "loweralpha" => ListMarkerStyle.LowerAlpha,
            "upperalpha" => ListMarkerStyle.UpperAlpha,
            "lowerroman" => ListMarkerStyle.LowerRoman,
            _ => ListMarkerStyle.Disc,
        });

        _editor.Focus();
    }
}
