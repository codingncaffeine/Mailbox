using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The newsletters already in the mailbox, offered as feeds.
/// </summary>
/// <remarks>
/// <b>The one thing a hosted reader cannot do better.</b> Feedly gives you an
/// <c>@feedly.com</c> address and asks you to re-subscribe every newsletter to it, because a
/// website has no mailbox. This is a mail client: the mailbox is here, the newsletters are
/// already arriving in it, and nothing has to be re-subscribed, forwarded, or routed through
/// somebody else who then holds your mail.
/// <para>
/// And the reader does not have to remember what they subscribed to, which they do not: the
/// inbox is read and what it holds is offered, with how many issues of each are there and when
/// the last one came. Same idea as feed discovery — the application does the knowing.
/// </para>
/// <para>
/// Nothing is moved without being asked for. What looks like bulk mail includes receipts,
/// password resets and calendar invitations, so detection is a suggestion and the ticks are the
/// decision.
/// </para>
/// </remarks>
public sealed class NewslettersDialog : Window
{
    private readonly FeedSubscriptions _feeds;
    private readonly Func<OpenAccount?> _account;

    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBlock _message = new();
    private readonly ComboBox _category = new();
    private readonly CheckBox _gather;
    private readonly Button _subscribe;

    private readonly List<(FoundNewsletter Found, CheckBox Tick)> _offered = [];

    /// <summary>How many newsletters were taken up.</summary>
    public int Added { get; private set; }

    /// <summary>How many back numbers were moved into their folders.</summary>
    public int Gathered { get; private set; }

    public NewslettersDialog(FeedSubscriptions feeds, Func<OpenAccount?> account)
    {
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));
        _account = account ?? throw new ArgumentNullException(nameof(account));

        Title = "Read Newsletters Here";
        Width = 660;
        Height = 640;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _gather = new CheckBox { Content = "Move the issues already in the inbox as well", IsChecked = true };
        Bind(_gather, CheckBox.ForegroundProperty, "dialog.foreground.brush");

        _subscribe = Push("Read Here", Commit);
        _subscribe.IsDefault = true;
        _subscribe.IsEnabled = false;

        var cancel = Push("Cancel", Close);
        cancel.IsCancel = true;

        DialogChrome.Apply(this, Layout(cancel));
        Opened += (_, _) => Dispatcher.UIThread.Post(Scan, DispatcherPriority.Background);

        // The harness's way in. Registered after the scan's own post and at the same priority, so
        // it runs on the pass after it — a pose that ticked before the inbox had been read would
        // be ticking an empty list and reporting that nothing was found.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                Report();
                if (Environment.GetEnvironmentVariable("MAILBOX_NEWSLETTERS") is { Length: > 0 } posed) Harness(posed);
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// What the scan offered, before anything is ticked.
    /// </summary>
    /// <remarks>
    /// The claim this file makes hardest is that detection is a <em>suggestion</em>: nothing is
    /// moved because it looks like bulk mail. Only a listing taken before the ticks can say that —
    /// what was found, what the ordinary correspondence beside it was, and the identity each would
    /// be filed under, which is the List-ID where the publisher sent one and the sending address
    /// where they did not.
    /// </remarks>
    private void Report()
    {
        Log.Info($"Harness: newsletters — {_offered.Count} offered, {_offered.Count(o => o.Tick.IsChecked == true)} ticked, "
            + $"Read Here is {(_subscribe.IsEnabled ? "enabled" : "greyed")}.");

        foreach (var (found, tick) in _offered)
        {
            Log.Info($"Harness: newsletters   · “{found.Name}” — {found.From}, {found.Issues} issue(s), "
                + $"last {found.Latest:yyyy-MM-dd}, identity “{found.Identity}”, would be filed as {found.Address}"
                + $"{(tick.IsEnabled ? string.Empty : ", already read here")}"
                + $"{(tick.IsChecked == true ? ", ticked" : string.Empty)}");
        }
    }

    /// <summary>
    /// Ticks by name and presses Read Here:
    /// <c>MAILBOX_NEWSLETTERS=Ledger,Briefing|heading:News|nogather</c>.
    /// </summary>
    /// <remarks>
    /// The ticks are the decision, so a pose has to make them the way a reader does — on the tick
    /// boxes themselves, and then on the button, which is greyed until at least one is on. Nothing
    /// here writes a subscription: <see cref="Commit"/> does, and that is the point.
    /// </remarks>
    private void Harness(string spec)
    {
        var parts = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var wanted = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var option in parts.Skip(1))
        {
            if (option.StartsWith("heading:", StringComparison.OrdinalIgnoreCase))
            {
                var heading = option["heading:".Length..].Trim();
                var choices = (_category.ItemsSource as IReadOnlyList<string>) ?? [];
                var at = choices.ToList().FindIndex(c => c.Contains(heading, StringComparison.OrdinalIgnoreCase));
                _category.SelectedIndex = at < 0 ? 0 : at;
                Log.Info($"Harness: newsletters — filing under “{_category.SelectedItem}”.");
            }
            else if (option.Equals("nogather", StringComparison.OrdinalIgnoreCase))
            {
                _gather.IsChecked = false;
            }
        }

        foreach (var name in wanted)
        {
            var row = _offered.FirstOrDefault(o =>
                o.Found.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || o.Found.Identity.Contains(name, StringComparison.OrdinalIgnoreCase));

            if (row.Tick is null)
            {
                Log.Info($"Harness: newsletters — nothing offered reads “{name}”.");
                continue;
            }

            if (!row.Tick.IsEnabled)
            {
                Log.Info($"Harness: newsletters — “{row.Found.Name}” cannot be ticked; it is already read here.");
                continue;
            }

            row.Tick.IsChecked = true;
        }

        Log.Info($"Harness: newsletters — {_offered.Count(o => o.Tick.IsChecked == true)} ticked, "
            + $"Read Here is {(_subscribe.IsEnabled ? "enabled" : "greyed")} and reads “{_subscribe.Content}”.");

        if (!_subscribe.IsEnabled) return;

        _subscribe.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Log.Info($"Harness: newsletters — {Added} taken up, {Gathered} back number(s) moved.");

        foreach (var feed in _feeds.All.Where(f => f.IsNewsletter()))
        {
            Log.Info($"Harness: newsletters   → “{feed.Name}” at {feed.Url} under “{feed.Category}”.");
        }
    }

    private Control Layout(Button cancel)
    {
        var heading = Label("Newsletters in your mailbox", bold: true, size: 15);

        var explain = Label(
            "Newsletters you already receive can be read here as articles instead of sitting in "
            + "the inbox. Nothing is forwarded anywhere and no new address is needed — the mail "
            + "is already arriving, and this only decides where it is filed.");
        explain.TextWrapping = TextWrapping.Wrap;
        explain.Margin = new Thickness(0, 4, 0, 12);
        Bind(explain, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _message.TextWrapping = TextWrapping.Wrap;
        Bind(_message, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _category.ItemsSource = new[] { "(no heading)" }.Concat(_feeds.Categories).ToList();
        _category.SelectedIndex = 0;
        _category.MinWidth = 200;

        var options = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { Label("File them under:"), _category },
                },
                _gather,
            },
        };

        var top = new StackPanel { Children = { heading, explain, _message, options } };
        DockPanel.SetDock(top, Dock.Top);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _subscribe, cancel },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var scroll = new ScrollViewer
        {
            Content = _list,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        return new DockPanel { Margin = new Thickness(18), Children = { top, buttons, scroll } };
    }

    /// <summary>Reads the inbox and offers what it found.</summary>
    private void Scan()
    {
        if (_account() is not { } account)
        {
            _message.Text = "There is no mail account to read.";
            return;
        }

        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox);
        if (inbox is null)
        {
            _message.Text = "There is no inbox to read.";
            return;
        }

        _message.Text = "Reading the inbox…";
        var found = NewsletterScan.In(account.Mail, inbox.Id);

        _list.Children.Clear();
        _offered.Clear();

        if (found.Count == 0)
        {
            _message.Text = $"Nothing in the last {NewsletterScan.MostMessages} messages looks like a newsletter.";
            return;
        }

        _message.Text = found.Count == 1
            ? "One newsletter found — tick it to read it here."
            : $"{found.Count} newsletters found — tick the ones you want to read here.";

        foreach (var one in found) _list.Children.Add(Row(one));
        Recount();
    }

    private Control Row(FoundNewsletter found)
    {
        var already = _feeds.Contains(found.Address);

        var tick = new CheckBox
        {
            Content = found.Name,
            FontWeight = FontWeight.SemiBold,
            IsEnabled = !already,
        };
        Bind(tick, CheckBox.ForegroundProperty, "dialog.foreground.brush");
        tick.IsCheckedChanged += (_, _) => Recount();

        var detail = Label(already
            ? $"{found.From} · already read here"
            : $"{found.From} · {found.Issues} issue{(found.Issues == 1 ? string.Empty : "s")} · "
              + $"last {found.Latest.ToLocalTime():d MMM}");
        detail.Margin = new Thickness(24, 2, 0, 0);
        detail.FontSize = 11;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        Bind(detail, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _offered.Add((found, tick));

        var row = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Child = new StackPanel { Children = { tick, detail } },
        };
        row[!BackgroundProperty] = new DynamicResourceExtension("list.row.hover.brush");
        return row;
    }

    private void Recount()
    {
        var ticked = _offered.Count(o => o.Tick.IsChecked == true);

        _subscribe.IsEnabled = ticked > 0;
        _subscribe.Content = ticked > 1 ? $"Read {ticked} Here" : "Read Here";
    }

    private void Commit()
    {
        if (_account() is not { } account) return;

        var category = _category.SelectedIndex > 0 ? _category.SelectedItem as string ?? string.Empty : string.Empty;
        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox);

        using (_feeds.Batch())
        {
            foreach (var (found, tick) in _offered)
            {
                if (tick.IsChecked != true) continue;

                var feed = _feeds.Add(found.Address, found.Name, category);
                Added++;

                // The back numbers come too, unless the reader would rather leave them: a
                // subscription that starts empty and fills up over weeks looks broken.
                if (_gather.IsChecked == true && inbox is not null)
                {
                    Gathered += NewsletterScan.Gather(account.Mail, account.Account.Id, inbox.Id, feed, found.Identity);
                }
            }
        }

        Close();
    }

    // ---- Small helpers ------------------------------------------------------------------------

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    private static TextBlock Label(string text, bool bold = false, double size = 12)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private static Button Push(string text, Action onClick)
    {
        var button = new Button { Content = text, MinWidth = 96 };
        button.Click += (_, _) => onClick();
        return button;
    }
}
