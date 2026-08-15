using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The Send/Receive Progress dialog: what the run is doing, and what went wrong.
/// </summary>
/// <remarks>
/// Measured from the reference at 476x313. It is one of the few windows the reference does not
/// theme — a legacy dialog in system colours — which is a quirk of its age rather than a design,
/// so this one follows the theme like everything else.
/// <para>
/// It is not modal. A send/receive that blocks the window until it finishes is a mail client
/// that stops being a mail client every time it checks for mail.
/// </para>
/// </remarks>
public sealed class SendReceiveProgressDialog : Window
{
    /// <summary>Set from the checkbox, so the next run does not open this at all.</summary>
    public const string HideSetting = "sendreceive.hideprogress";

    private const double TableHeight = 118;

    private readonly SendReceiveTasks _tasks;
    private readonly SettingsStore _settings;
    private readonly Action _cancelAll;

    private readonly TextBlock _headline = new();
    private readonly TextBlock _current = new();
    private readonly Grid _bar = new() { ColumnDefinitions = new ColumnDefinitions("0*,1*") };
    private readonly ListBox _table = new() { Height = TableHeight };
    private readonly ListBox _errors = new() { Height = TableHeight };
    private readonly Button _cancel;
    private readonly StackPanel _details = new();
    private readonly TextBlock _detailsLabel = new() { Text = "<< Details" };

    public SendReceiveProgressDialog(
        SendReceiveTasks tasks, SettingsStore settings, Action cancelAll)
    {
        _tasks = tasks;
        _settings = settings;
        _cancelAll = cancelAll;

        Title = "Mailbox Send/Receive Progress";
        Width = 476;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _cancel = DialogButton("Cancel All");
        _cancel.Click += (_, _) => _cancelAll();

        DialogChrome.Apply(this, Body());
        Refresh();
    }

    private Control Body()
    {
        var stack = new StackPanel { Margin = new Thickness(12), Spacing = 8 };

        stack.Children.Add(HeadlineRow());
        stack.Children.Add(BarRow());
        stack.Children.Add(HideRow());

        BuildDetails();
        stack.Children.Add(_details);

        return stack;
    }

    private Control HeadlineRow()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        _headline.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_headline, 0);
        row.Children.Add(_headline);

        Grid.SetColumn(_cancel, 1);
        row.Children.Add(_cancel);

        return row;
    }

    /// <summary>
    /// The bar, drawn rather than templated.
    /// </summary>
    /// <remarks>
    /// Two star-sized columns whose widths are the fraction and its remainder, so the fill is
    /// exact without anything having to measure the track first.
    /// </remarks>
    private Control BarRow()
    {
        var fill = new Border();
        Bind(fill, Border.BackgroundProperty, "status.success.brush");
        Grid.SetColumn(fill, 0);
        _bar.Children.Add(fill);

        var track = new Border
        {
            Child = _bar,
            Height = 16,
            BorderThickness = new Thickness(1),
        };
        Bind(track, Border.BackgroundProperty, "dialog.surface.brush");
        Bind(track, Border.BorderBrushProperty, "dialog.border.brush");

        var details = DialogButton(string.Empty);
        details.Content = _detailsLabel;
        Bind(_detailsLabel, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        details.Click += (_, _) => ToggleDetails();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        track.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(track, 0);
        row.Children.Add(track);
        Grid.SetColumn(details, 1);
        row.Children.Add(details);

        return row;
    }

    private Control HideRow()
    {
        var box = new CheckBox
        {
            Content = "Don't show this dialog box during Send/Receive",
            IsChecked = _settings.GetBool(HideSetting),
        };
        box.IsCheckedChanged += (_, _) => _settings.Set(HideSetting, box.IsChecked == true);
        return box;
    }

    private void BuildDetails()
    {
        _details.Spacing = 6;

        _table.ItemTemplate = new FuncDataTemplate<TransferTask>((task, _) => TaskRow(task));
        _errors.ItemTemplate = new FuncDataTemplate<string>((text, _) => ErrorRow(text));

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "Tasks", Content = TasksTab() },
                new TabItem { Header = "Errors", Content = _errors },
            },
        };

        _details.Children.Add(tabs);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        _current.VerticalAlignment = VerticalAlignment.Center;
        _current.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_current, 0);
        footer.Children.Add(_current);

        // One task cannot be cancelled on its own: the run works through the accounts in order
        // and the protocol sessions are not separable. Shown because the reference shows it,
        // disabled because that is honest.
        var cancelTask = DialogButton("Cancel Task");
        cancelTask.IsEnabled = false;
        Grid.SetColumn(cancelTask, 1);
        footer.Children.Add(cancelTask);

        _details.Children.Add(footer);
    }

    private Control TasksTab()
    {
        var panel = new DockPanel();

        var header = Header();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(_table);

        return panel;
    }

    private Control Header()
    {
        var grid = ColumnGrid();

        var names = new[] { "Name", "Progress", "Remaining" };
        for (var i = 0; i < names.Length; i++)
        {
            var text = new TextBlock
            {
                Text = names[i],
                Margin = new Thickness(i == 0 ? 22 : 4, 3, 4, 3),
                FontWeight = FontWeight.SemiBold,
            };
            Bind(text, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }

        var band = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1) };
        Bind(band, Border.BorderBrushProperty, "dialog.border.brush");
        Bind(band, Border.BackgroundProperty, "dialog.selection.brush");
        return band;
    }

    private static Grid ColumnGrid()
        => new() { ColumnDefinitions = new ColumnDefinitions("*,104,88") };

    private Control TaskRow(TransferTask task)
    {
        var grid = ColumnGrid();

        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(4, 1),
        };

        var marker = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(task.Marker, 16),
            FontFamily = IconFont.Family,
            FontSize = 11,
            Width = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(marker, TextBlock.ForegroundProperty,
            task.State == TransferTaskState.Failed ? "status.danger.brush" : "status.success.brush");
        name.Children.Add(marker);

        name.Children.Add(new TextBlock
        {
            Text = task.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var progress = new TextBlock
        {
            Text = task.State switch
            {
                TransferTaskState.Completed => "Completed",
                TransferTaskState.Processing => "Processing",
                TransferTaskState.Failed => "Failed",
                _ => string.Empty,
            },
            Margin = new Thickness(4, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(progress, 1);
        grid.Children.Add(progress);

        var remaining = new TextBlock
        {
            Text = task.State == TransferTaskState.Processing ? task.Progress : string.Empty,
            Margin = new Thickness(4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(remaining, 2);
        grid.Children.Add(remaining);

        return grid;
    }

    private Control ErrorRow(string text) => new TextBlock
    {
        Text = text,
        Margin = new Thickness(4, 2),
        TextWrapping = TextWrapping.Wrap,
    };

    private void ToggleDetails()
    {
        var showing = _details.IsVisible;
        _details.IsVisible = !showing;
        _detailsLabel.Text = showing ? "Details >>" : "<< Details";
    }

    /// <summary>Re-reads the run. Called on the UI thread as progress arrives.</summary>
    public void Refresh()
    {
        _headline.Text = _tasks.Headline;
        _current.Text = _tasks.Current;

        _bar.ColumnDefinitions[0].Width = new GridLength(_tasks.Fraction, GridUnitType.Star);
        _bar.ColumnDefinitions[1].Width = new GridLength(1 - _tasks.Fraction, GridUnitType.Star);

        _table.ItemsSource = _tasks.Tasks.ToList();
        _errors.ItemsSource = _tasks.Errors.Count > 0
            ? _tasks.Errors.ToList()
            : new List<string> { "No errors." };

        _cancel.IsEnabled = !_tasks.IsFinished;
    }

    private Button DialogButton(string label)
    {
        var button = new Button
        {
            Content = label,
            Height = 24,
            MinWidth = 96,
            Padding = new Thickness(10, 0),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        Bind(button, BorderBrushProperty, "dialog.border.brush");
        Bind(button, BackgroundProperty, "dialog.surface.brush");
        return button;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
