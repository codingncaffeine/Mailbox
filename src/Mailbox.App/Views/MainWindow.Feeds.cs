using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Mailbox.App.ViewModels;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Core.Ribbon;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The Feeds module in the shell: switching to it, the workspace it puts in the window, and the
/// commands its ribbon presses.
/// </summary>
/// <remarks>
/// A partial of the shell for the reason the other modules' halves are: it needs the window's
/// ribbon, its dialogs and its status line.
/// </remarks>
public partial class MainWindow
{
    private FeedsWorkspace? _feedModule;

    /// <summary>The pictures the article list draws, shared across a session.</summary>
    private readonly FeedThumbnails _feedPictures = new();

    /// <summary>The Feeds ribbon: the shipped layout with the reader's edits over it.</summary>
    private static RibbonLayout FeedsRibbon() => App.RibbonEdits.Apply(App.Plugins.InjectRibbon(FeedsRibbonLayout.Build()));

    /// <summary>
    /// Where feed articles are filed: the default account's store.
    /// </summary>
    /// <remarks>
    /// One account rather than all of them, and the first rather than a chosen one, because a
    /// feed belongs to the reader and not to a mail account — it is filed somewhere because the
    /// store is where messages live, not because a server has an opinion about it.
    /// </remarks>
    private static OpenAccount? FeedAccount() => App.Accounts.All.FirstOrDefault();

    private FeedsWorkspace EnsureFeeds(ShellViewModel shell)
    {
        if (_feedModule is not null) return _feedModule;

        _feedPictures.Enabled = App.MailOptions.FeedPictures;

        var workspace = new FeedsWorkspace(App.Feeds, FeedAccount, _feedPictures)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.AddRequested += (_, _) => _ = SubscribeToFeedAsync(shell);
        workspace.RefreshRequested += (_, _) => _ = UpdateFeedsAsync(shell, force: true);
        workspace.OpenRequested += (_, id) => OpenFeedArticle(shell, id);
        workspace.Changed += (_, _) =>
        {
            shell.ModuleStatusLeft = workspace.Status;
            RefreshCommandEnablement();
        };

        // A subscription added or removed anywhere — the Account Settings tab, an import — shows
        // in the pane at once rather than on the next switch into the module.
        App.Feeds.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_feedModule is { } live) live.Reload();
        });

        _feedModule = workspace;
        return workspace;
    }

    /// <summary>
    /// The Feeds module's commands. Returns false for anything it does not own, so the shell's
    /// own list carries on.
    /// </summary>
    private bool RunFeedCommand(ShellViewModel shell, CommandId id)
    {
        // Subscribing, importing and exporting are reachable from every module, because that is
        // where a reader is when they come across a site worth following.
        switch (id.Value)
        {
            case "feeds.subscribe":
                _ = SubscribeToFeedAsync(shell);
                return true;

            case "feeds.import.opml":
                _ = ImportFeedsAsync(shell);
                return true;

            case "feeds.export.opml":
                _ = ExportFeedsAsync(shell);
                return true;
        }

        if (shell.Module != MailboxModule.Feeds) return false;
        var feeds = EnsureFeeds(shell);

        switch (id.Value)
        {
            case "feeds.update":
                _ = UpdateFeedsAsync(shell, force: false);
                return true;

            case "feeds.update.one":
                _ = UpdateFeedsAsync(shell, force: true, only: feeds.SelectedFeed);
                return true;

            case "feeds.markallread":
                MarkFeedsRead(shell, feeds);
                return true;

            case "feeds.readlater":
                feeds.ToggleReadLater();
                return true;

            case "feeds.open.original":
                feeds.OpenOriginal();
                return true;

            case "feeds.delete":
                feeds.DeleteSelected();
                return true;

            case "feeds.categorize":
                CategorizeFeedArticle(shell, feeds);
                return true;

            case "feeds.settings":
                _ = FeedSettingsAsync(shell, feeds.SelectedFeed);
                return true;

            case "feeds.unsubscribe":
                _ = UnsubscribeAsync(shell, feeds.SelectedFeed);
                return true;

            default:
                return false;
        }
    }

    // ---- Subscribing ------------------------------------------------------------------------------

    /// <summary>
    /// Asks for an address and subscribes to whatever is behind it.
    /// </summary>
    /// <remarks>
    /// The dialog does the finding, so the reader can type the address of a site rather than of a
    /// feed — which is the address they actually have.
    /// </remarks>
    private async Task SubscribeToFeedAsync(ShellViewModel shell)
    {
        var dialog = new SubscribeDialog(App.FeedReader.Finder, App.Feeds);
        await dialog.ShowDialog(this);

        if (dialog.Subscribed is not { } feed) return;

        shell.StatusRight = $"Subscribed to “{feed.Name}”.";
        Log.Info($"Feeds: subscribed to “{feed.Name}” at {feed.Url}.");

        _feedModule?.Reload();
        shell.Refresh();

        // Read it at once: a subscription that shows nothing until the next scheduled pass looks
        // like it did not work.
        await UpdateFeedsAsync(shell, force: true, only: feed);
    }

    /// <summary>Reads the subscriptions and files what arrived.</summary>
    private async Task UpdateFeedsAsync(ShellViewModel shell, bool force, FeedSubscription? only = null)
    {
        if (App.Feeds.All.Count == 0)
        {
            shell.StatusRight = "No feeds are subscribed to yet.";
            return;
        }

        if (FeedAccount() is not { } account)
        {
            shell.StatusRight = "Feeds are filed into an account, and there is not one yet.";
            return;
        }

        if (_feedsUpdating) return;
        _feedsUpdating = true;

        try
        {
            shell.StatusRight = only is null ? "Reading feeds…" : $"Reading “{only.Name}”…";

            var report = only is null
                ? await App.FeedReader.PollAsync(account, DateTimeOffset.UtcNow, force: force)
                : await App.FeedReader.PollOneAsync(account, only, DateTimeOffset.UtcNow);

            shell.StatusRight = "Feeds: " + report.Summary;
            Log.Info($"Feeds: {report.Summary}.");

            foreach (var (url, error) in report.Failed) Log.Warn($"Feeds: {url} — {error}");

            shell.Refresh();
            _feedModule?.Reload();
        }
        catch (OperationCanceledException)
        {
            shell.StatusRight = "Reading feeds was cancelled.";
        }
        finally
        {
            _feedsUpdating = false;
        }
    }

    private bool _feedsUpdating;

    // ---- The rest of the buttons --------------------------------------------------------------------

    private static void MarkFeedsRead(ShellViewModel shell, FeedsWorkspace feeds)
    {
        var marked = feeds.MarkAllRead();
        shell.StatusRight = marked == 0
            ? "Everything here has been read."
            : $"{marked} article{(marked == 1 ? string.Empty : "s")} marked as read.";
        shell.Refresh();
    }

    /// <summary>
    /// The Categorize menu over a feed article.
    /// </summary>
    /// <remarks>
    /// Through the same book and the same store the mail module writes to, because a feed
    /// article <em>is</em> a message: a category put on one here shows against it in the mail
    /// list and in a search, which is the whole reason feeds are filed as messages.
    /// </remarks>
    private void CategorizeFeedArticle(ShellViewModel shell, FeedsWorkspace feeds)
    {
        if (feeds.SelectedArticle is not { } article || FeedAccount() is not { } account) return;

        var carried = account.Mail.CategoriesFor([article.Id]).GetValueOrDefault(article.Id) ?? [];

        ShowItemCategorizeMenu(
            article.Subject,
            [.. carried.Select(c => c.Name)],
            wanted =>
            {
                foreach (var category in carried.Where(c => !wanted.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
                {
                    account.Mail.Unassign([article.Id], category.Id);
                }

                foreach (var name in wanted)
                {
                    if (App.Categories.Named(name) is not { } category) continue;
                    if (account.Mail.Categories().FirstOrDefault(c => c.Name == name) is not { } mirrored) continue;
                    account.Mail.Assign([article.Id], mirrored.Id);
                }

                feeds.Refresh();
                shell.Refresh();
            });
    }

    private void OpenFeedArticle(ShellViewModel shell, long id)
    {
        if (FeedAccount() is not { } account) return;
        if (account.Mail.GetMessage(id) is not { } message) return;

        var raw = account.Mail.LoadRaw(id);
        if (raw is null) return;

        try
        {
            using var stream = new MemoryStream(raw);
            var mime = MimeKit.MimeMessage.Load(stream);
            new MessageWindow(App.Themes, () => account.Mail, mime, raw).Show(this);
            shell.StatusRight = $"Opened “{message.Subject}”.";
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            Log.Warn($"Feeds: “{message.Subject}” could not be opened.", ex);
        }
    }

    private async Task FeedSettingsAsync(ShellViewModel shell, FeedSubscription? feed)
    {
        if (feed is null)
        {
            shell.StatusRight = "Choose a feed in the list first.";
            return;
        }

        var dialog = new RssFeedOptionsDialog(feed, App.Feeds);
        await dialog.ShowDialog(this);

        if (!dialog.Changed) return;

        _feedModule?.Reload();
        shell.Refresh();
        shell.StatusRight = $"“{feed.Name}” updated.";
    }

    private async Task UnsubscribeAsync(ShellViewModel shell, FeedSubscription? feed)
    {
        if (feed is null)
        {
            shell.StatusRight = "Choose a feed in the list first.";
            return;
        }

        // The articles are not deleted with the subscription: they are messages, and deleting
        // somebody's mail because they stopped following a site would be a surprise.
        if (!await Confirm.AskAsync(this, "Unsubscribe",
                $"Stop reading “{feed.Name}”?\n\nThe articles already filed stay where they are.",
                "Unsubscribe")) return;

        App.Feeds.Remove(feed.Url);
        _feedModule?.Reload();
        shell.Refresh();
        shell.StatusRight = $"Unsubscribed from “{feed.Name}”.";
        Log.Info($"Feeds: unsubscribed from {feed.Url}.");
    }

    // ---- OPML ---------------------------------------------------------------------------------------

    /// <summary>Brings in a subscription list from another reader.</summary>
    private async Task ImportFeedsAsync(ShellViewModel shell)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Feeds",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Outline files") { Patterns = ["*.opml", "*.xml"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.FirstOrDefault() is not { } file) return;

        try
        {
            var entries = Opml.Read(await File.ReadAllTextAsync(file.Path.LocalPath));

            var added = 0;
            using (App.Feeds.Batch())
            {
                foreach (var entry in entries)
                {
                    if (App.Feeds.Contains(entry.Url)) continue;
                    App.Feeds.Add(entry.Url, entry.Title, entry.Category);
                    added++;
                }
            }

            shell.StatusRight = added == 0
                ? $"All {entries.Count} of those feeds were already here."
                : $"{added} feed{(added == 1 ? string.Empty : "s")} added of {entries.Count} in the file.";

            Log.Info($"Feeds: imported {added} of {entries.Count} from {file.Name}.");

            _feedModule?.Reload();
            shell.Refresh();

            if (added > 0) await UpdateFeedsAsync(shell, force: true);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            await Confirm.TellAsync(this, "Import Feeds", $"That file could not be read.\n\n{ex.Message}");
        }
    }

    /// <summary>Writes the subscription list out, so this reader can be left.</summary>
    private async Task ExportFeedsAsync(ShellViewModel shell)
    {
        if (App.Feeds.All.Count == 0)
        {
            shell.StatusRight = "There are no feeds to export.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Feeds",
            SuggestedFileName = "feeds.opml",
            DefaultExtension = "opml",
            FileTypeChoices = [new FilePickerFileType("Outline files") { Patterns = ["*.opml"] }],
        });

        if (file is null) return;

        try
        {
            var text = Opml.Write("Mailbox subscriptions",
                App.Feeds.All.Select(f => new OpmlEntry(f.Name, f.Url, f.Category, f.SiteUrl)),
                DateTimeOffset.Now);

            await File.WriteAllTextAsync(file.Path.LocalPath, text);

            shell.StatusRight = $"{App.Feeds.All.Count} feed{(App.Feeds.All.Count == 1 ? string.Empty : "s")} written to {file.Name}.";
            Log.Info($"Feeds: exported {App.Feeds.All.Count} to {file.Path.LocalPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Confirm.TellAsync(this, "Export Feeds", $"That file could not be written.\n\n{ex.Message}");
        }
    }
}
