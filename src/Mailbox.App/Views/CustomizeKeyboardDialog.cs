using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Mailbox.Core.Commands;
using Mailbox.Core.Keyboard;

namespace Mailbox.App.Views;

/// <summary>
/// Customize Keyboard — the reference's dialog behind Customize Ribbon's "Keyboard shortcuts:
/// Customize…": categories and their commands, the command's current key, a box that reads
/// the next key pressed, what that key is assigned to now, Assign, Remove, Reset All.
/// </summary>
/// <remarks>
/// A shortcut is one chord to one command: assigning a chord another command holds takes it
/// from that command, and the box says so before Assign is pressed. The compose window's
/// commands are left out — its keys are the editor's own and are not routed through the map.
/// </remarks>
public sealed class CustomizeKeyboardDialog : Window
{
    private const string AllCommands = "All Commands";

    private readonly KeyMap _keys;
    private readonly IReadOnlyList<MailboxCommand> _commands;
    private readonly ListBox _categories = ViewDialogKit.SurfaceList(180, 200);
    private readonly ListBox _commandList = ViewDialogKit.SurfaceList(300, 200);
    private readonly ListBox _current = ViewDialogKit.SurfaceList(180, 60);
    private readonly TextBox _press = new() { Width = 300, IsReadOnly = true };
    private readonly TextBlock _assignedTo = ViewDialogKit.Label(string.Empty, subtle: true);
    private readonly TextBlock _description = ViewDialogKit.Label(string.Empty, subtle: true);
    private readonly Button _assign = new() { Content = "Assign", Width = 90 };
    private readonly Button _remove = new() { Content = "Remove", Width = 90 };
    private Chord? _pressed;

    public CustomizeKeyboardDialog(KeyMap keys, CommandCatalog catalog)
    {
        _keys = keys;
        _commands = [.. catalog.All
            .Where(c => !c.Id.Value.StartsWith("compose.", StringComparison.Ordinal) && c.Label.Length > 0)
            .OrderBy(c => c.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)];

        Title = "Customize Keyboard";
        Width = 600;
        Height = 560;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _categories.ItemTemplate = new FuncDataTemplate<string>((c, _) => ViewDialogKit.SurfaceText(c));
        _categories.ItemsSource = new[] { AllCommands }.Concat(_commands.Select(c => c.Category).Where(c => c.Length > 0).Distinct()).ToList();
        _categories.SelectionChanged += (_, _) => FillCommands();

        _commandList.ItemTemplate = new FuncDataTemplate<MailboxCommand>((c, _) =>
        {
            if (c is null) return new Control();

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = ViewDialogKit.SurfaceText(c.Label);
            var key = ViewDialogKit.SurfaceText(_keys.GestureFor(c.Id)?.Display ?? string.Empty);
            key.Opacity = 0.7;
            key.Margin = new Thickness(8, 0, 4, 0);
            Grid.SetColumn(key, 1);
            row.Children.Add(name);
            row.Children.Add(key);
            return row;
        });
        _commandList.SelectionChanged += (_, _) => ShowSelected();

        _current.ItemTemplate = new FuncDataTemplate<string>((c, _) => ViewDialogKit.SurfaceText(c));

        // The box reads the next key pressed rather than typing it.
        _press.AddHandler(KeyDownEvent, (_, e) =>
        {
            e.Handled = true;
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            var modifiers = ChordModifiers.None;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= ChordModifiers.Control;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= ChordModifiers.Alt;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= ChordModifiers.Shift;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= ChordModifiers.Meta;
            _pressed = new Chord(modifiers, e.Key.ToString());
            _press.Text = _pressed.Display;
            ShowAssignedTo();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _press.AddHandler(TextInputEvent, (_, e) => e.Handled = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _assign.Click += (_, _) => Assign();
        _remove.Click += (_, _) => Remove();

        DialogChrome.Apply(this, Layout());
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
        _categories.SelectedIndex = 0;
    }

    private Control Layout()
    {
        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label("Categories:"), _categories } },
                new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label("Commands:"), _commandList } },
            },
        };

        var keys = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label("Current keys:"), _current } },
                new StackPanel { Spacing = 4, Children = { ViewDialogKit.Label("Press new shortcut key:"), _press, _assignedTo } },
            },
        };

        var resetAll = new Button { Content = "Reset All…", Width = 100 };
        resetAll.Click += async (_, _) =>
        {
            var go = await Confirm.AskAsync(this, "Customize Keyboard", "Put every shortcut back the way it shipped?", "Reset All", destructive: false);
            if (!go) return;
            _keys.ResetAll();
            FillCommands();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { _assign, _remove, resetAll } };
        var close = ViewDialogKit.Cancel(this, "Close");

        _description.MaxWidth = 540;
        _description.HorizontalAlignment = HorizontalAlignment.Left;

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Children = { ViewDialogKit.Buttons(close) } },
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        ViewDialogKit.Label("Specify a command", bold: true),
                        top,
                        ViewDialogKit.Label("Specify keyboard sequence", bold: true),
                        keys,
                        buttons,
                        ViewDialogKit.Label("Description", bold: true),
                        _description,
                    },
                },
            },
        };
    }

    private void FillCommands()
    {
        var category = _categories.SelectedItem as string ?? AllCommands;
        var selected = (_commandList.SelectedItem as MailboxCommand)?.Id;
        var rows = category == AllCommands ? _commands : [.. _commands.Where(c => c.Category == category)];
        _commandList.ItemsSource = rows;
        _commandList.SelectedItem = rows.FirstOrDefault(c => c.Id == selected) ?? rows.FirstOrDefault();
        ShowSelected();
    }

    private void ShowSelected()
    {
        var command = _commandList.SelectedItem as MailboxCommand;
        _current.ItemsSource = command is null ? [] : new[] { _keys.GestureFor(command.Id)?.Display ?? "(none)" };
        _current.SelectedIndex = 0;
        _description.Text = command?.Description ?? string.Empty;
        _remove.IsEnabled = command is not null && _keys.GestureFor(command.Id) is not null;
        _pressed = null;
        _press.Text = string.Empty;
        ShowAssignedTo();
    }

    private void ShowAssignedTo()
    {
        var command = _commandList.SelectedItem as MailboxCommand;
        _assign.IsEnabled = command is not null && _pressed is not null;
        if (_pressed is null) { _assignedTo.Text = string.Empty; return; }

        var holder = _keys.CommandFor(_pressed);
        _assignedTo.Text = holder is { } id && (command is null || id != command.Id)
            ? $"Currently assigned to: {(App.Commands.TryGet(id, out var other) ? other.Label : id.Value)}"
            : "Currently assigned to: [unassigned]";
    }

    private void Assign()
    {
        if (_commandList.SelectedItem is not MailboxCommand command || _pressed is null) return;
        _keys.Assign(command.Id, _pressed);
        FillCommands();
    }

    private void Remove()
    {
        if (_commandList.SelectedItem is not MailboxCommand command) return;
        _keys.Remove(command.Id);
        FillCommands();
    }
}
