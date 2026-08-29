using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using Mailbox.Core.Rules;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The small dialogs a rule's values are edited in — the people, the words, the folder, the
/// level, the size, the date span, the category, the alert text, the sound — shared by the wizard,
/// the Rules and Alerts pane and the Create Rule dialog, so a value is edited the same way from
/// all three.
/// </summary>
internal static class RuleValues
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>
    /// Edits a condition's value in place and returns the edited condition, or null when the
    /// dialog was dismissed. A condition kind that carries no value returns itself.
    /// </summary>
    public static async Task<RuleCondition?> EditAsync(Window owner, RuleCondition condition, MailRepository mail)
    {
        switch (condition.Kind)
        {
            case RuleConditionKind.From:
            case RuleConditionKind.SentTo:
                return await PeopleAsync(owner, condition.Kind == RuleConditionKind.From ? "Rule Address" : "Rule Address", condition.Values)
                    is { } people ? condition with { Values = people } : null;

            case RuleConditionKind.SubjectContains:
            case RuleConditionKind.BodyContains:
            case RuleConditionKind.SubjectOrBodyContains:
            case RuleConditionKind.HeaderContains:
            case RuleConditionKind.SenderAddressContains:
            case RuleConditionKind.RecipientAddressContains:
                return await WordsAsync(owner, condition.Values) is { } words ? condition with { Values = words } : null;

            case RuleConditionKind.Importance:
                return await LevelAsync(owner, "Importance", ["Low", "Normal", "High"], condition.Level ?? 1)
                    is { } importance ? condition with { Level = importance } : null;

            case RuleConditionKind.Sensitivity:
                return await LevelAsync(owner, "Sensitivity", ["Normal", "Personal", "Private", "Confidential"], condition.Level ?? 0)
                    is { } sensitivity ? condition with { Level = sensitivity } : null;

            case RuleConditionKind.SizeBetween:
                return await SizeAsync(owner, condition.Min, condition.Max) is { } size
                    ? condition with { Min = size.Min, Max = size.Max }
                    : null;

            case RuleConditionKind.ReceivedBetween:
                return await SpanAsync(owner, condition.After, condition.Before) is { } span
                    ? condition with { After = span.After, Before = span.Before }
                    : null;

            case RuleConditionKind.AssignedToCategory:
                return await CategoryAsync(owner, mail, condition.Values) is { } categories
                    ? condition with { Values = categories }
                    : null;

            default:
                return condition;
        }
    }

    /// <summary>Edits an action's value in place, as <see cref="EditAsync(Window, RuleCondition, MailRepository)"/> does a condition's.</summary>
    public static async Task<RuleAction?> EditAsync(Window owner, RuleAction action, MailRepository mail, long accountId)
    {
        switch (action.Kind)
        {
            case RuleActionKind.MoveToFolder:
            case RuleActionKind.CopyToFolder:
                return await FolderAsync(owner, mail, accountId, action.FolderId) is { } folder
                    ? action with { FolderId = folder.Id, FolderName = folder.Name }
                    : null;

            case RuleActionKind.ForwardTo:
            case RuleActionKind.ForwardAsAttachmentTo:
            case RuleActionKind.RedirectTo:
                return await PeopleAsync(owner, "Rule Address", action.Values) is { } people ? action with { Values = people } : null;

            case RuleActionKind.FlagForFollowUp:
            {
                var choices = new List<Choice>
                {
                    new("Today", "0"), new("Tomorrow", "1"), new("This week", "5"),
                    new("Next week", "7"), new("No date", "none"),
                };
                var current = action.Level is { } d ? d.ToString(CultureInfo.InvariantCulture) : "none";
                if (await Chooser.AskAsync(owner, "Flag Message", "Follow up:", choices, current) is not { } chosen) return null;
                return action with { Level = chosen == "none" ? null : int.Parse(chosen, CultureInfo.InvariantCulture) };
            }

            case RuleActionKind.AssignCategory:
                return await CategoryAsync(owner, mail, action.Values) is { } categories ? action with { Values = categories } : null;

            case RuleActionKind.DisplayAlert:
                return await Prompt.AskAsync(owner, "Alert Message", "Specify the text to display in the New Item Alert window:",
                        action.Values.FirstOrDefault() ?? string.Empty)
                    is { } text ? action with { Values = text.Trim().Length > 0 ? [text.Trim()] : [] } : null;

            case RuleActionKind.PlaySound:
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select a Sound to Play",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Sounds") { Patterns = ["*.wav", "*.oga", "*.ogg", "*.mp3", "*.flac"] }],
                });
                if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return null;
                return action with { Values = [path] };
            }

            default:
                return action;
        }
    }

    /// <summary>Whether a condition of this kind carries a value the reader must fill in.</summary>
    public static bool NeedsValue(RuleConditionKind kind) => kind is not (
        RuleConditionKind.SentOnlyToMe or RuleConditionKind.MyNameInTo or RuleConditionKind.MyNameInCc
        or RuleConditionKind.MyNameInToOrCc or RuleConditionKind.MyNameNotInTo or RuleConditionKind.HasAttachment
        or RuleConditionKind.Flagged);

    /// <summary>Whether an action of this kind carries a value the reader must fill in.</summary>
    public static bool NeedsValue(RuleActionKind kind) => kind is
        RuleActionKind.MoveToFolder or RuleActionKind.CopyToFolder or RuleActionKind.ForwardTo
        or RuleActionKind.ForwardAsAttachmentTo or RuleActionKind.RedirectTo or RuleActionKind.AssignCategory
        or RuleActionKind.DisplayAlert;

    /// <summary>Whether a condition has the value its kind needs — the wizard will not finish without.</summary>
    public static bool IsComplete(RuleCondition c) => c.Kind switch
    {
        RuleConditionKind.SizeBetween => c.Min is not null || c.Max is not null,
        RuleConditionKind.ReceivedBetween => c.After is not null || c.Before is not null,
        RuleConditionKind.Importance or RuleConditionKind.Sensitivity => c.Level is not null,
        _ => !NeedsValue(c.Kind) || c.Values.Count > 0,
    };

    /// <summary>Whether an action has the value its kind needs.</summary>
    public static bool IsComplete(RuleAction a) => a.Kind switch
    {
        RuleActionKind.MoveToFolder or RuleActionKind.CopyToFolder => a.FolderId is not null || a.FolderName is { Length: > 0 },
        _ => !NeedsValue(a.Kind) || a.Values.Count > 0,
    };

    // ---- The editors ---------------------------------------------------------------------------

    /// <summary>
    /// People or a public group: addresses, names or "@domain"s, one per line. The reference
    /// opens the address book from here; this is the typed form instead, with the Auto-Complete
    /// List's shape of entry. Wiring it to the address book the People module already has is
    /// the obvious next step.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> PeopleAsync(Window owner, string title, IReadOnlyList<string> current)
    {
        var text = await Prompt.AskAsync(owner, title,
            "Enter addresses, names or domains (@example.com), one per line or separated by semicolons:",
            string.Join("\n", current), multiline: true);
        if (text is null) return null;

        return [.. text.Split(['\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
    }

    /// <summary>The reference's Search Text dialog: a word or phrase, added to a list.</summary>
    public static async Task<IReadOnlyList<string>?> WordsAsync(Window owner, IReadOnlyList<string> current)
    {
        IReadOnlyList<string>? answer = null;
        var words = new List<string>(current);

        var entry = new TextBox { PlaceholderText = "Specify a word or phrase to search for", Width = 300 };
        var list = new ListBox { Height = 140, Width = 300, ItemsSource = words.ToList() };
        Bind(list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");

        void Refresh() => list.ItemsSource = words.ToList();

        var add = new Button { Content = "Add", Width = 80 };
        add.Click += (_, _) =>
        {
            var word = entry.Text?.Trim();
            if (string.IsNullOrEmpty(word) || words.Contains(word, StringComparer.OrdinalIgnoreCase)) return;
            words.Add(word);
            entry.Text = string.Empty;
            Refresh();
            entry.Focus();
        };

        var remove = new Button { Content = "Remove", Width = 80 };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is string word) { words.Remove(word); Refresh(); }
        };

        var window = new Window
        {
            Title = "Search Text",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 74 };
        cancel.Click += (_, _) => window.Close();
        var ok = new Button { Content = "OK", IsDefault = false, Width = 74 };
        ok.Click += (_, _) => { answer = words.ToList(); window.Close(); };

        // Enter in the field adds rather than closing: the dialog is a list being built.
        entry.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        };

        var caption1 = new TextBlock { Text = "Specify words or phrases to search for:" };
        Bind(caption1, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        var caption2 = new TextBlock { Text = "Search list:" };
        Bind(caption2, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 10,
            Children =
            {
                caption1,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { entry, add } },
                caption2,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { list, remove } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };

        DialogChrome.Apply(window, body);
        window.Opened += (_, _) => entry.Focus();
        await window.ShowDialog(owner);
        return answer;
    }

    /// <summary>
    /// A folder of the account, by name, the Outbox left out. The window is titled for the job
    /// the reader is doing — a Quick Step's first-time setup is not "Rules and Alerts".
    /// </summary>
    public static async Task<Folder?> FolderAsync(Window owner, MailRepository mail, long accountId, long? current, string title = "Rules and Alerts")
    {
        var folders = mail.Folders(accountId).Where(f => f.Role != FolderRole.Outbox).ToList();
        var choices = folders.Select(f => new Choice(f.Name, f.Id.ToString(CultureInfo.InvariantCulture))).ToList();
        if (choices.Count == 0) return null;

        var chosen = await Chooser.AskAsync(owner, title, "Choose a folder:", choices,
            current?.ToString(CultureInfo.InvariantCulture));
        return chosen is null ? null : folders.FirstOrDefault(f => f.Id.ToString(CultureInfo.InvariantCulture) == chosen);
    }

    private static async Task<int?> LevelAsync(Window owner, string title, string[] names, int current)
    {
        var choices = names.Select((n, i) => new Choice(n, i.ToString(CultureInfo.InvariantCulture))).ToList();
        var chosen = await Chooser.AskAsync(owner, title, $"{title}:", choices, current.ToString(CultureInfo.InvariantCulture));
        return chosen is null ? null : int.Parse(chosen, CultureInfo.InvariantCulture);
    }

    private static async Task<(long? Min, long? Max)?> SizeAsync(Window owner, long? min, long? max)
    {
        var current = $"{min?.ToString(CultureInfo.InvariantCulture)}-{max?.ToString(CultureInfo.InvariantCulture)}";
        var text = await Prompt.AskAsync(owner, "Size", "Size in kilobytes, as \"at least-at most\" (either may be blank):", current);
        if (text is null) return null;

        var parts = text.Split('-', 2);
        long? a = parts.Length > 0 && long.TryParse(parts[0].Trim(), out var lo) ? lo : null;
        long? b = parts.Length > 1 && long.TryParse(parts[1].Trim(), out var hi) ? hi : null;
        return (a, b);
    }

    private static async Task<(DateTimeOffset? After, DateTimeOffset? Before)?> SpanAsync(Window owner, DateTimeOffset? after, DateTimeOffset? before)
    {
        var current = $"{after?.ToLocalTime():yyyy-MM-dd} to {before?.ToLocalTime():yyyy-MM-dd}".Trim();
        var text = await Prompt.AskAsync(owner, "Date Received", "Received between, as \"yyyy-MM-dd to yyyy-MM-dd\" (either may be blank):", current);
        if (text is null) return null;

        var parts = text.Split(" to ", 2, StringSplitOptions.None);
        DateTimeOffset? a = parts.Length > 0 && DateTime.TryParse(parts[0].Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var lo)
            ? new DateTimeOffset(lo.Date) : null;
        DateTimeOffset? b = parts.Length > 1 && DateTime.TryParse(parts[1].Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var hi)
            ? new DateTimeOffset(hi.Date.AddDays(1).AddSeconds(-1)) : null;
        return (a, b);
    }

    private static async Task<IReadOnlyList<string>?> CategoryAsync(Window owner, MailRepository mail, IReadOnlyList<string> current)
    {
        var categories = mail.Categories();
        if (categories.Count == 0) return null;

        var chosen = await PickListDialog.PickAsync(owner, "Color Categories", "Categories:",
            categories.Select(c => new PickListDialog.Item(c.Name, c.Name)).ToList(), current);
        return chosen;
    }
}
