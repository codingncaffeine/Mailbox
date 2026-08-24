using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Import;

namespace Mailbox.App.Views;

/// <summary>
/// Brings a Maildir tree into an account: pick the directory, pick the account, watch the
/// count, read the report. The counts are the point — a migration is the one operation a
/// reader checks by number, so the dialog ends by saying them rather than closing on success.
/// </summary>
public static class ImportMaildirDialog
{
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>Runs the dialog. True when anything was imported, so the caller refreshes.</summary>
    public static async Task<bool> RunAsync(Window owner)
    {
        var imported = false;

        var caption = new TextBlock
        {
            Text = "Import mail from a Maildir — the folder-of-files store Dovecot, Evolution, " +
                   "KMail, mutt and offlineimap keep. The source is only read, never changed, " +
                   "and running an import twice tops up rather than doubling.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var path = new TextBox { Width = 340, PlaceholderText = "Maildir directory", Name = "ImportPath" };

        var browse = new Button { Content = "Browse…" };

        var accounts = new ComboBox
        {
            Width = 460,
            ItemsSource = App.Accounts.All.Select(a => a.Account.Address).ToList(),
            SelectedIndex = App.Accounts.All.Count > 0 ? 0 : -1,
        };

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            IsVisible = false,
        };
        Bind(status, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var window = new Window
        {
            Title = "Import Maildir",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        browse.Click += async (_, _) =>
        {
            var picked = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the Maildir directory",
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

            if (root.Length == 0 || !Maildir.LooksLikeATree(root))
            {
                status.IsVisible = true;
                status.Text = "That directory holds no maildir — expected cur/ and new/ inside " +
                              "it, or folders that carry them.";
                return;
            }

            run.IsEnabled = false;
            browse.IsEnabled = false;
            close.IsEnabled = false;
            status.IsVisible = true;
            status.Text = "Importing…";

            try
            {
                // The count on the UI thread, the reading off it: an import of ten thousand
                // files must not freeze the window that reports it.
                var report = await Task.Run(() =>
                    new MaildirImporter(open.Mail, open.Account.Id).Run(
                        root,
                        (done, total) =>
                        {
                            if (done % 50 == 0 || done == total)
                            {
                                Dispatcher.UIThread.Post(() => status.Text = $"Importing… {done:N0} of {total:N0}.");
                            }
                        }));

                imported = report.Imported > 0;
                status.Text = report.Summary
                    + (report.Notes.Count > 0 ? "\n" + string.Join("\n", report.Notes.Take(5)) : string.Empty);
                Log.Info($"Import: {report.Summary} ({root} → {address})");
            }
            catch (Exception ex)
            {
                Log.Warn("The Maildir import failed.", ex);
                status.Text = $"The import stopped: {ex.Message}";
            }
            finally
            {
                run.IsEnabled = true;
                browse.IsEnabled = true;
                close.IsEnabled = true;
            }
        };

        var pathRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { path, browse },
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                caption,
                pathRow,
                accounts,
                status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { close, run },
                },
            },
        };

        DialogChrome.Apply(window, body);
        window.Opened += (_, _) => path.Focus();

        await window.ShowDialog(owner);
        return imported;
    }
}
