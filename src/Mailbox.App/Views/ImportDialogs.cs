using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Import;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The Thunderbird profile importer's door: pick the profile — found ones offered, any
/// directory browsable — pick the account, watch the count, read the report.
/// </summary>
public static class ImportThunderbirdDialog
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public static async Task<bool> RunAsync(Window owner)
    {
        var imported = false;
        var profiles = ThunderbirdImporter.FindProfiles();

        var caption = new TextBlock
        {
            Text = "Import a Thunderbird profile: the mail, the address books, and the filters " +
                   "that translate — one that would change meaning is skipped and named. The " +
                   "profile is only read, never changed.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var path = new TextBox
        {
            Width = 340,
            PlaceholderText = "Profile directory",
            Text = profiles.FirstOrDefault()?.Directory ?? string.Empty,
            Name = "TbPath",
        };

        var browse = new Button { Content = "Browse…" };

        var found = new ComboBox
        {
            Width = 460,
            ItemsSource = profiles.Select(p => $"{p.Name} — {p.Directory}").ToList(),
            SelectedIndex = profiles.Count > 0 ? 0 : -1,
            IsVisible = profiles.Count > 0,
        };
        found.SelectionChanged += (_, _) =>
        {
            if (found.SelectedIndex >= 0 && found.SelectedIndex < profiles.Count)
            {
                path.Text = profiles[found.SelectedIndex].Directory;
            }
        };

        var accounts = new ComboBox
        {
            Width = 460,
            ItemsSource = App.Accounts.All.Select(a => a.Account.Address).ToList(),
            SelectedIndex = App.Accounts.All.Count > 0 ? 0 : -1,
        };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, IsVisible = false };
        Bind(status, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var window = new Window
        {
            Title = "Import Thunderbird Profile",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        browse.Click += async (_, _) =>
        {
            var picked = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the Thunderbird profile directory",
                AllowMultiple = false,
            });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local) path.Text = local;
        };

        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => window.Close();

        var run = new Button { Content = "Import", IsDefault = true };
        run.Click += async (_, _) =>
        {
            var root = (path.Text ?? string.Empty).Trim();
            var address = accounts.SelectedItem as string;

            if (address is null || App.Accounts.All.FirstOrDefault(a =>
                    string.Equals(a.Account.Address, address, StringComparison.OrdinalIgnoreCase)) is not { } open)
            {
                status.IsVisible = true;
                status.Text = "Add an account first — imported mail needs somewhere to live.";
                return;
            }

            if (root.Length == 0 || !Directory.Exists(root))
            {
                status.IsVisible = true;
                status.Text = "That is not a directory.";
                return;
            }

            run.IsEnabled = false;
            close.IsEnabled = false;
            status.IsVisible = true;
            status.Text = "Importing…";

            try
            {
                var report = await Task.Run(() =>
                    new ThunderbirdImporter(open.Mail, open.Account.Id, App.Pim, App.PimSync.QueuePut).Run(
                        root,
                        (done, total) => Dispatcher.UIThread.Post(() =>
                            status.Text = $"Importing… folder {done:N0} of {total:N0}.")));

                imported = report.Mail.Imported > 0 || report.AddressBooks.Imported > 0 || report.Rules > 0;
                status.Text = report.Summary
                    + (report.Notes.Count > 0 ? "\n" + string.Join("\n", report.Notes.Take(5)) : string.Empty);
            }
            catch (Exception ex)
            {
                Log.Warn("The Thunderbird import failed.", ex);
                status.Text = $"The import stopped: {ex.Message}";
            }
            finally
            {
                run.IsEnabled = true;
                close.IsEnabled = true;
            }
        };

        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { path, browse } };

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children = { caption, found, pathRow, accounts, status, new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { close, run },
            } },
        };

        DialogChrome.Apply(window, body);
        await window.ShowDialog(owner);
        return imported;
    }
}

/// <summary>
/// The file importer's door: any mix of mbox, .eml, .ics and .vcf, each routed by what it is —
/// mail into the chosen account's Inbox, appointments and tasks into the default calendar and
/// list, cards into the default address book. Said in the dialog, so nobody hunts for where
/// things went.
/// </summary>
public static class ImportFilesDialog
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    public static async Task<bool> RunAsync(Window owner)
    {
        var imported = false;

        var caption = new TextBlock
        {
            Text = "Import files: a .pst brings its whole folder tree into the chosen account — " +
                   "mail only, its calendar and contacts wait for their own importer — an mbox " +
                   "or .eml lands in the account's Inbox, .ics appointments and tasks go to your " +
                   "default calendar and task list, and .vcf cards to your address book.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var chosen = new List<string>();
        var files = new TextBlock { Text = "No files chosen yet.", TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        Bind(files, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var pick = new Button { Content = "Choose Files…" };

        var accounts = new ComboBox
        {
            Width = 460,
            ItemsSource = App.Accounts.All.Select(a => a.Account.Address).ToList(),
            SelectedIndex = App.Accounts.All.Count > 0 ? 0 : -1,
        };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, IsVisible = false };
        Bind(status, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var window = new Window
        {
            Title = "Import Files",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        pick.Click += async (_, _) =>
        {
            var picked = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose files to import",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Everything importable") { Patterns = ["*.pst", "*.mbox", "*.eml", "*.ics", "*.vcf", "*"] },
                ],
            });

            chosen.Clear();
            chosen.AddRange(picked.Select(f => f.TryGetLocalPath()).Where(p => p is not null)!);
            files.Text = chosen.Count == 0
                ? "No files chosen yet."
                : string.Join(", ", chosen.Select(System.IO.Path.GetFileName));
        };

        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => window.Close();

        var run = new Button { Content = "Import", IsDefault = true };
        run.Click += async (_, _) =>
        {
            if (chosen.Count == 0)
            {
                status.IsVisible = true;
                status.Text = "Choose at least one file.";
                return;
            }

            var address = accounts.SelectedItem as string;
            var open = address is null ? null : App.Accounts.All.FirstOrDefault(a =>
                string.Equals(a.Account.Address, address, StringComparison.OrdinalIgnoreCase));

            run.IsEnabled = false;
            close.IsEnabled = false;
            status.IsVisible = true;
            status.Text = "Importing…";

            try
            {
                var lines = await Task.Run(() => ImportFiles.Run(chosen, open, App.Pim, App.PimSync.QueuePut));
                imported = true;
                status.Text = string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                Log.Warn("The file import failed.", ex);
                status.Text = $"The import stopped: {ex.Message}";
            }
            finally
            {
                run.IsEnabled = true;
                close.IsEnabled = true;
            }
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children = { caption, pick, files, accounts, status, new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { close, run },
            } },
        };

        DialogChrome.Apply(window, body);
        await window.ShowDialog(owner);
        return imported;
    }
}

/// <summary>The routing the Import Files door and the harness pose share.</summary>
internal static class ImportFiles
{
    /// <summary>Imports each file by what it is, and answers one line per file.</summary>
    public static IReadOnlyList<string> Run(
        IReadOnlyList<string> paths, OpenAccount? mailAccount,
        Mailbox.Store.Pim.PimRepository pim, Action<Mailbox.Store.Pim.PimItem>? queuePut)
    {
        var lines = new List<string>();
        var pimImporter = new PimFileImporter(pim, queuePut);

        foreach (var path in paths)
        {
            var name = System.IO.Path.GetFileName(path);
            var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();

            try
            {
                switch (extension)
                {
                    case ".pst":
                        // The one importer here that brings a whole folder tree, not a file
                        // into the Inbox — the account is the destination.
                        lines.Add(mailAccount is null
                            ? $"{name}: add an account first."
                            : $"{name}: {new PstImporter(mailAccount.Mail, mailAccount.Account.Id).Run(path).Summary}");
                        break;

                    case ".ics":
                        lines.Add($"{name}: {pimImporter.Ics(File.ReadAllText(path)).Summary}");
                        break;

                    case ".vcf":
                        lines.Add($"{name}: {pimImporter.Vcf(File.ReadAllText(path)).Summary}");
                        break;

                    case ".eml":
                        lines.Add(MailLine(name, mailAccount, (mail, inbox) =>
                            MailFileImport.Eml(mail, inbox, [path]).Summary));
                        break;

                    default:
                        if (Mbox.Looks(path))
                        {
                            lines.Add(MailLine(name, mailAccount, (mail, inbox) =>
                                MailFileImport.Mbox(mail, inbox, path).Summary));
                        }
                        else
                        {
                            lines.Add($"{name}: not a format this importer reads.");
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                lines.Add($"{name}: {ex.Message}");
            }
        }

        return lines;
    }

    private static string MailLine(string name, OpenAccount? account, Func<Mailbox.Store.MailRepository, long, string> import)
    {
        if (account is null) return $"{name}: add an account first.";
        var inbox = account.Mail.FolderWithRole(account.Account.Id, Mailbox.Store.FolderRole.Inbox);
        return inbox is null ? $"{name}: the account has no Inbox." : $"{name}: {import(account.Mail, inbox.Id)}";
    }
}
