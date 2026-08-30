using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;
using Mailbox.Junk;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Junk Email Options: the filter level and its switches, and the five lists — safe senders,
/// safe recipients, blocked senders, blocked top-level domains and blocked encodings.
/// </summary>
/// <remarks>
/// The reference's dialog, tab for tab. The level and the switches are settings; the lists live
/// in the account's store, because "who is safe" is a fact about one mailbox. Everything writes
/// as it goes, like every other dialog here — Add is added and Remove is removed — so OK and
/// Cancel both close.
/// </remarks>
public sealed class JunkOptionsDialog : Window
{
    private readonly MailRepository _mail;
    private readonly MailOptions _options;
    private TabControl? _tabs;

    /// <summary>Opens on a tab by index. Harness only: the tabs need a click otherwise.</summary>
    public void SelectTab(int index)
    {
        if (_tabs is not null) _tabs.SelectedIndex = Math.Clamp(index, 0, 4);
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public JunkOptionsDialog(MailRepository mail, MailOptions options)
    {
        _mail = mail;
        _options = options;

        Title = "Junk Email Options";
        Width = 560;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        DialogChrome.Apply(this, Layout());
    }

    private Control Layout()
    {
        var tabs = _tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "Options", Content = OptionsTab() },
                new TabItem
                {
                    Header = "Safe Senders",
                    Content = ListTab(
                        "Email from addresses or domain names on your Safe Senders List will never be "
                        + "treated as junk email.",
                        _mail.SafeSenders, _mail.AddSafeSender, _mail.RemoveSafeSender,
                        "safe-senders", extras: SafeSenderSwitches()),
                },
                new TabItem
                {
                    Header = "Safe Recipients",
                    Content = ListTab(
                        "Email sent to addresses or domain names on your Safe Recipients List will never "
                        + "be treated as junk email.",
                        _mail.SafeRecipients, _mail.AddSafeRecipient, _mail.RemoveSafeRecipient,
                        "safe-recipients"),
                },
                new TabItem
                {
                    Header = "Blocked Senders",
                    Content = ListTab(
                        "Email from addresses or domain names on your Blocked Senders List will always "
                        + "be treated as junk email.",
                        _mail.BlockedSenders, _mail.AddBlockedSender, _mail.RemoveBlockedSender,
                        "blocked-senders"),
                },
                new TabItem { Header = "International", Content = InternationalTab() },
            },
        };

        var ok = new Button { Content = "OK", IsDefault = true, Width = 74 };
        ok.Click += (_, _) => Close();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 74 };
        cancel.Click += (_, _) => Close();

        return new DockPanel
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
                    Children = { ok, cancel },
                },
                tabs,
            },
        };
    }

    // ---- Options ---------------------------------------------------------------------------

    private Control OptionsTab()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };

        stack.Children.Add(Text(
            "Mailbox can move messages that appear to be junk email into the Junk Email folder.",
            subtle: false));
        stack.Children.Add(Text("Choose the level of junk email protection you want:", subtle: false));

        var levels = new (string Label, string Detail)[]
        {
            ("No Automatic Filtering.", "Mail from blocked senders is still moved to the Junk Email folder."),
            ("Low:", "Move the most obvious junk email to the Junk Email folder."),
            ("High:", "Most junk email is caught, but some regular mail may be caught as well. Check your Junk Email folder often."),
            ("Safe Lists Only:", "Only mail from people or domains on your Safe Senders List or Safe Recipients List will be delivered to your Inbox."),
        };

        for (var i = 0; i < levels.Length; i++)
        {
            var index = i;
            var radio = new RadioButton
            {
                GroupName = "junk-level",
                IsChecked = _options.JunkLevelIndex == index,
                Content = LevelLabel(levels[i].Label, levels[i].Detail),
                Margin = new Thickness(12, 0, 0, 0),
            };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true) _options.JunkLevelIndex = index;
            };
            stack.Children.Add(radio);
        }

        stack.Children.Add(new Panel { Height = 8 });

        stack.Children.Add(Check(
            "Permanently delete suspected junk email instead of moving it to the Junk Email folder",
            _options.DeleteSuspectedJunk, v => _options.DeleteSuspectedJunk = v));
        stack.Children.Add(Text(
            "Not recommended: a message the filter gets wrong is gone, and there is no folder to find it in.",
            subtle: true, indent: 28));
        stack.Children.Add(Check(
            "Disable links and other functionality in phishing messages. (recommended)",
            _options.DisableLinksInJunk, v => _options.DisableLinksInJunk = v));
        stack.Children.Add(Check(
            "Warn me about suspicious domain names in email addresses. (recommended)",
            _options.WarnAboutSuspiciousDomains, v => _options.WarnAboutSuspiciousDomains = v));

        return stack;
    }

    private Control LevelLabel(string label, string detail)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(label) { FontWeight = FontWeight.SemiBold });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run(" " + detail));
        Bind(text, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return text;
    }

    // ---- The lists -------------------------------------------------------------------------

    /// <summary>
    /// A list tab: the explanation, the list, Add / Edit / Remove, and Import / Export of a text
    /// file with one entry per line, which is the reference's format too.
    /// </summary>
    private Control ListTab(
        string explanation,
        Func<IReadOnlyList<string>> all,
        Action<string, DateTimeOffset> add,
        Action<string> remove,
        string fileStem,
        Control? extras = null)
    {
        var list = new ListBox { Height = 250 };
        Bind(list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((entry, _) =>
        {
            var text = new TextBlock { Text = entry, Margin = new Thickness(4, 2) };
            Bind(text, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            return text;
        });

        void Reload()
        {
            var selected = list.SelectedItem as string;
            var entries = all();
            list.ItemsSource = entries;
            list.SelectedItem = entries.FirstOrDefault(e => e == selected) ?? entries.FirstOrDefault();
        }

        var addButton = Action("Add…", async () =>
        {
            var entry = await Prompt.AskAsync(this, "Add address or domain", "Enter an email address or Internet domain name to be added to the list:");
            if (Normalise(entry) is not { } value) return;
            add(value, DateTimeOffset.UtcNow);
            Reload();
        });

        var edit = Action("Edit…", async () =>
        {
            if (list.SelectedItem is not string current) return;
            var entry = await Prompt.AskAsync(this, "Edit address or domain", "Email address or Internet domain name:", current);
            if (Normalise(entry) is not { } value || value == current) return;
            remove(current);
            add(value, DateTimeOffset.UtcNow);
            Reload();
        });

        var removeButton = Action("Remove", () =>
        {
            if (list.SelectedItem is not string current) return Task.CompletedTask;
            remove(current);
            Reload();
            return Task.CompletedTask;
        });

        var import = Action("Import from File…", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import list",
                AllowMultiple = false,
                FileTypeFilter = [ListFiles],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

            var added = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (Normalise(line) is { } value) { add(value, DateTimeOffset.UtcNow); added++; }
            }

            Reload();
            Log.Info($"Imported {added} entries into the {fileStem} list.");
        });

        var export = Action("Export to File…", async () =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export list",
                SuggestedFileName = fileStem + ".txt",
                DefaultExtension = "txt",
                FileTypeChoices = [ListFiles],
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            File.WriteAllLines(path, all());
        });

        var buttons = new StackPanel
        {
            Spacing = 6,
            Width = 150,
            Children = { addButton, edit, removeButton, new Panel { Height = 10 }, import, export },
        };

        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 10 };
        stack.Children.Add(Text(explanation, subtle: false));
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { new Border { Child = list, Width = 330 }, buttons },
        });
        if (extras is not null) stack.Children.Add(extras);

        Reload();
        return stack;
    }

    /// <summary>The Safe Senders tab's two switches under the list.</summary>
    private Control SafeSenderSwitches()
    {
        var contacts = Check("Also trust email from my Contacts", _options.TrustContacts, v => _options.TrustContacts = v);
        ToolTip.SetTip(contacts, "Contacts arrive with the People module; the switch is kept for it.");

        var auto = Check("Automatically add people I email to the Safe Senders List",
            _options.AutoAddRecipientsToSafeSenders, v => _options.AutoAddRecipientsToSafeSenders = v);

        return new StackPanel { Spacing = 6, Children = { contacts, auto } };
    }

    /// <summary>
    /// An address or a domain, lower-cased, or null for something that is neither. A domain is
    /// kept in the "@example.com" form the store matches on, whether it was typed with the @ or
    /// without.
    /// </summary>
    internal static string? Normalise(string? entry)
    {
        var text = entry?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text.Contains(' ')) return null;

        var at = text.IndexOf('@');
        if (at < 0) return text.Contains('.') ? "@" + text : null;
        if (at == 0) return text.Length > 1 && text.Contains('.') ? text : null;
        return at < text.Length - 1 && text.LastIndexOf('@') == at ? text : null;
    }

    private static FilePickerFileType ListFiles { get; } = new("Text files")
    {
        Patterns = ["*.txt"],
        MimeTypes = ["text/plain"],
    };

    // ---- International ---------------------------------------------------------------------

    private Control InternationalTab()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 12 };

        stack.Children.Add(Text(
            "Some email messages you receive might be written in languages you are unfamiliar with "
            + "and don't want to read. These messages can be marked as junk and moved to the Junk "
            + "Email folder.", subtle: false));

        stack.Children.Add(Text(
            "The Blocked Top-Level Domain List allows you to block all email addresses that end in a "
            + "specific top-level domain.", subtle: false));
        stack.Children.Add(Action("Blocked Top-Level Domain List…", async () =>
        {
            var chosen = await PickListDialog.PickAsync(this, "Blocked Top-Level Domain List",
                "Select one or more countries/regions to block:",
                JunkLists.TopLevelDomains.Select(t => new PickListDialog.Item($"{t.Code.ToUpperInvariant()} ({t.Country})", t.Code)).ToList(),
                _mail.BlockedTlds());
            if (chosen is not null) _mail.SetBlockedTlds(chosen, DateTimeOffset.UtcNow);
        }, width: 260));

        stack.Children.Add(Text(
            "The Blocked Encodings List allows you to block all email messages in a specific encoding.",
            subtle: false));
        stack.Children.Add(Action("Blocked Encodings List…", async () =>
        {
            var chosen = await PickListDialog.PickAsync(this, "Blocked Encodings List",
                "Select one or more encodings to block:",
                JunkLists.Encodings.Select(e => new PickListDialog.Item(e.Label, e.Charset)).ToList(),
                _mail.BlockedEncodings());
            if (chosen is not null) _mail.SetBlockedEncodings(chosen, DateTimeOffset.UtcNow);
        }, width: 260));

        return stack;
    }

    // ---- Building blocks -------------------------------------------------------------------

    private static Button Action(string label, Func<Task> run, double? width = null)
    {
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = width is null ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
        };
        if (width is { } w) button.Width = w;
        button.Click += async (_, _) => await run();
        return button;
    }

    private static Control Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        Bind(box, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
        return box;
    }

    private static TextBlock Text(string text, bool subtle, double indent = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, 0, 0, 0),
        };
        Bind(block, TextBlock.ForegroundProperty, subtle ? "dialog.foreground.subtle.brush" : "dialog.foreground.brush");
        return block;
    }
}
