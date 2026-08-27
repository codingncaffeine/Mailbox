using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Settings;

namespace Mailbox.App.Views;

/// <summary>
/// The reading settings that belong to the whole module rather than to one feed.
/// </summary>
/// <remarks>
/// There were none, and the settings behind them existed with nothing to reach them: pictures in
/// the article list had a key and no control anywhere, and when an article counts as read — the
/// most-used setting in any reader — had no answer at all.
/// <para>
/// Separate from the per-feed RSS Feed Options dialog, and deliberately: that one is the
/// reference's own and belongs to a subscription, and burying "mark as read after three seconds"
/// inside the settings for one feed would be answering a question about all of them in the wrong
/// place.
/// </para>
/// </remarks>
public sealed class FeedReadingDialog : Window
{
    private readonly SettingsStore _settings;
    private readonly MailOptions _options;

    private readonly ComboBox _markRead = new();
    private readonly TextBox _seconds = new();
    private readonly TextBox _every = new();
    private readonly CheckBox _pictures;
    private readonly CheckBox _inMailPane;

    /// <summary>True when something was changed and saved.</summary>
    public bool Changed { get; private set; }

    /// <summary>Where the reader's own "check everything every N minutes" is kept.</summary>
    public const string IntervalKey = "rss.interval";

    /// <summary>The choices, in the order they read, and the values behind them.</summary>
    private static readonly (string Label, FeedReadModeChoice Mode)[] Choices =
    [
        ("When I open it", FeedReadModeChoice.OnOpen),
        ("After a few seconds of reading it", FeedReadModeChoice.AfterAMoment),
        ("When I scroll past it", FeedReadModeChoice.OnScroll),
        ("Only when I say so", FeedReadModeChoice.Never),
    ];

    public FeedReadingDialog(SettingsStore settings, MailOptions options)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        Title = "Reading";
        Width = 520;
        Height = 430;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _pictures = Tick("Show pictures in the article list", _options.FeedPictures);
        _inMailPane = Tick("Also show RSS Feeds in the Mail folder pane", _options.FeedsInMailPane);
        _inMailPane.Margin = new Thickness(0, 6, 0, 0);

        var ok = Push("OK", Save);
        ok.IsDefault = true;

        var cancel = Push("Cancel", Close);
        cancel.IsCancel = true;

        DialogChrome.Apply(this, Layout(ok, cancel));
    }

    private Control Layout(Button ok, Button cancel)
    {
        var heading = Label("Reading", bold: true, size: 15);

        var explain = Label("How the article list behaves, for every feed. One feed's own "
            + "settings — where it is filed, how often it is checked, how much of it is kept — "
            + "are on that feed, under Feed Settings.");
        explain.TextWrapping = TextWrapping.Wrap;
        explain.Margin = new Thickness(0, 4, 0, 16);
        Bind(explain, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _markRead.ItemsSource = Choices.Select(c => c.Label).ToList();
        _markRead.SelectedIndex = Math.Max(0, Array.FindIndex(Choices, c => c.Mode == Current));
        _markRead.MinWidth = 260;
        _markRead.HorizontalAlignment = HorizontalAlignment.Left;
        _markRead.SelectionChanged += (_, _) => ShowDelay();

        _seconds.Text = _settings.GetNumber(FeedsWorkspace.ReadDelayKey, 3)
            .ToString("0.#", CultureInfo.CurrentCulture);
        _seconds.Width = 48;

        _delay = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { Middle(Label("Wait")), _seconds, Middle(Label("seconds")) },
        };

        _every.Text = _settings.GetNumber(IntervalKey, 0) is > 0 and var minutes
            ? ((int)minutes).ToString(CultureInfo.CurrentCulture)
            : string.Empty;
        _every.Width = 56;

        var interval = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                Middle(Label("Check every")),
                _every,
                Middle(Label("minutes — blank to follow Send/Receive")),
            },
        };

        var scrollNote = Label("Scrolling past marks an article read only when it has gone "
            + "completely off the top of the list, so what you are part-way through is never "
            + "taken from under you.");
        scrollNote.TextWrapping = TextWrapping.Wrap;
        scrollNote.FontSize = 11;
        scrollNote.Margin = new Thickness(0, 8, 0, 0);
        Bind(scrollNote, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
            Children = { ok, cancel },
        };

        var page = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                heading,
                explain,
                Label("Mark an article as read:", bold: true),
                _markRead,
                _delay,
                scrollNote,
                Gap(),
                Label("Checking for new articles", bold: true),
                interval,
                Gap(),
                _pictures,
                _inMailPane,
                buttons,
            },
        };

        ShowDelay();
        return page;
    }

    private StackPanel _delay = new();

    private static Control Gap() => new Border { Height = 16 };

    private static Control Middle(Control control)
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    /// <summary>The delay only means anything for the one choice that waits.</summary>
    private void ShowDelay()
        => _delay.IsVisible = Chosen == FeedReadModeChoice.AfterAMoment;

    private FeedReadModeChoice Chosen
        => Choices[Math.Clamp(_markRead.SelectedIndex, 0, Choices.Length - 1)].Mode;

    private FeedReadModeChoice Current
        => Enum.TryParse<FeedReadModeChoice>(_settings.GetString(FeedsWorkspace.ReadModeKey), out var mode)
            ? mode
            : FeedReadModeChoice.OnOpen;

    private void Save()
    {
        _settings.Set(FeedsWorkspace.ReadModeKey, Chosen.ToString());

        if (double.TryParse((_seconds.Text ?? string.Empty).Trim(), NumberStyles.Float,
                CultureInfo.CurrentCulture, out var seconds) && seconds > 0)
        {
            _settings.Set(FeedsWorkspace.ReadDelayKey, seconds);
        }

        var minutes = int.TryParse((_every.Text ?? string.Empty).Trim(), NumberStyles.Integer,
            CultureInfo.CurrentCulture, out var every) && every > 0
            ? every
            : 0;

        _settings.Set(IntervalKey, minutes);
        _options.FeedPictures = _pictures.IsChecked == true;
        _options.FeedsInMailPane = _inMailPane.IsChecked == true;

        Changed = true;
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
            Margin = new Thickness(0, 0, 0, 4),
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private static CheckBox Tick(string label, bool isChecked)
    {
        var box = new CheckBox { Content = label, IsChecked = isChecked };
        Bind(box, CheckBox.ForegroundProperty, "dialog.foreground.brush");
        return box;
    }

    private static Button Push(string text, Action onClick)
    {
        var button = new Button { Content = text, MinWidth = 80 };
        button.Click += (_, _) => onClick();
        return button;
    }
}

/// <summary>
/// When an article counts as read, as the reader's setting names it.
/// </summary>
/// <remarks>
/// Mirrors the workspace's own enum rather than sharing it, because the workspace's is internal
/// to the view and this is what is written into the settings file — a value a person may read
/// there and a later build has to keep understanding.
/// </remarks>
public enum FeedReadModeChoice
{
    OnOpen,
    AfterAMoment,
    OnScroll,
    Never,
}
