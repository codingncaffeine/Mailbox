using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Mailbox.Core.Settings;
using Mailbox.Store;
using Mailbox.Theming.Fonts;

namespace Mailbox.App.Views;

/// <summary>
/// Signatures and Stationery — the reference's one dialog behind both Signatures… and Stationery
/// and Fonts… on the Mail page, and the compose window's Signature menu: an E-mail Signature tab
/// over the signatures and which account signs with which, and a Personal Stationery tab over the
/// fonts new mail, replies and plain text are written in.
/// </summary>
/// <remarks>
/// Every control writes as it goes, like the Options pages; OK and Cancel both close. Two things
/// on the reference's second tab are not offered as they are there: Office's stationery themes
/// (Theme…) — a Word-era feature that made mail heavier and no more legible, and rule 4 lets it
/// stay unbuilt — and the two comment switches, which are kept but not yet acted on because
/// marking a comment inside quoted text needs the editor to know where the quote begins.
/// </remarks>
public sealed class StationeryDialog : Window
{
    private readonly Signatures _signatures;
    private readonly StationeryFonts _fonts;
    private readonly IReadOnlyList<OpenAccount> _accounts;

    private ListBox _list = null!;
    private TextBox _editor = null!;
    private ComboBox _forNew = null!;
    private ComboBox _forReply = null!;
    private ComboBox _account = null!;
    private string? _editing;

    /// <param name="tab">0 for E-mail Signature, 1 for Personal Stationery.</param>
    public StationeryDialog(Signatures signatures, StationeryFonts fonts, IReadOnlyList<OpenAccount> accounts, string? address = null, int tab = 0)
    {
        _signatures = signatures;
        _fonts = fonts;
        _accounts = accounts;

        Title = "Signatures and Stationery";
        Width = 700;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "E-mail Signature", Content = SignatureTab(address) },
                new TabItem { Header = "Personal Stationery", Content = StationeryTab() },
            },
            SelectedIndex = Math.Clamp(tab, 0, 1),
        };

        var ok = SystemInkKit.Ok(() => { SaveEditing(); Close(); });
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
                tabs,
            },
        };

        SystemDialogChrome.Apply(this, body);
    }

    // ---- E-mail Signature ----------------------------------------------------------------

    private Control SignatureTab(string? address)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };

        // Which account the defaults at the bottom belong to.
        _account = new ComboBox
        {
            ItemsSource = _accounts.Select(a => a.Account.Address).ToList(),
            MinWidth = 400,
        };
        var wanted = address is null ? -1 : _accounts.ToList().FindIndex(a => string.Equals(a.Account.Address, address, StringComparison.OrdinalIgnoreCase));
        _account.SelectedIndex = wanted >= 0 ? wanted : (_accounts.Count > 0 ? 0 : -1);
        _account.SelectionChanged += (_, _) => LoadDefaults();
        stack.Children.Add(Row("E-mail account:", _account, 110));

        stack.Children.Add(SystemInkKit.Label("Select signature to edit"));

        _list = SystemInkKit.SurfaceList(500, 84);
        _list.ItemTemplate = new FuncDataTemplate<object>((item, _) => SystemInkKit.SurfaceText(item?.ToString() ?? string.Empty));
        _list.SelectionChanged += (_, _) => { SaveEditing(); LoadEditing(); };

        var make = new Button { Content = "New", Width = 74 };
        make.Click += async (_, _) => await NewSignatureAsync();
        var delete = new Button { Content = "Delete", Width = 74 };
        delete.Click += async (_, _) => await DeleteSignatureAsync();
        var rename = new Button { Content = "Rename", Width = 74 };
        rename.Click += async (_, _) => await RenameSignatureAsync();

        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                _list,
                new StackPanel { Spacing = 6, Children = { make, delete, rename } },
            },
        });

        stack.Children.Add(SystemInkKit.Label("Edit signature"));
        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = 590,
            Height = 120,
            VerticalContentAlignment = VerticalAlignment.Top,
            Classes = { "sysfield" },
        };
        stack.Children.Add(_editor);

        var save = new Button { Content = "Save", Width = 74 };
        save.Click += (_, _) => SaveEditing();
        stack.Children.Add(save);

        stack.Children.Add(SystemInkKit.Label("Choose default signature", bold: true));
        _forNew = new ComboBox { MinWidth = 400 };
        _forReply = new ComboBox { MinWidth = 400 };
        _forNew.SelectionChanged += (_, _) => Choose(_forNew, _signatures.UseForNew);
        _forReply.SelectionChanged += (_, _) => Choose(_forReply, _signatures.UseForReply);
        stack.Children.Add(Row("New messages:", _forNew, 110));
        stack.Children.Add(Row("Replies/forwards:", _forReply, 110));

        FillList(select: _signatures.All.FirstOrDefault()?.Name);
        return stack;
    }

    private static Control Row(string label, Control control, double labelWidth)
    {
        var caption = SystemInkKit.Label(label);
        caption.Width = labelWidth;
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { caption, control } };
    }

    private void FillList(string? select)
    {
        var names = _signatures.All.Select(s => s.Name).ToList();
        _list.ItemsSource = names.Cast<object>().ToList();
        _list.SelectedIndex = select is null ? -1 : names.IndexOf(select);
        LoadEditing();
        LoadDefaults();
    }

    private void LoadEditing()
    {
        _editing = _list.SelectedItem as string;
        _editor.Text = _editing is null ? string.Empty : _signatures.Find(_editing)?.Text ?? string.Empty;
        _editor.IsEnabled = _editing is not null;
    }

    /// <summary>What is in the editor goes back to the signature it came from — on Save, on selecting another, on OK.</summary>
    private void SaveEditing()
    {
        if (_editing is null || _signatures.Find(_editing) is null) return;
        var text = _editor.Text ?? string.Empty;
        if (text == (_signatures.Find(_editing)?.Text ?? string.Empty)) return;
        _signatures.Save(new Signature { Name = _editing, Text = text, Html = SignatureEditor.AsHtml(text) });
    }

    private void LoadDefaults()
    {
        var address = _account.SelectedItem as string;
        var choices = new List<string> { "(none)" };
        choices.AddRange(_signatures.All.Select(s => s.Name));
        foreach (var (combo, chosen) in new[] { (_forNew, address is null ? null : _signatures.ForNew(address)), (_forReply, address is null ? null : _signatures.ForReply(address)) })
        {
            combo.ItemsSource = choices.ToList();
            combo.SelectedIndex = chosen is null ? 0 : Math.Max(0, choices.IndexOf(chosen.Name));
            combo.IsEnabled = address is not null;
        }
    }

    private void Choose(ComboBox combo, Action<string, string?> use)
    {
        if (_account.SelectedItem is not string address || combo.SelectedIndex < 0) return;
        use(address, combo.SelectedIndex == 0 ? null : combo.SelectedItem as string);
    }

    private async Task NewSignatureAsync()
    {
        SaveEditing();
        var name = await Prompt.AskAsync(this, "New Signature", "Type a name for this signature:", string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (_signatures.Find(name) is not null)
        {
            await Confirm.SayAsync(this, "New Signature", $"A signature named “{name}” already exists.");
            return;
        }

        _signatures.Save(new Signature { Name = name, Text = string.Empty, Html = string.Empty });
        FillList(select: name);
        _editor.Focus();
    }

    private async Task DeleteSignatureAsync()
    {
        if (_list.SelectedItem is not string name) return;
        var go = await Confirm.AskAsync(this, "Delete Signature", $"Are you sure you want to delete the “{name}” signature?", "Delete");
        if (!go) return;
        _editing = null;
        _signatures.Remove(name);
        FillList(select: _signatures.All.FirstOrDefault()?.Name);
    }

    private async Task RenameSignatureAsync()
    {
        if (_list.SelectedItem is not string name) return;
        SaveEditing();
        var renamed = await Prompt.AskAsync(this, "Rename Signature", "Type a new name for this signature:", name);
        if (string.IsNullOrWhiteSpace(renamed) || renamed.Trim() == name) return;
        renamed = renamed.Trim();
        if (_signatures.Find(renamed) is not null)
        {
            await Confirm.SayAsync(this, "Rename Signature", $"A signature named “{renamed}” already exists.");
            return;
        }

        var old = _signatures.Find(name)!;
        // The accounts that signed with the old name sign with the new one.
        var users = _accounts.Select(a => a.Account.Address)
            .Select(address => (address, forNew: _signatures.ForNew(address)?.Name == name, forReply: _signatures.ForReply(address)?.Name == name))
            .ToList();
        _signatures.Save(old with { Name = renamed });
        _signatures.Remove(name);
        foreach (var (address, forNew, forReply) in users)
        {
            if (forNew) _signatures.UseForNew(address, renamed);
            if (forReply) _signatures.UseForReply(address, renamed);
        }

        _editing = null;
        FillList(select: renamed);
    }

    // ---- Personal Stationery ---------------------------------------------------------------

    private Control StationeryTab()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 6 };

        stack.Children.Add(SystemInkKit.Label("Theme or stationery for new HTML e-mail message", bold: true));
        var theme = new Button { Content = "Theme…", Width = 90, IsEnabled = false };
        ToolTip.SetTip(theme, "Stationery themes are not offered: mail set in one is heavier and no more legible.");
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { theme, SystemInkKit.Label("No theme currently selected") },
        });
        var themeFont = new ComboBox { ItemsSource = new[] { "Use theme's font" }, SelectedIndex = 0, MinWidth = 200, IsEnabled = false };
        stack.Children.Add(Row("Font:", themeFont, 40));

        stack.Children.Add(FontRow("New mail messages", StationeryUse.NewMessages));

        stack.Children.Add(FontRow("Replying or forwarding messages", StationeryUse.Replies));
        var mark = SystemInkKit.Ink(new CheckBox { Content = "Mark my comments with:", IsChecked = _fonts.MarkComments });
        var markWith = new TextBox { Width = 300, Text = _fonts.MarkCommentsWith(_accounts.FirstOrDefault()?.Account.DisplayName ?? string.Empty), Classes = { "sysfield" } };
        mark.IsCheckedChanged += (_, _) => _fonts.MarkComments = mark.IsChecked == true;
        markWith.LostFocus += (_, _) => _fonts.SetMarkCommentsWith(markWith.Text ?? string.Empty);
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 0, 0, 0), Children = { mark, markWith } });
        var pick = SystemInkKit.Ink(new CheckBox { Content = "Pick a new color when replying or forwarding", IsChecked = _fonts.PickColourOnReply, Margin = new Thickness(20, 0, 0, 0) });
        pick.IsCheckedChanged += (_, _) => _fonts.PickColourOnReply = pick.IsChecked == true;
        stack.Children.Add(pick);
        stack.Children.Add(SystemInkKit.Label("Comments in a reply are not yet marked; the choices are kept for when they are.", subtle: true));

        stack.Children.Add(FontRow("Composing and reading plain text messages", StationeryUse.PlainText));
        return stack;
    }

    /// <summary>A heading, then Font… beside the sample drawn in the font — the reference's row, thrice.</summary>
    private Control FontRow(string heading, StationeryUse use)
    {
        var sample = new TextBlock
        {
            Text = "Sample Text",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = SystemInkKit.Boxed(sample, 400, 30);
        var summary = SystemInkKit.Label(string.Empty, subtle: true);

        void Draw()
        {
            var font = _fonts.Get(use);
            var resolved = App.Fonts.Resolve(font.Family);
            sample.FontFamily = BundledFonts.FamilyFor(resolved.Rendered);
            sample.FontSize = font.Points / 0.75;
            sample.FontWeight = font.Bold ? FontWeight.Bold : FontWeight.Normal;
            sample.FontStyle = font.Italic ? FontStyle.Italic : FontStyle.Normal;
            if (font.Colour is { } hex && Color.TryParse(hex, out var c)) sample.Foreground = new SolidColorBrush(c);
            else SystemInkKit.Bind(sample, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
            summary.Text = font.Summary + (font.Colour is null ? string.Empty : $", {font.Colour}");
        }

        var button = new Button { Content = "Font…", Width = 90 };
        button.Click += async (_, _) =>
        {
            var dialog = new FontDialog("Font", _fonts.Get(use), App.Fonts.InstalledFamilies);
            await dialog.ShowDialog(this);
            if (dialog.Result is { } chosen)
            {
                _fonts.Set(use, chosen);
                Draw();
            }
        };
        Draw();

        return new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                SystemInkKit.Label(heading, bold: true),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { button, box, summary } },
            },
        };
    }
}
