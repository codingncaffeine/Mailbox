using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Mailbox.Core.Settings;
using Mailbox.Editor;

namespace Mailbox.App.Views;

/// <summary>
/// AutoCorrect — the dialog behind Editor Options' AutoCorrect Options… button.
/// </summary>
/// <remarks>
/// The reference's three tabs that mean anything here: AutoCorrect, which is about words;
/// AutoFormat As You Type, which is about marks and paragraphs; and Math AutoCorrect, whose
/// table is the only part of it that can apply where there are no equations. Its fourth tab,
/// Actions, is smart tags reaching a web service, and is not offered at all.
/// <para>
/// Every switch writes as it goes, as the rest of Editor Options does, and a message already
/// being written picks the change up on its next word. Two of the reference's own switches say
/// what is absent instead of pretending: ordinals want a superscript the editor does not carry,
/// and heading styles want named styles it does not have either.
/// </para>
/// </remarks>
public sealed class AutocorrectDialog : Window
{
    private readonly SettingsStore _settings;
    private readonly AutocorrectTable _table;
    private readonly AutocorrectExceptions _exceptions;

    private readonly ListBox _rows = ViewDialogKit.SurfaceList(430, 190);
    private readonly TextBox _replace = new() { Width = 190 };
    private readonly TextBox _with = new() { Width = 230 };
    private readonly Button _add = new() { Content = "Add", Width = 74, IsEnabled = false };
    private readonly Button _delete = new() { Content = "Delete", Width = 74, IsEnabled = false };

    public AutocorrectDialog(SettingsStore settings, int tab = 0)
    {
        _settings = settings;
        _table = AutocorrectTable.FromJson(settings.GetString(MailOptions.AutocorrectTableKey));
        _exceptions = AutocorrectExceptions.FromJson(settings.GetString(MailOptions.AutocorrectExceptionsKey));

        Title = "AutoCorrect";
        Width = 640;
        Height = 700;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "AutoCorrect", Content = new ScrollViewer { Content = WordsTab() } },
                new TabItem { Header = "AutoFormat As You Type", Content = new ScrollViewer { Content = MarksTab() } },
                new TabItem { Header = "Math AutoCorrect", Content = new ScrollViewer { Content = MathTab() } },
            },
            SelectedIndex = Math.Clamp(tab, 0, 2),
        };

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                ViewDialogKit.Buttons(ViewDialogKit.Ok(Close), ViewDialogKit.Cancel(this)),
                tabs,
            },
        };

        body.Children[0][DockPanel.DockProperty] = Dock.Bottom;

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    // ---- AutoCorrect ---------------------------------------------------------------------

    private Control WordsTab()
    {
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(14) };

        // The reference's first switch is the little button that appears beside a correction.
        // There is none here, and what replaces it is the undo the correction already collapses
        // into: one Ctrl+Z puts back exactly what was typed.
        var buttons = ViewDialogKit.Ink(new CheckBox
        {
            Content = "Show AutoCorrect Options buttons",
            IsChecked = false,
            IsEnabled = false,
        });

        ToolTip.SetTip(buttons,
            "The button the reference shows beside a correction is not drawn. Ctrl+Z immediately "
            + "after a correction puts back what you typed, in one press.");

        stack.Children.Add(buttons);

        stack.Children.Add(Row(
            Switch("Correct TWo INitial CApitals", MailOptions.AutocorrectTwoInitialsKey),
            Exceptions("Exceptions…", initialCaps: true)));

        stack.Children.Add(Switch("Capitalize first letter of sentences", MailOptions.AutocorrectSentencesKey));
        stack.Children.Add(Switch("Capitalize first letter of table cells", MailOptions.AutocorrectTableCellsKey));
        stack.Children.Add(Switch("Capitalize names of days", MailOptions.AutocorrectDaysKey));
        stack.Children.Add(Switch("Correct accidental usage of cAPS LOCK key", MailOptions.AutocorrectCapsLockKey));

        stack.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 4) });
        stack.Children.Add(Switch("Replace text as you type", MailOptions.AutocorrectReplaceKey));

        var replaceLabel = ViewDialogKit.Label("Replace:");
        var withLabel = ViewDialogKit.Label("With:");
        replaceLabel.Width = 60;
        withLabel.Width = 44;

        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { replaceLabel, _replace, withLabel, _with },
        });

        _rows.ItemTemplate = new FuncDataTemplate<AutocorrectEntry>((entry, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            Children =
            {
                ViewDialogKit.SurfaceText(entry?.Replace ?? string.Empty),
                Cell(ViewDialogKit.SurfaceText(entry?.With ?? string.Empty), 1),
            },
        });

        _rows.SelectionChanged += (_, _) =>
        {
            if (_rows.SelectedItem is not AutocorrectEntry entry) return;

            _replace.Text = entry.Replace;
            _with.Text = entry.With;
            _delete.IsEnabled = true;
        };

        _replace.TextChanged += (_, _) => Typed();
        _with.TextChanged += (_, _) => Typed();

        _add.Click += (_, _) =>
        {
            _table.Add(_replace.Text ?? string.Empty, _with.Text ?? string.Empty);
            Save();
            Fill();
            _replace.Text = string.Empty;
            _with.Text = string.Empty;
        };

        _delete.Click += (_, _) =>
        {
            if (_rows.SelectedItem is not AutocorrectEntry entry) return;

            _table.Remove(entry.Replace);
            Save();
            Fill();
        };

        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                _rows,
                new StackPanel { Spacing = 6, Children = { _add, _delete } },
            },
        });

        stack.Children.Add(Switch(
            "Automatically use suggestions from the spelling checker", MailOptions.AutocorrectSuggestionsKey));

        stack.Children.Add(ViewDialogKit.Label(
            "Your own rows sit on top of this build's, so a later version's corrections still "
            + "reach you. The checker covers what a list cannot.",
            subtle: true));

        Fill();
        return stack;
    }

    // ---- AutoFormat As You Type ----------------------------------------------------------

    private Control MarksTab()
    {
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(14) };

        stack.Children.Add(ViewDialogKit.Label("Replace as you type", bold: true));
        stack.Children.Add(Switch("\"Straight quotes\" with \"smart quotes\"", MailOptions.AutoformatQuotesKey));

        var ordinals = ViewDialogKit.Ink(new CheckBox
        {
            Content = "Ordinals (1st) with superscript",
            IsChecked = false,
            IsEnabled = false,
        });

        ToolTip.SetTip(ordinals, "The editor has no superscript, so there is nothing to raise the letters into.");
        stack.Children.Add(ordinals);

        stack.Children.Add(Switch("Fractions (1/2) with fraction character", MailOptions.AutoformatFractionsKey));
        stack.Children.Add(Switch("Hyphens (--) with dash (—)", MailOptions.AutoformatDashesKey));
        stack.Children.Add(Switch("*Bold* and _italic_ with real formatting", MailOptions.AutoformatEmphasisKey));
        stack.Children.Add(Switch("Internet and network paths with hyperlinks", MailOptions.AutoformatHyperlinksKey));

        var apply = ViewDialogKit.Label("Apply as you type", bold: true);
        apply.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(apply);
        stack.Children.Add(Switch("Automatic bulleted lists", MailOptions.AutoformatBulletsKey));
        stack.Children.Add(Switch("Automatic numbered lists", MailOptions.AutoformatNumberingKey));
        stack.Children.Add(Switch("Border lines", MailOptions.AutoformatBordersKey));

        var headings = ViewDialogKit.Ink(new CheckBox
        {
            Content = "Built-in heading styles",
            IsChecked = false,
            IsEnabled = false,
        });

        ToolTip.SetTip(headings,
            "The editor carries headings but no named styles, so there is no style for this to apply.");

        stack.Children.Add(headings);

        var note = ViewDialogKit.Label(
            "Border lines: three or more hyphens on a line of their own, then Return, draws a "
            + "rule across the page — a divider, which is this editor's version of the border "
            + "the reference draws.",
            subtle: true);

        note.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(note);

        return stack;
    }

    // ---- Math AutoCorrect ----------------------------------------------------------------

    private Control MathTab()
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(14) };

        stack.Children.Add(Switch(
            "Use Math AutoCorrect rules outside of math regions", MailOptions.AutocorrectMathKey));

        stack.Children.Add(ViewDialogKit.Label(
            "There are no equations here — the editor has no equation model — so this switch is "
            + "the whole of the tab: with it on, the names below become their characters as you "
            + "type, anywhere in a message.",
            subtle: true));

        var list = ViewDialogKit.SurfaceList(560, 330);
        list.ItemTemplate = new FuncDataTemplate<AutocorrectEntry>((entry, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            Children =
            {
                ViewDialogKit.SurfaceText(entry?.Replace ?? string.Empty),
                Cell(ViewDialogKit.SurfaceText(entry?.With ?? string.Empty), 1),
            },
        });

        list.ItemsSource = AutocorrectTable.MathDefaults;
        stack.Children.Add(list);

        return stack;
    }

    // ---- The pieces ----------------------------------------------------------------------

    private void Fill()
    {
        _rows.ItemsSource = _table.Entries;
        _delete.IsEnabled = _rows.SelectedItem is AutocorrectEntry;
    }

    private void Typed()
    {
        var replace = (_replace.Text ?? string.Empty).Trim();
        var with = _with.Text ?? string.Empty;

        _add.IsEnabled = replace.Length > 0 && with.Length > 0 && replace != with;
        _add.Content = _table.Lookup(replace) is null ? "Add" : "Replace";
    }

    private void Save() => _settings.Set(MailOptions.AutocorrectTableKey, _table.ToJson());

    private void SaveExceptions() =>
        _settings.Set(MailOptions.AutocorrectExceptionsKey, _exceptions.ToJson());

    private CheckBox Switch(string label, string key)
    {
        var box = ViewDialogKit.Ink(new CheckBox
        {
            Content = label,
            IsChecked = _settings.GetBool(key, key != MailOptions.AutocorrectMathKey),
        });

        box.IsCheckedChanged += (_, _) => _settings.Set(key, box.IsChecked == true);
        return box;
    }

    private Button Exceptions(string label, bool initialCaps)
    {
        var button = new Button { Content = label, Width = 110 };
        button.Click += async (_, _) =>
            await new AutocorrectExceptionsDialog(_exceptions, SaveExceptions, initialCaps ? 1 : 0)
                .ShowDialog(this);

        return button;
    }

    private static Control Row(Control left, Control right) => new DockPanel
    {
        Children = { Docked(right, Dock.Right), left },
    };

    private static Control Docked(Control control, Dock dock)
    {
        control[DockPanel.DockProperty] = dock;
        return control;
    }

    private static Control Cell(Control control, int column)
    {
        control[Grid.ColumnProperty] = column;
        return control;
    }
}

/// <summary>
/// AutoCorrect Exceptions — the reference's two lists, under the names it gives them.
/// </summary>
/// <remarks>
/// The reference has a third tab, Other Corrections, which excepts words from the Replace/With
/// table. There is no such tab here because deleting the row is the same thing and the dialog
/// behind this one already does it — a list of exceptions to a list the reader can edit is two
/// ways of saying one thing.
/// </remarks>
public sealed class AutocorrectExceptionsDialog : Window
{
    public AutocorrectExceptionsDialog(AutocorrectExceptions exceptions, Action save, int tab = 0)
    {
        Title = "AutoCorrect Exceptions";
        Width = 460;
        Height = 440;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem
                {
                    Header = "First Letter",
                    Content = List(
                        "Don't capitalize after:",
                        () => exceptions.FirstLetter,
                        word => exceptions.AddFirstLetter(word),
                        word => exceptions.RemoveFirstLetter(word),
                        save,
                        "A word ending in a full stop that does not end a sentence: etc., e.g., Mr."),
                },
                new TabItem
                {
                    Header = "INitial CAps",
                    Content = List(
                        "Don't correct:",
                        () => exceptions.InitialCaps,
                        word => exceptions.AddInitialCaps(word),
                        word => exceptions.RemoveInitialCaps(word),
                        save,
                        "A word that begins with two capitals on purpose: IDs, PhDs, TVs."),
                },
            },
            SelectedIndex = Math.Clamp(tab, 0, 1),
        };

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children = { tabs },
        };

        var buttons = ViewDialogKit.Buttons(ViewDialogKit.Ok(Close), ViewDialogKit.Cancel(this));
        buttons[DockPanel.DockProperty] = Dock.Bottom;
        body.Children.Insert(0, buttons);

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    private static Control List(
        string caption,
        Func<IReadOnlyList<string>> read,
        Func<string, bool> add,
        Func<string, bool> remove,
        Action save,
        string note)
    {
        var list = ViewDialogKit.SurfaceList(280, 230);
        list.ItemTemplate = new FuncDataTemplate<object>(
            (item, _) => ViewDialogKit.SurfaceText(item?.ToString() ?? string.Empty));

        var entry = new TextBox { Width = 280 };
        var addButton = new Button { Content = "Add", Width = 74, IsEnabled = false };
        var removeButton = new Button { Content = "Delete", Width = 74, IsEnabled = false };

        void Fill() => list.ItemsSource = read().Cast<object>().ToList();

        entry.TextChanged += (_, _) => addButton.IsEnabled = (entry.Text ?? string.Empty).Trim().Length > 0;
        list.SelectionChanged += (_, _) => removeButton.IsEnabled = list.SelectedItem is string;

        addButton.Click += (_, _) =>
        {
            if (add((entry.Text ?? string.Empty).Trim()))
            {
                save();
                Fill();
            }

            entry.Text = string.Empty;
        };

        removeButton.Click += (_, _) =>
        {
            if (list.SelectedItem is string word && remove(word))
            {
                save();
                Fill();
            }
        };

        Fill();

        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(12),
            Children =
            {
                ViewDialogKit.Label(caption),
                entry,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { list, new StackPanel { Spacing = 6, Children = { addButton, removeButton } } },
                },
                ViewDialogKit.Label(note, subtle: true),
            },
        };
    }
}
