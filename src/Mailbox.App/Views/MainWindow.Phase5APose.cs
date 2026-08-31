using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;

namespace Mailbox.App.Views;

/// <summary>
/// The audit's compose-lane doors: what a pose puts on a message, what it presses, and what it
/// reads back off the window afterwards.
/// </summary>
/// <remarks>
/// <c>MAILBOX_COMPOSE</c> could fill To, Cc and Subject and press exactly three commands — Send,
/// Sign and Encrypt. Everything else the compose window does was provable only by reading its
/// handlers, which is the thing the rules of evidence forbid. These add the missing half:
/// <list type="bullet">
/// <item><description><c>MAILBOX_COMPOSE_TO</c>, <c>_CC</c>, <c>_BCC</c>, <c>_SUBJECT</c> — the
/// whole address block, including a display name with a comma in it, which nothing could
/// write.</description></item>
/// <item><description><c>MAILBOX_COMPOSE_RUN=id,id</c> — presses compose commands through the same
/// entry point the ribbon uses, reporting the info bar and the fields after each.</description></item>
/// <item><description><c>MAILBOX_COMPOSE_ATTACH=path:path</c> — real files on a real message,
/// without the desktop's picker, which no pose can answer.</description></item>
/// <item><description><c>MAILBOX_COMPOSE_PROBE=</c><c>enablement</c> | <c>fields</c> |
/// <c>accounts</c> | <c>completions</c> | <c>build</c> | <c>draft</c> — the states a capture cannot
/// show.</description></item>
/// </list>
/// <para>
/// All of it runs in one action posted at <see cref="DispatcherPriority.Loaded"/>, which is above
/// the Background where <c>MAILBOX_COMPOSE_QUEUE</c> presses Send: a pose that sets a header and a
/// pose that sends have to happen in that order or the read-back measures the wrong message.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this lane's doors onto a compose window the harness has just built.</summary>
    internal static void WirePhase5ADoors(ComposeWindow compose)
    {
        var from = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_FROM");
        var to = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_TO");
        var cc = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_CC");
        var bcc = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_BCC");
        var subject = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_SUBJECT");
        var run = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_RUN");
        var attach = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_ATTACH");
        var probe = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_PROBE");
        var typeLine = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_TYPE_LINE");
        var body = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_BODY");
        var menu = Environment.GetEnvironmentVariable("MAILBOX_COMPOSE_MENU");

        if (from is null && to is null && cc is null && subject is null
            && bcc is null && run is null && attach is null && probe is null && typeLine is null
            && menu is null && body is null)
        {
            return;
        }

        // Before the window opens, as the shell's own reply path does it: the sending account
        // decides the signature, the From line and — the reason this door exists — which secret
        // key a signature is made with. The seeded ring holds one, and it is not the default
        // account's, so nothing could have signed anything without a way to say so.
        if (from is { Length: > 0 }) compose.SendFromAccount(from);

        compose.Opened += (_, _) => Dispatcher.UIThread.Post(
            async () =>
            {
                try
                {
                    // Held across every door: render-and-exit fires at the first idle moment,
                    // and the settled press's awaits hand it several — the shell's run door
                    // learnt this the hard way.
                    using var hold = WindowCapture.Hold();

                    // The body goes in before the presses, so a Save or Send acts on it —
                    // which is the order a writer works in.
                    if (body is { Length: > 0 }) compose.Surface.PoseBodyText(body);

                    await RunPhase5ADoorsAsync(compose, to, cc, bcc, subject, run, attach, probe, typeLine);

                    // The From button's account menu, read back the way every menu is now.
                    if (menu is "from" && compose.Surface is { } surface
                        && !surface.PoseFromMenu())
                    {
                        Log.Info("Harness: compose — the From button is not on this window.");
                    }
                }
                catch (Exception ex)
                {
                    // Logged rather than dropped: a posted action that throws leaves a run with a
                    // plausible capture, no error and nothing to grep — a trap the audit has
                    // already fallen into once.
                    Log.Warn("Harness: a compose door failed.", ex);
                }
            },
            DispatcherPriority.Loaded);
    }

    private static async Task RunPhase5ADoorsAsync(
        ComposeWindow compose,
        string? to, string? cc, string? bcc, string? subject,
        string? run, string? attach, string? probe, string? typeLine)
    {
        if (to is not null || cc is not null || bcc is not null || subject is not null)
        {
            compose.PoseRecipients(to, cc, bcc, subject);
            Log.Info($"Harness: compose header posed — {Describe(compose)}");
        }

        // Before the presses: Check Names and Send both read the list, and a file that arrives
        // after them would not be in the message they acted on.
        if (attach is { Length: > 0 })
        {
            await compose.PoseAttachAsync(
                attach.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var (visible, text, files) = compose.HarnessAttachments;
            Log.Info($"Harness: attachments — strip visible={visible}, “{text}”, "
                     + $"{files.Count} file(s): {string.Join(", ", files)}");
        }

        // Types into Cc or Bcc, whose Auto-Complete Lists are separate objects from To's and had
        // no door of their own: MAILBOX_COMPOSE_TYPE_LINE=cc:per
        if (typeLine is { Length: > 0 })
        {
            var colon = typeLine.IndexOf(':');
            var which = colon > 0 ? typeLine[..colon].Trim().ToLowerInvariant() : "to";
            var typed = colon > 0 ? typeLine[(colon + 1)..] : typeLine;
            var line = which switch { "cc" => 1, "bcc" => 2, _ => 0 };

            compose.PoseTypingInto(line, typed);
            var (open, offered, entries) = compose.HarnessCompletion(line);
            Log.Info($"Harness: auto-complete on {which} for “{typed}”: open={open}, offered={offered}");
            foreach (var entry in entries) Log.Info($"Harness:   offers {entry}");
        }

        if (run is { Length: > 0 })
        {
            foreach (var id in run.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // "forget:<address>" is the ✕ on a suggestion, which is a click on a popup rather
                // than a command — and the one recipient affordance with no id to press.
                if (id.StartsWith("forget:", StringComparison.OrdinalIgnoreCase))
                {
                    var address = id["forget:".Length..];
                    compose.PoseForget(address);
                    Log.Info($"Harness: compose forgot {address} from the Auto-Complete List.");
                    continue;
                }

                // "type:<line>:<text>" re-asks a line for its suggestions in the middle of a
                // sequence, which is the only way to see that forgetting one took it out of the
                // list rather than merely writing to the store.
                if (id.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = id.Split(':', 3);
                    var line = parts.Length > 1
                        ? parts[1].Trim().ToLowerInvariant() switch { "cc" => 1, "bcc" => 2, _ => 0 }
                        : 0;
                    var typed = parts.Length > 2 ? parts[2] : string.Empty;

                    compose.PoseTypingInto(line, typed);
                    var completion = compose.HarnessCompletion(line);
                    Log.Info($"Harness: auto-complete on line {line} for “{typed}”: "
                             + $"open={completion.IsOpen}, offered={completion.Offered}");
                    foreach (var entry in completion.Entries) Log.Info($"Harness:   offers {entry}");
                    continue;
                }

                // "remove:<name>" presses the chip's Remove through its own handler — the way
                // off a message that used to mean discarding the draft.
                if (id.StartsWith("remove:", StringComparison.OrdinalIgnoreCase))
                {
                    var taken = compose.Surface.PoseRemoveAttachment(id["remove:".Length..]);
                    var (nowVisible, nowText, nowFiles) = compose.HarnessAttachments;
                    Log.Info($"Harness: remove — {taken}; strip visible={nowVisible}, "
                             + $"“{nowText}”, {nowFiles.Count} file(s) left.");
                    continue;
                }

                // wait-<ms> holds the next press, as the shell's run door does — for anything
                // driven by idle-time machinery.
                if (id.StartsWith("wait-", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(id["wait-".Length..], out var pause))
                {
                    await Task.Delay(pause);
                    continue;
                }

                await PressSettledAsync(compose, id);
            }
        }

        foreach (var what in (probe ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await ProbeAsync(compose, what.ToLowerInvariant());
        }
    }

    /// <summary>
    /// One compose press with the settled read-back the press sweep classifies from — the same
    /// discipline as the shell's run door: whether the id is known and enabled, and what moved
    /// across every channel the window has, read again after a settle.
    /// </summary>
    /// <remarks>
    /// The immediate read the old door took reported "nothing happened" about a command that was
    /// mid-happening — a dialog shown from an async continuation, a task queued. Same lesson as
    /// the shell's run door, at the same 600ms. The <c>fallback</c> token is the surface's own
    /// honesty recognised rather than guessed at: a command it does not carry answers with its
    /// recorded status sentence, and that is a different class from a press that acted.
    /// </remarks>
    private static async Task PressSettledAsync(ComposeWindow compose, string id)
    {
        MailboxCommand? command = null;
        var known = false;
        try
        {
            known = App.Commands.TryGet(new CommandId(id), out command);
        }
        catch (ArgumentException)
        {
            // A malformed id is as unknown as an unregistered one.
        }

        var enabled = known && compose.HarnessEnabled(id);
        var barBefore = compose.HarnessStatus.Text;
        var fieldsBefore = Describe(compose);
        var bodyBefore = compose.Surface.BodyText;
        var markupBefore = compose.Surface.BodyHtml;
        var caretBefore = CaretDigest(compose);
        var stateBefore = StateDigest(compose);
        var attachedBefore = compose.HarnessAttachments.Files.Count;

        Log.Info($"Harness: compose running {id}.");
        compose.PressCommand(id);

        await Task.Delay(600);

        var barAfter = compose.HarnessStatus.Text;
        var fieldsAfter = Describe(compose);
        var bodyAfter = compose.Surface.BodyText;
        var markupAfter = compose.Surface.BodyHtml;
        var caretAfter = CaretDigest(compose);
        var stateAfter = StateDigest(compose);
        var attachedAfter = compose.HarnessAttachments.Files.Count;

        var fallback = known
                       && ComposeAvailability.For(command!.Id) is { } status
                       && barAfter == $"{command.Label} — {status.Note}";

        Log.Info(
            $"Harness: ran {id} — {(known ? "known" : "UNKNOWN to the catalogue")}, "
            + $"{(enabled ? "enabled" : "disabled")}{(fallback ? ", fallback" : string.Empty)}, "
            + $"bar “{barBefore}”→“{barAfter}”, "
            + $"fields {(fieldsAfter == fieldsBefore ? "unchanged" : "changed")}, "
            + $"body {bodyBefore.Length}→{bodyAfter.Length}, "
            + $"markup {(markupAfter == markupBefore ? "unchanged" : "changed")}, "
            + $"caret {(caretAfter == caretBefore ? "unchanged" : $"“{caretBefore}”→“{caretAfter}”")}, "
            + $"state {(stateAfter == stateBefore ? "unchanged" : $"“{stateBefore}”→“{stateAfter}”")}, "
            + $"attachments {attachedBefore}→{attachedAfter}, "
            + $"window {(compose.IsVisible ? "open" : "closed")}, "
            + $"windows: {WindowsBeside(compose)}");
    }

    /// <summary>The caret's format on one line, so a formatting press has a before and after.</summary>
    private static string CaretDigest(ComposeWindow compose)
    {
        if (compose.GetVisualDescendants().OfType<Mailbox.Editor.ComposeEditor>().FirstOrDefault()
            is not { } editor)
        {
            return "no editor";
        }

        var caret = editor.GetCaretFormat();
        return $"{caret.FontFamily} {caret.FontSize}"
               + (caret.Bold ? " bold" : string.Empty)
               + (caret.Italic ? " italic" : string.Empty)
               + (caret.Underline ? " underline" : string.Empty)
               + (caret.Strike ? " strike" : string.Empty);
    }

    /// <summary>Protection, delay and format on one line, so the switches have a before and after.</summary>
    private static string StateDigest(ComposeWindow compose)
    {
        var (protection, notBefore, plainText, _) = compose.HarnessState;
        return protection
               + (notBefore is { } t ? $" not before {t:u}" : string.Empty)
               + (plainText ? " plain" : string.Empty);
    }

    /// <summary>
    /// Visible windows other than the shell and the given one — what an item window's press
    /// opened. Shared by the compose, contact and appointment doors, whose settled lines all
    /// answer the same question.
    /// </summary>
    internal static string WindowsBeside(Avalonia.Controls.Window besides)
    {
        var others = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.Windows
            .Where(w => !ReferenceEquals(w, besides) && w is not MainWindow && w.IsVisible)
            .Select(w => string.IsNullOrWhiteSpace(w.Title) ? w.GetType().Name : w.Title)
            .ToList() ?? [];

        return others.Count == 0 ? "none" : string.Join(", ", others.Select(t => $"“{t}”"));
    }

    private static async Task ProbeAsync(ComposeWindow compose, string what)
    {
        switch (what)
        {
            case "fields":
                Log.Info($"Harness: compose fields — {Describe(compose)}");
                break;

            case "accounts":
                foreach (var entry in compose.HarnessAccountMenu())
                {
                    Log.Info($"Harness: the From menu offers {entry}");
                }

                break;

            case "completions":
                for (var line = 0; line < 3; line++)
                {
                    var (open, offered, entries) = compose.HarnessCompletion(line);
                    Log.Info($"Harness: line {line} completion open={open}, offered={offered}, "
                             + $"{entries.Count} described");
                }

                break;

            // Every command the compose bar places, its recorded availability, and whether the
            // ribbon would draw it enabled — with an empty body and with one. The greying rule is
            // the claim; this is the only way to see it without photographing 200 buttons.
            case "enablement":
                Report(compose, "empty");
                compose.PoseBodyText("The quick brown fox.");
                Report(compose, "filled");
                break;

            // Which windows are open. Select Names is a modal owned by the compose window, and a
            // modal never appears in that window's own capture — so "the button opened the address
            // book" and "the button did nothing" photograph identically. The list tells them apart.
            case "windows":
                var showing = (Avalonia.Application.Current?.ApplicationLifetime
                        as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                    ?.Windows.Select(w => $"{w.GetType().Name} “{w.Title}” {w.Width:0}x{w.Height:0}")
                    .ToList() ?? [];

                Log.Info($"Harness: {showing.Count} window(s) open: {string.Join(" | ", showing)}");
                break;

            case "build":
                if (await compose.HarnessBuildAsync() is { } bytes)
                {
                    var path = Path.Combine(Path.GetTempPath(), "mailbox-compose-build.eml");
                    await File.WriteAllBytesAsync(path, bytes);
                    Log.Info($"Harness: built {bytes.Length} bytes to {path}");
                }
                else
                {
                    Log.Info("Harness: nothing to build — no sending account.");
                }

                break;

            case "draft":
                var id = await compose.PoseSaveDraftAsync();
                var (visible, text) = compose.HarnessStatus;
                Log.Info($"Harness: draft saved as row {id?.ToString() ?? "none"}; "
                         + $"info bar {(visible ? "shows" : "hidden")} “{text}”");
                break;

            default:
                Log.Info($"Harness: no compose probe called “{what}”.");
                break;
        }

        static void Report(ComposeWindow compose, string when)
        {
            foreach (var id in DefaultRibbonLayouts.Compose.PlacedCommands
                         .Where(id => id != RibbonItem.SeparatorId)
                         .Distinct()
                         .OrderBy(id => id.Value, StringComparer.Ordinal))
            {
                if (!App.Commands.TryGet(id, out var command)) continue;

                var status = ComposeAvailability.For(id);
                Log.Info($"Harness: enablement\t{when}\t{id.Value}\t{command.Label}\t"
                         + $"{status?.State.ToString() ?? "(untabled)"}\t"
                         + $"neutralIcon={command.NeutralIcon}\tenabled={compose.HarnessEnabled(id.Value)}");
            }
        }
    }

    /// <summary>The address block on one line, which is what almost every read-back wants.</summary>
    private static string Describe(ComposeWindow compose)
    {
        var (to, cc, bcc, subject) = compose.HarnessFields;
        var rows = compose.HarnessOptionalRows;

        return $"To=“{to}” Cc=“{cc}” Bcc=“{bcc}” "
               + $"Subject=“{subject}” From=“{compose.HarnessFrom}” "
               + $"(bccRow={rows.Bcc}, fromRow={rows.From})";
    }
}
