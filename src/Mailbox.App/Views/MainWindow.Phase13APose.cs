using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.Theming;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;

namespace Mailbox.App.Views;

/// <summary>
/// A door onto the fields of a system dialog, and the read-backs that say where a credential
/// went.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> The Account Settings family is four dialogs made almost entirely of
/// text boxes, tick boxes and dropdowns — the account wizard, Server Settings, Update Password
/// and the pickers those open. Every door onto them before this one either opened the window and
/// photographed it, or pressed a button that took no input: <c>MAILBOX_ACCOUNTS_ACTION</c>
/// selects a row and presses a toolbar button, <c>MAILBOX_DIALOG_PRESS</c> presses a button by
/// its caption, and <c>MAILBOX_ACCOUNT_ACTION</c> types into three of the wizard's nine boxes by
/// name. <b>Nothing could type into Server Settings at all</b>, so the claim that its fields read,
/// write and take effect had never been made by anything but the code.
/// <para>
/// So this takes the shape the audit's own "doors still missing" list asks for — a pose that
/// names a control, a gesture and a value — restricted to the controls a form is built from. The
/// label is how a reader names a field, so it is how the pose names one: every text box, tick box,
/// dropdown and spinner in these dialogs sits in a panel behind its own caption, and the caption
/// is what identifies it. <c>#3</c> names the third of a kind where two captions read alike.
/// </para>
/// <para>
/// The read-back steps matter as much as the presses. A form's claim is never what the form
/// shows afterwards — it is what landed in the settings file and what landed in the keyring, and
/// those are two different places with two different lifetimes. <c>settings:</c> reads the first
/// back; <c>secret:</c> reads the second back <em>through the store the application is actually
/// using</em>, and says only whether what is held is the value expected, so a password is never
/// written to a log.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this file's doors. Called once, from the constructor.</summary>
    private void WirePhase13ADoors()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_SYSFORM") is not { Length: > 0 } script) return;

        // The hold is taken here, on the dispatcher, in the same pass the window is built, so the
        // capture's own timer cannot photograph the dialog before the script has driven it.
        var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;
        Opened += (_, _) => _ = RunSystemFormAsync(script, hold);
    }

    /// <summary>
    /// Drives the newest open dialog's own controls, in order.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_SYSFORM=set:Email address=a.person@example.com;set:Password=…;press:Add Account</c>.
    /// The steps:
    /// <list type="bullet">
    /// <item><c>set:&lt;label&gt;=&lt;text&gt;</c> — a text box, by the caption in front of it.</item>
    /// <item><c>tick:&lt;label&gt;=on|off</c> — a tick box, by what it reads.</item>
    /// <item><c>choose:&lt;label&gt;=&lt;text or #n&gt;</c> — a dropdown.</item>
    /// <item><c>spin:&lt;label&gt;=&lt;number&gt;</c> — a spinner.</item>
    /// <item><c>open:&lt;header&gt;</c> — unfolds an expander.</item>
    /// <item><c>press:&lt;caption&gt;</c> — a button, through a real pointer press.</item>
    /// <item><c>dump</c> — every control the dialog holds and what it reads.</item>
    /// <item><c>shot</c> — photograph this dialog rather than the shell.</item>
    /// <item><c>wait</c>, <c>wait:&lt;ms&gt;</c> — a beat for a handler that awaits.</item>
    /// <item><c>settings:&lt;address&gt;</c> — the account settings as a later run would load them.</item>
    /// <item><c>secret:&lt;address&gt;[=&lt;expected&gt;]</c> — what the credential store holds.</item>
    /// <item><c>accounts</c> — the account list, in order, with the default marked.</item>
    /// </list>
    /// The window is looked up again at every step, because a press opens children: Add Account
    /// puts a certificate question over the wizard, and the step after it means that window.
    /// </remarks>
    private async Task RunSystemFormAsync(string script, IDisposable? hold)
    {
        try
        {
            var settled = false;

            foreach (var raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var (verb, argument) = raw.Split(':', 2) is [var head, var tail]
                    ? (head.Trim().ToLowerInvariant(), tail)
                    : (raw.Trim().ToLowerInvariant(), string.Empty);

                // The steps that want no dialog first: a form that has done its work has closed,
                // and a read-back after it is the whole point of the run.
                switch (verb)
                {
                    case "wait":
                        await Task.Delay(int.TryParse(argument, out var ms) ? ms : 400);
                        continue;

                    case "settings":
                        ReportAccountSettings(argument.Trim());
                        continue;

                    case "secret":
                        await ReportSecretAsync(argument);
                        continue;

                    case "accounts":
                        Log.Info("Harness: sysform — accounts are "
                                 + string.Join(", ", App.Accounts.All.Select(
                                     a => a.Account.Address + $" ({a.Account.Protocol}"
                                          + (a.IsDefault ? ", default)" : ")")))
                                 + ".");
                        continue;

                    // What the folder pane holds *now*. MAILBOX_FOLDERS=dump reads it once at
                    // startup, which is before an account has been added — so the claim that a
                    // new account reaches the pane could not be made at all.
                    case "folders":
                        if (DataContext is ViewModels.ShellViewModel shell) PoseFolderDump(shell);
                        continue;
                }

                if (await DialogAsync() is not { } dialog)
                {
                    Log.Warn($"Harness: sysform — no dialog is open for “{raw}”.");
                    return;
                }

                // A window that has just appeared is not answering the pointer yet: a press in the
                // pass it opened in raises a handler over a control that has never been laid out,
                // which reads exactly like a button that is not wired.
                if (!settled)
                {
                    settled = true;
                    await Task.Delay(400);
                }

                dialog.UpdateLayout();

                switch (verb)
                {
                    case "dump":
                        DumpForm(dialog);
                        break;

                    case "shot":
                        Log.Info($"Harness: sysform — photographing {dialog.GetType().Name}.");
                        CaptureNextWindow();
                        break;

                    case "set":
                        SetField(dialog, argument);
                        break;

                    case "tick":
                        TickField(dialog, argument);
                        break;

                    case "choose":
                        ChooseField(dialog, argument);
                        break;

                    case "spin":
                        SpinField(dialog, argument);
                        break;

                    case "open":
                        OpenExpander(dialog, argument.Trim());
                        break;

                    case "row":
                        PickListRow(dialog, argument.Trim(), twice: false);
                        break;

                    case "pick":
                        PickBoxRow(dialog, argument.Trim());
                        break;

                    case "activate":
                        PickListRow(dialog, argument.Trim(), twice: true);
                        break;

                    case "press":
                        PressInForm(dialog, argument.Trim());
                        break;

                    default:
                        Log.Warn($"Harness: sysform — there is no step named “{verb}”.");
                        break;
                }

                // A beat after every step: setting Text raises TextChanged on the next pass, so a
                // press in the same call presses a button whose state still belongs to the box
                // before it was typed into.
                await Task.Delay(250);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the sysform pose failed.", ex);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    // ---- Naming a control ---------------------------------------------------------------------

    /// <summary>
    /// What a control is called: the text of every label in front of it in its own panel.
    /// </summary>
    /// <remarks>
    /// "Incoming server" for the host box and "Incoming server Port" for the one beside it, which
    /// is how a reader tells the two Ports apart and so is how the pose does. A control whose
    /// panel holds no label before it takes its own name — a tick box carries its caption inside
    /// itself.
    /// </remarks>
    private static string LabelOf(Control control)
    {
        if (control is CheckBox { Content: string own }) return own;

        if (control.GetVisualParent() is not Panel panel) return string.Empty;

        var words = new List<string>();
        foreach (var child in panel.Children)
        {
            if (ReferenceEquals(child, control)) break;
            if (child is TextBlock { Text: { Length: > 0 } text }) words.Add(text.Trim());
        }

        return string.Join(" ", words).Trim();
    }

    /// <summary>
    /// The control of a kind that the given name picks out: <c>#2</c> by position, an exact
    /// caption, then the only one whose caption contains it.
    /// </summary>
    private static T? Field<T>(Window dialog, string name) where T : Control
    {
        var all = dialog.GetVisualDescendants().OfType<T>().ToList();
        if (all.Count == 0) return null;

        if (name.StartsWith('#') && int.TryParse(name[1..], out var index))
        {
            return index >= 0 && index < all.Count ? all[index] : null;
        }

        var exact = all.Where(c => LabelOf(c).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1)
        {
            Log.Warn($"Harness: sysform — {exact.Count} controls read “{name}”; taking the first.");
            return exact[0];
        }

        var near = all.Where(c => LabelOf(c).Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (near.Count == 1) return near[0];

        if (near.Count > 1)
        {
            Log.Warn($"Harness: sysform — “{name}” is ambiguous: "
                     + string.Join(" | ", near.Select(LabelOf)) + ". Name it exactly, or by #index.");
            return null;
        }

        Log.Warn($"Harness: sysform — {dialog.GetType().Name} has no {typeof(T).Name} called “{name}”. "
                 + $"It has: {string.Join(" | ", all.Select(c => LabelOf(c) is { Length: > 0 } l ? l : "(unlabelled)"))}.");
        return null;
    }

    private static (string Name, string Value) Pair(string argument)
        => argument.Split('=', 2) is [var name, var value]
            ? (name.Trim(), value)
            : (argument.Trim(), string.Empty);

    // ---- The steps ----------------------------------------------------------------------------

    private static void SetField(Window dialog, string argument)
    {
        var (name, value) = Pair(argument);
        if (Field<TextBox>(dialog, name) is not { } box) return;

        box.Text = value;

        // Read back off the control rather than trusting the assignment: a box bound to a
        // property that coerces would take something else, and this is the only place that would
        // show.
        Log.Info($"Harness: sysform — “{LabelOf(box)}” now holds "
                 + $"{(box.PasswordChar == '\0' ? $"“{box.Text}”" : $"{(box.Text ?? string.Empty).Length} hidden character(s)")}.");
    }

    private static void TickField(Window dialog, string argument)
    {
        var (name, value) = Pair(argument);
        if (Field<CheckBox>(dialog, name) is not { } box) return;

        var wanted = value.Trim() is "on" or "true" or "1" or "yes";
        box.IsChecked = wanted;
        Log.Info($"Harness: sysform — “{LabelOf(box)}” is {(box.IsChecked == true ? "ticked" : "clear")}"
                 + $"{(box.IsEffectivelyEnabled ? string.Empty : ", and greyed")}.");
    }

    private static void ChooseField(Window dialog, string argument)
    {
        var (name, value) = Pair(argument);
        if (Field<ComboBox>(dialog, name) is not { } combo) return;

        var wanted = value.Trim();
        if (wanted.StartsWith('#') && int.TryParse(wanted[1..], out var index))
        {
            combo.SelectedIndex = index;
        }
        else
        {
            var items = combo.ItemsSource?.Cast<object?>().ToList() ?? [];
            var found = items.FindIndex(
                i => (i?.ToString() ?? string.Empty).Contains(wanted, StringComparison.OrdinalIgnoreCase));

            if (found < 0)
            {
                Log.Warn($"Harness: sysform — “{LabelOf(combo)}” offers no “{wanted}”. It offers: "
                         + string.Join(" | ", items.Select(i => i?.ToString())) + ".");
                return;
            }

            combo.SelectedIndex = found;
        }

        Log.Info($"Harness: sysform — “{LabelOf(combo)}” is now “{combo.SelectedItem}” (index {combo.SelectedIndex}).");
    }

    private static void SpinField(Window dialog, string argument)
    {
        var (name, value) = Pair(argument);
        if (Field<NumericUpDown>(dialog, name) is not { } spinner) return;

        if (!decimal.TryParse(value.Trim(), out var wanted))
        {
            Log.Warn($"Harness: sysform — “{value}” is not a number.");
            return;
        }

        spinner.Value = wanted;
        Log.Info($"Harness: sysform — “{LabelOf(spinner)}” is now {spinner.Value} "
                 + $"(bounded {spinner.Minimum}…{spinner.Maximum})"
                 + $"{(spinner.IsEffectivelyEnabled ? string.Empty : ", and greyed")}.");
    }

    private static void OpenExpander(Window dialog, string header)
    {
        var expander = dialog.GetVisualDescendants().OfType<Expander>().FirstOrDefault(
            e => (e.Header as string ?? string.Empty).Contains(header, StringComparison.OrdinalIgnoreCase));

        if (expander is null)
        {
            Log.Warn($"Harness: sysform — {dialog.GetType().Name} has no expander reading “{header}”.");
            return;
        }

        expander.IsExpanded = true;
        Log.Info($"Harness: sysform — unfolded “{expander.Header}”.");
    }

    /// <summary>
    /// Picks a row of the page's list, by what it reads or by <c>#index</c>, through a real
    /// pointer press on the row.
    /// </summary>
    /// <remarks>
    /// <c>ClassicListView</c> draws its rows rather than realising a container per row, so nothing
    /// that walks the visual tree can find one and the generic dialog-press door's <c>pick:</c>
    /// cannot reach it. The row is therefore pressed where it was drawn — index times the row
    /// height — which is also what makes <c>activate:</c> possible: a second press with a click
    /// count of two is the double click that opens the selected account, and no pose had ever
    /// made one on this list.
    /// <para>
    /// The first list that is actually on screen, because the dialog builds all six of its tabs
    /// and only one of them is showing.
    /// </para>
    /// </remarks>
    private static void PickListRow(Window dialog, string wanted, bool twice)
    {
        var list = dialog.GetVisualDescendants().OfType<ClassicListView>()
            .FirstOrDefault(l => l.IsEffectivelyVisible);

        if (list is null)
        {
            Log.Warn($"Harness: sysform — {dialog.GetType().Name} draws no list.");
            return;
        }

        var rows = list.Rows;
        var index = wanted.StartsWith('#') && int.TryParse(wanted[1..], out var n)
            ? n
            : rows.ToList().FindIndex(
                r => r.Cells.Any(c => c.Contains(wanted, StringComparison.OrdinalIgnoreCase)));

        if (index < 0 || index >= rows.Count)
        {
            Log.Warn($"Harness: sysform — the list has no row matching “{wanted}”. It holds "
                     + string.Join(" | ", rows.Select(r => string.Join(" / ", r.Cells))) + ".");
            return;
        }

        // The drawn body inside the list's scroller, which is what handles the press.
        var body = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Content as Control
                   ?? list.GetVisualDescendants().OfType<Control>().LastOrDefault();

        if (body is null)
        {
            Log.Warn("Harness: sysform — the list has no body to press.");
            return;
        }

        // 17px rows, from the list's own metric; the middle of the row, and clear of the tick box
        // at its left edge so a press picks the row rather than toggling a mark.
        var at = new Point(60, (index * 17) + 8.5);
        Press(body, at);
        if (twice) PressAgain(body, at);

        Log.Info($"Harness: sysform — {(twice ? "double-clicked" : "picked")} row {list.SelectedIndex} of "
                 + $"{rows.Count}: “{string.Join(" / ", list.SelectedRow?.Cells ?? [])}”.");
    }

    /// <summary>
    /// Chooses a row in an ordinary list box — the folder picker's tree, which is a
    /// <see cref="ListBox"/> and so is reached by its rows rather than by a label.
    /// </summary>
    private static void PickBoxRow(Window dialog, string wanted)
    {
        var list = dialog.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
        if (list is null)
        {
            Log.Warn($"Harness: sysform — {dialog.GetType().Name} draws no list box.");
            return;
        }

        if (wanted.StartsWith('#') && int.TryParse(wanted[1..], out var index))
        {
            list.SelectedIndex = index;
        }
        else
        {
            // On what the row draws rather than on the item behind it: a list bound to records
            // has no text of its own, and what a reader picks is what they can read.
            var found = list.GetRealizedContainers().FirstOrDefault(
                c => c.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true));

            if (found is null)
            {
                Log.Warn($"Harness: sysform — no row of the list reads “{wanted}”. It shows: "
                         + string.Join(" | ", list.GetRealizedContainers()
                             .Select(c => c.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text))
                         + ".");
                return;
            }

            list.SelectedItem = list.ItemFromContainer(found);
        }

        Log.Info($"Harness: sysform — picked row {list.SelectedIndex} of {list.ItemCount} in "
                 + $"{dialog.GetType().Name}.");
    }

    /// <summary>The second half of a double click: the same point, with a click count of two.</summary>
    private static void PressAgain(Control view, Point point)
    {
        var root = TopLevel.GetTopLevel(view) as Visual ?? view;
        var at = view.TranslatePoint(point, root) ?? point;

        var pointer = new Avalonia.Input.Pointer(4, Avalonia.Input.PointerType.Mouse, isPrimary: true);
        var down = new Avalonia.Input.PointerPointProperties(
            Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
        var up = new Avalonia.Input.PointerPointProperties(
            Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonReleased);

        view.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
            view, pointer, root, at, 0, down, Avalonia.Input.KeyModifiers.None, 2));
        view.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(
            view, pointer, root, at, 1, up, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left));
    }

    /// <summary>Presses a button by its caption, through a real pointer press.</summary>
    private void PressInForm(Window dialog, string caption)
    {
        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        var button = buttons.FirstOrDefault(
            b => ButtonCaption(b).Replace("_", string.Empty)
                .Equals(caption, StringComparison.OrdinalIgnoreCase))
            ?? buttons.FirstOrDefault(
                b => ButtonCaption(b).Replace("_", string.Empty)
                    .Contains(caption, StringComparison.OrdinalIgnoreCase));

        if (button is null)
        {
            Log.Warn($"Harness: sysform — {dialog.GetType().Name} has no button reading “{caption}”. "
                     + $"It has: {string.Join(" | ", buttons.Select(ButtonCaption).Where(t => t.Length > 0))}.");
            return;
        }

        Log.Info($"Harness: sysform — pressing “{ButtonCaption(button)}” in {dialog.GetType().Name}"
                 + $"{(button.IsEffectivelyEnabled ? string.Empty : " (which is greyed, so nothing should happen)")}.");

        Press(button, new Point(button.Bounds.Width / 2, button.Bounds.Height / 2));
    }

    private static string ButtonCaption(Button button) => button.Content switch
    {
        string text => text,
        TextBlock block => block.Text ?? string.Empty,
        ContentControl { Content: string inner } => inner,
        _ => (button.Content as Control)?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text
             ?? string.Empty,
    };

    /// <summary>Everything the dialog is made of, and what each part reads.</summary>
    private static void DumpForm(Window dialog)
    {
        Log.Info($"Harness: sysform — {dialog.GetType().Name} “{dialog.Title}”, "
                 + $"{dialog.ClientSize.Width:0}×{dialog.ClientSize.Height:0}.");

        foreach (var box in dialog.GetVisualDescendants().OfType<TextBox>())
        {
            Log.Info($"Harness: sysform — box “{LabelOf(box)}” = "
                     + (box.PasswordChar == '\0'
                         ? $"“{box.Text}”"
                         : $"{(box.Text ?? string.Empty).Length} hidden character(s)")
                     + State(box)
                     + $"{(box.IsReadOnly ? " (read-only)" : string.Empty)}.");
        }

        foreach (var box in dialog.GetVisualDescendants().OfType<CheckBox>())
        {
            Log.Info($"Harness: sysform — tick “{LabelOf(box)}” = {(box.IsChecked == true ? "on" : "off")}"
                     + State(box) + ".");
        }

        foreach (var combo in dialog.GetVisualDescendants().OfType<ComboBox>())
        {
            Log.Info($"Harness: sysform — list “{LabelOf(combo)}” = “{combo.SelectedItem}” of "
                     + string.Join(" | ", combo.ItemsSource?.Cast<object?>().Select(i => i?.ToString()) ?? [])
                     + State(combo) + ".");
        }

        foreach (var spinner in dialog.GetVisualDescendants().OfType<NumericUpDown>())
        {
            Log.Info($"Harness: sysform — spinner “{LabelOf(spinner)}” = {spinner.Value} "
                     + $"({spinner.Minimum}…{spinner.Maximum})" + State(spinner) + ".");
        }

        foreach (var block in dialog.GetVisualDescendants().OfType<TextBlock>()
                     .Where(t => t.IsEffectivelyVisible && (t.Text ?? string.Empty).Trim().Length > 20))
        {
            Log.Info($"Harness: sysform — says “{block.Text}”.");
        }

        var buttons = dialog.GetVisualDescendants().OfType<Button>()
            .Select(b => (Caption: ButtonCaption(b), State: State(b)))
            .Where(b => b.Caption.Length > 0);

        Log.Info("Harness: sysform — buttons: "
                 + string.Join(", ", buttons.Select(
                     b => $"“{b.Caption}”{(b.State.Length == 0 ? " on" : b.State)}"))
                 + ".");
    }

    /// <summary>
    /// Whether a control can be used, and whether it is on screen at all.
    /// </summary>
    /// <remarks>
    /// A hidden control is still in the visual tree, so a dump that only asked whether one is
    /// enabled reported the wizard's password box as present on an account that signs in — which
    /// is precisely the thing the wizard hides.
    /// </remarks>
    private static string State(Control control)
        => !control.IsEffectivelyVisible ? " (hidden)"
            : !control.IsEffectivelyEnabled ? " (greyed)"
            : string.Empty;

    // ---- The read-backs -----------------------------------------------------------------------

    /// <summary>The account's settings as a later run would load them, or a plain "nothing".</summary>
    private static void ReportAccountSettings(string address)
    {
        if (AccountSettings.Load(App.Settings, address) is not { } settings)
        {
            Log.Info($"Harness: sysform — nothing is recorded for {address}.");
            return;
        }

        Log.Info($"Harness: sysform — {address}: incoming {settings.IncomingHost}:{settings.IncomingPort} "
                 + $"{settings.IncomingSecurity} as “{settings.IncomingUser}”; "
                 + $"outgoing {settings.OutgoingHost}:{settings.OutgoingPort} {settings.OutgoingSecurity} "
                 + $"as “{settings.OutgoingUser}”; auth {settings.Auth}; "
                 + $"provider {(settings.OAuthProviderId.Length > 0 ? settings.OAuthProviderId : "none")}; "
                 + $"client {(settings.OAuthClientId.Length > 0 ? "pasted" : "none")}; "
                 + $"leave on server {settings.LeaveOnServer}; delete after "
                 + $"{(settings.DeleteAfterDays is { } days ? $"{days} day(s)" : "never")}; "
                 + $"offline {settings.OfflineMonths} month(s); "
                 + $"sieve {(settings.SieveHost.Length > 0 ? settings.SieveHost : "(the incoming server)")}:{settings.SievePort}; "
                 + $"delivers to {(settings.DeliveryFolderId is { } folder ? $"folder {folder}" : "the Inbox")}.");
    }

    /// <summary>
    /// What the credential store holds for an address, without writing a secret into a log.
    /// </summary>
    /// <remarks>
    /// <c>secret:a.person@example.com</c> says which purposes are held; adding <c>=&lt;expected&gt;</c>
    /// says whether what is held is that value. Never the value itself — a log is a file, and a
    /// password in one is exactly the arrangement the keyring exists to avoid.
    /// </remarks>
    private static async Task ReportSecretAsync(string argument)
    {
        var (address, expected) = Pair(argument);
        if (address.Length == 0) return;

        foreach (var purpose in new[] { Credentials.Incoming, Credentials.Outgoing, Credentials.OAuthRefresh })
        {
            var held = await App.Secrets.LoadAsync(address, purpose);
            var verdict = held is null
                ? "nothing"
                : expected.Length == 0
                    ? $"{held.Length} character(s)"
                    : string.Equals(held, expected, StringComparison.Ordinal)
                        ? "what was typed"
                        : "something else";

            Log.Info($"Harness: sysform — {App.Secrets.Description} holds {verdict} for "
                     + $"{address} ({purpose}).");
        }
    }
}
