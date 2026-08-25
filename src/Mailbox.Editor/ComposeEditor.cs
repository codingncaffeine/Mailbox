using Avalonia.Input;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;

namespace Mailbox.Editor;

/// <summary>What was corrected, for a host that wants to say so.</summary>
/// <param name="From">What was typed.</param>
/// <param name="To">What it became. Empty when the correction was formatting rather than letters.</param>
public sealed class AutocorrectedEventArgs(string from, string to) : EventArgs
{
    public string From { get; } = from;

    public string To { get; } = to;
}

/// <summary>
/// The editor the compose window uses: the library's control, and autocorrect on top of it.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> Correcting a word as it is finished means replacing exactly the one
/// just typed, and the editor's public surface offers find-and-replace over every match and
/// nothing narrower. It does expose two things that together are enough: where the caret is —
/// as a line and a column, which are a paragraph and an offset into it — and the whole document
/// as text. Those give the word behind the caret without guessing.
/// <para>
/// <b>How the replacement is made.</b> Not by editing the document: by pressing Backspace as
/// many times as the word is long and typing the correction, through the editor's own key
/// handler. Everything then follows from the editor rather than being reimplemented beside it —
/// the caret ends up in the right place, the layout is invalidated once, the text-changed event
/// fires, and the whole correction collapses into a single undo step, so Ctrl+Z immediately
/// after one puts back exactly what was typed. Reaching into the document instead would have
/// bypassed all four.
/// <para>
/// The one thing the deletion can cost is the caret's own formatting: taking away every
/// character of a word empties the run it was in, and an empty run takes its bold with it. So
/// the format is read before the deletion and put back before the correction is typed.
/// </para>
/// </para>
/// </remarks>
public class ComposeEditor : RichEditor
{
    /// <summary>The rules, or null for an editor that corrects nothing.</summary>
    /// <remarks>
    /// Null rather than a switch, so that a host which has not asked for autocorrect pays
    /// nothing for it: no status read, no text read, on any keystroke.
    /// </remarks>
    public Autocorrect? Autocorrect { get; set; }

    /// <summary>
    /// Whether a correction may carry formatting — <c>*bold*</c> and <c>_italic_</c>.
    /// </summary>
    /// <remarks>Off while a message is being written as plain text, where there is none to carry.</remarks>
    public bool AllowFormatting { get; set; } = true;

    /// <summary>Raised after a correction is made, with what it was and what it became.</summary>
    public event EventHandler<AutocorrectedEventArgs>? Autocorrected;

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (Autocorrect is null || IsReadOnly || e.Text is not { Length: 1 } text)
        {
            base.OnTextInput(e);
            return;
        }

        var typed = text[0];

        if (TextBeforeCaret() is not { } before)
        {
            base.OnTextInput(e);
            return;
        }

        // A character that stands for another one — a quotation mark, the second star of a
        // pair. The typed character never reaches the document.
        if (Autocorrect.AtCharacter(before, typed) is { } instead && Allowed(instead))
        {
            Apply(instead, string.Empty);
            e.Handled = true;
            return;
        }

        // A word has been finished. Correct it, then let the character that ended it be typed:
        // the space belongs after the correction, not before it.
        var action = Autocorrect.EndsWord(typed)
            ? Autocorrect.AtWordBoundary(before, typed, StartsCell())
            : null;

        if (action is not null && Allowed(action))
        {
            Apply(action, Word(before, action));

            // A list marker takes its space with it: "* " asked for a bullet, and the bullet is
            // the answer to both characters.
            if (action.ReplacesInput)
            {
                e.Handled = true;
                return;
            }
        }

        base.OnTextInput(e);

        // The character that ended the word has now been typed, so the caret is out of the word
        // and the emphasis can be closed without the editor reading it as "un-bold this word".
        if (action?.Format is AutocorrectFormat.Bold or AutocorrectFormat.Italic)
        {
            Emphasize(action.Format == AutocorrectFormat.Bold, on: false);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Autocorrect is null || IsReadOnly || e.Key is not (Key.Enter or Key.Return)
            || e.KeyModifiers != KeyModifiers.None)
        {
            base.OnKeyDown(e);
            return;
        }

        if (TextBeforeCaret() is { } before)
        {
            // A line of hyphens is a rule rather than a word, so it is asked about first.
            var action = Autocorrect.AtParagraphBreak(before)
                ?? Autocorrect.AtWordBoundary(before, '\n', StartsCell());

            if (action is not null && Allowed(action))
            {
                Apply(action, Word(before, action));

                // The rule drawn across the page is what the Return asked for, so it is not
                // pressed again afterwards.
                if (action.ReplacesInput)
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        base.OnKeyDown(e);
    }

    /// <summary>Whether this correction is one this message can carry.</summary>
    private bool Allowed(AutocorrectAction action) =>
        AllowFormatting || action.Format is not (AutocorrectFormat.Bold or AutocorrectFormat.Italic);

    /// <summary>The text this action is about to take away, for the event.</summary>
    private static string Word(string before, AutocorrectAction action) =>
        action.Remove <= before.Length ? before[^action.Remove..] : string.Empty;

    /// <summary>
    /// Takes away what was typed and types the correction, through the editor's own handlers.
    /// </summary>
    private void Apply(AutocorrectAction action, string replaced)
    {
        // What the caret was going to type in, so that emptying a run does not silently drop
        // the bold, the colour or the family the writer had chosen.
        var format = GetCaretFormat();

        for (var i = 0; i < action.Remove; i++) Backspace();

        Restore(format);

        switch (action.Format)
        {
            case AutocorrectFormat.Bold:
            case AutocorrectFormat.Italic:
                // Turned on here and off again once the terminator has been typed: see the
                // remark on the emphasis rule for why it cannot be turned off any sooner.
                Emphasize(action.Format == AutocorrectFormat.Bold, on: true);
                InsertText(action.Insert);
                break;

            case AutocorrectFormat.Bullet:
                ToggleBullet();
                break;

            case AutocorrectFormat.Numbering:
                ToggleNumbering();
                break;

            case AutocorrectFormat.Divider:
                InsertDivider();
                break;

            default:
                if (action.Insert.Length > 0) InsertText(action.Insert);
                break;
        }

        Autocorrected?.Invoke(this, new AutocorrectedEventArgs(replaced, action.Insert));
    }

    /// <summary>One Backspace, as the reader would have pressed it.</summary>
    private void Backspace() => base.OnKeyDown(new KeyEventArgs
    {
        Key = Key.Back,
        KeyModifiers = KeyModifiers.None,
        RoutedEvent = InputElement.KeyDownEvent,
        Source = this,
    });

    /// <summary>Turns bold or italic on or off, but only if it is not already that way.</summary>
    private void Emphasize(bool bold, bool on)
    {
        var format = GetCaretFormat();
        var already = bold ? format.Bold : format.Italic;
        if (already == on) return;

        if (bold) ToggleBold();
        else ToggleItalic();
    }

    /// <summary>Puts back the formatting the deletion may have taken with it.</summary>
    private void Restore(CaretFormat format)
    {
        var now = GetCaretFormat();

        if (now.Bold != format.Bold) ToggleBold();
        if (now.Italic != format.Italic) ToggleItalic();
        if (now.Underline != format.Underline) ToggleUnderline();
        if (now.Strike != format.Strike) ToggleStrikethrough();

        if (format.FontFamily is { Length: > 0 } family && now.FontFamily != family) SetFontFamily(family);
        if (format.FontSize > 0 && Math.Abs(now.FontSize - format.FontSize) > 0.01) SetFontSize(format.FontSize);
        if (format.Foreground is { } foreground && !Equals(now.Foreground, foreground)) SetForeground(foreground);
        if (format.Background is { } background && !Equals(now.Background, background)) SetHighlight(background);
    }

    /// <summary>
    /// Everything in the caret's own paragraph before the caret, or null when that cannot be
    /// said for certain.
    /// </summary>
    /// <remarks>
    /// The editor reports the caret as a one-based line and column, and its plain text puts one
    /// line per paragraph — a table's cells included, in the order they are laid out. So the
    /// line picks the paragraph and the column the offset into it. Null when the two do not
    /// agree, which is what a paragraph carrying something that is not text — an inline image —
    /// looks like from here: no correction is better than one measured against the wrong
    /// characters.
    /// </remarks>
    internal string? TextBeforeCaret()
    {
        var (_, _, line, column) = GetStatus();
        if (line < 1 || column < 1) return null;

        var text = GetPlainText();
        var start = 0;

        for (var i = 1; i < line; i++)
        {
            var next = text.IndexOf('\n', start);
            if (next < 0) return null;
            start = next + 1;
        }

        var end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;

        var offset = column - 1;
        return start + offset > end ? null : text.Substring(start, offset);
    }

    /// <summary>
    /// Whether the caret is in a table cell, which is a sentence of its own to the reference.
    /// </summary>
    /// <remarks>
    /// Answered by counting: every paragraph is one line of the plain text and a table is one
    /// line per cell paragraph, so a running count says which block the caret's line falls in.
    /// If that count and the text disagree the answer is no — the block model has something in
    /// it this does not know how to count, and the rule it feeds only ever adds a capital.
    /// </remarks>
    private bool StartsCell()
    {
        var document = Document;
        if (document is null) return false;

        var tables = document.Blocks.OfType<TableBlock>().ToList();
        if (tables.Count == 0) return false;

        var (_, _, line, _) = GetStatus();
        var counted = 0;
        var inCell = false;

        foreach (var block in document.Blocks)
        {
            var lines = Lines(block);
            if (line > counted && line <= counted + lines) inCell = block is TableBlock;
            counted += lines;
        }

        return inCell && counted == GetPlainText().Count(c => c == '\n') + 1;

        static int Lines(Block block) => block switch
        {
            Paragraph => 1,
            TableBlock table => table.Cells.SelectMany(row => row).Sum(cell => cell.Blocks.Sum(Lines)),
            _ => 0,
        };
    }
}
