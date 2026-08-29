using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Mailbox.App.Options;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// Reads an Options page back control by control, and sets one to a chosen value.
/// </summary>
/// <remarks>
/// <c>MAILBOX_OPTIONS_PRESS</c> could toggle a tick and nothing else, so two thirds of what is on
/// these pages — every dropdown, every spinner, every text field — had no door at all: a capture
/// could photograph a combo but not say how many entries it held, and nothing could type a number
/// past a spinner's maximum to find out whether the maximum was real. The three questions the
/// audit asks of a row are whether it reads its setting, whether it writes it, and whether the
/// value it writes means anything, and the first two need a value put in and read back rather than
/// a tick flipped.
/// <para>
/// Everything here goes through the control the renderer really built, found by the label the
/// reader really sees, and reports the key the row declared — <see cref="OptionsPageRenderer.Keys"/>
/// is the one place a row's key is worked out, so nothing here can guess one.
/// </para>
/// </remarks>
internal static class OptionsPageAudit
{
    /// <summary>
    /// Every value-carrying row on the rendered page: its kind, label, key, what the store holds
    /// and what the control shows — with a dropdown's length and a spinner's bounds, which are
    /// what "populated" and "bounded" mean.
    /// </summary>
    internal static void Dump(string pageId, Control page, OptionsPageRenderer renderer)
    {
        var rows = 0;

        foreach (var control in page.GetLogicalDescendants().OfType<Control>())
        {
            var key = renderer.Keys.TryGetValue(control, out var declared) ? declared : null;

            var line = control switch
            {
                CheckBox box => $"check   “{Caption(box)}” = {box.IsChecked == true}",
                RadioButton button => $"radio   “{Caption(button)}” [{button.GroupName}] = {button.IsChecked == true}",
                ComboBox combo =>
                    $"combo   “{LabelBeside(combo)}” = [{combo.SelectedIndex}] "
                    + $"“{combo.SelectedItem}” of {combo.ItemCount} entr{(combo.ItemCount == 1 ? "y" : "ies")}",
                NumericUpDown spinner =>
                    $"spinner “{LabelBeside(spinner)}” = {spinner.Value} "
                    + $"({spinner.Minimum}–{spinner.Maximum} step {spinner.Increment})",
                TextBox text => $"text    “{LabelBeside(text)}” = “{text.Text}”",
                _ => null,
            };

            if (line is null) continue;
            rows++;

            // The key a row would write, and what is stored under it now. A row keyed by its own
            // label is one of the backlog's — said plainly, because "persists something" and
            // "drives something" look identical from a photograph and from a press. A control a
            // slot built without registering a key is different again: it drives live state —
            // the send/receive schedule, an autostart entry — and calling it unread would report
            // §20's backlog about a wired row.
            var caption = Caption(control) is { Length: > 0 } own ? own : LabelBeside(control);
            var stored = key is not null
                ? string.Equals(key, caption, StringComparison.Ordinal)
                    ? $"persists under its label, read by no feature — {App.Settings.Stored(key) ?? "(unset)"}"
                    : $"{key} = {App.Settings.Stored(key) ?? "(unset)"}"
                : InSlot(control, renderer)
                    ? "a live control the window wires — state of its own, no key to read back"
                    : "no key of its own — persists under its label, read by no feature";

            Log.Info($"Harness: options row — {pageId}: {line}"
                     + (control.IsEffectivelyEnabled ? string.Empty : "  [greyed]")
                     + $"  · {stored}");
        }

        Log.Info($"Harness: options page — {pageId} drew {rows} value-carrying row(s).");
    }

    /// <summary>
    /// Sets rows on the page that is up, and says what each wrote.
    /// </summary>
    /// <param name="spec">
    /// Comma-separated. <c>label</c> toggles a tick or chooses a radio. <c>label=value</c> sets a
    /// dropdown (by index when the value is a number, otherwise by the entry that contains it), a
    /// spinner (to that number, out of range included — a bound that is not enforced is the thing
    /// worth finding) or a text field.
    /// </param>
    internal static void Press(Control page, OptionsPageRenderer renderer, string spec)
    {
        foreach (var step in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = step.IndexOf('=', StringComparison.Ordinal);
            var wanted = eq > 0 ? step[..eq].Trim() : step;
            var value = eq > 0 ? step[(eq + 1)..].Trim() : null;

            if (Find(page, wanted, value is null) is not { } control)
            {
                Log.Info($"Harness: no options row reads '{wanted}'"
                         + (value is null ? string.Empty : " as something a value can be put into") + ".");
                continue;
            }

            var what = Set(control, value);

            var wrote = renderer.Keys.TryGetValue(control, out var key)
                ? $"{key} = {App.Settings.Stored(key) ?? "(unset)"}"
                : "nothing — the row carries no key";

            var named = Caption(control) is { Length: > 0 } caption ? caption : LabelBeside(control);
            Log.Info($"Harness: pressed '{named}', now {what}, wrote {wrote}.");
        }
    }

    /// <summary>Whether the control lives inside one of the page's live slots.</summary>
    private static bool InSlot(Control control, OptionsPageRenderer renderer)
        => renderer.Slots.Values.Any(host => control.GetLogicalAncestors().Contains(host));

    /// <summary>The row named, preferring a toggle when no value was given and a field when one was.</summary>
    private static Control? Find(Control page, string wanted, bool toggle)
    {
        var all = page.GetLogicalDescendants().OfType<Control>().ToList();

        if (toggle)
        {
            return all.OfType<ToggleButton>()
                .FirstOrDefault(c => Caption(c).Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        return all.FirstOrDefault(c =>
            c is ComboBox or NumericUpDown or TextBox
            && LabelBeside(c).Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Puts the value in through the control, so the row's own handler is what writes it.</summary>
    private static string Set(Control control, string? value)
    {
        switch (control)
        {
            case RadioButton radio:
                radio.IsChecked = true;
                return "on";

            case ToggleButton toggle:
                toggle.IsChecked = toggle.IsChecked != true;
                return toggle.IsChecked == true ? "on" : "off";

            case ComboBox combo when value is not null:
                if (int.TryParse(value, CultureInfo.InvariantCulture, out var index))
                {
                    combo.SelectedIndex = index;
                }
                else
                {
                    var at = combo.Items.ToList()
                        .FindIndex(i => i?.ToString()?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);

                    if (at < 0) return $"unchanged — no entry reads “{value}”";
                    combo.SelectedIndex = at;
                }

                return $"[{combo.SelectedIndex}] “{combo.SelectedItem}”";

            case NumericUpDown spinner when value is not null:
                spinner.Value = decimal.TryParse(value, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : spinner.Value;

                // What the control kept, which is not what was asked for when a bound holds.
                return $"{spinner.Value} (asked for {value}, bounds {spinner.Minimum}–{spinner.Maximum})";

            case TextBox text when value is not null:
                text.Text = value;

                // The write is on LostFocus, as a reader's would be: assigning Text alone leaves
                // the store holding the old value and the page looking as though it took. The
                // args type matters — the event is typed, and a plain RoutedEventArgs takes the
                // run down inside whatever handler casts it.
                text.RaiseEvent(new Avalonia.Input.FocusChangedEventArgs(
                    Avalonia.Input.InputElement.LostFocusEvent) { Source = text });
                return $"“{text.Text}”";

            default:
                return "unchanged";
        }
    }

    private static string Caption(Control control)
        => control is ContentControl { Content: { } content } ? content.ToString() ?? string.Empty : string.Empty;

    /// <summary>The label the renderer stood to the left of a control, or empty for one with none.</summary>
    private static string LabelBeside(Control control)
    {
        for (ILogical? node = control; node is not null; node = node.LogicalParent)
        {
            if (node is not StackPanel row) continue;

            if (row.Children.OfType<TextBlock>().FirstOrDefault() is { Text: { Length: > 0 } text })
            {
                return text;
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// The doors Phase 12A needed: a message whose remote picture really can be fetched, and a report
/// of what the Trust Center's and Advanced page's switches are actually set to.
/// </summary>
/// <remarks>
/// <b>Why a delivered message rather than the seed's.</b> The audit's seed carries a newsletter
/// with two remote images on it, and both point at hosts that do not exist — which is right for
/// proving that nothing is fetched, and useless for proving that something is. "Don't download
/// pictures automatically in messages" is a switch whose off position means <em>a request goes
/// out and the picture appears</em>, and a host that never resolves cannot tell a request that
/// was made from one that was not. <c>MAILBOX_REMOTE_PICTURE=&lt;url&gt;</c> files a message
/// carrying one <c>img</c> at an address the caller controls — a local server — so both
/// directions are readable: the publisher's own request log says whether the fetch happened, and
/// the pane's inlined count says whether the picture arrived.
/// <para>
/// <b>And why the switches are logged.</b> Rule 2 of the audit's evidence: a capture run's
/// settings are a scratch copy, so what a run was posed with cannot be read out of the settings
/// file afterwards. The line below is that read-back — what this process believes, printed from
/// the same properties the behaviour asks.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this file's doors. Called once, from the constructor.</summary>
    private void WirePhase12ADoors()
    {
        // Before anything selects a row: the message has to be in the folder for the list to
        // draw it, and Loaded is the pass the folder pose runs in.
        if (Environment.GetEnvironmentVariable("MAILBOX_REMOTE_PICTURE") is { Length: > 0 } url)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => DeliverRemotePicture(url), DispatcherPriority.Send);
        }

        if (WindowCapture.IsRequested)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(ReportTrustSwitches, DispatcherPriority.Background);
        }

        // The current view, read after everything else has driven it. This is the read-back the
        // carried Phase 3 item wanted: columns, the formatting rules, and which rule each drawn
        // row actually meets, which a photograph of the dialog cannot say. `=1` reads on the
        // next idle pass; a number is milliseconds to wait first, for a dialog chain whose
        // presses take seconds — the store report's `@ms` lesson, learned once already.
        if (Environment.GetEnvironmentVariable("MAILBOX_VIEW_REPORT") is { Length: > 0 } when)
        {
            var hold = int.TryParse(when, out var ms) && ms > 1 && WindowCapture.IsRequested
                ? WindowCapture.Hold()
                : null;

            Opened += (_, _) => Dispatcher.UIThread.Post(
                async () =>
                {
                    try
                    {
                        if (ms > 1) await Task.Delay(ms);
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        ReportCurrentView();
                    }
                    finally
                    {
                        hold?.Dispose();
                    }
                },
                DispatcherPriority.Background);
        }
    }

    private void ReportCurrentView()
    {
        if (DataContext is not ShellViewModel shell) return;

        var view = shell.CurrentView;
        Log.Info($"Harness: view — “{view.Name}”, columns "
                 + string.Join(", ", view.Columns.Select(c => $"{c.Id}={c.Width}")) + ".");

        foreach (var format in view.Formats)
        {
            Log.Info($"Harness: view format — “{format.Name}”"
                     + (format.Enabled ? string.Empty : " (off)")
                     + (format.BuiltIn ? " (built-in)" : string.Empty)
                     + $", condition “{format.Condition}”,"
                     + $"{(format.Bold ? " bold" : string.Empty)}{(format.Italic ? " italic" : string.Empty)}"
                     + $" ink {format.ColourToken ?? "(theme)"}.");
        }

        foreach (var row in shell.VisibleRows.OfType<MessageRow>())
        {
            var name = shell.AppliedFormatName(row);
            Log.Info($"Harness: view row — “{row.Subject}” formatted by "
                     + $"{(name.Length > 0 ? $"“{name}”" : "nothing")}.");
        }
    }

    /// <summary>
    /// What the switches behind the Trust Center's and the Advanced page's rows hold in this run.
    /// </summary>
    /// <remarks>
    /// Read through the option objects rather than out of the store, so a row wired to the wrong
    /// key reads back wrong here too — which is the failure this line exists to catch. A settings
    /// file cannot be consulted instead: under a capture it is a scratch copy that dies with the
    /// process.
    /// </remarks>
    private static void ReportTrustSwitches()
    {
        Log.Info("Harness: trust switches — "
                 + $"block pictures {On(App.Security.BlockRemotePictures)}, "
                 + $"report hosts {On(App.Security.ReportTrackerHosts)}, "
                 + $"show authentication results {On(App.Security.ShowAuthenticationResults)}, "
                 + $"warn on display-name mismatch {On(App.Security.WarnDisplayNameMismatch)}, "
                 + $"warn on lookalike domains {On(App.MailOptions.WarnAboutSuspiciousDomains)}, "
                 + $"confirm permanent delete {On(App.MailOptions.ConfirmPermanentDelete)}.");

        static string On(bool value) => value ? "on" : "off";
    }

    /// <summary>
    /// Files a message carrying one remote picture into the open account's Inbox.
    /// </summary>
    /// <remarks>
    /// Through <see cref="MailRepository.AddMessage"/> with the real MIME beside the row, which is
    /// what a receiver writes — the pane parses the stored bytes, so a row without them renders
    /// its preview and blocks nothing at all. Invented start to finish, like every other fixture
    /// in this tree.
    /// </remarks>
    private void DeliverRemotePicture(string url)
    {
        if (DataContext is not ShellViewModel shell)
        {
            Log.Warn("Harness: remote picture — there is no shell to deliver into.");
            return;
        }

        // Into the account the run is going to open, not into whichever store sorts first: a
        // seeded run has three, and a message filed in the one the folder pose is about to leave
        // is a message no capture ever sees. MAILBOX_FOLDER names it as "address/Folder".
        var wanted = Environment.GetEnvironmentVariable("MAILBOX_FOLDER") ?? string.Empty;
        var slash = wanted.IndexOf('/', StringComparison.Ordinal);
        var address = slash > 0 ? wanted[..slash] : string.Empty;

        var account = App.Accounts.All.FirstOrDefault(a =>
                          string.Equals(a.Account.Address, address, StringComparison.OrdinalIgnoreCase))
                      ?? App.Accounts.All.FirstOrDefault();

        if (account is null)
        {
            Log.Warn("Harness: remote picture — no account is open.");
            return;
        }

        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox);
        if (inbox is null)
        {
            Log.Warn($"Harness: remote picture — {account.Account.Address} has no Inbox.");
            return;
        }

        var message = new MimeMessage
        {
            Subject = "A picture from elsewhere",
            Date = new DateTimeOffset(2026, 8, 16, 9, 15, 0, TimeSpan.Zero),
        };
        message.From.Add(new MailboxAddress("A. Publisher", "notices@example.net"));
        message.To.Add(new MailboxAddress("A. Person", account.Account.Address));
        message.MessageId = "harness-remote-picture@example.net";
        message.Body = new TextPart("html")
        {
            Text = $"""
                <html><body>
                <p>One picture, held at a distance.</p>
                <p><img src="{url}" width="16" height="16" alt="a picture"></p>
                </body></html>
                """,
        };

        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();

        var summary = new MessageSummary(
            0, inbox.Id, "harness-remote-picture", message.MessageId,
            "A. Publisher", "notices@example.net", message.Subject,
            "One picture, held at a distance.",
            message.Date, message.Date, raw.Length,
            IsRead: false, IsFlagged: false, HasAttachment: false);

        var id = account.Mail.AddMessage(inbox.Id, summary, raw);

        Log.Info(id is null
            ? "Harness: remote picture — the message was already in the Inbox."
            : $"Harness: remote picture — filed “{message.Subject}” into {account.Account.Address}/Inbox "
              + $"pointing at {url}.");

        shell.Refresh();
    }
}
