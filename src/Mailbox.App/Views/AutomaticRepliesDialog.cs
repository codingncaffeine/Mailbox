using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Rules;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Automatic Replies (Out of Office): the message an account's server sends on the reader's
/// behalf while they are away.
/// </summary>
/// <remarks>
/// <b>The server sends it, not this application.</b> That is the whole point — an automatic reply
/// that only answers while a mail client happens to be running is not one — so this window writes
/// a setting and then publishes it, as RFC 5230's <c>vacation</c> action, in the same Sieve script
/// the account's server-side rules go in. An account whose server cannot do that is told so here
/// rather than left believing its mail is being answered.
/// <para>
/// <b>Divergence, stated.</b> The reference offers this for Exchange accounts only, and its
/// dialog has two tabs — inside the organization and outside it — because Exchange has a notion
/// of both. There is no such thing over IMAP, so there is one message, and the audience is
/// narrowed the way Sieve narrows it: <c>vacation</c> answers only what was addressed to one of
/// the reader's own addresses, which is what keeps a mailing list from being replied to.
/// </para>
/// <para>
/// <b>This is a system dialog</b>, like Rules and Alerts, which it is a sibling of: both write to
/// the one script, and both are drawn with the desktop's own controls in every theme.
/// </para>
/// </remarks>
public sealed class AutomaticRepliesDialog : Window
{
    private readonly ComboBox _account = new() { MinWidth = 220 };
    private readonly RadioButton _off = new() { Content = "Do not send automatic replies", GroupName = "away" };
    private readonly RadioButton _on = new() { Content = "Send automatic replies", GroupName = "away" };
    private readonly CheckBox _dated = new() { Content = "Only send during this time range:" };
    private readonly DatePicker _from = new() { MinWidth = 200 };
    private readonly DatePicker _until = new() { MinWidth = 200 };
    private readonly TextBox _subject = new() { Classes = { "sysfield" }, MinWidth = 360 };
    private readonly TextBox _body = new()
    {
        Classes = { "sysfield" },
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,

        // From the top, like every other box somebody types a letter into. A multi-line box that
        // centres its text vertically starts the message half way down itself.
        VerticalContentAlignment = VerticalAlignment.Top,
        Height = 150,
    };

    private readonly TextBox _addresses = new() { Classes = { "sysfield" }, MinWidth = 360 };
    private readonly ComboBox _days = new() { ItemsSource = new[] { 1, 3, 7, 14, 30 }, Width = 70 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 470, VerticalAlignment = VerticalAlignment.Center };

    private IReadOnlyList<OpenAccount> _accounts = [];
    private OpenAccount? _current;
    private bool _loading;

    /// <param name="address">The account to open on, or null for the default.</param>
    public AutomaticRepliesDialog(string? address = null)
    {
        Title = "Automatic Replies";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _accounts = [.. App.Accounts.All];
        _current = (address is { Length: > 0 } ? App.Accounts.Find(address) : null) ?? App.Accounts.Default;

        _account.ItemsSource = _accounts.Select(a => a.Account.Address).ToList();
        _account.SelectedIndex = _current is null
            ? -1
            : _accounts.ToList().FindIndex(a => a.Account.Address == _current.Account.Address);
        _account.SelectionChanged += (_, _) =>
        {
            if (_account.SelectedIndex < 0 || _account.SelectedIndex >= _accounts.Count) return;
            _current = _accounts[_account.SelectedIndex];
            Load();
        };

        _on.IsCheckedChanged += (_, _) => ShowState();
        _dated.IsCheckedChanged += (_, _) => ShowState();

        SystemDialogChrome.Apply(this, Layout());
        Load();
        WireDoors();
    }

    private Control Layout()
    {
        var heading = new TextBlock
        {
            Text = "Automatic Replies (Out of Office)",
            FontSize = 20,
            Margin = new Thickness(0, 0, 0, 4),
        };
        Bind(heading, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

        var subheading = new TextBlock
        {
            Text = "Your mail server sends these, so they keep going while this computer is off. "
                   + "Only messages addressed to you are answered, and each person is answered once.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        Bind(subheading, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");

        var save = new Button { Content = "OK", IsDefault = true, Classes = { "sysbutton" } };
        save.Click += async (_, _) => await SaveAsync(close: true);

        var apply = new Button { Content = "Apply", Classes = { "sysbutton" } };
        apply.Click += async (_, _) => await SaveAsync(close: false);

        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "sysbutton" } };
        cancel.Click += (_, _) => Close();

        return new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    heading,
                    subheading,
                    Row(Caption("Account", 90), _account),
                    _off,
                    _on,
                    Indented(_dated),
                    Indented(Row(Caption("Start", 60), _from), 40),
                    Indented(Row(Caption("End", 60), _until), 40),
                    new Separator { Margin = new Thickness(0, 6) },
                    Row(Caption("Subject", 90), _subject),
                    Caption("Reply once to each sender with:"),
                    _body,
                    Row(Caption("Also mine", 90), _addresses),
                    Indented(
                        new TextBlock
                        {
                            Text = "Aliases and role addresses of yours, separated by commas. "
                                   + "A message to one of these is a message to you.",
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 460,
                            FontSize = 11,
                            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("systemdialog.foreground.subtle.brush"),
                        },
                        98),
                    Row(Caption("Answer again after", 130), _days, Caption("days")),
                    new Separator { Margin = new Thickness(0, 6) },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Children =
                        {
                            _status,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                [Grid.ColumnProperty] = 1,
                                Children = { save, cancel, apply },
                            },
                        },
                    },
                },
            },
        };
    }

    // ---- What is on screen, and what is in the store -----------------------------------------

    private void Load()
    {
        _loading = true;
        try
        {
            var away = _current is null
                ? new AwayMessage()
                : AwayMessage.Load(App.Settings, _current.Account.Address);

            _on.IsChecked = away.Enabled;
            _off.IsChecked = !away.Enabled;
            _dated.IsChecked = away.HasDates;

            var today = Mailbox.Core.PosedClock.Today;
            _from.SelectedDate = new DateTimeOffset((away.From ?? today).ToDateTime(TimeOnly.MinValue));
            _until.SelectedDate = new DateTimeOffset((away.Until ?? today.AddDays(7)).ToDateTime(TimeOnly.MinValue));
            _subject.Text = away.Subject;
            _body.Text = away.Body;
            _addresses.Text = string.Join(", ", away.Addresses);
            _days.SelectedItem = ((int[])[1, 3, 7, 14, 30]).Contains(away.Days) ? away.Days : 7;
        }
        finally
        {
            _loading = false;
        }

        ShowState();
    }

    /// <summary>What the boxes say, as a settings record.</summary>
    internal AwayMessage OnScreen() => new()
    {
        Enabled = _on.IsChecked == true,
        From = _dated.IsChecked == true && _from.SelectedDate is { } from ? DateOnly.FromDateTime(from.Date) : null,
        Until = _dated.IsChecked == true && _until.SelectedDate is { } until ? DateOnly.FromDateTime(until.Date) : null,
        Subject = (_subject.Text ?? string.Empty).Trim(),
        Body = _body.Text ?? string.Empty,
        Days = _days.SelectedItem is int days ? days : 7,
        Addresses = (_addresses.Text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };

    /// <summary>
    /// Greys what does not apply and says where the account stands. The one thing worth saying
    /// out loud is a server that cannot do this at all: everything else on this window would
    /// otherwise look like it was working.
    /// </summary>
    private void ShowState()
    {
        var on = _on.IsChecked == true;
        foreach (var control in (Control[])[_dated, _subject, _body, _addresses, _days]) control.IsEnabled = on;
        _from.IsEnabled = on && _dated.IsChecked == true;
        _until.IsEnabled = _from.IsEnabled;

        if (_loading) return;

        if (_current is null)
        {
            _status.Text = "There is no account to reply for yet.";
            return;
        }

        if (!SieveSync.Supports(_current))
        {
            _status.Text = "This account's mail is collected by POP3, which has no server to hold a reply. "
                           + "An IMAP account can have one.";
            return;
        }

        // What the server said last time it was asked. Never asked is not the same as cannot:
        // the first publish is what settles it, and it says so then.
        var extensions = SieveSync.KnownExtensions(_current);
        _status.Text = extensions is null
            ? "Your server has not been asked yet whether it can send replies. OK will ask it."
            : extensions.Contains("vacation")
                ? "Your server sends automatic replies."
                : "Your server does not offer automatic replies, so this cannot be switched on for it.";
    }

    private async Task SaveAsync(bool close)
    {
        if (_current is null)
        {
            Close();
            return;
        }

        var away = OnScreen();
        away.Save(App.Settings, _current.Account.Address);
        Log.Info($"Automatic replies: {_current.Account.Address} — {(away.Enabled ? "on" : "off")}"
                 + (away.HasDates ? $", {away.From?.ToString() ?? "now"} to {away.Until?.ToString() ?? "further notice"}" : string.Empty)
                 + ".");

        if (SieveSync.Supports(_current))
        {
            _status.Text = "Telling the server…";
            var outcome = await SieveSync.PublishAsync(_current);
            _status.Text = outcome.Message;
            if (!outcome.Ok) Log.Warn($"Automatic replies: {outcome.Message}");

            // A failure keeps the window open whatever was pressed. The setting is written
            // either way, and the send/receive retry will carry it up; but somebody who pressed
            // OK and saw the window vanish would believe the server had been told.
            if (!outcome.Ok) return;
        }

        if (close) Close();
    }

    // ---- The harness door ---------------------------------------------------------------------

    /// <summary>
    /// <c>MAILBOX_AWAY=[account:…;]on|off[;subject:…][;body:…][;from:yyyy-mm-dd][;until:yyyy-mm-dd]
    /// [;days:N][;also:a@b,c@d][;save]</c> — fills the window in and, with <c>save</c>, presses OK.
    /// </summary>
    /// <remarks>
    /// The window is the only place these settings are written, so a run that could not press it
    /// could not check that what is typed is what is stored — and the stored record is what the
    /// script is compiled from.
    /// </remarks>
    private void WireDoors()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_AWAY") is not { Length: > 0 } spec) return;

        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            async () =>
            {
                using var hold = Theming.WindowCapture.Hold();
                var save = false;

                foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var (verb, argument) = part.Split(':', 2) is [var head, var tail] ? (head, tail) : (part, string.Empty);
                    switch (verb.ToLowerInvariant())
                    {
                        // The window opens on whichever account the shell is showing, and a pose
                        // that wants another one has no other way to reach the picker.
                        case "account":
                            var at = _accounts.ToList().FindIndex(a => a.Account.Address.Contains(argument, StringComparison.OrdinalIgnoreCase));
                            if (at >= 0) _account.SelectedIndex = at;
                            break;
                        case "on": _on.IsChecked = true; break;
                        case "off": _off.IsChecked = true; break;
                        case "subject": _subject.Text = argument; break;
                        case "body": _body.Text = argument.Replace("\\n", "\n", StringComparison.Ordinal); break;
                        case "days": if (int.TryParse(argument, out var days)) _days.SelectedItem = days; break;
                        case "also": _addresses.Text = argument; break;
                        case "save": save = true; break;
                        case "from":
                        case "until":
                            if (!DateOnly.TryParse(argument, out var day)) break;
                            _dated.IsChecked = true;
                            var picker = verb.Equals("from", StringComparison.OrdinalIgnoreCase) ? _from : _until;
                            picker.SelectedDate = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
                            break;
                    }
                }

                ShowState();
                if (save) await SaveAsync(close: false);

                var stored = _current is null ? new AwayMessage() : AwayMessage.Load(App.Settings, _current.Account.Address);
                Log.Info(
                    $"Harness: automatic replies — screen {Say(OnScreen())}; stored {Say(stored)}; "
                    + $"status “{_status.Text}”.");
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private static string Say(AwayMessage away)
        => $"{(away.Enabled ? "on" : "off")}, {away.From?.ToString("yyyy-MM-dd") ?? "no start"} to "
           + $"{away.Until?.ToString("yyyy-MM-dd") ?? "no end"}, subject “{away.Subject}”, "
           + $"{away.Body.Length} character(s), every {away.Days} day(s), "
           + $"also [{string.Join(" ", away.Addresses)}]";

    // ---- Small pieces of layout ----------------------------------------------------------------

    private static Control Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private static Control Indented(Control child, double left = 22)
    {
        child.Margin = new Thickness(left, 0, 0, 0);
        return child;
    }

    private static TextBlock Caption(string text, double width = double.NaN)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (!double.IsNaN(width)) block.Width = width;
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        return block;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
