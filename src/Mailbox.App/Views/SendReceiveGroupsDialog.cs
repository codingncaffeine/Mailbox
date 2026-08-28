using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Mailbox.Core.Settings;

namespace Mailbox.App.Views;

/// <summary>
/// Define Send/Receive Groups: which accounts a group covers, and when it runs.
/// </summary>
/// <remarks>
/// The reference splits this over two dialogs — a list, and a settings window behind an Edit
/// button. One is enough here: the settings are four controls and a list of accounts, and a
/// second window to reach them is a click that buys nothing.
/// </remarks>
public sealed class SendReceiveGroupsDialog : Window
{
    private readonly SendReceiveGroups _groups;
    private readonly IReadOnlyList<string> _accounts;

    private readonly ListBox _list = new() { Width = 200 };
    private readonly StackPanel _settings = new() { Spacing = 8 };

    private List<SendReceiveGroup> _working;

    public SendReceiveGroupsDialog(SendReceiveGroups groups, IReadOnlyList<string> accounts)
    {
        _groups = groups;
        _accounts = accounts;
        _working = [.. groups.All];

        Title = "Send/Receive Groups";
        Width = 690;
        Height = 460;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.ItemTemplate = new FuncDataTemplate<SendReceiveGroup>((group, _) => group is null ? new Control() : Row(group));
        _list.SelectionChanged += (_, _) => ShowSettings();

        DialogChrome.Apply(this, Body());

        Rebuild();
        _list.SelectedIndex = 0;
    }

    private Control Row(SendReceiveGroup group)
    {
        var text = new TextBlock
        {
            Text = group.Name + (group.IncludeInSendReceiveAll ? string.Empty : "  (on request)"),
            Margin = new Thickness(4, 2),
        };
        Bind(text, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        return text;
    }

    private Control Body()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(14),
        };

        var heading = new TextBlock
        {
            Text = "A group is a set of accounts checked together. Press F9 to run every group "
                   + "that asks to be included.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        Grid.SetColumnSpan(heading, 2);
        root.Children.Add(heading);

        var listBox = new Border
        {
            Child = _list,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
        };
        Bind(listBox, BorderBrushProperty, "dialog.border.brush");
        Bind(listBox, BackgroundProperty, "dialog.surface.brush");
        Grid.SetRow(listBox, 1);
        root.Children.Add(listBox);

        var settings = new ScrollViewer { Content = _settings };
        Grid.SetRow(settings, 1);
        Grid.SetColumn(settings, 1);
        root.Children.Add(settings);

        var buttons = Buttons();
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);

        return root;
    }

    private Control Buttons()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var add = Button("New");
        add.Click += (_, _) =>
        {
            _working.Add(new SendReceiveGroup { Name = _groups.NextName() });
            Commit();
            _list.SelectedIndex = _working.Count - 1;
        };
        row.Children.Add(add);

        var rename = Button("Rename...");
        rename.Click += async (_, _) => await RenameAsync();
        row.Children.Add(rename);

        var copy = Button("Copy");
        copy.Click += (_, _) =>
        {
            if (Selected is not { } group) return;
            _working.Add(group with { Name = _groups.NextName() });
            Commit();
        };
        row.Children.Add(copy);

        var remove = Button("Remove");
        remove.Click += (_, _) =>
        {
            // The last group is not removable: F9 has to have something to act on, and a
            // mailbox with no group is a mail client that has quietly stopped checking mail.
            if (Selected is not { } group || _working.Count == 1) return;

            _working.Remove(group);
            Commit();
            _list.SelectedIndex = 0;
        };
        row.Children.Add(remove);

        var close = Button("Close");
        close.Click += (_, _) => Close();
        row.Children.Add(close);

        return row;
    }

    private SendReceiveGroup? Selected => _list.SelectedItem as SendReceiveGroup;

    private async Task RenameAsync()
    {
        if (Selected is not { } group) return;
        if (await Prompt.AskAsync(this, "Rename group", "Group name:", group.Name) is not { } name) return;
        if (string.IsNullOrWhiteSpace(name)) return;

        Update(group with { Name = name.Trim() });
    }

    /// <summary>Swaps one group for an edited copy, keeping its place in the list.</summary>
    private void Update(SendReceiveGroup edited)
    {
        var index = _list.SelectedIndex;
        if (index < 0 || index >= _working.Count) return;

        _working[index] = edited;
        Commit();
        _list.SelectedIndex = index;
    }

    private void Commit()
    {
        _groups.Replace(_working);
        _working = [.. _groups.All];
        Rebuild();
    }

    private void Rebuild()
    {
        var index = _list.SelectedIndex;
        _list.ItemsSource = _working.ToList();
        _list.SelectedIndex = Math.Clamp(index, 0, _working.Count - 1);
    }

    // ---- The selected group's settings --------------------------------------------------------

    private void ShowSettings()
    {
        _settings.Children.Clear();
        if (Selected is not { } group) return;

        var include = Check("Include this group in send/receive (F9)", group.IncludeInSendReceiveAll);
        include.IsCheckedChanged += (_, _) =>
            Update(Selected! with { IncludeInSendReceiveAll = include.IsChecked == true });
        _settings.Children.Add(include);

        var scheduled = Check("Schedule an automatic send/receive every", group.ScheduleEnabled);
        scheduled.IsCheckedChanged += (_, _) =>
            Update(Selected! with { ScheduleEnabled = scheduled.IsChecked == true });

        var minutes = new NumericUpDown
        {
            Value = group.ScheduleMinutes,
            Minimum = 1,
            Maximum = 1440,
            Increment = 1,
            Width = 84,
            IsEnabled = group.ScheduleEnabled,
        };
        minutes.ValueChanged += (_, _) =>
            Update(Selected! with { ScheduleMinutes = (int)(minutes.Value ?? 30) });

        var scheduleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        scheduleRow.Children.Add(scheduled);
        scheduleRow.Children.Add(minutes);
        scheduleRow.Children.Add(Label("minutes"));
        _settings.Children.Add(scheduleRow);

        _settings.Children.Add(Label("Accounts in this group:"));

        if (_accounts.Count == 0)
        {
            _settings.Children.Add(Label("No accounts are set up yet."));
            return;
        }

        // An empty list means every account, so a group with nothing ticked shows everything
        // ticked — which is what it does, and what a reader would otherwise have to infer.
        var everything = group.Accounts.Count == 0;

        foreach (var address in _accounts)
        {
            var box = Check(address, everything || group.Includes(address));
            box.Margin = new Thickness(14, 0, 0, 0);

            box.IsCheckedChanged += (_, _) =>
            {
                if (Selected is not { } current) return;

                var chosen = _accounts
                    .Where(a => a == address ? box.IsChecked == true : Ticked(current, a))
                    .ToList();

                // Everything ticked is the same statement as nothing listed, and storing it
                // that way is what keeps the group covering an account added later.
                Update(current with
                {
                    Accounts = chosen.Count == _accounts.Count ? [] : chosen,
                });
            };

            _settings.Children.Add(box);
        }
    }

    private static bool Ticked(SendReceiveGroup group, string address)
        => group.Accounts.Count == 0 || group.Includes(address);

    private CheckBox Check(string label, bool isChecked)
    {
        var box = new CheckBox { Content = label, IsChecked = isChecked };
        Bind(box, ForegroundProperty, "dialog.foreground.brush");
        return box;
    }

    private TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Bind(label, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return label;
    }

    private static Button Button(string label) => new()
    {
        Content = label,
        Height = 24,
        Padding = new Thickness(12, 0),
    };

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
