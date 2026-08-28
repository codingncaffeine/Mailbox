using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Mailbox.Core.Archive;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// AutoArchive Settings — Options › Advanced's button: how often, whether to ask, what to do
/// with expired and old items, how old is old, and where old mail goes. "Apply these settings
/// to all folders now" clears every folder's own choice.
/// </summary>
public sealed class AutoArchiveSettingsDialog : Window
{

    /// <summary>True when OK saved something.</summary>
    public bool Saved { get; private set; }

    /// <summary>True when "Apply these settings to all folders now" was pressed.</summary>
    public bool AppliedToAllFolders { get; private set; }

    public AutoArchiveSettingsDialog(AutoArchiveOptions options)
    {

        Title = "AutoArchive";
        Width = 520;
        Height = 470;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var run = ViewDialogKit.Ink(new CheckBox { Content = "Run AutoArchive every", IsChecked = options.Enabled });
        var every = new NumericUpDown { Width = 80, Minimum = 1, Maximum = 60, Value = options.EveryDays };
        var prompt = ViewDialogKit.Ink(new CheckBox { Content = "Prompt before AutoArchive runs", IsChecked = options.Prompt, Margin = new Thickness(24, 0, 0, 0) });
        var expired = ViewDialogKit.Ink(new CheckBox { Content = "Delete expired items (email folders only)", IsChecked = options.DeleteExpired, Margin = new Thickness(24, 0, 0, 0) });
        var old = ViewDialogKit.Ink(new CheckBox { Content = "Archive or delete old items", IsChecked = options.ArchiveOld, Margin = new Thickness(24, 0, 0, 0) });
        var olderThan = new NumericUpDown { Width = 80, Minimum = 1, Maximum = 999, Value = options.OlderThan };
        var unit = new ComboBox { Width = 110, ItemsSource = new[] { "days", "weeks", "months" }, SelectedIndex = (int)options.Unit };
        var move = ViewDialogKit.Ink(new RadioButton { Content = "Move old items to the account's Archive folder", GroupName = "action", IsChecked = options.Action == ArchiveAction.Move });
        var delete = ViewDialogKit.Ink(new RadioButton { Content = "Permanently delete old items", GroupName = "action", IsChecked = options.Action == ArchiveAction.Delete });

        void Enable()
        {
            var on = run.IsChecked == true;
            every.IsEnabled = on;
            prompt.IsEnabled = on;
            expired.IsEnabled = on;
            old.IsEnabled = on;
            var archiving = on && old.IsChecked == true;
            olderThan.IsEnabled = archiving;
            unit.IsEnabled = archiving;
            move.IsEnabled = archiving;
            delete.IsEnabled = archiving;
        }

        run.IsCheckedChanged += (_, _) => Enable();
        old.IsCheckedChanged += (_, _) => Enable();
        Enable();

        void Save()
        {
            options.Enabled = run.IsChecked == true;
            options.EveryDays = (int)(every.Value ?? 14);
            options.Prompt = prompt.IsChecked == true;
            options.DeleteExpired = expired.IsChecked == true;
            options.ArchiveOld = old.IsChecked == true;
            options.OlderThan = (int)(olderThan.Value ?? 6);
            options.Unit = (ArchiveUnit)Math.Max(0, unit.SelectedIndex);
            options.Action = delete.IsChecked == true ? ArchiveAction.Delete : ArchiveAction.Move;
            Saved = true;
        }

        var applyAll = new Button { Content = "Apply these settings to all folders now", Padding = new Thickness(10, 4), Margin = new Thickness(24, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        applyAll.Click += (_, _) =>
        {
            Save();
            foreach (var account in App.Accounts.All)
            {
                foreach (var folder in account.Mail.Folders(account.Account.Id)) account.Mail.SetFolderAutoArchive(folder.Id, null);
            }

            AppliedToAllFolders = true;
        };

        var note = ViewDialogKit.Label("To give a folder its own settings, right-click it and choose Properties, then AutoArchive.", subtle: true);
        note.Margin = new Thickness(24, 4, 0, 0);
        note.MaxWidth = 440;
        note.HorizontalAlignment = HorizontalAlignment.Left;

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { run, every, ViewDialogKit.Label("days") } },
                prompt,
                Section("During AutoArchive:"),
                expired,
                old,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(48, 0, 0, 0), Children = { ViewDialogKit.Label("Clean out items older than"), olderThan, unit } },
                new StackPanel { Margin = new Thickness(48, 0, 0, 0), Spacing = 4, Children = { move, delete } },
                applyAll,
                note,
                ViewDialogKit.Buttons(ViewDialogKit.Ok(() => { Save(); Close(); }), ViewDialogKit.Cancel(this)),
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    private static TextBlock Section(string text)
    {
        var block = ViewDialogKit.Label(text, bold: true);
        block.Margin = new Thickness(0, 6, 0, 0);
        return block;
    }
}

/// <summary>
/// A folder's own AutoArchive choice — the reference's folder Properties › AutoArchive tab —
/// as a panel, so Folder Properties can host it and the dialog below can stand alone.
/// </summary>
public sealed class FolderAutoArchivePanel : StackPanel
{
    private readonly RadioButton _off;
    private readonly RadioButton _defaults;
    private readonly RadioButton _custom;
    private readonly NumericUpDown _olderThan;
    private readonly ComboBox _unit;
    private readonly RadioButton _move;
    private readonly RadioButton _delete;

    public FolderAutoArchivePanel(FolderArchivePolicy policy)
    {
        Spacing = 8;

        _off = ViewDialogKit.Ink(new RadioButton { Content = "Do not archive items in this folder", GroupName = "foldermode", IsChecked = policy.Mode == FolderArchiveMode.Off });
        _defaults = ViewDialogKit.Ink(new RadioButton { Content = "Archive items in this folder using the default settings", GroupName = "foldermode", IsChecked = policy.Mode == FolderArchiveMode.Default });
        _custom = ViewDialogKit.Ink(new RadioButton { Content = "Archive this folder using these settings:", GroupName = "foldermode", IsChecked = policy.Mode == FolderArchiveMode.Custom });
        _olderThan = new NumericUpDown { Width = 80, Minimum = 1, Maximum = 999, Value = policy.OlderThan };
        _unit = new ComboBox { Width = 110, ItemsSource = new[] { "days", "weeks", "months" }, SelectedIndex = (int)policy.Unit };
        _move = ViewDialogKit.Ink(new RadioButton { Content = "Move old items to the account's Archive folder", GroupName = "folderaction", IsChecked = policy.Action == ArchiveAction.Move });
        _delete = ViewDialogKit.Ink(new RadioButton { Content = "Permanently delete old items", GroupName = "folderaction", IsChecked = policy.Action == ArchiveAction.Delete });

        void Enable()
        {
            var custom = _custom.IsChecked == true;
            _olderThan.IsEnabled = custom;
            _unit.IsEnabled = custom;
            _move.IsEnabled = custom;
            _delete.IsEnabled = custom;
        }

        _off.IsCheckedChanged += (_, _) => Enable();
        _defaults.IsCheckedChanged += (_, _) => Enable();
        _custom.IsCheckedChanged += (_, _) => Enable();
        Enable();

        Children.Add(_off);
        Children.Add(_defaults);
        Children.Add(_custom);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(24, 0, 0, 0), Children = { ViewDialogKit.Label("Clean out items older than"), _olderThan, _unit } });
        Children.Add(new StackPanel { Margin = new Thickness(24, 0, 0, 0), Spacing = 4, Children = { _move, _delete } });
    }

    /// <summary>The choice as made.</summary>
    public FolderArchivePolicy Policy => new()
    {
        Mode = _off.IsChecked == true ? FolderArchiveMode.Off : _custom.IsChecked == true ? FolderArchiveMode.Custom : FolderArchiveMode.Default,
        OlderThan = (int)(_olderThan.Value ?? 6),
        Unit = (ArchiveUnit)Math.Max(0, _unit.SelectedIndex),
        Action = _delete.IsChecked == true ? ArchiveAction.Delete : ArchiveAction.Move,
    };
}

/// <summary>
/// Archive — the reference's Clean Up Old Items: everything by its AutoArchive settings, or one
/// folder and its subfolders older than a date, now.
/// </summary>
public sealed class ArchiveDialog : Window
{
    /// <summary>What was archived when OK ran it, or null when cancelled.</summary>
    public ArchiveOutcome? Outcome { get; private set; }

    public ArchiveDialog(IReadOnlyList<OpenAccount> accounts, AutoArchiveOptions options, OpenAccount? current, long? currentFolderId)
    {
        Title = "Archive";
        Width = 480;
        Height = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var all = ViewDialogKit.Ink(new RadioButton { Content = "Archive all folders according to their AutoArchive settings", GroupName = "archive", IsChecked = true });
        var one = ViewDialogKit.Ink(new RadioButton { Content = "Archive this folder and all subfolders:", GroupName = "archive" });

        // The folder tree, one account at a time: the account on screen, or the default.
        var account = current ?? accounts.FirstOrDefault();
        var folders = account is null ? [] : account.Mail.Folders(account.Account.Id).Where(f => f.Role is not (FolderRole.Outbox or FolderRole.Drafts)).ToList();
        var tree = ViewDialogKit.SurfaceList(420, 170);
        var ordered = new List<Folder>();
        void Add(long? parent) { foreach (var f in folders.Where(f => f.ParentId == parent).OrderBy(f => f.Ordinal).ThenBy(f => f.Name)) { ordered.Add(f); Add(f.Id); } }
        Add(null);
        tree.ItemTemplate = new FuncDataTemplate<Folder>((f, _) =>
        {
            if (f is null) return new Control();

            var text = ViewDialogKit.SurfaceText(f.Name);
            text.Margin = new Thickness(Depth(f, folders) * 16, 0, 0, 0);
            return text;
        });
        tree.ItemsSource = ordered;
        tree.SelectedItem = ordered.FirstOrDefault(f => f.Id == currentFolderId) ?? ordered.FirstOrDefault();

        var date = new CalendarDatePicker { Width = 200, SelectedDate = AutoArchive.Cutoff(options.OlderThan, options.Unit, DateTimeOffset.Now).Date };
        var include = ViewDialogKit.Ink(new CheckBox { Content = "Include items with \"Do not AutoArchive\" checked" });

        void Enable() { var chosen = one.IsChecked == true; tree.IsEnabled = chosen; date.IsEnabled = chosen; include.IsEnabled = chosen; }
        all.IsCheckedChanged += (_, _) => Enable();
        one.IsCheckedChanged += (_, _) => Enable();
        Enable();

        var ok = ViewDialogKit.Ok(() =>
        {
            if (all.IsChecked == true)
            {
                Outcome = Archiver.RunAll(accounts, options, DateTimeOffset.Now);
                options.LastRun = DateTimeOffset.Now;
            }
            else if (account is not null && tree.SelectedItem is Folder folder)
            {
                var olderThan = new DateTimeOffset((date.SelectedDate ?? DateTime.Today).Date, DateTimeOffset.Now.Offset);
                Outcome = Archiver.ArchiveFolderTree(account, folder.Id, subfolders: true, olderThan, include.IsChecked == true);
            }

            Close();
        });

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                all,
                one,
                tree,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ViewDialogKit.Label("Archive items older than:"), date } },
                include,
                ViewDialogKit.Label("Archived mail goes to the account's Archive folder.", subtle: true),
                ViewDialogKit.Buttons(ok, ViewDialogKit.Cancel(this)),
            },
        };

        DialogChrome.Apply(this, body);
        ViewDialogKit.Bind(this, BackgroundProperty, "dialog.background.brush");
    }

    private static int Depth(Folder folder, IReadOnlyList<Folder> all)
    {
        var depth = 0;
        var parent = folder.ParentId;
        while (parent is { } id && all.FirstOrDefault(f => f.Id == id) is { } up) { depth++; parent = up.ParentId; }
        return depth;
    }
}
