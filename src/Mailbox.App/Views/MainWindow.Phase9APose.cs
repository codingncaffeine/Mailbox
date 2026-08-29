using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The notes lane's doors: filling a note window, closing it the way a reader closes one, and
/// reading back what the wall drew, what the store kept and what a forwarded note put on the wire.
/// </summary>
/// <remarks>
/// Four things could not be reached before these.
/// <list type="bullet">
/// <item><description><b>Save-on-close.</b> A note has no Save button — closing the window is the
/// save — so the only proof it works is to change the writing and then read the row back. Nothing
/// could type into that window at all, and every note in every seed was written straight through
/// the repository, so the form's own save path had never run.</description></item>
/// <item><description><b>What the window is.</b> Its size, its chrome and its buttons decide
/// whether it is the reference's sticky square or a dialog wearing a note's colours, and a
/// photograph cannot say which controls it holds.</description></item>
/// <item><description><b>Where the wall puts a note.</b> A square's place is either remembered or
/// recomputed, and only the geometry the view really drew — held against the store — tells those
/// apart.</description></item>
/// <item><description><b>What leaves when a note is forwarded.</b> Forward opens a compose window
/// that no pose could reach: <c>MAILBOX_COMPOSE_QUEUE</c> only presses Send on windows the compose
/// poses open themselves, so the message a note becomes had never been read as MIME.</description></item>
/// </list>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Wires this lane's doors. Called at the end of the Notes module pose, which is the only
    /// route that builds the module — so nothing here can fire in a run that never opened it.
    /// </summary>
    /// <remarks>
    /// Posted twice below Background, because that is where <c>MAILBOX_RUN</c> presses a command:
    /// a probe that read the store from the same turn would report the wall as it opened rather
    /// than as the presses left it.
    /// </remarks>
    private void WirePhase9ADoors(ShellViewModel shell)
    {
        var probe = Environment.GetEnvironmentVariable("MAILBOX_NOTE_PROBE");
        var send = Environment.GetEnvironmentVariable("MAILBOX_NOTE_SEND");
        if (probe is not { Length: > 0 } && send is not { Length: > 0 }) return;

        Dispatcher.UIThread.Post(
            () => Dispatcher.UIThread.Post(
                () =>
                {
                    if (send is { Length: > 0 }) GuardedNoteDoor(() => PoseNoteSend(send));
                    if (probe is { Length: > 0 }) GuardedNoteDoor(() => PoseNoteProbe(shell, probe));
                },
                DispatcherPriority.ApplicationIdle),
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Runs a door and says so when it throws.
    /// </summary>
    /// <remarks>
    /// A posted action that throws leaves a run with a plausible capture, no error and nothing to
    /// grep, which is the trap this sweep has already been caught by once.
    /// </remarks>
    private static void GuardedNoteDoor(Action door)
    {
        try
        {
            door();
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: a note door failed.", ex);
        }
    }

    // ---- The note window's own controls ----------------------------------------------------------

    /// <summary>
    /// Wires the form doors onto a note window the shell has just built.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_NOTE_FORM=body=Shopping\nmilk and coffee;categories=Red Category|close</c>: the
    /// fields before, the fields the pose sets, the fields after, and then <c>close</c> pressed
    /// through the caption button's own <c>Click</c> — the path a pointer takes, and the whole of
    /// a note's saving. <c>\n</c> in a value is a line break, which matters here more than
    /// anywhere else in the application: a note's title is its first line.
    /// <para>
    /// Every field is read back whether or not the pose set one, because "what does this window
    /// carry" is itself a question the audit asks and a photograph answers badly.
    /// </para>
    /// </remarks>
    internal static void WirePhase9AForm(NoteWindow window)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_NOTE_FORM") is not { Length: > 0 } spec) return;

        var parts = spec.Split('|', StringSplitOptions.TrimEntries);
        var sets = parts[0];
        var press = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

        window.Opened += (_, _) => Dispatcher.UIThread.Post(
            () => GuardedNoteDoor(() =>
            {
                foreach (var (field, value) in window.FormFields)
                {
                    Log.Info($"Harness: note form before — {field}: {value}");
                }

                foreach (var pair in NoteFields(sets))
                {
                    Log.Info(window.SetFormField(pair.Key, pair.Value.Replace("\\n", "\n", StringComparison.Ordinal))
                        ? $"Harness: note form set {pair.Key} to “{pair.Value}”."
                        : $"Harness: the note form has no field called “{pair.Key}”.");
                }

                foreach (var (field, value) in window.FormFields)
                {
                    Log.Info($"Harness: note form after — {field}: {value}");
                }

                if (press.Length == 0) return;

                Log.Info(press switch
                {
                    "close" when window.PressClose() => "Harness: pressed close on the note.",
                    "close" => "Harness: the note window draws no caption button to close.",
                    _ => "Harness: closing the note window without its caption button.",
                });

                if (press != "close") window.Close();
            }),
            DispatcherPriority.Loaded);
    }

    private static IReadOnlyDictionary<string, string> NoteFields(string spec)
    {
        var got = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0) continue;
            got[pair[..at].Trim()] = pair[(at + 1)..].Trim();
        }

        return got;
    }

    // ---- What the wall drew, and what the store kept ---------------------------------------------

    /// <summary>
    /// <c>MAILBOX_NOTE_PROBE=store|wall|shell</c>, or <c>all</c>.
    /// </summary>
    /// <remarks>
    /// <c>store</c> reads every journal collection out of the repository — the row behind each
    /// square, its type, its categories and the moment it was last written — which is what a save
    /// has to be checked against and what says whether a note carries a size or a place at all.
    /// <c>wall</c> reads the geometry the view really drew for each note, so "where is it" is
    /// measured rather than assumed. <c>shell</c> reads the window state a modal child leaves the
    /// application in, which is the difference between a sticky note and a dialog.
    /// </remarks>
    private void PoseNoteProbe(ShellViewModel shell, string spec)
    {
        var wanted = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant()).ToHashSet();
        var all = wanted.Contains("all");

        if (all || wanted.Contains("store"))
        {
            foreach (var list in App.Pim.Collections(CollectionKind.Journal))
            {
                var rows = App.Pim.Items(list.Id).ToList();
                Log.Info($"Harness: note store — folder {list.Id} “{list.DisplayName}”, "
                    + $"default {list.IsDefault}, shown {list.IsVisible}, {rows.Count} row(s).");

                foreach (var item in rows)
                {
                    var entry = PimJournalCodec.FromItem(item);
                    Log.Info($"Harness: note row {item.Id} — type “{entry.EntryType}”, "
                        + $"title “{entry.Summary}”, body “{Flat(entry.Description)}”, "
                        + $"categories [{string.Join(", ", entry.Categories)}], "
                        + $"made {Moment(entry.When)}, "
                        + $"written {entry.LastModified.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}, "
                        + $"sync {item.SyncState}, uid {entry.Uid}.");
                }
            }
        }

        if (all || wanted.Contains("wall"))
        {
            var notes = EnsureNotes(shell);
            UpdateLayout();

            Log.Info($"Harness: the wall is {notes.View.Bounds.Width:0}×{notes.View.Bounds.Height:0}, "
                + $"showing {notes.Arrangement}, {notes.Rows.Count} note(s).");

            foreach (var row in notes.Rows)
            {
                var box = notes.View.BoxOf(row.ItemId);
                Log.Info($"Harness: the wall drew “{row.Title}” (row {row.ItemId}) at "
                    + (box is { } at
                        ? $"{at.X:0},{at.Y:0} {at.Width:0}×{at.Height:0}."
                        : "nowhere — it is off the visible part."));
            }
        }

        if (!all && !wanted.Contains("shell")) return;

        var windows = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows ?? [];

        Log.Info($"Harness: the shell is {(IsEnabled ? "enabled" : "disabled")}; "
            + $"{windows.Count} window(s): "
            + string.Join(", ", windows.Select(w => $"{w.GetType().Name} ({(w.IsEnabled ? "enabled" : "disabled")})")));

        // What the module's own View tab moves. Its Arrangement and Current View groups carry the
        // shell's commands, and the shell's commands act on the message list — so a press that
        // looks inert has to be checked against the list it really moved, not against the wall.
        Log.Info($"Harness: the message list is sorted {(shell.SortDescending ? "descending" : "ascending")}, "
            + $"view “{shell.CurrentViewName}” of [{string.Join(", ", shell.ViewNames)}], "
            + $"folder “{shell.SelectedFolder?.Name ?? "—"}” in {shell.CurrentAddress ?? "no account"}.");
    }

    // ---- Two pointer gestures the wall answers and no pose could make ----------------------------

    /// <summary>
    /// The pointer moved over a point a drawn view drew at, which is the only way to reach a hover
    /// state on a surface that has no controls to style.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_HOVER</c> reaches the rail, a ribbon command and the caption buttons — all of
    /// them controls. A drawn view keeps its own hover, repaints itself for it, and had no way to
    /// be put in one, so every drawn surface in this application has an unphotographed state.
    /// </remarks>
    internal static void Hover(Control view, Point point)
    {
        var root = TopLevel.GetTopLevel(view) as Visual ?? view;
        var at = view.TranslatePoint(point, root) ?? point;

        view.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            view,
            new Pointer(4, PointerType.Mouse, isPrimary: true),
            root,
            at,
            0,
            new PointerPointProperties(),
            KeyModifiers.None));
    }

    /// <summary>
    /// Two clicks at a point a drawn view drew at, which is the gesture the reference opens a note
    /// with and the one it makes a new note with.
    /// </summary>
    /// <remarks>
    /// The single-click press already existed; a double click did not, so the two branches that
    /// only a second click reaches — a square opening, and the empty wall making a note — were
    /// posed by calling the method the branch calls rather than by the gesture. That proves the
    /// method, not the wall.
    /// </remarks>
    internal static void DoubleClick(Control view, Point point)
    {
        var root = TopLevel.GetTopLevel(view) as Visual ?? view;
        var at = view.TranslatePoint(point, root) ?? point;

        var pointer = new Pointer(5, PointerType.Mouse, isPrimary: true);
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);

        view.RaiseEvent(new PointerPressedEventArgs(view, pointer, root, at, 0, down, KeyModifiers.None, 2));
        view.RaiseEvent(new PointerReleasedEventArgs(view, pointer, root, at, 1, up, KeyModifiers.None, MouseButton.Left));
    }

    /// <summary>One line of a note's body, so a log line stays one line.</summary>
    private static string Flat(string text)
        => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', '⏎');

    private static string Moment(EventTime? when)
        => when is { } at ? at.Wall.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    // ---- What leaves when a note is forwarded ----------------------------------------------------

    /// <summary>
    /// <c>MAILBOX_NOTE_SEND=b.person@example.com</c> — addresses and sends whatever compose window
    /// is open, then reads the queued message back off its own blob as MIME.
    /// </summary>
    /// <remarks>
    /// Forward on a note opens a plain compose window through the shell's own <c>NewMessage</c>,
    /// which none of the compose poses reach: those wire themselves onto a window they opened
    /// themselves. So this door takes the window that is there — the one the press opened — rather
    /// than making a second one, which is the only way the claim being checked ("a note becomes a
    /// message") is the thing measured.
    /// </remarks>
    private void PoseNoteSend(string to)
    {
        var compose = ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.Windows ?? [])
            .OfType<ComposeWindow>()
            .LastOrDefault();

        if (compose is null)
        {
            Log.Info("Harness: nothing opened a compose window, so there is no note to send.");
            return;
        }

        compose.PoseHeader(to.Trim(), string.Empty, string.Empty);
        compose.PressSend();

        Dispatcher.UIThread.Post(() => GuardedNoteDoor(ReadOutboxBack), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>The message that left, as it was written rather than as the window drew it.</summary>
    private void ReadOutboxBack()
    {
        foreach (var account in App.Accounts.All)
        {
            foreach (var queued in account.Mail.Outbox(account.Account.Id))
            {
                if (account.Mail.LoadBlob(queued.BlobId) is not { } raw)
                {
                    Log.Info($"Harness: outbox #{queued.Id} in {account.Account.Address} has no blob.");
                    continue;
                }

                using var stream = new MemoryStream(raw);
                var message = MimeKit.MimeMessage.Load(stream);

                Log.Info($"Harness: the outbox holds #{queued.Id} in {account.Account.Address}, {queued.State} — "
                    + $"from “{message.From}”, to “{message.To}”, subject “{message.Subject}”.");

                foreach (var header in message.Headers)
                {
                    Log.Info($"Harness: sent header — {header.Field}: {header.Value}");
                }

                Log.Info($"Harness: sent text — “{Flat(message.TextBody ?? "—")}”");
                Log.Info($"Harness: sent html — “{Flat(message.HtmlBody ?? "—")}”");
                Log.Info($"Harness: sent attachments — {message.Attachments.Count()}: "
                    + string.Join(", ", message.Attachments.Select(a => a.ContentType.MimeType)));
            }
        }
    }
}
