using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Mailbox.Core.Settings;
using Mailbox.Store;
using static Mailbox.App.Views.SystemDialogKit;

namespace Mailbox.App.Views;

/// <summary>
/// The addresses one account may send as, from the E-mail tab's Identities… button.
/// </summary>
/// <remarks>
/// A deliberate divergence: the reference has no such window, because it grew up beside an
/// Exchange server that hands out proxy addresses of its own. Everywhere else on this desktop —
/// Thunderbird, Evolution, KMail — an account has a list of identities, and the people this
/// application is built to attract have a work alias or a role address and expect to send as it.
/// So the shape is theirs, drawn in the chrome of the dialog it hangs off.
/// <para>
/// The account's own address is the first row and cannot be removed or renamed here: it belongs
/// to the account, and Change… is where it is edited. It is listed rather than hidden so the
/// list is the whole answer to "what can this account send as".
/// </para>
/// </remarks>
public sealed class IdentitiesDialog : Window
{
    private const double DialogWidth = 520;
    private const double DialogHeight = 384;

    private readonly OpenAccount _account;
    private readonly ClassicListView _list = new();
    private readonly Button _new;
    private readonly Button _change;
    private readonly Button _remove;
    private readonly Button _up;
    private readonly Button _down;

    private readonly List<Identity> _identities = [];

    /// <summary>True when the list was written, so the caller can refresh what reads it.</summary>
    public bool Changed { get; private set; }

    public IdentitiesDialog(OpenAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        _account = account;
        Title = "Identities";
        Width = DialogWidth;
        Height = DialogHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AutomationProperties.SetName(_list, "Identities");
        _list.Columns = [new ClassicColumn("Name", 200), new ClassicColumn("E-mail Address", 285)];
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.ItemActivated += async (_, _) => await ChangeSelectedAsync();

        _new = ToolButton("new", "New...", NewAsync);
        _change = ToolButton("change", "Change...", ChangeSelectedAsync);
        _remove = ToolButton("remove", "Remove", RemoveSelected);
        _up = ToolButton("up", string.Empty, () => Move(-1));
        _down = ToolButton("down", string.Empty, () => Move(1));

        SystemDialogChrome.Apply(this, Layout());
        Reload();
    }

    private Control Layout()
    {
        var close = PushButton("Close", Close);
        close.IsCancel = true;
        close.IsDefault = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Bottom;
        close.Margin = new Thickness(0, 0, 7, 9);

        var heading = Label($"Send mail from {_account.Account.Address} as:", bold: true);
        heading.Margin = new Thickness(9, 9, 9, 0);

        // Said here rather than nowhere. A great many servers will refuse to carry a message
        // whose envelope sender is not the address that authenticated — the alias has to be one
        // the provider already knows about — and a reader who has not been told that reads the
        // server's refusal as a bug in this application.
        var note = Paragraph(
            "Each identity is sent through this account's own outgoing server. A provider that "
            + "does not know an address may refuse to carry mail from it.");
        note.Margin = new Thickness(9, 2, 9, 6);

        var top = new StackPanel { Children = { heading, note } };
        DockPanel.SetDock(top, Dock.Top);

        var toolbar = Toolbar(_new, _change, _remove, _up, _down);
        DockPanel.SetDock(toolbar, Dock.Top);

        var bottom = new Panel { Height = 38, Children = { close } };
        DockPanel.SetDock(bottom, Dock.Bottom);

        _list.Margin = new Thickness(8, 0, 8, 4);

        return new DockPanel { Children = { top, toolbar, bottom, _list } };
    }

    private Identity? Selected
        => _list.SelectedIndex >= 0 && _list.SelectedIndex < _identities.Count
            ? _identities[_list.SelectedIndex]
            : null;

    private void Reload(int select = 0)
    {
        _identities.Clear();
        _identities.AddRange(App.Identities.Of(_account.Account.Address, _account.Account.DisplayName));

        _list.SetRows(
        [
            .. _identities.Select(i => new ClassicRow(
                [
                    i.DisplayName.Length > 0 ? i.DisplayName : "(no name)",
                    i.Address,
                ],
                // The account's own carries the marker, as the default account does on the tab
                // this window hangs off — one mark, meaning the same thing in both places.
                Marked: i.IsAccountDefault)),
        ]);

        _list.SelectedIndex = Math.Clamp(select, 0, Math.Max(0, _identities.Count - 1));
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        // The account's own identity is the account. Removing it would mean an account that
        // cannot send, and renaming it here would leave two places saying the name.
        var extra = Selected is { IsAccountDefault: false };

        _change.IsEnabled = extra;
        _remove.IsEnabled = extra;
        _up.IsEnabled = extra && _list.SelectedIndex > 1;
        _down.IsEnabled = extra && _list.SelectedIndex < _identities.Count - 1;
    }

    private async Task NewAsync()
    {
        var editor = new IdentityDialog(new Identity { Address = string.Empty }, Taken(null));
        await editor.ShowDialog(this);
        if (editor.Result is not { } added) return;

        Write([.. Extras(), added], _identities.Count);
    }

    private async Task ChangeSelectedAsync()
    {
        if (Selected is not { IsAccountDefault: false } current) return;

        var editor = new IdentityDialog(current, Taken(current.Address));
        await editor.ShowDialog(this);
        if (editor.Result is not { } edited) return;

        var extras = Extras();
        var at = extras.FindIndex(i => string.Equals(i.Address, current.Address, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;

        extras[at] = edited;
        Write(extras, _list.SelectedIndex);
    }

    private void RemoveSelected()
    {
        if (Selected is not { IsAccountDefault: false } current) return;

        var extras = Extras();
        extras.RemoveAll(i => string.Equals(i.Address, current.Address, StringComparison.OrdinalIgnoreCase));

        // A message already being written under this identity keeps going out as it: the
        // compose window falls back to the account's own address the next time it resolves, and
        // that is a From line the account can actually send.
        Write(extras, _list.SelectedIndex - 1);
    }

    private void Move(int by)
    {
        if (Selected is not { IsAccountDefault: false } current) return;

        var extras = Extras();
        var at = extras.FindIndex(i => string.Equals(i.Address, current.Address, StringComparison.OrdinalIgnoreCase));
        var to = at + by;
        if (at < 0 || to < 0 || to >= extras.Count) return;

        (extras[at], extras[to]) = (extras[to], extras[at]);

        // Plus one: the list shows the account's own first, and the extras are indexed after it.
        Write(extras, to + 1);
    }

    private void Write(List<Identity> extras, int select)
    {
        App.Identities.Save(_account.Account.Address, extras);
        Changed = true;
        Reload(select);
    }

    private List<Identity> Extras() => [.. _identities.Where(i => !i.IsAccountDefault)];

    /// <summary>
    /// The addresses this account already sends as, so the editor can refuse a duplicate —
    /// except the one being edited, which is allowed to keep its own address.
    /// </summary>
    private IReadOnlyList<string> Taken(string? except)
        => [.. _identities
            .Select(i => i.Address)
            .Where(a => except is null || !string.Equals(a, except, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Presses this window's buttons, for the harness.
    /// </summary>
    /// <remarks>
    /// <c>select:&lt;n&gt;</c>, <c>up</c>, <c>down</c> and <c>remove</c> go through the buttons,
    /// so a disabled one refuses exactly as it does under a pointer. <c>new</c> and
    /// <c>change</c> type into a real editor and take what it validates, which is where an
    /// unparseable or duplicate address is refused. The stored list is logged at the end, so
    /// every claim here is read back out of the settings store rather than off the screen.
    /// </remarks>
    internal void Harness(string actions)
    {
        foreach (var raw in actions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (action, argument) = raw.Split(':', 2) is [var a, var b] ? (a, b) : (raw, string.Empty);

            switch (action.ToLowerInvariant())
            {
                case "select":
                    _list.SelectedIndex = int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
                    UpdateButtons();
                    break;

                case "new":
                    if (!_new.IsEnabled) break;
                    var added = new IdentityDialog(new Identity { Address = string.Empty }, Taken(null));
                    added.Harness(argument);
                    if (added.Result is { } fresh) Write([.. Extras(), fresh], _identities.Count);
                    break;

                case "change":
                    if (!_change.IsEnabled || Selected is not { IsAccountDefault: false } current) break;
                    var editor = new IdentityDialog(current, Taken(current.Address));
                    editor.Harness(argument);
                    if (editor.Result is { } edited)
                    {
                        var extras = Extras();
                        var at = extras.FindIndex(
                            i => string.Equals(i.Address, current.Address, StringComparison.OrdinalIgnoreCase));
                        if (at >= 0)
                        {
                            extras[at] = edited;
                            Write(extras, _list.SelectedIndex);
                        }
                    }
                    break;

                case "remove": Press(_remove); break;
                case "up": Press(_up); break;
                case "down": Press(_down); break;
            }
        }

        // What the store says now, for the log to be read back.
        var stored = App.Identities.Of(_account.Account.Address, _account.Account.DisplayName);
        Mailbox.Core.Diagnostics.Log.Info(
            $"Harness: {_account.Account.Address} sends as "
            + string.Join("; ", stored.Select(i =>
                i.Label
                + (i.IsAccountDefault ? " (the account's own)" : string.Empty)
                + (i.ReplyTo.Length > 0 ? $" replies to {i.ReplyTo}" : string.Empty)
                + (i.Organization.Length > 0 ? $" organization '{i.Organization}'" : string.Empty)))
            + ".");

        Mailbox.Core.Diagnostics.Log.Info($"Harness: buttons — change {(_change.IsEnabled ? "on" : "off")}, "
            + $"remove {(_remove.IsEnabled ? "on" : "off")}, up {(_up.IsEnabled ? "on" : "off")}, "
            + $"down {(_down.IsEnabled ? "on" : "off")}.");
    }

    private static void Press(Button button)
    {
        if (!button.IsEnabled) return;
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }
}
