using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Mailbox.Core.Settings;
using Mailbox.Editor;

namespace Mailbox.App.Views;

/// <summary>
/// Editor Options — the reference's dialog behind Editor Options… and Spelling and Autocorrect…
/// on the Mail page, opened on its Proofing page: how the checker treats capitals, numbers,
/// addresses and repeated words, and the words it has been taught.
/// </summary>
/// <remarks>
/// The switches that act are the four the checker has and everything behind AutoCorrect
/// Options…; the rest of the reference's page says what it waits on rather than pretending —
/// there is no grammar checker, and the editor cannot underline as you type.
/// Accessibility and Advanced are the reference's other two pages, and both are notes here
/// until their phases. Every switch writes as it goes; OK and Cancel both close.
/// </remarks>
public sealed class EditorOptionsDialog : Window
{
    private readonly SettingsStore _settings;
    private readonly ContentControl _page = new();
    private readonly StackPanel _rail = new() { Spacing = 2 };
    private string _selected = "proofing";

    public EditorOptionsDialog(MailOptions options, SettingsStore settings)
    {
        _settings = settings;

        Title = "Editor Options";
        Width = 820;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var railBox = SystemInkKit.Boxed(_rail, 120);
        railBox.Padding = new Thickness(4);
        railBox.VerticalAlignment = VerticalAlignment.Top;
        BuildRail();
        ShowPage();

        var ok = SystemInkKit.Ok(Close);
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
                    Children = { ok, SystemInkKit.Cancel(this) },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children = { railBox, _page },
                },
            },
        };

        SystemDialogChrome.Apply(this, body);
    }

    private void BuildRail()
    {
        _rail.Children.Clear();
        foreach (var (id, title) in new[] { ("proofing", "Proofing"), ("accessibility", "Accessibility"), ("advanced", "Advanced") })
        {
            var selected = id == _selected;
            var text = SystemInkKit.SurfaceText(title);
            text.Margin = new Thickness(8, 0);
            var button = new Button
            {
                Content = text,
                Height = 27,
                Padding = default,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                BorderThickness = new Thickness(selected ? 1 : 0),
                Background = Brushes.Transparent,
            };
            if (selected)
            {
                SystemInkKit.Bind(button, BorderBrushProperty, "systemdialog.border.brush");
                SystemInkKit.Bind(button, BackgroundProperty, "systemdialog.selection.focused.brush");
            }

            var target = id;
            button.Click += (_, _) => { _selected = target; BuildRail(); ShowPage(); };
            _rail.Children.Add(button);
        }
    }

    private void ShowPage()
    {
        _page.Content = _selected switch
        {
            "accessibility" => Note("Accessibility",
                "Make e-mail messages more accessible.",
                "The editor's accessibility options wait on the accessibility work the whole "
                + "application waits on: automation peers, screen reader traversal, focus order."),
            "advanced" => Note("Advanced",
                "Advanced options for working with the editor.",
                "Editing options — typing replaces selected text, smart cut and paste, overtype — wait on the editor exposing them."),
            _ => ProofingPage(),
        };
    }

    private static Control Note(string title, string heading, string note)
    {
        var stack = new StackPanel { Spacing = 10, Width = 620 };
        stack.Children.Add(SystemInkKit.Label(heading, bold: true));
        stack.Children.Add(SystemInkKit.Label(note, subtle: true));
        return stack;
    }

    private Control ProofingPage()
    {
        var stack = new StackPanel { Spacing = 8, Width = 620 };

        stack.Children.Add(SystemInkKit.Label("Specify how Mailbox corrects and formats the contents of your e-mails.", bold: true));

        stack.Children.Add(Section("AutoCorrect options"));

        var autocorrect = new Button { Content = "AutoCorrect Options…" };
        autocorrect.Click += async (_, _) => await new AutocorrectDialog(_settings).ShowDialog(this);
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { SystemInkKit.Label("Specify how Mailbox corrects and formats text as you type."), autocorrect },
        });

        stack.Children.Add(Section("When correcting spelling in Mailbox"));
        stack.Children.Add(Switch("Ignore words in UPPERCASE", MailOptions.IgnoreUppercaseKey, true));
        stack.Children.Add(Switch("Ignore words that contain numbers", MailOptions.IgnoreNumbersKey, true));
        stack.Children.Add(Switch("Ignore Internet and file addresses", MailOptions.IgnoreAddressesKey, true));
        stack.Children.Add(Switch("Flag repeated words", MailOptions.FlagRepeatedKey, true));

        var dictionaries = new Button { Content = "Custom Dictionaries…", Margin = new Thickness(14, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        dictionaries.Click += async (_, _) => await new CustomDictionariesDialog().ShowDialog(this);
        stack.Children.Add(dictionaries);
        stack.Children.Add(Row("Dictionary language:", DictionaryCombo()));

        stack.Children.Add(Section("When correcting spelling in messages"));
        var asYouType = SystemInkKit.Ink(new CheckBox { Content = "Check spelling as you type", IsChecked = false, IsEnabled = false, Margin = new Thickness(14, 0, 0, 0) });
        ToolTip.SetTip(asYouType, "The editor cannot underline a word as it is typed; F7 checks the whole message, and Send can check before it goes.");
        stack.Children.Add(asYouType);
        var grammar = SystemInkKit.Ink(new CheckBox { Content = "Mark grammar errors as you type", IsChecked = false, IsEnabled = false, Margin = new Thickness(14, 0, 0, 0) });
        ToolTip.SetTip(grammar, "There is no grammar checker.");
        stack.Children.Add(grammar);
        stack.Children.Add(Switch("Always check spelling before sending", MailOptions.CheckSpellingBeforeSendKey, false));
        stack.Children.Add(Switch("Ignore original message text in reply or forward", MailOptions.IgnoreOriginalSpellingKey, true));
        stack.Children.Add(SystemInkKit.Label("Spelling runs against the desktop's own Hunspell dictionaries; F7 checks a message, and the words you add are kept beside your mail.", subtle: true));

        return stack;
    }

    private static Control Section(string title)
    {
        var label = SystemInkKit.Label(title, bold: true);
        var rule = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 4) };
        SystemInkKit.Bind(rule, Border.BackgroundProperty, "systemdialog.border.brush");
        return new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { label, rule } };
    }

    private Control Switch(string label, string key, bool fallback)
    {
        var box = SystemInkKit.Ink(new CheckBox { Content = label, IsChecked = _settings.GetBool(key, fallback), Margin = new Thickness(14, 0, 0, 0) });
        box.IsCheckedChanged += (_, _) => _settings.Set(key, box.IsChecked == true);
        return box;
    }

    private static Control Row(string label, Control control)
    {
        var caption = SystemInkKit.Label(label);
        caption.Width = 130;
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 0, 0, 0), Children = { caption, control } };
    }

    /// <summary>The dictionaries this machine has, with the one the checker would pick first.</summary>
    private static Control DictionaryCombo()
    {
        var available = SpellCheck.Available();
        var combo = new ComboBox
        {
            ItemsSource = available.Count == 0 ? ["(no dictionaries installed)"] : available.ToList(),
            SelectedIndex = 0,
            MinWidth = 220,
            IsEnabled = false,
        };
        ToolTip.SetTip(combo, "The checker takes the dictionary that matches the desktop's language, or the first it finds; choosing another is not yet offered.");
        return combo;
    }
}

/// <summary>Custom Dictionaries: the words the reader has taught the checker, and a way to take one back.</summary>
public sealed class CustomDictionariesDialog : Window
{
    public CustomDictionariesDialog()
    {
        Title = "Custom Dictionaries";
        Width = 420;
        Height = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = SystemInkKit.SurfaceList(272, 260);
        list.ItemTemplate = new FuncDataTemplate<object>((item, _) => SystemInkKit.SurfaceText(item?.ToString() ?? string.Empty));
        var remove = new Button { Content = "Delete", Width = 74, IsEnabled = false };
        var count = SystemInkKit.Label(string.Empty, subtle: true);

        SpellCheck? spelling = null;

        void Fill()
        {
            var words = spelling?.PersonalWords ?? [];
            list.ItemsSource = words.Cast<object>().ToList();
            count.Text = words.Count == 0 ? "No words have been added yet." : $"{words.Count} word{(words.Count == 1 ? "" : "s")}, kept beside your mail.";
        }

        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is string;
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is string word && spelling?.Remove(word) == true) Fill();
        };

        Opened += async (_, _) =>
        {
            spelling = await SpellCheck.LoadAsync(personalPath: ComposeSurface.PersonalDictionaryPath());
            Fill();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                SystemInkKit.Label("Words you have added to the dictionary:"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { list, new StackPanel { Spacing = 6, Children = { remove } } },
                },
                count,
                SystemInkKit.Buttons(SystemInkKit.Ok(Close), SystemInkKit.Cancel(this)),
            },
        };

        SystemDialogChrome.Apply(this, body);
    }
}
