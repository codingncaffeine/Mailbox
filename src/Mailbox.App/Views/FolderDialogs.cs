using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Mailbox.Core.Archive;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>Create New Folder: a name, what it holds, and where it goes in the tree.</summary>
public sealed class NewFolderDialog : Window
{
    /// <summary>The name and the parent chosen when OK was pressed, or null.</summary>
    public (string Name, long? ParentId)? Result { get; private set; }

    public NewFolderDialog(OpenAccount account, long? parentId)
    {
        Title = "Create New Folder";
        Width = 420;
        Height = 480;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var name = new TextBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var contains = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "Mail and Post Items" }, SelectedIndex = 0 };

        // Every folder, Drafts and the Outbox included: the Move and Copy picker beside this
        // one lists both, and two "where to put a folder" trees that disagree read as a bug.
        var folders = account.Mail.Folders(account.Account.Id).ToList();
        var ordered = new List<Folder?> { null };
        void Add(long? parent) { foreach (var f in folders.Where(f => f.ParentId == parent).OrderBy(f => f.Ordinal).ThenBy(f => f.Name)) { ordered.Add(f); Add(f.Id); } }
        Add(null);

        var tree = ViewDialogKit.SurfaceList(360, 220);
        tree.ItemTemplate = new FuncDataTemplate<object>((item, _) =>
        {
            var folder = item as Folder;
            var text = ViewDialogKit.SurfaceText(folder?.Name ?? account.Account.Address);
            text.Margin = new Thickness(folder is null ? 0 : (Depth(folder, folders) + 1) * 16, 0, 0, 0);

            // The account's own row stands for its top level and is set apart from the folders
            // under it, as the same row is in the picker beside this one.
            if (folder is null) text.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            return text;
        });
        tree.ItemsSource = ordered.Select(f => (object?)f ?? account.Account.Address).ToList();
        tree.SelectedIndex = Math.Max(0, ordered.FindIndex(f => f?.Id == parentId));

        var ok = ViewDialogKit.Ok(() =>
        {
            var typed = (name.Text ?? string.Empty).Trim();
            if (typed.Length == 0) return;
            var parent = tree.SelectedIndex > 0 && tree.SelectedIndex < ordered.Count ? ordered[tree.SelectedIndex]?.Id : null;
            Result = (typed, parent);
            Close();
        });

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                ViewDialogKit.Label("Name:"),
                name,
                ViewDialogKit.Label("Folder contains:"),
                contains,
                ViewDialogKit.Label("Select where to place the folder:"),
                tree,
                ViewDialogKit.Buttons(ok, ViewDialogKit.Cancel(this)),
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
        Opened += (_, _) => name.Focus();
    }

    internal static int Depth(Folder folder, IReadOnlyList<Folder> all)
    {
        var depth = 0;
        var parent = folder.ParentId;
        while (parent is { } id && all.FirstOrDefault(f => f.Id == id) is { } up) { depth++; parent = up.ParentId; }
        return depth;
    }
}

/// <summary>
/// One folder out of every account's tree — the reference's Go to Folder, Move Folder and Copy
/// Folder dialogs are this list under three titles, with New… beside OK and Cancel.
/// </summary>
/// <remarks>
/// A flat list indented by depth rather than a tree control, as Create New Folder's is, so the
/// dialog surface paints it. An account's own row stands for its top level: allowed as a place
/// to move or copy a folder to, and never as a place to go.
/// </remarks>
public sealed class FolderPickerDialog : Window
{
    /// <summary>What was chosen when OK was pressed: the account, and the folder or null for its top level.</summary>
    public (OpenAccount Account, Folder? Folder)? Result { get; private set; }

    private readonly IReadOnlyList<OpenAccount> _accounts;
    private readonly ListBox _list;
    private readonly bool _allowRoot;
    private readonly long? _exclude;
    private readonly string? _excludeAddress;
    private List<(OpenAccount Account, Folder? Folder, int Depth)> _rows = [];

    /// <param name="prompt">The line over the list — "Move the selected folder to the folder:" — or null for none.</param>
    /// <param name="allowRoot">Whether an account's own row can be chosen, as a destination can be and a place to go cannot.</param>
    /// <param name="exclude">A folder to leave out with everything under it: the one being moved or copied.</param>
    public FolderPickerDialog(
        string title, string? prompt, IReadOnlyList<OpenAccount> accounts,
        (OpenAccount Account, long? FolderId)? preselect, bool allowRoot,
        (OpenAccount Account, long FolderId)? exclude = null)
    {
        _accounts = accounts;
        _allowRoot = allowRoot;
        _exclude = exclude?.FolderId;
        _excludeAddress = exclude?.Account.Account.Address;

        Title = title;
        Width = 420;
        Height = 480;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // The list fills the dialog: 400 rows' worth without a prompt over it, 374 with one.
        _list = ViewDialogKit.SurfaceList(272, prompt is { Length: > 0 } ? 374 : 400);
        _list.ItemTemplate = new FuncDataTemplate<object>((item, _) =>
        {
            // The template is asked about a null item as the list settles; that row is nothing.
            if (item is not ValueTuple<OpenAccount, Folder?, int> row) return new TextBlock();
            var text = ViewDialogKit.SurfaceText(row.Item2?.Name ?? row.Item1.Account.Address);
            text.Margin = new Thickness(row.Item3 * 16, 0, 0, 0);
            if (row.Item2 is null) text.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            return text;
        });
        Fill(preselect);

        var ok = ViewDialogKit.Ok(() =>
        {
            if (Chosen() is not { } chosen) return;
            Result = chosen;
            Close();
        });
        _list.SelectionChanged += (_, _) => ok.IsEnabled = Chosen() is not null;
        _list.DoubleTapped += (_, _) => { if (Chosen() is { } chosen) { Result = chosen; Close(); } };
        ok.IsEnabled = Chosen() is not null;

        var make = new Button { Content = "New…", Width = 74 };
        make.Click += async (_, _) => await NewFolderAsync();

        var buttons = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(ViewDialogKit.Cancel(this));
        buttons.Children.Add(make);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
        Grid.SetColumn(_list, 0);
        columns.Children.Add(_list);
        Grid.SetColumn(buttons, 1);
        columns.Children.Add(buttons);

        var body = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
        if (prompt is { Length: > 0 }) body.Children.Add(ViewDialogKit.Label(prompt));
        body.Children.Add(columns);

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
        Opened += (_, _) => _list.Focus();
    }

    private (OpenAccount Account, Folder? Folder)? Chosen()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _rows.Count) return null;
        var row = _rows[_list.SelectedIndex];
        if (row.Folder is null && !_allowRoot) return null;
        return (row.Account, row.Folder);
    }

    /// <summary>Every account and its folders in tree order, the excluded subtree left out.</summary>
    private void Fill((OpenAccount Account, long? FolderId)? select)
    {
        var rows = new List<(OpenAccount Account, Folder? Folder, int Depth)>();
        foreach (var account in _accounts)
        {
            rows.Add((account, null, 0));
            var folders = account.Mail.Folders(account.Account.Id);
            var excluding = string.Equals(account.Account.Address, _excludeAddress, StringComparison.OrdinalIgnoreCase);

            void Add(long? parent, int depth)
            {
                foreach (var folder in folders.Where(f => f.ParentId == parent).OrderBy(f => f.Ordinal).ThenBy(f => f.Name))
                {
                    if (excluding && folder.Id == _exclude) continue;
                    rows.Add((account, folder, depth));
                    Add(folder.Id, depth + 1);
                }
            }

            Add(null, 1);
        }

        _rows = rows;
        _list.ItemsSource = rows.Cast<object>().ToList();

        var index = select is { } wanted
            ? rows.FindIndex(r =>
                string.Equals(r.Account.Account.Address, wanted.Account.Account.Address, StringComparison.OrdinalIgnoreCase)
                && r.Folder?.Id == wanted.FolderId)
            : -1;
        _list.SelectedIndex = index >= 0 ? index : 0;
        if (index >= 0) _list.ScrollIntoView(index);
    }

    /// <summary>New…: a folder under whatever is selected, made through the same dialog New Folder uses, then chosen.</summary>
    private async Task NewFolderAsync()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _rows.Count) return;
        var row = _rows[_list.SelectedIndex];

        var dialog = new NewFolderDialog(row.Account, row.Folder?.Id);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } wanted) return;

        var made = await MakeFolder(row.Account, wanted.Name, wanted.ParentId);
        if (made is null) return;
        Fill((row.Account, made.Id));
    }

    /// <summary>How a folder is made from here; the shell supplies it, because the server call is its business.</summary>
    public Func<OpenAccount, string, long?, Task<Folder?>> MakeFolder { get; set; } =
        (account, name, parent) => Task.FromResult<Folder?>(account.Mail.AddFolder(account.Account.Id, name, FolderRole.None, parent));
}

/// <summary>
/// Folder Properties: General — the name, what it is, where it is, how much is in it — and
/// AutoArchive, the folder's own choice.
/// </summary>
public sealed class FolderPropertiesDialog : Window
{
    /// <summary>The name as OK left it — the same as before when unchanged.</summary>
    public string? NewName { get; private set; }

    /// <summary>The AutoArchive choice as OK left it.</summary>
    public FolderArchivePolicy? Policy { get; private set; }

    public FolderPropertiesDialog(OpenAccount account, Folder folder, int startTab = 0)
    {
        Title = $"{folder.Name} Properties";
        Width = 460;
        Height = 440;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // General
        var name = new TextBox { Text = folder.Name, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = folder.Role == FolderRole.None };

        // One pass over the folder for both numbers. Asked twice, a folder of five thousand is
        // read twice off the dispatcher before the dialog draws, for two lines of one label.
        var mail = account.Mail.Messages(folder.Id, int.MaxValue);
        var unread = mail.Count(m => !m.IsRead);
        var bytes = mail.Sum(m => m.SizeBytes);
        var location = Location(account, folder);

        var general = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                Row("Name:", name),
                Row("Type:", ViewDialogKit.Label("Folder containing Mail and Post Items")),
                Row("Location:", ViewDialogKit.Label(location)),
                Row("Contents:", ViewDialogKit.Label($"{folder.Total:N0} item{(folder.Total == 1 ? "" : "s")}, {unread:N0} unread, {MailboxCleanupDialog.Size(bytes)}")),
                Row("On the server:", ViewDialogKit.Label(folder.ImapPath ?? "no — this folder is on this computer only")),
            },
        };

        // AutoArchive
        var panel = new FolderAutoArchivePanel(FolderArchivePolicy.FromJson(account.Mail.FolderAutoArchive(folder.Id)))
        {
            Margin = new Thickness(0, 8, 0, 0),
        };

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "General", Content = general },
                new TabItem { Header = "AutoArchive", Content = panel },
            },
            SelectedIndex = Math.Clamp(startTab, 0, 1),
        };

        var ok = ViewDialogKit.Ok(() =>
        {
            NewName = (name.Text ?? string.Empty).Trim() is { Length: > 0 } typed ? typed : folder.Name;
            Policy = panel.Policy;
            Close();
        });

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Children = { ViewDialogKit.Buttons(ok, ViewDialogKit.Cancel(this)) } },
                tabs,
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    private static Control Row(string label, Control control)
    {
        var caption = ViewDialogKit.Label(label);
        caption.Width = 110;
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { caption, control } };
    }

    /// <summary>"Account › Parent › Folder", as the reference writes a location.</summary>
    private static string Location(OpenAccount account, Folder folder)
    {
        var all = account.Mail.Folders(account.Account.Id);
        var parts = new List<string>();
        var parent = folder.ParentId;
        while (parent is { } id && all.FirstOrDefault(f => f.Id == id) is { } up) { parts.Insert(0, up.Name); parent = up.ParentId; }
        parts.Insert(0, account.Account.Address);
        return string.Join(" › ", parts);
    }
}
