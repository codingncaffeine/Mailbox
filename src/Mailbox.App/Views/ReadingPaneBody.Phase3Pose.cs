using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// What the reading pane is actually showing, written out for a harness run.
/// </summary>
/// <remarks>
/// The pane is the one surface where a capture proves least. Its body is drawn by an offscreen
/// web engine that regularly loses the race with the screenshot, so a blank picture is the
/// expected artifact rather than evidence; and the claims worth checking are not in the picture
/// at all — how many remote resources were refused and which hosts they pointed at, whether the
/// sanitizer kept the HTML or fell back to text, which bars are up and what each of them offers
/// to do about it, and what the attachment strip made of the parts.
/// <para>
/// So this walks the bars that were built and says what is in them. The text and the buttons are
/// read out of the visual tree rather than out of the code that built it: a bar whose label is
/// right and whose button was never added reads identically from the source, and the question
/// this pose exists to answer is what a reader would find on screen.
/// </para>
/// </remarks>
public sealed partial class ReadingPaneBody
{
    /// <summary>Whether a run asked for the reading pane to describe itself.</summary>
    internal static bool DumpRequested { get; } =
        Environment.GetEnvironmentVariable("MAILBOX_READING") == "dump";

    /// <summary>
    /// Writes what this pane holds: the message, what the sanitizer did to it, every bar with its
    /// wording and its buttons, and the attachment strip.
    /// </summary>
    private void LogForHarness()
    {
        if (!DumpRequested || !Mailbox.App.Theming.WindowCapture.IsRequested) return;

        if (_message is null)
        {
            Log.Info($"Harness: reading — nothing selected; {_bars.Children.Count} bar(s).");
            LogBars();
            return;
        }

        var from = _message.From.Mailboxes.FirstOrDefault();

        Log.Info($"Harness: reading — “{HeaderSubject ?? _message.Subject}” from "
                 + $"{from?.Name} <{from?.Address}>"
                 + (HeaderFrom is { Length: > 0 } shown && shown != from?.Address ? $" (header draws {shown})" : string.Empty)
                 + $"; {_bars.Children.Count} bar(s).");

        if (_rendered is { } rendered)
        {
            // Blocked *and* hosts: the count is what the bar says, and the hosts are what the
            // tracker report claims. They come out of one sanitizing walk so they cannot disagree,
            // which is exactly the claim worth being able to check.
            Log.Info($"Harness: reading render — {(rendered.WasHtml ? "HTML" : "plain text")}, "
                     + $"{rendered.Html.Length} character(s); "
                     + $"{rendered.Blocked.Count} remote resource(s) refused "
                     + $"({rendered.BlockedImages} image, {rendered.Blocked.Count - rendered.BlockedImages} style)"
                     + (rendered.Hosts.Count > 0 ? $"; hosts: {string.Join(", ", rendered.Hosts)}" : "; no hosts")
                     + $"; {_inlined.Count} already inlined.");

            foreach (var blocked in rendered.Blocked)
            {
                Log.Info($"Harness: reading blocked — {blocked.Kind} {blocked.Host}  {blocked.Url}");
            }

            // What must not have survived. Checked against the document that reaches the engine
            // rather than against the sanitizer's own account of itself, because the question is
            // what the reader's browser is handed — and a message is a stranger's markup, so
            // "the parser drops it" and "it is not in the output" are different claims.
            var survived = new[] { "<script", "<iframe", "<form", "<object", "<embed", "javascript:", "onclick", "onload", "onerror", "@import", "expression(" }
                .Where(bad => rendered.Html.Contains(bad, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // A short excerpt of the words themselves, which is the only way to check a charset:
            // mojibake is well-formed text in the wrong alphabet, so nothing about the document's
            // shape gives it away. Harness-only, over a corpus that is invented start to finish.
            // The stylesheet first — the document carries the pane's own, and stripping tags
            // without dropping its contents reports the theme's font stack as the message's words.
            var text = System.Text.RegularExpressions.Regex.Replace(
                rendered.Html, "<(style|script|head)\\b[^>]*>.*?</\\1>", " ",
                System.Text.RegularExpressions.RegexOptions.Singleline
                | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            text = System.Text.RegularExpressions.Regex
                .Replace(text, "<[^>]+>", " ")
                .Replace("&nbsp;", " ", StringComparison.Ordinal);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

            // The tail rather than the head: every document opens with the Memo print header, so
            // the first hundred characters are the From/Sent/To block of every message alike and
            // say nothing about the one being read.
            Log.Info($"Harness: reading words — “{text[Math.Max(0, text.Length - 110)..]}”");

            Log.Info($"Harness: reading sanitized — {(survived.Count == 0 ? "nothing dangerous survived" : "SURVIVED: " + string.Join(", ", survived))}"
                     + $"; remote urls left in the document: "
                     + $"{(rendered.Html.Contains("http://", StringComparison.OrdinalIgnoreCase) || rendered.Html.Contains("https://", StringComparison.OrdinalIgnoreCase) ? "yes" : "none")}.");
        }

        LogBars();
        LogAttachments();
        PressForHarness();
    }

    /// <summary>Holds the capture while the engine is still drawing, so the answer below exists.</summary>
    private IDisposable? _engineHold;

    /// <summary>Takes a hold for the load that is about to start, releasing any earlier one.</summary>
    /// <remarks>
    /// The pane is refreshed more than once as a selection lands, and each load supersedes the
    /// last — so the hold moves to the newest load rather than stacking, and releasing twice is
    /// safe by <see cref="Mailbox.App.Theming.WindowCapture.Hold"/>'s own contract. Two guards
    /// keep a hold from outliving its answer: a pane that is not visible loads into an engine
    /// that never initialises — the shell's own pane still refreshes with the reading pane off —
    /// so no hold is taken for it; and a watchdog lets go after fifteen seconds regardless,
    /// because a hold that waits on an engine that never answers is a run that never ends.
    /// </remarks>
    private void HoldForEngine()
    {
        if (!DumpRequested || !Mailbox.App.Theming.WindowCapture.IsRequested) return;

        // Attached and not visible is the reading pane switched off: its engine will never
        // initialise, so a hold for it would only ever be the watchdog's to release. A pane
        // that is not attached yet is a message window still being built — that one is about
        // to be shown, and its engine's answer is the whole point of the hold.
        if (TopLevel.GetTopLevel(this) is not null && !IsEffectivelyVisible) return;

        _engineHold?.Dispose();
        var hold = Mailbox.App.Theming.WindowCapture.Hold();
        _engineHold = hold;

        _ = Task.Delay(15_000).ContinueWith(
            _ =>
            {
                if (ReferenceEquals(_engineHold, hold))
                {
                    Log.Warn($"Harness: reading engine — “{_message?.Subject}” never finished "
                             + "loading within 15s (a pane that never attached never will; "
                             + "a visible one that logs this never drew).");
                }

                hold.Dispose();
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Asks the engine what it actually drew, and writes the answer beside the sanitizer's.
    /// </summary>
    /// <remarks>
    /// The pane's other read-backs stop at the document: what the sanitizer produced and what
    /// was handed to the engine. Between that hand-off and the screen sits a whole web engine,
    /// and a capture cannot arbitrate — the offscreen renderer regularly has no frame yet when
    /// the settle timer fires, so a blank picture is expected even when everything worked. This
    /// is the read-back for that last leg: once the engine says the load finished, it is asked
    /// for the body's own text, which is the words a reader would see. A message whose document
    /// says one thing and whose engine says nothing is exactly the failure nothing else here
    /// can catch.
    /// </remarks>
    private async Task ReportEngineWordsAsync(bool loaded)
    {
        if (!DumpRequested || !Mailbox.App.Theming.WindowCapture.IsRequested) return;

        try
        {
            if (!loaded)
            {
                Log.Warn("Harness: reading engine — the load did not succeed; nothing to ask.");
                return;
            }

            if (_web is not { } web) return;

            // On the dispatcher, which is where the engine's bridge lives; bounded, because a
            // hold with no timeout turns an engine that never answers into a run that never ends.
            object? answer = null;
            var read = Dispatcher.UIThread.InvokeAsync(
                async () => answer = await web.InvokeScript("document.body.innerText"));
            var done = await Task.WhenAny(read, Task.Delay(10_000));

            if (done != read)
            {
                Log.Warn("Harness: reading engine — no answer to the text read-back within 10s.");
                return;
            }

            await read;

            var words = System.Text.RegularExpressions.Regex
                .Replace(answer?.ToString() ?? string.Empty, @"\s+", " ").Trim();

            Log.Info($"Harness: reading engine — the engine draws {words.Length} character(s)"
                     + (words.Length > 0 ? $": “{words[Math.Max(0, words.Length - 110)..]}”" : "."));
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: reading engine — the text read-back failed.", ex);
        }
        finally
        {
            _engineHold?.Dispose();
            _engineHold = null;
        }
    }

    /// <summary>What a run asked to be pressed on the pane's bars, or null.</summary>
    private static readonly string? PressWanted =
        Environment.GetEnvironmentVariable("MAILBOX_READING_PRESS") is { Length: > 0 } wanted
            ? wanted
            : null;

    /// <summary>Pressed already — a press rebuilds the bars, and a second one would never end.</summary>
    private static bool _pressedOnce;

    /// <summary>
    /// Presses one of the bar's own buttons by its caption: <c>MAILBOX_READING_PRESS=Show images</c>.
    /// </summary>
    /// <remarks>
    /// "Remote images are blocked until allowed" is two claims and a capture can only see the
    /// first. Whether allowing works, and whether allowing *this* sender leaves the next one
    /// blocked, needs the button pressed and the next message looked at — and these buttons live
    /// on a bar the pane builds, not in a dialog, so the dialog door cannot reach them.
    /// <para>
    /// Once per run: the press rebuilds the bars, which comes back through here, and a pose that
    /// pressed again on every rebuild would never finish.
    /// </para>
    /// </remarks>
    private void PressForHarness()
    {
        if (PressWanted is not { } wanted || _pressedOnce) return;
        if (!Mailbox.App.Theming.WindowCapture.IsRequested) return;

        // Only on the message the run asked for. The pane draws whatever the list selected first
        // and is asked again when MAILBOX_SELECT lands, so a press taken on the first message to
        // arrive is spent on the wrong one — which is exactly what happened, silently, and left
        // the safe-sender list empty with the log claiming the button was not there.
        if (Environment.GetEnvironmentVariable("MAILBOX_SELECT") is { Length: > 0 } chosen
            && !(_message?.Subject ?? string.Empty).Contains(chosen, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var bar in _bars.Children)
        {
            foreach (var button in bar.GetSelfAndVisualDescendants().OfType<Button>())
            {
                var caption = button.Content switch
                {
                    string words => words,
                    TextBlock { Text: { Length: > 0 } words } => words,
                    _ => string.Empty,
                };

                if (!caption.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) continue;

                _pressedOnce = true;
                Log.Info($"Harness: reading press — “{caption}”.");
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return;
            }
        }

        // Deliberately not consumed: the bars are rebuilt several times as a message is opened,
        // and the run's own message may not be the one on screen yet. A miss is reported and the
        // press stays available.
        Log.Warn($"Harness: reading press — no bar button starting “{wanted}” on “{_message?.Subject}”.");
    }

    /// <summary>Every info bar, in the order they are stacked, with what each says and offers.</summary>
    private void LogBars()
    {
        var index = 0;
        foreach (var bar in _bars.Children)
        {
            var words = Texts(bar);
            var buttons = Buttons(bar);

            Log.Info($"Harness: reading bar {index++} — {bar.GetType().Name}: "
                     + $"“{(words.Count > 0 ? string.Join(" | ", words) : "no text")}”"
                     + (buttons.Count > 0 ? $"  [{string.Join(", ", buttons)}]" : "  [no buttons]"));
        }
    }

    /// <summary>
    /// The attachment strip's chips, and whether the strip is up at all.
    /// </summary>
    /// <remarks>
    /// A message forwarded as an attachment is a <c>message/rfc822</c> part rather than a
    /// <c>MimePart</c>, and a strip that matched only the latter drew nothing for the commonest
    /// way of passing mail on — so the count here is held against what the message really carries.
    /// </remarks>
    private void LogAttachments()
    {
        if (Carried is not { } carried) return;

        // What the message carries only. The strip itself is a sibling of this control in the
        // shell rather than a child of it, and it is filled *after* the pane has refreshed — so
        // looking for it from here found nothing and reported a hidden strip over a message with
        // four attachments, which is a bug in the instrument that reads exactly like a bug in the
        // strip. `MainWindow.LogAttachmentStrip` reports the strip, after it has been shown.
        var parts = Mailbox.Rendering.MessageAttachments.List(carried);

        Log.Info($"Harness: reading attachments — the message carries {parts.Count}.");

        foreach (var part in parts)
        {
            Log.Info($"Harness: reading attachment — “{part.Name}” {part.MimeType}, {part.Describe()}"
                     + (part.IsMessage ? ", a carried message" : string.Empty)
                     + (part.FromTnef ? ", out of a winmail.dat" : string.Empty));
        }
    }

    /// <summary>Every piece of text drawn inside a control, in layout order.</summary>
    private static List<string> Texts(Visual root) =>
    [
        .. root.GetSelfAndVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim()),
    ];

    /// <summary>
    /// Every button inside a control, by its caption, with a note when it is greyed.
    /// </summary>
    /// <remarks>
    /// The caption is read off <c>Content</c> before the visual tree, because a bar's buttons
    /// carry a <see cref="TextBlock"/> as their content and a button that has not been templated
    /// yet has no visual children at all — which under a capture is most of them, and which read
    /// back as a row of buttons with no captions.
    /// </remarks>
    private static List<string> Buttons(Visual root) =>
    [
        .. root.GetSelfAndVisualDescendants()
            .OfType<Button>()
            .Select(b =>
            {
                var caption = b.Content switch
                {
                    string words when words.Trim().Length > 0 => words.Trim(),
                    TextBlock { Text: { Length: > 0 } words } => words.Trim(),
                    Visual inner when Texts(inner).Count > 0 => Texts(inner)[0],
                    _ => Texts(b).FirstOrDefault() ?? b.Name ?? "unnamed",
                };

                return b.IsEffectivelyEnabled ? caption : caption + " (greyed)";
            }),
    ];
}
