using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mailbox.Core.Rules;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// Create Rule: the reference's quick dialog off a message — its sender, its subject and who it
/// was sent to as conditions; an alert, a sound and a move as actions — with Advanced Options
/// opening the whole wizard on the same rule.
/// </summary>
public sealed class CreateRuleDialog : Window
{
    private readonly MailRepository _mail;
    private readonly long _accountId;

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>The rule as saved, or null when the dialog was cancelled.</summary>
    public MailRule? Result { get; private set; }

    /// <summary>Whether the reader asked for the rule to be run on the folder now.</summary>
    public bool RunNow { get; private set; }

    public CreateRuleDialog(MailRepository mail, long accountId, MimeMessage message)
    {
        _mail = mail;
        _accountId = accountId;

        Title = "Create Rule";
        Width = 520;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var from = message.From.Mailboxes.FirstOrDefault();
        var fromText = from is null ? string.Empty : (from.Name is { Length: > 0 } n ? $"{n} <{from.Address}>" : from.Address);
        var subject = message.Subject ?? string.Empty;
        var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(m => m.Address).ToList();

        // Conditions.
        var fromBox = Check($"From {fromText}", from is not null);
        var subjectBox = Check("Subject contains", subject.Length > 0);
        var subjectText = new TextBox { Text = subject, Width = 260, VerticalAlignment = VerticalAlignment.Center, Classes = { "sysfield" } };
        var sentToBox = Check("Sent to", recipients.Count > 0);
        var sentTo = new ComboBox
        {
            ItemsSource = new List<string> { "me only" }.Concat(recipients).ToList(),
            SelectedIndex = 0,
            MinWidth = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Actions.
        var alertBox = Check("Display in the New Item Alert window", false);
        var alertText = new TextBox { Text = subject, Width = 260, VerticalAlignment = VerticalAlignment.Center, Classes = { "sysfield" } };
        var soundBox = Check("Play a selected sound", false);
        var soundText = new TextBlock { Text = "the desktop's new-mail sound", VerticalAlignment = VerticalAlignment.Center };
        Bind(soundText, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");
        string? soundFile = null;
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select a Sound to Play",
                AllowMultiple = false,
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                soundFile = path;
                soundText.Text = Path.GetFileName(path);
                soundBox.IsChecked = true;
            }
        };

        var moveBox = Check("Move the item to folder:", false);
        Folder? folder = null;
        var pick = new Button { Content = "Select Folder…" };
        pick.Click += async (_, _) =>
        {
            folder = await RuleValues.FolderAsync(this, _mail, _accountId, folder?.Id);
            if (folder is not null)
            {
                pick.Content = folder.Name;
                moveBox.IsChecked = true;
            }
        };

        MailRule Build()
        {
            var conditions = new List<RuleCondition>();
            if (fromBox.IsChecked == true && from is not null)
                conditions.Add(new RuleCondition(RuleConditionKind.From) { Values = [from.Address] });
            if (subjectBox.IsChecked == true && subjectText.Text is { Length: > 0 } words)
                conditions.Add(new RuleCondition(RuleConditionKind.SubjectContains) { Values = [words.Trim()] });
            if (sentToBox.IsChecked == true)
            {
                conditions.Add(sentTo.SelectedIndex <= 0
                    ? new RuleCondition(RuleConditionKind.SentOnlyToMe)
                    : new RuleCondition(RuleConditionKind.SentTo) { Values = [recipients[sentTo.SelectedIndex - 1]] });
            }

            var actions = new List<RuleAction>();
            if (alertBox.IsChecked == true)
                actions.Add(new RuleAction(RuleActionKind.DisplayAlert) { Values = alertText.Text is { Length: > 0 } t ? [t.Trim()] : [] });
            if (soundBox.IsChecked == true)
                actions.Add(new RuleAction(RuleActionKind.PlaySound) { Values = soundFile is null ? [] : [soundFile] });
            if (moveBox.IsChecked == true && folder is not null)
                actions.Add(new RuleAction(RuleActionKind.MoveToFolder) { FolderId = folder.Id, FolderName = folder.Name });

            var name = from?.Name is { Length: > 0 } fn ? fn : from?.Address ?? (subject.Length > 0 ? subject : "New rule");
            return new MailRule { Name = name, Conditions = conditions, Actions = actions };
        }

        var advanced = new Button { Content = "Advanced Options…" };
        advanced.Click += async (_, _) =>
        {
            var wizard = new RuleWizard(_mail, _accountId, Build());
            await wizard.ShowDialog(this);
            if (wizard.Result is null) return;

            Result = _mail.AddRule(wizard.Result, DateTimeOffset.UtcNow);
            RunNow = wizard.RunNow;
            Close();
        };

        var ok = new Button { Content = "OK", Width = 74, IsDefault = true };
        ok.Click += async (_, _) =>
        {
            var rule = Build();
            if (rule.Actions.Count == 0)
            {
                await Confirm.SayAsync(this, "Create Rule", "Choose at least one thing to do when the rule matches.");
                return;
            }

            Result = _mail.AddRule(rule, DateTimeOffset.UtcNow);

            // The reference's Success box, with its one checkbox.
            RunNow = await Confirm.AskAsync(this, "Success",
                $"The rule \"{rule.Name}\" has been created.\n\nRun this rule now on messages already in the current folder?",
                "Run Now", destructive: false);
            Close();
        };

        var cancel = new Button { Content = "Cancel", Width = 74, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                Heading("When I get email with all of the selected conditions"),
                fromBox,
                Row(subjectBox, subjectText),
                Row(sentToBox, sentTo),
                new Panel { Height = 6 },
                Heading("Do the following"),
                Row(alertBox, alertText),
                Row(soundBox, soundText, browse),
                Row(moveBox, pick),
                new Panel { Height = 6 },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                    Children =
                    {
                        advanced,
                        Placed(ok, 2),
                        Placed(cancel, 3),
                    },
                },
            },
        };

        ok.Margin = new Thickness(0, 0, 8, 0);
        SystemDialogChrome.Apply(this, body);
    }

    private static Control Placed(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static CheckBox Check(string label, bool value)
    {
        var box = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        Bind(box, TemplatedControl.ForegroundProperty, "systemdialog.foreground.brush");
        return box;
    }

    private static Control Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    private static TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold };
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        return block;
    }
}
