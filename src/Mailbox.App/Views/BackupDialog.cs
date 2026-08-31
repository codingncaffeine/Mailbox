using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Backup and restore for the whole profile: every account's store, the calendars and
/// contacts, the feeds, the settings, the themes and the plugin manifests, in one archive.
/// </summary>
/// <remarks>
/// A member of the Account Settings family, reached from the Data Files tab. The engine is
/// <see cref="ProfileBackup"/>; this window is the choosing — where the archive goes, whether
/// one is written daily, how many are kept — and the one flow the engine will not do on its
/// own: closing the application after a restore, because the running stores still hold the
/// displaced files and only a fresh start opens the restored ones.
/// </remarks>
public sealed class BackupDialog : Window
{
    /// <summary>The settings keys the schedule runs on.</summary>
    internal const string EnabledKey = "backup.daily";
    internal const string DirectoryKey = "backup.directory";
    internal const string KeepKey = "backup.keep";
    internal const string LastKey = "backup.last";

    private readonly TextBox _directory = new() { Classes = { "sysfield" }, Width = 330 };
    private readonly CheckBox _daily = new() { Content = "Back up daily, after the first send/receive of the day" };
    private readonly ComboBox _keep = new() { ItemsSource = new[] { 3, 5, 10, 20 }, Width = 70 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 470 };

    public BackupDialog()
    {
        Title = "Backup & Restore";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _directory.Text = App.Settings.GetString(DirectoryKey,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mailbox Backups"));
        _daily.IsChecked = App.Settings.GetBool(EnabledKey);
        _keep.SelectedItem = (int)App.Settings.GetNumber(KeepKey, 5) is var kept && ((int[])[3, 5, 10, 20]).Contains(kept) ? kept : 5;

        _daily.IsCheckedChanged += (_, _) => SaveSchedule();
        _keep.SelectionChanged += (_, _) => SaveSchedule();
        _directory.TextChanged += (_, _) => SaveSchedule();

        Bind(_status, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");

        SystemDialogChrome.Apply(this, Layout());
        WireDoors();
    }

    private Control Layout()
    {
        var heading = new TextBlock { Text = "Backup & Restore", FontSize = 20, Margin = new Thickness(0, 0, 0, 4) };
        Bind(heading, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

        var subheading = new TextBlock
        {
            Text = "One archive carries everything: your mail, calendars, contacts, feeds, "
                   + "settings, themes and plugin list.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        Bind(subheading, TextBlock.ForegroundProperty, "systemdialog.foreground.subtle.brush");

        var browse = new Button { Content = "Browse…", Classes = { "sysbutton" } };
        browse.Click += async (_, _) => await BrowseAsync();

        var backUp = new Button { Content = "Back Up Now", Classes = { "sysbutton" } };
        backUp.Click += async (_, _) => await BackUpAsync();

        var restore = new Button { Content = "Restore from a Backup…", Classes = { "sysbutton" } };
        restore.Click += async (_, _) => await RestoreAsync();

        var close = new Button { Content = "Close", IsCancel = true, Classes = { "sysbutton" } };
        close.Click += (_, _) => Close();

        var keepRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(24, 0, 0, 0),
            Children = { Caption("Keep the newest"), _keep, Caption("backups") },
        };

        return new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    heading,
                    subheading,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { Caption("Back up to", 90), _directory, browse },
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(98, 0, 0, 0),
                        Children = { backUp },
                    },
                    _daily,
                    keepRow,
                    new Separator { Margin = new Thickness(0, 8) },
                    restore,
                    _status,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { close },
                    },
                },
            },
        };
    }

    private void SaveSchedule()
    {
        App.Settings.Set(EnabledKey, _daily.IsChecked == true);
        App.Settings.Set(DirectoryKey, (_directory.Text ?? string.Empty).Trim());
        if (_keep.SelectedItem is int kept) App.Settings.Set(KeepKey, (double)kept);
    }

    private async Task BrowseAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Back up to",
            AllowMultiple = false,
        });

        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            _directory.Text = path;
        }
    }

    /// <summary>The write half, shared with the harness door: no picker in it.</summary>
    internal ProfileArchiveResult BackUpTo(string directory)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, ProfileBackup.SuggestedName(DateTimeOffset.Now));

        var result = WriteArchive(destination);
        if (result.Ok)
        {
            var pruned = ProfileBackup.Prune(directory, KeepCount());
            _status.Text = $"Backed up to {Path.GetFileName(result.Path)} "
                           + $"({Mailbox.Rendering.Attachment.Sized(result.Bytes)}, {result.Entries} items"
                           + (pruned > 0 ? $"; {pruned} old backup{(pruned == 1 ? string.Empty : "s")} pruned" : string.Empty)
                           + ").";
        }
        else
        {
            _status.Text = $"The backup failed: {result.Error}";
        }

        return result;
    }

    private async Task BackUpAsync()
    {
        var directory = (_directory.Text ?? string.Empty).Trim();
        if (directory.Length == 0)
        {
            _status.Text = "Choose where the backup should go first.";
            return;
        }

        _status.Text = "Backing up…";
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, ProfileBackup.SuggestedName(DateTimeOffset.Now));
        var result = await Task.Run(() => WriteArchive(destination));

        if (result.Ok)
        {
            var pruned = ProfileBackup.Prune(directory, KeepCount());
            _status.Text = $"Backed up to {Path.GetFileName(result.Path)} "
                           + $"({Mailbox.Rendering.Attachment.Sized(result.Bytes)}, {result.Entries} items"
                           + (pruned > 0 ? $"; {pruned} old backup{(pruned == 1 ? string.Empty : "s")} pruned" : string.Empty)
                           + ").";
        }
        else
        {
            _status.Text = $"The backup failed: {result.Error}";
        }
    }

    private async Task RestoreAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore from a Backup",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Mailbox backup") { Patterns = ["*.zip"] }],
        });

        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } archive) return;
        await RestoreFromAsync(archive);
    }

    /// <summary>The restore flow over one archive, shared with the harness door.</summary>
    internal async Task RestoreFromAsync(string archive)
    {
        var (manifest, error) = ProfileBackup.Inspect(archive);
        if (manifest is null)
        {
            _status.Text = error;
            return;
        }

        var accounts = manifest.Entries.Count(e => e.StartsWith("accounts/", StringComparison.Ordinal));
        var going = await Confirm.AskAsync(
            this,
            "Restore from a Backup",
            $"This backup was made {manifest.Made:d MMMM yyyy 'at' HH:mm} and holds "
            + $"{accounts} account store{(accounts == 1 ? string.Empty : "s")}, "
            + $"{manifest.Entries.Count} items in all.\n\n"
            + "Restoring replaces what Mailbox holds now. Nothing is destroyed — everything "
            + "replaced is moved aside with a dated name — and Mailbox closes when the restore "
            + "finishes, so the next start opens the restored data.",
            "Restore",
            destructive: true);

        if (!going)
        {
            _status.Text = "Nothing was restored.";
            return;
        }

        var result = await Task.Run(() => RestoreArchive(archive));
        if (!result.Ok)
        {
            _status.Text = $"The restore failed, and nothing was touched: {result.Error}";
            return;
        }

        Log.Info($"Restore: {result.Restored.Count} target(s) in, {result.Displaced.Count} moved aside.");
        await Confirm.TellAsync(
            this,
            "Restore from a Backup",
            $"Restored. {result.Displaced.Count} thing{(result.Displaced.Count == 1 ? " was" : "s were")} "
            + "moved aside with a dated name.\n\nMailbox will close now — start it again to open "
            + "the restored data.");

        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private int KeepCount() => _keep.SelectedItem is int kept ? kept : 5;

    /// <summary>Everything the archive carries, from the running application's own paths.</summary>
    internal static ProfileArchiveResult WriteArchive(string destination)
        => ProfileBackup.WriteArchive(
            destination,
            Path.Combine(App.StoreDirectory, "accounts"),
            Path.Combine(App.StoreDirectory, "pim.db"),
            Path.Combine(App.StoreDirectory, "feeds.db"),
            files: App.Settings.PathOnDisk is { Length: > 0 } settings
                ? [(settings, "settings.json")]
                : [],
            directories:
            [
                (Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory(), "themes"),
                (App.Plugins.Root, "plugins"),
            ],
            DateTimeOffset.Now);

    private static ProfileRestoreResult RestoreArchive(string archive)
        => ProfileBackup.Restore(
            archive,
            Path.Combine(App.StoreDirectory, "accounts"),
            Path.Combine(App.StoreDirectory, "pim.db"),
            Path.Combine(App.StoreDirectory, "feeds.db"),
            files: App.Settings.PathOnDisk is { Length: > 0 } settings
                ? [("settings.json", settings)]
                : [],
            directories:
            [
                ("themes", Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory()),
                ("plugins", App.Plugins.Root),
            ],
            DateTimeOffset.Now);

    /// <summary>
    /// The window's doors: <c>MAILBOX_BACKUP=run:&lt;dir&gt;</c> writes an archive there through
    /// the same handler the button runs and reads it back; <c>restore:&lt;zip&gt;</c> runs the
    /// restore flow, with the Confirms answered by <c>MAILBOX_ANSWER</c>.
    /// </summary>
    private void WireDoors()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_BACKUP") is not { Length: > 0 } spec) return;

        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            async () =>
            {
                using var hold = Theming.WindowCapture.Hold();

                try
                {
                    if (spec.StartsWith("run:", StringComparison.OrdinalIgnoreCase))
                    {
                        var directory = spec["run:".Length..];
                        var result = BackUpTo(directory);
                        Log.Info($"Harness: backup — ok={result.Ok}, {result.Entries} entries, "
                                 + $"{result.Bytes} bytes, status “{_status.Text}”.");

                        if (result.Ok && ProfileBackup.Inspect(result.Path) is { Manifest: { } manifest })
                        {
                            foreach (var entry in manifest.Entries)
                            {
                                Log.Info($"Harness:   holds {entry}");
                            }
                        }

                        return;
                    }

                    if (spec.StartsWith("restore:", StringComparison.OrdinalIgnoreCase))
                    {
                        await RestoreFromAsync(spec["restore:".Length..]);
                        Log.Info($"Harness: restore settled — status “{_status.Text}”.");
                        return;
                    }

                    // roundtrip:<dir> — an archive written and then restored in one posed run,
                    // which is the whole promise pressed end to end: what goes in comes back,
                    // and the application closes on the restored data.
                    if (spec.StartsWith("roundtrip:", StringComparison.OrdinalIgnoreCase))
                    {
                        var result = BackUpTo(spec["roundtrip:".Length..]);
                        Log.Info($"Harness: roundtrip backup — ok={result.Ok}, {result.Entries} entries.");
                        if (result.Ok) await RestoreFromAsync(result.Path);
                        Log.Info($"Harness: roundtrip settled — status “{_status.Text}”.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Harness: the backup door failed.", ex);
                }
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private static TextBlock Caption(string text, double width = double.NaN)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (!double.IsNaN(width)) block.Width = width;
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        return block;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
