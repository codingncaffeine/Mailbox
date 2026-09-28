using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Localization;
using Mailbox.Core.Weather;
using Mailbox.Protocols;

namespace Mailbox.App.Views;

/// <summary>
/// Adding a place: one box that takes a city or a zip code, and a list of what it matches to
/// choose from.
/// </summary>
/// <remarks>
/// The reference adds a weather location the same way — a name or a zip code — and so does this,
/// searching as the reader types (after a pause, so a word typed costs one request, not one per
/// letter) and on Enter. The matches name their region and country, because "Springfield" is
/// forty places and only the reader knows which one is theirs.
/// <para>
/// The application's own dialog, painted from tokens in all four themes, as the feed dialog is.
/// </para>
/// </remarks>
public sealed class AddLocationDialog : Window
{
    private readonly WeatherReceiver _weather;
    private readonly TextBox _query = new();
    private readonly ListBox _matches = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _add;
    private readonly DispatcherTimer _pause = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _searching;

    /// <summary>
    /// A harness run is typing. The box's change event is raised after the text is set, so it
    /// would start the type-ahead's timer after the pose had already searched — and that second
    /// search cancels the pose's own before its answer is shown.
    /// </summary>
    private bool _posing;

    /// <summary>The place chosen, or null when the dialog was cancelled.</summary>
    public WeatherPlace? Chosen { get; private set; }

    public AddLocationDialog(WeatherReceiver weather)
    {
        _weather = weather ?? throw new ArgumentNullException(nameof(weather));

        Title = Strings.T("Add Location");
        Width = 520;
        Height = 520;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _add = Push(Strings.T("Add"), Commit);
        _add.IsDefault = true;
        _add.IsEnabled = false;
        var cancel = Push(Strings.T("Cancel"), Close);
        cancel.IsCancel = true;

        _pause.Tick += async (_, _) =>
        {
            _pause.Stop();
            await SearchAsync();
        };

        DialogChrome.Apply(this, Layout(cancel));
        Opened += (_, _) => _query.Focus();
    }

    private Control Layout(Button cancel)
    {
        var prompt = Label(Strings.T("Find a place"), bold: true, size: 15);
        var explain = Label(Strings.T("Type a city or a zip code — Beverly Hills, 90210, Oslo — and choose the right one from what is found."));
        explain.TextWrapping = TextWrapping.Wrap;
        explain.Margin = new Thickness(0, 4, 0, 12);

        _query.PlaceholderText = Strings.T("City or zip code");
        _query.TextChanged += (_, _) =>
        {
            _pause.Stop();
            if (_posing) return;
            if (!string.IsNullOrWhiteSpace(_query.Text) && _query.Text.Trim().Length >= 2) _pause.Start();
        };
        _query.KeyDown += async (_, e) =>
        {
            if (e.Key is Key.Down && _matches.ItemCount > 0)
            {
                e.Handled = true;
                _matches.SelectedIndex = Math.Max(0, _matches.SelectedIndex);
                _matches.ContainerFromIndex(_matches.SelectedIndex)?.Focus();
                return;
            }

            if (e.Key is not Key.Enter || _matches.SelectedItem is not null) return;
            e.Handled = true;
            _pause.Stop();
            await SearchAsync();
        };

        Bind(_message, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _matches.Margin = new Thickness(0, 10, 0, 0);
        _matches.SelectionChanged += (_, _) => _add.IsEnabled = _matches.SelectedItem is not null;
        _matches.DoubleTapped += (_, _) => { if (_matches.SelectedItem is not null) Commit(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _add, cancel },
        };

        var top = new StackPanel { Children = { prompt, explain, _query, _message } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);

        return new DockPanel { Margin = new Thickness(18), Children = { top, buttons, _matches } };
    }

    private async Task SearchAsync()
    {
        var query = _query.Text?.Trim() ?? string.Empty;
        if (query.Length < 2) return;

        if (_searching is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        var searching = new CancellationTokenSource();
        _searching = searching;
        _message.Text = Strings.T("Searching…");

        try
        {
            var language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var (places, error) = await _weather.SearchAsync(query, language, searching.Token);
            if (searching.IsCancellationRequested) return;

            _matches.Items.Clear();
            foreach (var place in places) _matches.Items.Add(Row(place));
            if (places.Count > 0) _matches.SelectedIndex = 0;

            _message.Text = error.Length > 0
                ? error
                : places.Count == 0
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.T("Nothing was found for “{0}”."), query)
                    : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // A newer search took over.
        }
    }

    private static Control Row(WeatherPlace place)
    {
        var name = Label(place.Name, bold: true, size: 13);
        var where = Label(string.Join(", ", new[] { place.Region, place.Country }.Where(s => s.Length > 0 && s != place.Name)));
        Bind(where, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        var stack = new StackPanel { Margin = new Thickness(2, 3), Children = { name, where }, Tag = place };
        Avalonia.Automation.AutomationProperties.SetName(stack, place.FullName);
        return stack;
    }

    private void Commit()
    {
        if (_matches.SelectedItem is not StackPanel { Tag: WeatherPlace place }) return;
        Chosen = place;
        Log.Info($"Weather: chose {place.FullName} ({OpenMeteo.Coordinate(place.Latitude)}, {OpenMeteo.Coordinate(place.Longitude)}).");
        Close();
    }

    /// <summary>
    /// Types a search and waits for it, for a capture run — <c>MAILBOX_WEATHER_ADD=&lt;text&gt;[|add]</c>;
    /// with <c>add</c> the first match is taken, as pressing Add would take it.
    /// </summary>
    public void Pose(string spec)
    {
        var parts = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _posing = true;
        _query.Text = parts.Length > 0 ? parts[0] : spec;
        _ = PoseAsync(parts.Any(p => p.Equals("add", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task PoseAsync(bool add)
    {
        using (Mailbox.App.Theming.WindowCapture.IsRequested ? Mailbox.App.Theming.WindowCapture.Hold() : null)
        {
            _pause.Stop();
            await SearchAsync();
            Log.Info($"Harness: the place search for “{_query.Text}” found {_matches.ItemCount} match(es); the dialog says “{_message.Text}”.");
        }

        if (add) Commit();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _pause.Stop();
        _searching?.Cancel();
        _searching?.Dispose();
        _searching = null;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    private static TextBlock Label(string text, bool bold = false, double size = 12)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private static Button Push(string text, Action onClick)
    {
        var button = new Button { Content = text, MinWidth = 88 };
        button.Click += (_, _) => onClick();
        return button;
    }
}
