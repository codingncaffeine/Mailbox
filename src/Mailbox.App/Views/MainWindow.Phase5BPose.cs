using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// Answers a <see cref="Chooser"/> or a <see cref="Prompt"/> without showing it. Harness only.
/// </summary>
/// <remarks>
/// Half the Format Text tab cannot act without a value: a ribbon control reports which command
/// was pressed and never a value with it, so Font, Font Size, Font Colour, Highlight, Align,
/// Line Spacing, Multilevel List, Table and Link each open a small dialog and wait. A pose that
/// presses one of those buttons therefore blocks on a modal window nothing can click, which is
/// why none of them had ever been pressed by anything: eight formatting commands and two insert
/// commands with no route to a read-back at all.
/// <para>
/// <c>MAILBOX_ANSWER</c> is that route. Entries are separated by <c>|</c> and taken in order;
/// an entry written <c>Title=value</c> is taken only by the dialog with that title, so a pose
/// that presses several commands can name which answer belongs to which. An entry of
/// <c>cancel</c> dismisses the dialog, which is the other branch worth proving. When the list
/// runs out the dialog opens normally, so this can never silently swallow a real one.
/// </para>
/// <para>
/// Gated on <see cref="WindowCapture.IsRequested"/> as well as on the variable: a capture run
/// is the only place a posed answer is ever wanted, and the gate means an exported variable
/// left in a shell cannot reach a real session.
/// </para>
/// </remarks>
internal static class HarnessAnswer
{
    private const string Variable = "MAILBOX_ANSWER";

    private static readonly Lock Gate = new();
    private static List<string>? _pending;

    /// <summary>What this dialog should answer, or null to let it open.</summary>
    internal static string? Next(string title)
    {
        if (!WindowCapture.IsRequested) return null;

        lock (Gate)
        {
            _pending ??= [.. (Environment.GetEnvironmentVariable(Variable) ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

            if (_pending.Count == 0) return null;

            // A keyed entry first, so a pose that presses Font and then Align does not have to
            // guess which dialog will ask first.
            var keyed = _pending.FindIndex(e =>
                e.Contains('=', StringComparison.Ordinal)
                && string.Equals(e[..e.IndexOf('=', StringComparison.Ordinal)], title,
                    StringComparison.OrdinalIgnoreCase));

            var at = keyed >= 0 ? keyed : _pending.FindIndex(e => !e.Contains('=', StringComparison.Ordinal));
            if (at < 0) return null;

            var entry = _pending[at];
            _pending.RemoveAt(at);

            var answer = keyed >= 0 ? entry[(entry.IndexOf('=', StringComparison.Ordinal) + 1)..] : entry;
            Log.Info($"Harness: answering “{title}” with “{answer}”.");
            return answer;
        }
    }

    /// <summary>Whether a posed answer means "press Cancel".</summary>
    internal static bool IsCancel(string answer)
        => string.Equals(answer, "cancel", StringComparison.OrdinalIgnoreCase);
}

public partial class MainWindow
{
    /// <summary>
    /// <c>MAILBOX_EDITOR_RUN</c> — poses a body and presses formatting commands over it.
    /// </summary>
    /// <remarks>
    /// The editor's own door, beside the compose lane's <c>MAILBOX_COMPOSE_RUN</c>: that one
    /// presses a command and reports the info bar and the address fields, which is the envelope's
    /// question. What a <em>formatting</em> command did is only visible in the document it left
    /// behind and in the markup that goes on the wire, and those are two different things — the
    /// editor's own <c>ToHtml</c> is a preview and never leaves. So this logs the caret's format
    /// and the document, and <c>MAILBOX_COMPOSE_QUEUE=1</c> beside it puts the same message in the
    /// outbox to be read back as MIME, which is the half that is actually sent.
    /// <para>
    /// A step written <c>text:…</c> replaces the body first, because most of the formatting
    /// commands want a plain paragraph to act on rather than the rich fixture the queue poses,
    /// and a step written <c>type:…</c> types one through the real keystroke path. Everything
    /// else is a command id, pressed through <see cref="ComposeSurface.Invoke"/> — the same
    /// entry point the ribbon uses. Posted at Background, so it lands after anything typed and
    /// before the Send the queue pose presses.
    /// </para>
    /// </remarks>
    private static void PoseComposeEditor(ComposeWindow compose)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_EDITOR_RUN") is not { Length: > 0 } run) return;

        compose.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Reached through the tree rather than through a forwarder on the window: the surface
            // is public and host-neutral, and the inline reply strip is the same control in a
            // different tree.
            if (compose.GetVisualDescendants().OfType<ComposeSurface>().FirstOrDefault() is not { } surface)
            {
                Log.Info("Harness: no compose surface to run editor commands on.");
                return;
            }

            RunEditorSteps(surface, run);
        }, DispatcherPriority.Background);
    }

    /// <summary>The same steps, over whichever surface is asking — a window's or an inline reply's.</summary>
    private static void RunEditorSteps(ComposeSurface surface, string run)
    {
        foreach (var step in run.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (step.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
            {
                surface.PoseBodyText(step[5..].Replace("\\n", "\n", StringComparison.Ordinal));
                Log.Info($"Harness: body posed as “{step[5..]}”.");
                continue;
            }

            // Loads markup straight into the document, which is the only way to ask the editor's
            // own HTML parser what it keeps — the half of a round trip that a reply, a draft and
            // a pasted message all go through, and that nothing else can pose.
            if (step.StartsWith("html:", StringComparison.OrdinalIgnoreCase))
            {
                if (surface.GetVisualDescendants().OfType<Mailbox.Editor.ComposeEditor>().FirstOrDefault()
                    is { } target)
                {
                    target.LoadHtml(step[5..]);
                    Log.Info($"Harness: body loaded from “{step[5..]}”.");
                }

                continue;
            }

            // The From menu's own entry, which is where a signature is swapped after the window
            // is already open — a different path from the shell's SendFromAccount.
            if (step.StartsWith("from:", StringComparison.OrdinalIgnoreCase))
            {
                surface.PoseSendFrom(step[5..]);
                Log.Info($"Harness: From menu picked {surface.HarnessSendingAddress}.");
                continue;
            }

            if (step.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                surface.PoseBodyTyping(step[5..].Replace("\\n", "\n", StringComparison.Ordinal));
                Log.Info($"Harness: typed “{step[5..]}”.");
                continue;
            }

            // Named rather than pressed blind: the surface answers an id it does not know by
            // writing to its status line, which no log reads — so a mistyped id in a pose list
            // read exactly like a formatting command that does nothing. CommandId throws on a
            // malformed one, and a posted action that throws leaves a plausible capture and
            // nothing to grep, which is the trap Phase 0 recorded.
            MailboxCommand? command;

            try
            {
                if (!App.Commands.TryGet(new CommandId(step), out command)) command = null;
            }
            catch (ArgumentException)
            {
                command = null;
            }

            if (command is null)
            {
                Log.Warn($"Harness: there is no command with id '{step}'.");
                continue;
            }

            var id = command.Id;

            Log.Info($"Harness: compose running {step} ({command.Label}), "
                     + $"enabled: {surface.IsCommandEnabled(id)}.");
            surface.Invoke(id);
        }

        // What the document holds at the caret, in the editor's own units. Between this and the
        // markup below, a size or a family that comes out wrong can be blamed on the command or
        // on the serializer rather than on one of the two by elimination.
        if (surface.GetVisualDescendants().OfType<Mailbox.Editor.ComposeEditor>().FirstOrDefault() is { } editor)
        {
            var caret = editor.GetCaretFormat();
            Log.Info($"Harness: caret format — family “{caret.FontFamily}”, size {caret.FontSize}, "
                     + $"bold {caret.Bold}, italic {caret.Italic}, underline {caret.Underline}, "
                     + $"strike {caret.Strike}; editor default {editor.DefaultFontSize} "
                     + $"in “{editor.DefaultFontFamily}”.");

            // Paragraph-level marks, which the caret format does not carry. A command that sets
            // one of these and a serializer that never writes it are two different faults, and
            // only this line tells them apart.
            foreach (var block in editor.Document?.Blocks ?? [])
            {
                if (block is not AvaloniaRichEditor.Documents.Paragraph paragraph) continue;

                Log.Info($"Harness: paragraph — align {paragraph.TextAlignment}, "
                         + $"indent {paragraph.Indent}, line spacing {paragraph.LineSpacing}, "
                         + $"list {paragraph.ListType}, marker {paragraph.ListMarker}, "
                         + $"quote {paragraph.IsQuote}, heading {paragraph.HeadingLevel}.");
            }
        }

        // The verdict, and the only one a capture cannot give: a bold word and a plain one
        // photograph as text either way at this size, and what leaves is the markup.
        Log.Info($"Harness: compose body text: {surface.BodyText.Replace("\n", "\\n", StringComparison.Ordinal)}");
        Log.Info($"Harness: compose body html: {surface.BodyHtml.Replace("\n", " ", StringComparison.Ordinal)}");
    }

    /// <summary>
    /// The same door over an inline reply, which is where the compose surface actually lives
    /// under the owner's own reading-pane-off setup.
    /// </summary>
    private void PoseInlineComposeEditor()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_EDITOR_RUN") is not { Length: > 0 } run) return;
        if (_inlineCompose is not { } surface) return;

        Dispatcher.UIThread.Post(() => RunEditorSteps(surface, run), DispatcherPriority.Background);
    }
}
