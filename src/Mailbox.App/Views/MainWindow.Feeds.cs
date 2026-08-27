using Avalonia.Controls;
using Avalonia.Input.Platform;
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
        workspace.NewBoardRequested += (_, _) => _ = BoardsAsync(shell, string.Empty);
        workspace.SaveLinkRequested += (_, _) => _ = SaveLinkAsync(shell);
        workspace.SaveToBoardRequested += (_, anchor) => SaveToBoard(shell, workspace, anchor);
        workspace.RefreshRequested += (_, _) => _ = UpdateFeedsAsync(shell, force: true);
        workspace.OpenRequested += (_, id) => OpenFeedArticle(shell, id);
        workspace.ShortcutsRequested += (_, list) => _ = Confirm.ShowAsync(this, "Keyboard shortcuts", list);
        workspace.FullTextWanted += (_, article) => _ = FillInArticleAsync(shell, workspace, article);
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

            case "feeds.newsletters":
                _ = NewslettersAsync(shell);
                return true;

            case "feeds.import.opml":
                _ = ImportFeedsAsync(shell);
                return true;

            case "feeds.export.opml":
                _ = ExportFeedsAsync(shell);
                return true;

            // Saving an address needs nothing selected and no module: a reader comes across one
            // while they are reading their mail as often as while they are reading their feeds.
            case "feeds.board.link":
                _ = SaveLinkAsync(shell);
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

            case "feeds.board.save":
                SaveToBoard(shell, feeds, _ribbon ?? (Avalonia.Controls.Control)this);
                return true;

            case "feeds.board.remove":
                if (!feeds.RemoveFromOpenBoard()) shell.StatusRight = "Open a board first to take an article off it.";
                else shell.StatusRight = feeds.Status;
                return true;

            case "feeds.boards":
                _ = BoardsAsync(shell, string.Empty);
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

            case "feeds.mute":
                _ = MuteFiltersAsync(shell, string.Empty);
                return true;

            case "feeds.mute.this":
                _ = MuteFiltersAsync(shell, feeds.SelectedArticle?.Subject ?? string.Empty);
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

        if (dialog.Subscribed is not { Count: > 0 } added) return;

        shell.StatusRight = added.Count == 1
            ? $"Subscribed to “{added[0].Name}”."
            : $"Subscribed to {added.Count} feeds.";

        foreach (var feed in added) Log.Info($"Feeds: subscribed to “{feed.Name}” at {feed.Url}.");

        _feedModule?.Reload();
        shell.Refresh();

        // Read at once: a subscription that shows nothing until the next scheduled pass looks
        // like it did not work.
        foreach (var feed in added) await UpdateFeedsAsync(shell, force: true, only: feed);
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

    /// <summary>
    /// The filters dashboard.
    /// </summary>
    /// <param name="seed">
    /// A phrase to start the box with — what "Mute This" hands over, taken from the selected
    /// article so the reader edits a suggestion rather than typing from nothing.
    /// </param>
    private async Task MuteFiltersAsync(ShellViewModel shell, string seed)
    {
        var dialog = new MuteFiltersDialog(App.Mutes, App.Feeds, DateTimeOffset.UtcNow) { Suggested = seed };
        await dialog.ShowDialog(this);

        if (!dialog.Changed) return;

        var live = App.Mutes.Live(DateTimeOffset.UtcNow).Count;
        shell.StatusRight = live == 0
            ? "Nothing is muted."
            : $"{live} mute filter{(live == 1 ? string.Empty : "s")} in force.";
    }

    /// <summary>
    /// The newsletters already arriving in the mailbox, offered as feeds.
    /// </summary>
    /// <remarks>
    /// No forwarding address and no third party: the mail is already here, and this only decides
    /// where it is filed. Which is the whole of why a mail client can do this better than a
    /// website can.
    /// </remarks>
    private async Task NewslettersAsync(ShellViewModel shell)
    {
        if (FeedAccount() is null)
        {
            shell.StatusRight = "There is no mail account to read newsletters from.";
            return;
        }

        var dialog = new NewslettersDialog(App.Feeds, FeedAccount);
        await dialog.ShowDialog(this);

        if (dialog.Added == 0) return;

        shell.StatusRight = dialog.Gathered > 0
            ? $"{dialog.Added} newsletter{(dialog.Added == 1 ? string.Empty : "s")} moved here, "
              + $"with {dialog.Gathered} issue{(dialog.Gathered == 1 ? string.Empty : "s")}."
            : $"{dialog.Added} newsletter{(dialog.Added == 1 ? string.Empty : "s")} will be read here from now on.";

        Log.Info($"Newsletters: {dialog.Added} taken up, {dialog.Gathered} back number(s) moved.");

        _feedModule?.Reload();
        shell.Refresh();
    }

    /// <summary>
    /// Reads the publisher's page for an article the feed sent only a teaser of.
    /// </summary>
    /// <remarks>
    /// What makes clicking an article mean something for the many feeds that publish a sentence
    /// and a link. The teaser is already on screen when this starts, so the reader is reading
    /// while it runs and the article replaces it in place when it arrives.
    /// <para>
    /// Off the reader's own switch for the feed, so "do not fetch my articles' pages" is honoured
    /// here as it is in the poll — and skipped entirely for anything that is not from a feed.
    /// </para>
    /// </remarks>
    private async Task FillInArticleAsync(ShellViewModel shell, FeedsWorkspace feeds, MessageSummary article)
    {
        if (FeedAccount() is not { } account) return;

        // Which subscription filed it, so its own switch decides. A saved link has no
        // subscription and is left alone: its page was read when it was saved.
        var feed = App.Feeds.All.FirstOrDefault(f =>
            account.Mail.Folders(account.Account.Id)
                .Any(folder => folder.Id == article.FolderId && folder.Name == f.Name));

        if (feed is { ReadFullArticle: false }) return;

        try
        {
            var written = await Mailbox.Protocols.ArticleFill.FillAsync(account, article.Id, App.FeedReader.Fetch);
            if (written == 0) return;

            feeds.Reopen(article.Id);
            shell.StatusRight = $"Read {written:N0} characters of “{article.Subject}” from the publisher's page.";
        }
        catch (OperationCanceledException)
        {
            // The window is closing, or the reader moved on. Neither is worth saying.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Log.Debug($"Feeds: “{article.Subject}” could not be filled in — {ex.Message}");
        }
    }

    // ---- Boards -------------------------------------------------------------------------------------

    /// <summary>
    /// The Save to Board menu, over whatever the reader pressed it from.
    /// </summary>
    /// <remarks>
    /// The menu writes straight to the store — a board is a row in a table and there is nothing
    /// to compose — so what comes back is only the redraw: the pane's counts move, and the bar
    /// re-decides whether Take Off Board can act.
    /// </remarks>
    private void SaveToBoard(ShellViewModel shell, FeedsWorkspace feeds, Control anchor)
    {
        if (feeds.ArticleForBoard is not { } article)
        {
            shell.StatusRight = "Choose an article first.";
            return;
        }

        if (FeedAccount() is not { } account)
        {
            shell.StatusRight = "Boards are kept with your mail, and there is no account yet.";
            return;
        }

        BoardMenu.Show(
            account.Mail,
            anchor,
            article.Subject,
            [article.Id],
            DateTimeOffset.UtcNow,
            changed: () =>
            {
                // The pane's counts and the open board, without the reader losing the row they
                // were on — a save is not a reason to put them back at the top of the list.
                _feedModule?.RefreshBoards();
                shell.StatusRight = Standing(account, article);
                RefreshCommandEnablement();
            },
            newBoard: () => _ = BoardsAsync(shell, string.Empty, article),
            manage: () => _ = BoardsAsync(shell, string.Empty, article));
    }

    /// <summary>What the bar says after a save: where the article now is, rather than "done".</summary>
    private static string Standing(OpenAccount account, MessageSummary article)
    {
        var on = account.Mail.BoardsFor([article.Id]).GetValueOrDefault(article.Id) ?? [];

        return on.Count switch
        {
            0 => $"“{article.Subject}” is on no board.",
            1 => $"“{article.Subject}” is on {on[0].Name}.",
            _ => $"“{article.Subject}” is on {string.Join(", ", on.Select(b => b.Name))}.",
        };
    }

    /// <summary>
    /// The Boards dialog, optionally saving an article onto whatever is made in it.
    /// </summary>
    /// <param name="saving">
    /// The article the reader was on when they asked for a new board, so making one from Save to
    /// Board saves onto it — which is what a reader who reached the dialog that way meant.
    /// </param>
    private async Task BoardsAsync(ShellViewModel shell, string suggested, MessageSummary? saving = null)
    {
        if (FeedAccount() is not { } account)
        {
            shell.StatusRight = "Boards are kept with your mail, and there is no account yet.";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        Board? made = null;

        var dialog = new BoardsDialog(account.Mail, now)
        {
            Suggested = suggested,
            Made = board =>
            {
                made = board;
                if (saving is { } article) account.Mail.SaveToBoard([article.Id], board.Id, now);
            },
        };

        await dialog.ShowDialog(this);

        if (!dialog.Changed) return;

        _feedModule?.Reload();
        RefreshCommandEnablement();

        shell.StatusRight = made is { } fresh && saving is { } saved
            ? $"“{saved.Subject}” saved to {fresh.Name}."
            : $"{account.Mail.Boards().Count} board(s).";
    }

    /// <summary>
    /// Save a Link: an address becomes an article on a board.
    /// </summary>
    /// <remarks>
    /// The whole of what makes a board different from a heading. The fetch happens here rather
    /// than in the dialog, so the dialog stays a dialog and the reading of a page goes through
    /// the same controlled client every other page fetch in the module goes through — no
    /// cookies, no referer, a size cap.
    /// <para>
    /// The clipboard is read for a starting value, because a reader reaching for this has almost
    /// always just copied an address. Only when it is one: pasting the last thing somebody copied
    /// into a box they did not ask for it in is a surprise, and a paragraph of text in an address
    /// box is worse than an empty one.
    /// </para>
    /// </remarks>
    private async Task SaveLinkAsync(ShellViewModel shell)
    {
        if (FeedAccount() is not { } account)
        {
            shell.StatusRight = "A saved link is filed with your mail, and there is no account yet.";
            return;
        }

        // A dialog blocks a capture run, so the harness takes the same path without one:
        // MAILBOX_SAVE_LINK=<address>[|board]. What has to be provable is that an address becomes
        // an article on a board — not that a text box accepts typing.
        if (Environment.GetEnvironmentVariable("MAILBOX_SAVE_LINK") is { Length: > 0 } posedLink)
        {
            await PoseSaveLinkAsync(shell, account, posedLink);
            return;
        }

        var suggested = string.Empty;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } board
                && await board.TryGetTextAsync() is { Length: > 0 } copied
                && Mailbox.Protocols.SavedLinks.Normalize(copied) is { } address)
            {
                suggested = address;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // A desktop with no clipboard to read is not a reason to refuse to save a link.
            Log.Debug($"Save a Link: the clipboard could not be read — {ex.Message}");
        }

        Mailbox.Protocols.SavedLink? saved = null;
        var dialog = new SaveLinkDialog(
            account.Mail,
            async (address, board) =>
            {
                saved = await Mailbox.Protocols.SavedLinks.SaveAsync(
                    account, address, App.FeedReader.Fetch, DateTimeOffset.UtcNow);

                if (!saved.Ok) return (false, string.Empty, saved.Unreachable);
                if (board is { } chosen) account.Mail.SaveToBoard([saved.MessageId], chosen.Id, DateTimeOffset.UtcNow);

                return (true, saved.Card.Headline, saved.Unreachable);
            },
            preferred: _feedModule?.SelectedBoard,
            suggested: suggested);

        await dialog.ShowDialog(this);

        if (!dialog.Saved || saved is not { Ok: true } link) return;

        _feedModule?.Reload();
        shell.Refresh();

        var where = dialog.Chosen is { } chosen ? $" on {chosen.Name}" : string.Empty;
        shell.StatusRight = link.AlreadyHere
            ? $"“{link.Card.Headline}” was already saved{(where.Length > 0 ? $"; it is now{where}" : string.Empty)}."
            : $"“{link.Card.Headline}” saved{where}.";

        // Straight to the board it went on, because a save that shows nothing looks like nothing
        // happened — the same reason a new subscription is read at once.
        if (dialog.Chosen is { } opened) _feedModule?.ShowBoard(opened.Name);
    }

    /// <summary>The harness's Save a Link: the same two calls the dialog makes, and the readback.</summary>
    private async Task PoseSaveLinkAsync(ShellViewModel shell, OpenAccount account, string spec)
    {
        var parts = spec.Split('|', 2, StringSplitOptions.TrimEntries);
        var now = DateTimeOffset.UtcNow;

        var saved = await Mailbox.Protocols.SavedLinks.SaveAsync(account, parts[0], App.FeedReader.Fetch, now);

        if (!saved.Ok)
        {
            Log.Info($"Harness: “{parts[0]}” was not saved — {saved.Unreachable}");
            return;
        }

        Board? board = null;
        if (parts.Length > 1 && parts[1].Length > 0)
        {
            board = account.Mail.BoardNamed(parts[1]) ?? account.Mail.AddBoard(parts[1], now);
            account.Mail.SaveToBoard([saved.MessageId], board.Id, now);
        }

        Log.Info($"Harness: saved link “{saved.Card.Headline}” from {saved.Card.Url}"
            + $"{(board is null ? string.Empty : $" onto “{board.Name}”")}"
            + $"{(saved.AlreadyHere ? " (it was already here)" : string.Empty)}"
            + $"{(saved.Unreachable.Length > 0 ? $"; the page could not be read — {saved.Unreachable}" : string.Empty)}.");

        _feedModule?.Reload();
        shell.Refresh();

        if (board is { } opened && _feedModule is { } module)
        {
            module.ShowBoard(opened.Name);
            foreach (var headline in module.Showing.Take(5)) Log.Info($"Harness:   · {headline}");
        }
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
