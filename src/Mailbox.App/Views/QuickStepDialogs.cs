using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Commands;
using Mailbox.Core.Settings;
using Mailbox.Store;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>Shared building blocks for the three Quick Step dialogs.</summary>
internal static class QuickStepUi
{
    public static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public static TextBlock Label(string text, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    /// <summary>The icons a step may wear: the glyph map's names that read as actions.</summary>
    public static readonly string[] Icons =
    [
        "quicksteps", "move", "forward", "mail", "mark-complete", "reply", "reply-all", "delete", "archive",
        "flag", "categorize", "folder", "star", "unread", "importance", "person-add", "people", "meeting",
    ];

    /// <summary>
    /// Asks for the value an action needs, and returns the action with it — or null when the
    /// reader dismissed the question.
    /// </summary>
    public static async Task<QuickStepAction?> AskValueAsync(Window owner, QuickStepAction action, OpenAccount? account)
    {
        switch (action.Kind)
        {
            case QuickStepKind.MoveToFolder:
            case QuickStepKind.CopyToFolder:
            {
                if (account is null) return action;
                var folder = await RuleValues.FolderAsync(owner, account.Mail, account.Account.Id, action.FolderId, "Select Folder");
                return folder is null ? null : action with { FolderId = folder.Id, FolderName = folder.Name };
            }

            case QuickStepKind.NewMessage:
            case QuickStepKind.Forward:
            case QuickStepKind.ForwardAsAttachment:
            {
                var people = await RuleValues.PeopleAsync(owner, "Addresses", action.Values);
                return people is null ? null : action with { Values = people };
            }

            case QuickStepKind.SetImportance:
            {
                var choices = new List<Choice> { new("Low", "0"), new("Normal", "1"), new("High", "2") };
                var chosen = await Chooser.AskAsync(owner, "Set importance", "Importance:", choices, (action.Level ?? 1).ToString(CultureInfo.InvariantCulture));
                return chosen is null ? null : action with { Level = int.Parse(chosen, CultureInfo.InvariantCulture) };
            }

            case QuickStepKind.Categorize:
            {
                if (account is null) return action;
                var categories = account.Mail.Categories();
                var chosen = await PickListDialog.PickAsync(owner, "Categorize", "Categories:",
                    categories.Select(c => new PickListDialog.Item(c.Name, c.Name)).ToList(), action.Values);
                return chosen is null ? null : action with { Values = chosen };
            }

            case QuickStepKind.FlagMessage:
            {
                var choices = new List<Choice>
                {
                    new("Today", "0"), new("Tomorrow", "1"), new("This week", "5"), new("Next week", "7"), new("No date", "none"),
                };
                var chosen = await Chooser.AskAsync(owner, "Flag Message", "Flag:", choices,
                    action.Level is { } d ? d.ToString(CultureInfo.InvariantCulture) : "none");
                return chosen is null ? null : action with { Level = chosen == "none" ? null : int.Parse(chosen, CultureInfo.InvariantCulture) };
            }

            case QuickStepKind.RunCommand:
            {
                // Every catalogue command, plugins' included — that is the point (§13) — except
                // the steps themselves, which would be a loop offered in a dropdown. The value
                // keeps the id and the label both: the id is what runs, whatever the command is
                // later renamed to; the label is what the dialog's line reads back.
                var commands = App.Commands.All
                    .Where(c => !c.Id.Value.StartsWith("quickstep.", StringComparison.Ordinal))
                    .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
                    .Select(c => new Choice(
                        c.OwningPluginId is null ? c.Label : $"{c.Label} — plugin", c.Id.Value))
                    .ToList();

                var chosen = await Chooser.AskAsync(owner, "Run a command", "Command:", commands,
                    action.Values.FirstOrDefault() ?? string.Empty);
                if (chosen is null) return null;

                var label = App.Commands.TryGet(new CommandId(chosen), out var picked) ? picked.Label : chosen;
                return action with { Values = [chosen, label] };
            }

            default:
                return action;
        }
    }
}

/// <summary>
/// First Time Setup: the reference's short dialog when a shipped step is pressed before its
/// folder or addresses have been chosen — the name, each action with its blank to fill, Options
/// for the full editor, Save.
/// </summary>
public sealed class QuickStepSetupDialog : Window
{
    /// <summary>The step, set up, or null when cancelled.</summary>
    public QuickStep? Result { get; private set; }

    public QuickStepSetupDialog(QuickStep step, OpenAccount? account)
    {
        Title = "First Time Setup";
        Width = 520;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var working = step;
        var name = new TextBox { Text = step.Name, Width = 300 };
        var rows = new StackPanel { Spacing = 8 };

        void Redraw()
        {
            rows.Children.Clear();
            for (var i = 0; i < working.Actions.Count; i++)
            {
                var index = i;
                var action = working.Actions[i];
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(QuickStepUi.Label(action.Describe()));

                if (action.NeedsSetup || action.Kind is QuickStepKind.MoveToFolder or QuickStepKind.CopyToFolder
                    or QuickStepKind.NewMessage or QuickStepKind.Forward or QuickStepKind.ForwardAsAttachment)
                {
                    var choose = new Button { Content = action.NeedsSetup ? "Choose…" : "Change…" };
                    choose.Click += async (_, _) =>
                    {
                        if (await QuickStepUi.AskValueAsync(this, action, account) is not { } chosen) return;
                        var list = working.Actions.ToList();
                        list[index] = chosen;
                        working = working with { Actions = list };
                        if (working.Actions.All(a => !a.NeedsSetup) && working.Name.EndsWith("?", StringComparison.Ordinal)
                            && chosen.FolderName is { Length: > 0 } folder)
                        {
                            name.Text = working.Name.Replace("?", folder);
                        }
                        Redraw();
                    };
                    row.Children.Add(choose);
                }

                rows.Children.Add(row);
            }
        }

        Redraw();

        var options = new Button { Content = "Options…" };
        options.Click += async (_, _) =>
        {
            var editor = new EditQuickStepDialog(working with { Name = name.Text ?? working.Name }, account);
            await editor.ShowDialog(this);
            if (editor.Result is { } edited)
            {
                Result = edited;
                Close();
            }
        };

        var save = new Button { Content = "Save", Width = 74, IsDefault = true };
        save.Click += (_, _) =>
        {
            if (working.NeedsSetup)
            {
                _ = Confirm.SayAsync(this, "First Time Setup", "Choose the folder or the addresses the step needs first.");
                return;
            }

            Result = working with { Name = name.Text?.Trim() is { Length: > 0 } typed ? typed : working.Name };
            Close();
        };

        var cancel = new Button { Content = "Cancel", Width = 74, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { options, save, cancel },
                },
                new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        QuickStepUi.Label("This is the first time you have used this Quick Step. Choose what it should do."),
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { QuickStepUi.Label("Name:"), name } },
                        QuickStepUi.Label("Actions", bold: true),
                        rows,
                    },
                },
            },
        };

        DialogChrome.Apply(this, body);
        QuickStepUi.Bind(this, BackgroundProperty, "dialog.background.brush");
    }
}

/// <summary>
/// Edit Quick Step: the name and icon, the actions in order — each an action picked from the
/// reference's list with its value — the shortcut key and the tooltip.
/// </summary>
public sealed class EditQuickStepDialog : Window
{
    private readonly OpenAccount? _account;
    private QuickStep _step;
    private readonly StackPanel _rows = new() { Spacing = 6 };

    /// <summary>The step as edited, or null when cancelled.</summary>
    public QuickStep? Result { get; private set; }

    private static readonly QuickStepKind[] Kinds = Enum.GetValues<QuickStepKind>();

    public EditQuickStepDialog(QuickStep step, OpenAccount? account)
    {
        _step = step;
        _account = account;

        Title = step.Actions.Count == 0 ? "New Quick Step" : "Edit Quick Step";
        Width = 600;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var name = new TextBox { Text = step.Name, Width = 300 };

        var icon = new ComboBox { MinWidth = 90 };
        icon.ItemsSource = QuickStepUi.Icons.Select(i => new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(i, 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            Tag = i,
        }).ToList();
        icon.SelectedIndex = Math.Max(0, Array.IndexOf(QuickStepUi.Icons, step.Icon));

        var shortcut = new ComboBox { MinWidth = 160 };
        shortcut.ItemsSource = new List<string> { "Choose a shortcut" }.Concat(Enumerable.Range(1, 9).Select(n => $"Ctrl+Shift+{n}")).ToList();
        shortcut.SelectedIndex = step.Shortcut is { } n ? n : 0;

        var tooltip = new TextBox { Text = step.Tooltip, Width = 420 };

        var addAction = new Button { Content = "Add Action" };
        addAction.Click += async (_, _) => await AddActionAsync();

        var save = new Button { Content = "Finish", Width = 74, IsDefault = true };
        save.Click += (_, _) =>
        {
            if (_step.Actions.Count == 0)
            {
                _ = Confirm.SayAsync(this, Title ?? "Quick Step", "A Quick Step needs at least one action.");
                return;
            }

            Result = _step with
            {
                Name = name.Text?.Trim() is { Length: > 0 } typed ? typed : _step.Name,
                Icon = (icon.SelectedItem as TextBlock)?.Tag as string ?? _step.Icon,
                Shortcut = shortcut.SelectedIndex > 0 ? shortcut.SelectedIndex : null,
                Tooltip = tooltip.Text?.Trim() ?? string.Empty,
            };
            Close();
        };

        var cancel = new Button { Content = "Cancel", Width = 74, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var actions = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Height = 220,
            Child = new ScrollViewer { Content = _rows },
        };
        QuickStepUi.Bind(actions, Border.BackgroundProperty, "dialog.surface.brush");
        QuickStepUi.Bind(actions, Border.BorderBrushProperty, "dialog.border.brush");

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { save, cancel },
                },
                new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { QuickStepUi.Label("Name:"), name, icon } },
                        QuickStepUi.Label("Add actions below that will be performed when this quick step is clicked on.", bold: true),
                        actions,
                        addAction,
                        QuickStepUi.Label("Optional", bold: true),
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { QuickStepUi.Label("Shortcut key:"), shortcut } },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { QuickStepUi.Label("Tooltip text:"), tooltip } },
                    },
                },
            },
        };

        DialogChrome.Apply(this, body);
        QuickStepUi.Bind(this, BackgroundProperty, "dialog.background.brush");
        RedrawActions();
    }

    private void RedrawActions()
    {
        _rows.Children.Clear();
        for (var i = 0; i < _step.Actions.Count; i++)
        {
            var index = i;
            var action = _step.Actions[i];

            var text = new TextBlock
            {
                Text = action.Describe(),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            QuickStepUi.Bind(text, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

            var change = new Button { Content = "Change…", IsVisible = HasValue(action.Kind) };
            change.Click += async (_, _) =>
            {
                if (await QuickStepUi.AskValueAsync(this, action, _account) is not { } chosen) return;
                var list = _step.Actions.ToList();
                list[index] = chosen;
                _step = _step with { Actions = list };
                RedrawActions();
            };

            var remove = new Button { Content = "✕" };
            ToolTip.SetTip(remove, "Remove this action");
            remove.Click += (_, _) =>
            {
                var list = _step.Actions.ToList();
                list.RemoveAt(index);
                _step = _step with { Actions = list };
                RedrawActions();
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);
            Grid.SetColumn(change, 1);
            change.Margin = new Thickness(8, 0, 4, 0);
            grid.Children.Add(change);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
            _rows.Children.Add(grid);
        }

        if (_step.Actions.Count == 0)
        {
            var empty = new TextBlock { Text = "No actions yet — Add Action to choose one." };
            QuickStepUi.Bind(empty, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            _rows.Children.Add(empty);
        }
    }

    private static bool HasValue(QuickStepKind kind) => kind is
        QuickStepKind.MoveToFolder or QuickStepKind.CopyToFolder or QuickStepKind.NewMessage or QuickStepKind.Forward
        or QuickStepKind.ForwardAsAttachment or QuickStepKind.SetImportance or QuickStepKind.Categorize or QuickStepKind.FlagMessage;

    private async Task AddActionAsync()
    {
        var choices = Kinds.Select(k => new Choice(QuickStepAction.Label(k), k.ToString(), QuickStepAction.Group(k))).ToList();
        var chosen = await Chooser.AskAsync(this, "Choose an Action", "Action:", choices);
        if (chosen is null || !Enum.TryParse<QuickStepKind>(chosen, out var kind)) return;

        var action = new QuickStepAction(kind);
        if (HasValue(kind) && await QuickStepUi.AskValueAsync(this, action, _account) is { } valued) action = valued;

        _step = _step with { Actions = [.. _step.Actions, action] };
        RedrawActions();
    }
}

/// <summary>
/// Manage Quick Steps: the list in gallery order, the description of the selected step, and
/// New, Edit, Duplicate, Delete, Move Up and Down, Reset to Defaults.
/// </summary>
public sealed class ManageQuickStepsDialog : Window
{
    private readonly OpenAccount? _account;
    private readonly ListBox _list = new() { Height = 220 };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _shortcut = new();

    public ManageQuickStepsDialog(OpenAccount? account)
    {
        _account = account;

        Title = "Manage Quick Steps";
        Width = 620;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.ItemTemplate = new FuncDataTemplate<QuickStep>((step, _) => step is null ? new Control() : Row(step));
        _list.SelectionChanged += (_, _) => Describe();
        QuickStepUi.Bind(_list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        QuickStepUi.Bind(_list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");
        QuickStepUi.Bind(_description, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        QuickStepUi.Bind(_shortcut, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var buttons = new StackPanel
        {
            Spacing = 6,
            Width = 130,
            Children =
            {
                Tool("New…", NewAsync),
                Tool("Edit…", EditAsync),
                Tool("Duplicate", () => { Duplicate(); return Task.CompletedTask; }),
                Tool("Delete", DeleteAsync),
                Tool("▲ Move Up", () => { Move(-1); return Task.CompletedTask; }),
                Tool("▼ Move Down", () => { Move(1); return Task.CompletedTask; }),
                new Panel { Height = 8 },
                Tool("Reset to Defaults", ResetAsync),
            },
        };

        var close = new Button { Content = "Close", Width = 74, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { close },
                },
                new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        QuickStepUi.Label("Quick Step:", bold: true),
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            Children = { new Border { Child = _list, Width = 400 }, buttons },
                        },
                        QuickStepUi.Label("Description:", bold: true),
                        _description,
                        _shortcut,
                    },
                },
            },
        };

        DialogChrome.Apply(this, body);
        QuickStepUi.Bind(this, BackgroundProperty, "dialog.background.brush");
        Reload();
    }

    private static Button Tool(string label, Func<Task> run)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += async (_, _) => await run();
        return button;
    }

    private static Control Row(QuickStep step)
    {
        var glyph = new TextBlock
        {
            Text = IconGlyphs.GetOrEmpty(step.Icon, 16),
            FontFamily = IconFont.Family,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
        };
        QuickStepUi.Bind(glyph, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var name = new TextBlock { Text = step.Name, VerticalAlignment = VerticalAlignment.Center };
        QuickStepUi.Bind(name, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2), Children = { glyph, name } };
    }

    private void Reload(string? select = null)
    {
        var steps = App.QuickSteps.All.ToList();
        _list.ItemsSource = steps;
        _list.SelectedItem = steps.FirstOrDefault(s => s.Id == select) ?? steps.FirstOrDefault();
        Describe();
    }

    private QuickStep? Selected => _list.SelectedItem as QuickStep;

    private void Describe()
    {
        if (Selected is not { } step)
        {
            _description.Text = string.Empty;
            _shortcut.Text = string.Empty;
            return;
        }

        _description.Text = step.Tooltip.Length > 0
            ? step.Tooltip + "\n\nActions: " + string.Join("; ", step.Actions.Select(a => a.Describe()))
            : "Actions: " + string.Join("; ", step.Actions.Select(a => a.Describe()));
        _shortcut.Text = step.Shortcut is { } n ? $"Shortcut key: Ctrl+Shift+{n}" : "Shortcut key: none";
    }

    private async Task NewAsync()
    {
        var editor = new EditQuickStepDialog(new QuickStep { Id = QuickSteps.NewId(), Name = "My Quick Step" }, _account);
        await editor.ShowDialog(this);
        if (editor.Result is { } made)
        {
            App.QuickSteps.Upsert(made);
            Reload(made.Id);
        }
    }

    private async Task EditAsync()
    {
        if (Selected is not { } step) return;
        var editor = new EditQuickStepDialog(step, _account);
        await editor.ShowDialog(this);
        if (editor.Result is { } edited)
        {
            App.QuickSteps.Upsert(edited);
            Reload(edited.Id);
        }
    }

    private void Duplicate()
    {
        if (Selected is not { } step) return;
        var copy = step with { Id = QuickSteps.NewId(), Name = "Copy of " + step.Name, Shortcut = null };
        App.QuickSteps.Upsert(copy);
        Reload(copy.Id);
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } step) return;
        var go = await Confirm.AskAsync(this, "Manage Quick Steps", $"Delete the Quick Step \"{step.Name}\"?", "Delete");
        if (!go) return;

        App.QuickSteps.Remove(step.Id);
        if (!step.Id.StartsWith("mail.", StringComparison.Ordinal)) App.Commands.Unregister(step.CommandId);
        Reload();
    }

    private void Move(int direction)
    {
        if (Selected is not { } step) return;
        var order = App.QuickSteps.All.ToList();
        var index = order.FindIndex(s => s.Id == step.Id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= order.Count) return;

        (order[index], order[target]) = (order[target], order[index]);
        App.QuickSteps.Replace(order);
        Reload(step.Id);
    }

    private async Task ResetAsync()
    {
        var go = await Confirm.AskAsync(this, "Manage Quick Steps",
            "Reset the Quick Steps to the ones Mailbox ships with? Any you made will be removed.", "Reset");
        if (!go) return;

        foreach (var step in App.QuickSteps.All.Where(s => !s.Id.StartsWith("mail.", StringComparison.Ordinal) && QuickSteps.Defaults.All(d => d.Id != s.Id)))
        {
            App.Commands.Unregister(step.CommandId);
        }

        App.QuickSteps.Reset();
        Reload();
    }
}
