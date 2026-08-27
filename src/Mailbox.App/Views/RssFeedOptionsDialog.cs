using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Mailbox.Core.Feeds;
using static Mailbox.App.Views.SystemDialogKit;

namespace Mailbox.App.Views;

/// <summary>
/// One feed's settings: what it is called, where it is filed, what is downloaded with it, and
/// whether the publisher's update limit is honoured.
/// </summary>
/// <remarks>
/// The reference's own RSS Feed Options dialog, reached from Account Settings › RSS Feeds ›
/// Change… — four group boxes: General, Delivery Location, Downloads, Update Limit. It is a
/// system dialog and stays the desktop's light grey in every theme, like the rest of the Account
/// Settings family, because that is how the reference draws it.
/// <para>
/// Two divergences, both stated on the page rather than hidden. Delivery Location is a heading
/// rather than a folder picker: a feed here delivers into a folder named after it under its own
/// heading, and the heading is the thing worth choosing — it is what the Feeds module groups by
/// and what the unread counts total. And the update limit shows what the publisher actually
/// asked for, which the reference's version does not.
/// </para>
/// </remarks>
public sealed class RssFeedOptionsDialog : Window
{
    private const double DialogWidth = 456;
    private const double DialogHeight = 476;

    private readonly FeedSubscription _feed;
    private readonly FeedSubscriptions _feeds;

    private readonly TextBox _name = Field();
    private readonly ComboBox _category = new();
    private readonly CheckBox _enclosures;
    private readonly CheckBox _article;
    private readonly CheckBox _useLimit;

    /// <summary>True when something was changed and saved.</summary>
    public bool Changed { get; private set; }

    public RssFeedOptionsDialog(FeedSubscription feed, FeedSubscriptions feeds)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));

        Title = "RSS Feed Options";
        Width = DialogWidth;
        Height = DialogHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _enclosures = Tick("Automatically download Enclosures for this RSS Feed", feed.DownloadEnclosures);
        _article = Tick("Download the full article as an .html attachment", feed.DownloadFullArticle);
        _useLimit = Tick("Update this RSS Feed with the publisher's recommendation.", feed.UseProviderLimit);

        SystemDialogChrome.Apply(this, Layout());
    }

    private Control Layout()
    {
        var ok = PushButton("OK", Save);
        ok.IsDefault = true;

        var cancel = PushButton("Cancel", Close);
        cancel.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 10),
            Children = { ok, cancel },
        };

        var page = new StackPanel
        {
            Margin = new Thickness(12, 12, 12, 0),
            Children =
            {
                GroupBox("General", General()),
                GroupBox("Delivery Location", Delivery(), top: 8),
                GroupBox("Downloads", Downloads(), top: 8),
                GroupBox("Update Limit", UpdateLimit(), top: 8),
            },
        };

        DockPanel.SetDock(buttons, Dock.Bottom);
        return new DockPanel { Children = { buttons, page } };
    }

    private Control General()
    {
        _name.Text = _feed.Name;
        _name.Width = 268;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("104,*"),
            RowDefinitions = new RowDefinitions("26,20,20"),
        };

        Row(grid, 0, "Feed Name:", _name);
        Row(grid, 1, "Channel Name:", Label(_feed.ChannelTitle is { Length: > 0 } named ? named : "(not read yet)"));
        Row(grid, 2, "Location:", Trimmed(_feed.Url));

        return grid;
    }

    private Control Delivery()
    {
        // Every heading already in use, plus none — and the box takes a new one typed into it,
        // so a reader can file a feed somewhere new without going and making it first.
        _category.ItemsSource = new[] { "(no heading)" }.Concat(_feeds.Categories).ToList();
        _category.SelectedIndex = _feed.Category.Length == 0
            ? 0
            : Math.Max(0, _feeds.Categories.ToList().FindIndex(
                c => string.Equals(c, _feed.Category, StringComparison.OrdinalIgnoreCase)) + 1);
        _category.Width = 200;
        _category.HorizontalAlignment = HorizontalAlignment.Left;

        var text = Paragraph("Items from this RSS Feed are delivered to a folder named after it, "
            + "under the heading you choose here.");
        text.Width = 400;
        text.Margin = new Thickness(0, 0, 0, 8);

        var path = Label($"RSS Feeds\\{_feed.FolderPath.Replace('/', '\\')}", bold: true);
        path.Margin = new Thickness(0, 8, 0, 0);

        _category.SelectionChanged += (_, _) =>
            path.Text = $"RSS Feeds\\{Chosen()}{(Chosen().Length > 0 ? "\\" : string.Empty)}{_name.Text}";

        return new StackPanel { Children = { text, _category, path } };
    }

    private Control Downloads()
    {
        _article.Margin = new Thickness(0, 6, 0, 0);

        var note = Paragraph("An enclosure is a file an article carries — a podcast episode, a "
            + "video. The full article is fetched from the publisher's own page, for a feed that "
            + "publishes only the first paragraph.");
        note.Width = 400;
        note.Margin = new Thickness(0, 8, 0, 0);

        return new StackPanel { Children = { _enclosures, _article, note } };
    }

    private Control UpdateLimit()
    {
        var explain = Paragraph("Send/Receive groups do not update this feed more often than the "
            + "publisher asks, which is what stops a feed being suspended by its publisher.");
        explain.Width = 400;
        explain.Margin = new Thickness(17, 2, 0, 0);

        var current = Label(_feed.ProviderLimitMinutes is { } minutes
            ? $"Current provider limit: {Describe(minutes)}"
            : "Current provider limit: this publisher does not ask for one.");
        current.Margin = new Thickness(17, 8, 0, 0);

        return new StackPanel { Children = { _useLimit, explain, current } };
    }

    /// <summary>A limit in minutes, said the way a person would say it.</summary>
    private static string Describe(int minutes) => minutes switch
    {
        < 60 => $"{minutes} minutes",
        60 => "1 hour",
        < 1440 when minutes % 60 == 0 => $"{minutes / 60} hours",
        < 1440 => $"{minutes / 60} hours {minutes % 60} minutes",
        1440 => "1 day",
        _ => $"{minutes / 1440} days",
    };

    private string Chosen()
        => _category.SelectedIndex > 0 ? _category.SelectedItem as string ?? string.Empty : string.Empty;

    private static void Row(Grid grid, int row, string label, Control value)
    {
        var name = Label(label);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(name, row);
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        value.VerticalAlignment = VerticalAlignment.Center;
        value.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
    }

    /// <summary>A long address, kept to one line.</summary>
    private static TextBlock Trimmed(string text)
    {
        var block = Label(text);
        block.MaxWidth = 300;
        block.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(block, text);
        return block;
    }

    private void Save()
    {
        var name = _name.Text?.Trim() ?? string.Empty;
        if (name.Length == 0) name = _feed.Name;

        var category = Chosen();

        Changed = name != _feed.Name
            || !string.Equals(category, _feed.Category, StringComparison.Ordinal)
            || _enclosures.IsChecked == true != _feed.DownloadEnclosures
            || _article.IsChecked == true != _feed.DownloadFullArticle
            || _useLimit.IsChecked == true != _feed.UseProviderLimit;

        if (Changed)
        {
            _feeds.Update(_feed.Url, f => f with
            {
                Name = name,
                Category = category,
                DownloadEnclosures = _enclosures.IsChecked == true,
                DownloadFullArticle = _article.IsChecked == true,
                UseProviderLimit = _useLimit.IsChecked == true,

                // Turning the limit off should take effect now rather than after the limit the
                // reader has just declined to honour has expired.
                NextDueUtc = _useLimit.IsChecked == true ? f.NextDueUtc : null,
            });
        }

        Close();
    }
}
