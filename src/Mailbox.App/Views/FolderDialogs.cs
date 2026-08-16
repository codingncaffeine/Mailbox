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

        var folders = account.Mail.Folders(account.Account.Id).Where(f => f.Role is not (FolderRole.Outbox or FolderRole.Drafts)).ToList();
        var ordered = new List<Folder?> { null };
        void Add(long? parent) { foreach (var f in folders.Where(f => f.ParentId == parent).OrderBy(f => f.Ordinal).ThenBy(f => f.Name)) { ordered.Add(f); Add(f.Id); } }
        Add(null);

        var tree = ViewDialogKit.SurfaceList(360, 220);
        tree.ItemTemplate = new FuncDataTemplate<object>((item, _) =>
        {
            var folder = item as Folder;
            var text = ViewDialogKit.SurfaceText(folder?.Name ?? account.Account.Address);
            text.Margin = new Thickness(folder is null ? 0 : (Depth(folder, folders) + 1) * 16, 0, 0, 0);
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
        var unread = account.Mail.Messages(folder.Id, int.MaxValue).Count(m => !m.IsRead);
        var bytes = account.Mail.Messages(folder.Id, int.MaxValue).Sum(m => m.SizeBytes);
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
