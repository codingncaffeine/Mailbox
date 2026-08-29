using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Protocols;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// Subscribing: one box that takes a website address or a feed address, and shows what it found
/// before anything is committed to.
/// </summary>
/// <remarks>
/// <b>Not "paste the location of the RSS Feed".</b> Nobody knows a feed's address; they know the
/// address in the browser bar. So the box takes either, the finding happens here, and what comes
/// back is a card naming the publication, its description and its three most recent headlines —
/// which is enough to tell whether it is the right feed before subscribing to it.
/// <para>
/// Everything on the card comes out of the feed itself. No follower counts and no "people who
/// read this also read": those need a service that watches what everybody reads, which is exactly
/// what this application is not. What <em>can</em> be worked out honestly from the feed — how
/// often it publishes — is shown, and the rest is left off rather than invented.
/// </para>
/// <para>
/// This one is the application's own dialog rather than a system dialog: it is a new surface with
/// no counterpart in the reference, so it takes the shell's themed chrome and paints from tokens
/// in all four themes.
/// </para>
/// </remarks>
public sealed class SubscribeDialog : Window
{
    private readonly FeedFinder _finder;
    private readonly FeedSubscriptions _feeds;

    private readonly TextBox _address = new();
    private readonly Button _subscribe;
    private readonly Button _find;
    private readonly ComboBox _category = new();
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly TextBlock _message = new();

    private CancellationTokenSource? _searching;
    private readonly List<(DiscoveredFeed Feed, CheckBox Tick)> _offered = [];

    /// <summary>
    /// What was subscribed to. Empty when the dialog was cancelled.
    /// </summary>
    /// <remarks>
    /// A list rather than one, because a site usually publishes several — the articles, one per
    /// section, the comments — and the reader is the only one who knows which of them they want.
    /// Offering the first and hiding the rest is a guess made on their behalf.
    /// </remarks>
    public IReadOnlyList<FeedSubscription> Subscribed { get; private set; } = [];

    public SubscribeDialog(FeedFinder finder, FeedSubscriptions feeds)
    {
        _finder = finder ?? throw new ArgumentNullException(nameof(finder));
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));

        Title = "Add a Feed";
        Width = 660;
        Height = 660;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _find = Push("Find", async () => await SearchAsync());
        _find.IsDefault = true;

        _subscribe = Push("Subscribe", Commit);
        _subscribe.IsEnabled = false;

        var cancel = Push("Cancel", Close);
        cancel.IsCancel = true;

        DialogChrome.Apply(this, Layout(cancel));
        Opened += (_, _) => _address.Focus();
    }

    private Control Layout(Button cancel)
    {
        _address.PlaceholderText = "A website address, a feed address, or a subject";
        _address.Margin = new Thickness(0, 0, 8, 0);
        _address.KeyDown += async (_, e) =>
        {
            if (e.Key is not Key.Enter) return;
            e.Handled = true;
            await SearchAsync();
        };
        _address.TextChanged += (_, _) =>
        {
            _find.IsEnabled = !string.IsNullOrWhiteSpace(_address.Text);
            _subscribe.IsEnabled = false;
        };
        _find.IsEnabled = false;

        var prompt = Label("Follow a website", bold: true, size: 15);
        var explain = Label(
            "Type the address of a site — theverge.com — and every feed it publishes is found for "
            + "you. A feed's own address works, so do YouTube channels, subreddits and GitHub "
            + "repositories, and a subject with no address becomes a news search.");
        explain.TextWrapping = TextWrapping.Wrap;
        explain.Margin = new Thickness(0, 4, 0, 12);

        var box = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_address, 0);
        box.Children.Add(_address);
        Grid.SetColumn(_find, 1);
        box.Children.Add(_find);

        _message.TextWrapping = TextWrapping.Wrap;
        _message.Margin = new Thickness(0, 12, 0, 0);
        Bind(_message, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        // The heading a feed is filed under. Editable, so a reader can type a new one rather than
        // having to make it somewhere else first.
        _category.ItemsSource = new[] { "(no heading)" }.Concat(_feeds.Categories).ToList();
        _category.SelectedIndex = 0;
        _category.Width = 200;

        var categoryRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Label("File it under:"), _category },
        };

        var scroll = new ScrollViewer
        {
            Content = _results,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _subscribe, cancel },
        };

        var top = new StackPanel { Children = { prompt, explain, box, _message, categoryRow } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children = { top, buttons, scroll },
        };
    }

    /// <summary>
    /// Types an address and searches, for a capture run. Holds the shot until it is done.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_SUBSCRIBE=&lt;address&gt;[|heading:&lt;name&gt;][|subscribe]</c>. Without
    /// <c>subscribe</c> the box is typed into and searched, which is what a photograph of the
    /// results wants; with it the Subscribe button is pressed as a pointer would press it, which
    /// is the only way to prove that finding a site and taking up what it found are joined —
    /// the search half was posable and the committing half was not, so the flow could be
    /// photographed and never finished.
    /// </remarks>
    public void Pose(string spec)
    {
        var parts = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _address.Text = parts.Length > 0 ? parts[0] : spec;

        var heading = parts.FirstOrDefault(p => p.StartsWith("heading:", StringComparison.OrdinalIgnoreCase));
        var press = parts.Any(p => p.Equals("subscribe", StringComparison.OrdinalIgnoreCase));

        _ = PoseAsync(heading?["heading:".Length..].Trim() ?? string.Empty, press);
    }

    private async Task PoseAsync(string heading, bool press)
    {
        using (var hold = Mailbox.App.Theming.WindowCapture.IsRequested
            ? Mailbox.App.Theming.WindowCapture.Hold()
            : null)
        {
            await SearchAsync();

            if (heading.Length > 0)
            {
                var headings = (_category.ItemsSource as IEnumerable<string>)?.ToList() ?? [];
                var at = headings.FindIndex(h => string.Equals(h, heading, StringComparison.OrdinalIgnoreCase));

                if (at >= 0) _category.SelectedIndex = at;
                else Log.Warn($"Harness: the subscribe box offers no heading “{heading}” — "
                    + $"it offers {string.Join(", ", headings)}.");
            }

            Log.Info($"Harness: subscribe — “{_message.Text}”, {_offered.Count} offered, "
                + $"{_offered.Count(o => o.Tick.IsChecked == true)} ticked"
                + (_offered.Count == 0 ? string.Empty
                    : $": {string.Join(", ", _offered.Select(o => $"“{o.Feed.Label}” {o.Feed.Url}"
                        + (o.Tick.IsChecked == true ? " [ticked]" : string.Empty)))}"));
        }

        if (!press) return;

        Log.Info($"Harness: subscribe — pressing “{_subscribe.Content}”"
            + (_subscribe.IsEffectivelyEnabled ? string.Empty : " (which is greyed)"));

        _subscribe.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Log.Info($"Harness: subscribe — took up {Subscribed.Count}: "
            + string.Join(", ", Subscribed.Select(f => $"“{f.Name}” {f.Url} under “{f.Category}”")));
    }

    // ---- Finding ---------------------------------------------------------------------------------

    private async Task SearchAsync()
    {
        var typed = _address.Text?.Trim() ?? string.Empty;
        if (typed.Length == 0) return;

        // A second press while the first is still running cancels it, rather than racing it.
        await CancelSearchAsync();
        _searching = new CancellationTokenSource();
        var token = _searching.Token;

        _results.Children.Clear();
        _offered.Clear();
        _subscribe.IsEnabled = false;
        _find.IsEnabled = false;
        _message.Text = "Looking…";

        try
        {
            var search = await _finder.FindAsync(typed, token);
            if (token.IsCancellationRequested) return;

            if (!search.Found)
            {
                _message.Text = search.Error.Length > 0 ? search.Error : "No feed was found at that address.";
                return;
            }

            _message.Text = search.Feeds.Count == 1
                ? "One feed found."
                : $"{search.Feeds.Count} feeds found — tick the ones you want.";

            foreach (var found in search.Feeds) _results.Children.Add(await CardAsync(found, token));
            Recount();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later search, or the dialog closed. Nothing to say.
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException)
        {
            _message.Text = ex.Message;
            Log.Warn($"Feeds: looking for a feed at “{typed}” failed.", ex);
        }
        finally
        {
            if (!token.IsCancellationRequested) _find.IsEnabled = !string.IsNullOrWhiteSpace(_address.Text);
        }
    }

    /// <summary>
    /// One found feed, as a tickable card: what it is called, where it is, what it says it is,
    /// and the headlines it is carrying right now.
    /// </summary>
    private async Task<Control> CardAsync(DiscoveredFeed found, CancellationToken cancellation)
    {
        var channel = await PreviewAsync(found, cancellation);
        var already = _feeds.Contains(found.Url);

        var named = channel?.Title is { Length: > 0 } title ? title : found.Label;

        var tick = new CheckBox
        {
            Content = named,
            FontWeight = FontWeight.SemiBold,
            IsEnabled = !already,

            // One result is what the reader meant; several is a choice, and ticking nothing by
            // default would make them do the work twice.
            IsChecked = !already && _offered.Count == 0,
        };
        Bind(tick, CheckBox.ForegroundProperty, "dialog.foreground.brush");
        tick.IsCheckedChanged += (_, _) => Recount();

        var host = Label(Uri.TryCreate(found.Url, UriKind.Absolute, out var url) ? url.Host + url.AbsolutePath : found.Url);
        host.Margin = new Thickness(24, 2, 0, 0);
        host.TextTrimming = TextTrimming.CharacterEllipsis;
        Bind(host, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var stack = new StackPanel { Children = { tick, host } };

        if (channel?.Description is { Length: > 0 } description)
        {
            var summary = Label(description.Length > 200 ? description[..200] + "…" : description);
            summary.TextWrapping = TextWrapping.Wrap;
            summary.Margin = new Thickness(24, 6, 0, 0);
            stack.Children.Add(summary);
        }

        // The most recent headlines: the quickest way to tell whether this is the feed meant.
        foreach (var item in channel?.Items.Take(2) ?? [])
        {
            var headline = Label($"·  {item.Title}");
            headline.TextTrimming = TextTrimming.CharacterEllipsis;
            headline.Margin = new Thickness(24, 3, 0, 0);
            Bind(headline, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
            stack.Children.Add(headline);
        }

        var footnote = already
            ? "Already subscribed."
            : Cadence(channel);

        if (footnote.Length > 0)
        {
            var note = Label(footnote);
            note.Margin = new Thickness(24, 6, 0, 0);
            note.FontSize = 11;
            Bind(note, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
            stack.Children.Add(note);
        }

        _offered.Add((found with { Title = named }, tick));

        var card = new Border { Padding = new Thickness(12), Child = stack };
        card[!BackgroundProperty] = new DynamicResourceExtension("list.row.hover.brush");
        return card;
    }

    /// <summary>The feed itself, for the card, or null when it could not be read.</summary>
    private async Task<FeedChannel?> PreviewAsync(DiscoveredFeed found, CancellationToken cancellation)
    {
        try
        {
            var search = await _finder.PeekAsync(found.Url, cancellation);
            return search;
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// How often the feed publishes, worked out from the dates on the entries it is carrying.
    /// </summary>
    /// <remarks>
    /// The one statistic on Feedly's own card that can be had honestly from the feed alone. The
    /// others — followers, "people who read this also read" — need a service watching what
    /// everybody reads, so they are left off rather than invented.
    /// </remarks>
    private static string Cadence(FeedChannel? channel)
    {
        if (channel is null) return string.Empty;

        var dates = channel.Items.Select(i => i.Published).OfType<DateTimeOffset>().OrderBy(d => d).ToList();
        if (dates.Count < 3) return string.Empty;

        var span = dates[^1] - dates[0];
        if (span <= TimeSpan.Zero) return string.Empty;

        var perWeek = dates.Count / Math.Max(span.TotalDays / 7, 0.1);

        return perWeek >= 1
            ? $"About {Math.Round(perWeek)} article{(Math.Round(perWeek) == 1 ? string.Empty : "s")} a week."
            : "Less than one article a week.";
    }

    private async Task CancelSearchAsync()
    {
        if (_searching is not { } running) return;

        await running.CancelAsync();
        running.Dispose();
        _searching = null;
    }

    // ---- Subscribing -----------------------------------------------------------------------------

    /// <summary>Keeps the Subscribe button honest about how many it is about to add.</summary>
    private void Recount()
    {
        var ticked = _offered.Count(o => o.Tick.IsChecked == true);

        _subscribe.IsEnabled = ticked > 0;
        _subscribe.Content = ticked > 1 ? $"Subscribe to {ticked}" : "Subscribe";
    }

    private void Commit()
    {
        var category = _category.SelectedIndex > 0 ? _category.SelectedItem as string ?? string.Empty : string.Empty;
        var added = new List<FeedSubscription>();

        // One write of the subscription file for the whole set rather than one per feed.
        using (_feeds.Batch())
        {
            foreach (var (feed, tick) in _offered)
            {
                if (tick.IsChecked != true) continue;
                added.Add(_feeds.Add(feed.Url, feed.Label, category));
            }
        }

        Subscribed = added;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _searching?.Cancel();
        _searching?.Dispose();
        _searching = null;
    }

    // ---- Small helpers ----------------------------------------------------------------------------

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
        var button = new Button { Content = text, MinWidth = 88 };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button Push(string text, Func<Task> onClick)
    {
        var button = new Button { Content = text, MinWidth = 88 };
        button.Click += async (_, _) => await onClick();
        return button;
    }
}
