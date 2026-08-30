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

    /// <summary>
    /// Where a picture comes from when the feed sent none, shared across a session.
    /// </summary>
    /// <remarks>
    /// Session-lived rather than per-workspace so that what it has already asked about survives
    /// the reader switching modules and coming back, which would otherwise be a fresh round of
    /// requests to a publisher for pictures already looked up.
    /// </remarks>
    private FeedPictureLookup? _feedLookup;

    /// <summary>The Feeds ribbon: the shipped layout with the reader's edits over it.</summary>
    private static RibbonLayout FeedsRibbon() => App.RibbonEdits.Apply(App.Plugins.InjectRibbon(FeedsRibbonLayout.Build()));

    /// <summary>
    /// Where feed articles are filed: the feed reader's own store.
    /// </summary>
    /// <remarks>
    /// Its own file, not one of the reader's mail accounts. It used to be whichever account
    /// sorted first, which was wrong in principle — a subscription belongs to the reader, and
    /// nothing about a feed has anything to do with the server that carries their post — and
    /// unstable in practice: adding an account that sorted ahead of the old one pointed the whole
    /// module at a store with no feed folders, so the subscriptions appeared to empty themselves
    /// while the articles sat in a file nothing was looking at any more.
    /// </remarks>
    private static OpenAccount? FeedAccount() => App.FeedStore?.Account;

    private FeedsWorkspace EnsureFeeds(ShellViewModel shell)
    {
        if (_feedModule is not null) return _feedModule;

        _feedLookup ??= new FeedPictureLookup(FeedAccount, () => App.FeedReader?.Fetch);
        ApplyFeedReadingOptions();

        var workspace = new FeedsWorkspace(App.Feeds, FeedAccount, _feedPictures, _feedLookup)
        {
            IsNavVisible = shell.NavVisible,
            MessageFontSize = shell.ReadingFontSize,
        };

        // The status bar's zoom, followed here as it is in mail. It reached one pane and not the
        // other, which for the module a reader reads most in was the wrong one to miss.
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ShellViewModel.ReadingFontSize)) return;
            if (_feedModule is { } live) live.MessageFontSize = shell.ReadingFontSize;
        };

        workspace.AddRequested += (_, _) => _ = SubscribeToFeedAsync(shell);
        workspace.NewBoardRequested += (_, _) => _ = BoardsAsync(shell, string.Empty);
        workspace.SaveLinkRequested += (_, _) => _ = SaveLinkAsync(shell);
        workspace.SaveToBoardRequested += (_, anchor) => SaveToBoard(shell, workspace, anchor);
        workspace.RefreshRequested += (_, _) => _ = UpdateFeedsAsync(shell, force: true);
        workspace.OpenRequested += (_, id) => OpenFeedArticle(shell, id);
        workspace.ShortcutsRequested += (_, list) => _ = Confirm.ShowAsync(this, "Keyboard shortcuts", list);
        workspace.FullTextWanted += (_, article) => _ = FillInArticleAsync(shell, workspace, article);

        // The pane's own menu. Everything here was previously reachable only from the ribbon, or
        // — for renaming, moving and copying an address — not at all.
        workspace.UpdateFeedRequested += (_, feed) => _ = UpdateFeedsAsync(shell, force: true, only: feed);
        workspace.FeedSettingsRequested += (_, feed) => _ = FeedSettingsAsync(shell, feed);
        workspace.UnsubscribeRequested += (_, feed) => _ = UnsubscribeAsync(shell, feed);
        workspace.RenameFeedRequested += (_, feed) => _ = RenameFeedAsync(shell, feed);
        workspace.MoveFeedRequested += (_, move) => MoveFeed(shell, move.Feed, move.Category);
        workspace.NewHeadingRequested += (_, feed) => _ = NewHeadingAsync(shell, feed);
        workspace.RenameHeadingRequested += (_, heading) => _ = RenameHeadingAsync(shell, heading);
        workspace.RemoveHeadingRequested += (_, heading) => _ = RemoveHeadingAsync(shell, heading);
        workspace.ManageBoardsRequested += (_, _) => _ = BoardsAsync(shell, string.Empty);
        workspace.CopyRequested += (_, text) => _ = CopyToClipboardAsync(shell, text);
        workspace.PauseFeedRequested += (_, feed) => PauseFeed(shell, feed);
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

            // With no feed chosen this used to fall through to a forced pass over every
            // subscription — ignoring each one's own interval and every publisher's update limit,
            // which is the one thing the polling layer exists to avoid. It says what the module's
            // other three per-feed commands say instead.
            case "feeds.update.one" when feeds.SelectedFeed is null:
                shell.StatusRight = "Choose a feed in the list first.";
                return true;

            case "feeds.update.one":
                _ = UpdateFeedsAsync(shell, force: true, only: feeds.SelectedFeed);
                return true;

            case "feeds.markallread":
                MarkFeedsRead(shell, feeds);
                return true;

            // The per-article commands with nothing to act on: each used to return without a
            // word, which reads exactly like a button that does not work. The refusal is the
            // module's own family voice, and the sweep's guard poses hold it.
            case "feeds.readlater" when feeds.SelectedArticle is null:
            case "feeds.open.original" when feeds.SelectedArticle is null:
            case "feeds.delete" when feeds.SelectedArticle is null:
            case "feeds.categorize" when feeds.SelectedArticle is null:
                shell.StatusRight = "Select an article first.";
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

            case "feeds.next.unread":
                _ = feeds.NextUnreadAsync(scrollFirst: true);
                return true;

            case "feeds.pause":
                PauseFeed(shell, feeds.SelectedFeed);
                return true;

            case "feeds.reading":
                _ = ReadingOptionsAsync(shell);
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

        // MAILBOX_SUBSCRIBE types the address and, when it says so, presses Subscribe — so the
        // whole flow, finding through to the first read, is provable rather than only the box.
        if (Environment.GetEnvironmentVariable("MAILBOX_SUBSCRIBE") is { Length: > 0 } typed)
        {
            dialog.Opened += (_, _) => dialog.Pose(typed);
        }

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

            // What arrived is announced rather than inserted: putting new articles into the list
            // under a reader moves the one they are reading down the screen while they read it.
            if (report.Delivered > 0) _feedModule?.Announce(report.Delivered);
            else _feedModule?.Reload();
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
            Log.Warn("Feeds: an article could not be opened.", ex);
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

        // MAILBOX_FEED_OPTIONS fills the boxes in and presses a button, which a capture cannot.
        if (Environment.GetEnvironmentVariable("MAILBOX_FEED_OPTIONS") is { Length: > 0 } posed)
        {
            dialog.Opened += (_, _) => dialog.Pose(posed);
        }

        await dialog.ShowDialog(this);

        if (!dialog.Changed) return;

        FollowTheFolder(feed);

        _feedModule?.Reload();
        shell.Refresh();
        shell.StatusRight = $"“{feed.Name}” updated.";
    }

    /// <summary>
    /// Takes a feed's folder wherever its name and its heading have just been changed to.
    /// </summary>
    /// <remarks>
    /// A feed's folder is named after the feed and sits inside its heading, so both of those boxes
    /// in the options dialog are really instructions about the folder — and the dialog only writes
    /// the subscription. Left to itself, changing either in the dialog emptied the feed on the
    /// spot (its articles were in a folder nothing pointed at any more) and the next poll filed
    /// every entry a second time under the new name, so a four-article feed became seven articles
    /// in two folders and the reader's flags and read marks stayed with the copies nothing showed.
    /// The pane's own Rename and Move have always moved the folder first; this is the same two
    /// calls, in the same order, for the way in the reference draws.
    /// <para>
    /// <paramref name="before"/> is the subscription as it was, because that is what says where
    /// the folder is now.
    /// </para>
    /// </remarks>
    private void FollowTheFolder(FeedSubscription before)
    {
        if (FeedAccount() is not { } account) return;
        if (App.Feeds.Find(before.Url) is not { } now) return;

        if (!string.Equals(now.Category, before.Category, StringComparison.Ordinal)
            && !Mailbox.Protocols.FeedReceiver.MoveToHeading(account, before, now.Category))
        {
            return;
        }

        if (!string.Equals(now.Name, before.Name, StringComparison.Ordinal)
            && Mailbox.Protocols.FeedReceiver.Folder(account, before with { Category = now.Category }) is { } folder)
        {
            account.Mail.RenameFolder(folder.Id, now.Name, null);
        }

        // A heading nothing is filed under any more does not linger in the pane as an empty one.
        if (before.Category.Length > 0
            && !string.Equals(now.Category, before.Category, StringComparison.Ordinal)
            && App.Feeds.Under(before.Category).Count == 0)
        {
            Mailbox.Protocols.FeedReceiver.RemoveEmptyHeading(account, before.Category);
        }
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

        Unsubscribe(shell, feed);
    }

    /// <summary>Drops a subscription once the reader has agreed to it.</summary>
    /// <remarks>
    /// Split from the question so a run can reach it: a modal blocks a capture, and a pose that
    /// wrote the removal itself would prove the store rather than this.
    /// </remarks>
    private void Unsubscribe(ShellViewModel shell, FeedSubscription feed)
    {
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
        if (App.Accounts is null || App.Accounts.All.Count == 0)
        {
            shell.StatusRight = "There is no mail account to read newsletters from.";
            return;
        }

        var dialog = new NewslettersDialog(App.Feeds, FeedAccount, () => App.Accounts.All);
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

    /// <summary>
    /// The harness's way at the pane's own menu, which a capture cannot open.
    /// </summary>
    /// <remarks>
    /// Calls the same handlers the menu entries call, so what is proved is what a press does
    /// rather than that a menu can be built. The prompts are the one thing skipped: a modal
    /// blocks a run, and the name it would have asked for is given on the command line.
    /// </remarks>
    private void PoseFeedOrganise(ShellViewModel shell, FeedsWorkspace feeds, string spec)
    {
        var parts = spec.Split('|', StringSplitOptions.TrimEntries);
        var verb = parts[0].ToLowerInvariant();
        string Arg(int at) => parts.Length > at ? parts[at] : string.Empty;

        Mailbox.Core.Feeds.FeedSubscription? Named(string name)
            => App.Feeds.All.FirstOrDefault(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

        switch (verb)
        {
            case "newheading":
                Log.Info($"Harness: heading “{Arg(1)}” made: {App.Feeds.AddCategory(Arg(1))}.");
                break;

            case "move" when Named(Arg(1)) is { } moving:
                MoveFeed(shell, moving, Arg(2));
                break;

            case "renameheading":
                _ = RenameHeadingPosed(shell, Arg(1), Arg(2));
                break;

            case "removeheading":
                if (FeedAccount() is { } store)
                {
                    foreach (var under in App.Feeds.Under(Arg(1)))
                    {
                        Mailbox.Protocols.FeedReceiver.MoveToHeading(store, under, string.Empty);
                    }

                    App.Feeds.RemoveCategory(Arg(1));
                    Mailbox.Protocols.FeedReceiver.RemoveEmptyHeading(store, Arg(1));
                }

                break;

            case "seen" when int.TryParse(Arg(1), out var minutes):
                Log.Info($"Harness: last looked at this row {minutes} minute(s) ago — {feeds.PoseLastSeen(minutes)}.");
                return;

            case "pressnewheading":
                feeds.PoseNewHeading();

                // What the press opened, read back after the dialog has had a turn to appear: the
                // claim is that the row reaches it, and a run cannot photograph a modal it is
                // blocked behind.
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => Log.Info("Harness: pressing “New heading…” opened: "
                        + (OwnedWindows.Count == 0
                            ? "nothing"
                            : string.Join(", ", OwnedWindows.Select(w => $"“{w.Title}”")))),
                    Avalonia.Threading.DispatcherPriority.Background);
                return;

            // The two verbs that ask a question first. Both go in below the prompt rather than
            // writing the change themselves, so what a run proves is the handler a reader reaches.
            case "renamefeed" when Named(Arg(1)) is { } renaming:
                RenameFeed(shell, renaming, Arg(2));
                break;

            case "unsubscribe" when Named(Arg(1)) is { } dropping:
                Unsubscribe(shell, dropping);
                break;

            case "drop":
                Log.Info($"Harness: {feeds.PoseDrop(Arg(1), Arg(2))}.");
                break;

            case "unreadonly":
                feeds.PoseToggle(unreadOnly: true);
                break;

            case "oldestfirst":
                feeds.PoseToggle(unreadOnly: false);
                break;

            default:
                Log.Info($"Harness: “{verb}” is not something the pane's menu does.");
                return;
        }

        _feedModule?.Reload();
        shell.Refresh();

        Log.Info($"Harness: headings now [{string.Join(", ", App.Feeds.Categories)}]; "
            + string.Join("; ", App.Feeds.InOrder.Select(f =>
                $"{f.Name} under “{f.Category}” at {f.Ordinal}{(f.Paused ? " (paused)" : string.Empty)}")));
        foreach (var line in feeds.Showing.Take(3)) Log.Info($"Harness:   · {line}");
    }

    /// <summary>The rename without its prompt, for a run.</summary>
    private async Task RenameHeadingPosed(ShellViewModel shell, string from, string to)
    {
        await Task.Yield();

        if (FeedAccount() is { } account) Mailbox.Protocols.FeedReceiver.RenameHeading(account, from, to);

        var moved = App.Feeds.RenameCategory(from, to);
        Log.Info($"Harness: heading “{from}” renamed to “{to}”, {moved} feed(s) moved.");

        _feedModule?.Reload();
        shell.Refresh();
    }

    /// <summary>Stops or restarts a feed without unsubscribing from it.</summary>
    private void PauseFeed(ShellViewModel shell, FeedSubscription? feed)
    {
        if (feed is null)
        {
            shell.StatusRight = "Choose a feed in the list first.";
            return;
        }

        App.Feeds.Pause(feed.Url, !feed.Paused);
        _feedModule?.Reload();

        shell.StatusRight = feed.Paused
            ? $"“{feed.Name}” will be read again."
            : $"“{feed.Name}” paused. It stays in your list and is not asked for.";
    }

    /// <summary>The module's own reading settings, which had nowhere to live.</summary>
    private async Task ReadingOptionsAsync(ShellViewModel shell)
    {
        var dialog = new FeedReadingDialog(App.Settings, App.MailOptions);
        await dialog.ShowDialog(this);

        if (!dialog.Changed) return;

        ApplyFeedReadingOptions();
        _feedModule?.Reload();

        // The folder pane is the mail module's, so it has to be told: the switch that puts the
        // feeds store in it lives here.
        shell.Refresh();
        shell.StatusRight = "Reading settings saved.";
    }

    /// <summary>
    /// Puts the module's settings where the things that act on them can see them.
    /// </summary>
    /// <remarks>
    /// Called at startup and after the dialog, so turning pictures off takes effect at once
    /// rather than on the next launch — which is what "the setting is honoured" has to mean.
    /// </remarks>
    private void ApplyFeedReadingOptions()
    {
        _feedPictures.Enabled = App.MailOptions.FeedPictures;
        if (!App.MailOptions.FeedPictures) _feedPictures.Forget();

        if (_feedLookup is { } lookup)
        {
            lookup.Enabled = App.MailOptions.FeedPictures;
            if (App.MailOptions.FeedPictures) lookup.Forget();
        }

        App.FeedReader.DefaultRefresh = App.Settings.GetNumber(FeedReadingDialog.IntervalKey, 0) is > 0 and var minutes
            ? TimeSpan.FromMinutes(minutes)
            : null;
    }

    // ---- Organising the tree --------------------------------------------------------------------------

    /// <summary>
    /// Gives a feed a different name.
    /// </summary>
    /// <remarks>
    /// The name is also the folder its articles are filed in, so the folder moves with it — a
    /// rename that touched only the subscription would leave the whole feed's history behind in
    /// a folder nothing points at any more.
    /// </remarks>
    private async Task RenameFeedAsync(ShellViewModel shell, FeedSubscription feed)
    {
        var typed = await NameDialog.AskAsync(this, "Rename Feed", "What should this feed be called?", feed.Name);
        if (typed is not { Length: > 0 } name || name == feed.Name) return;

        RenameFeed(shell, feed, name);
    }

    /// <summary>Gives a feed the name the reader typed, and moves its folder with it.</summary>
    /// <remarks>Split from the prompt for the reason <see cref="Unsubscribe"/> is.</remarks>
    private void RenameFeed(ShellViewModel shell, FeedSubscription feed, string name)
    {
        if (FeedAccount() is { } account
            && Mailbox.Protocols.FeedReceiver.Folder(account, feed) is { } folder)
        {
            account.Mail.RenameFolder(folder.Id, name, null);
        }

        App.Feeds.Rename(feed.Url, name);
        _feedModule?.Reload();
        shell.Refresh();
        shell.StatusRight = $"“{feed.Name}” is now “{name}”.";
    }

    /// <summary>Files a feed under a heading, moving its folder with it.</summary>
    private void MoveFeed(ShellViewModel shell, FeedSubscription feed, string category)
    {
        if (FeedAccount() is not { } account) return;

        // The folder first, because it is found by where the subscription still says it is.
        if (!Mailbox.Protocols.FeedReceiver.MoveToHeading(account, feed, category))
        {
            shell.StatusRight = $"“{feed.Name}” could not be moved — something of that name is already there.";
            return;
        }

        var was = feed.Category;
        App.Feeds.Recategorize(feed.Url, category);

        // An emptied heading does not linger in the folder pane as a folder with nothing in it.
        if (was.Length > 0 && App.Feeds.Under(was).Count == 0)
        {
            Mailbox.Protocols.FeedReceiver.RemoveEmptyHeading(account, was);
        }

        _feedModule?.Reload();
        shell.Refresh();
        shell.StatusRight = category.Length > 0
            ? $"“{feed.Name}” moved to {category}."
            : $"“{feed.Name}” is no longer under a heading.";
    }

    /// <summary>Makes a heading, and puts a feed straight into it when one was pointed at.</summary>
    private async Task NewHeadingAsync(ShellViewModel shell, FeedSubscription? feed)
    {
        var typed = await NameDialog.AskAsync(this, "New Heading",
            "Headings group your subscriptions, and their unread counts add up. What is this one called?");

        if (typed is not { Length: > 0 } name) return;

        if (App.Feeds.Categories.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
        {
            shell.StatusRight = $"There is already a heading called “{name}”.";
            return;
        }

        App.Feeds.AddCategory(name);

        // Straight in, when the reader made it from a feed's own menu — which is the gesture that
        // means "and put this one in it".
        if (feed is not null)
        {
            MoveFeed(shell, feed, name);
            return;
        }

        _feedModule?.Reload();
        shell.StatusRight = $"“{name}” made. Move a feed into it from the feed's own menu.";
    }

    private async Task RenameHeadingAsync(ShellViewModel shell, string heading)
    {
        var typed = await NameDialog.AskAsync(this, "Rename Heading", "What should this heading be called?", heading);
        if (typed is not { Length: > 0 } name || name == heading) return;

        if (FeedAccount() is { } account) Mailbox.Protocols.FeedReceiver.RenameHeading(account, heading, name);

        var moved = App.Feeds.RenameCategory(heading, name);
        if (moved == 0)
        {
            shell.StatusRight = $"“{heading}” could not be renamed — there is already a heading called “{name}”.";
            return;
        }

        _feedModule?.Reload();
        shell.Refresh();
        shell.StatusRight = $"“{heading}” is now “{name}”, with {moved} feed{(moved == 1 ? string.Empty : "s")} under it.";
    }

    private async Task RemoveHeadingAsync(ShellViewModel shell, string heading)
    {
        var under = App.Feeds.Under(heading).Count;

        if (!await Confirm.AskAsync(this, "Remove Heading",
                under == 0
                    ? $"Remove the heading “{heading}”?"
                    : $"Remove the heading “{heading}”?\n\nIts {under} "
                      + $"feed{(under == 1 ? string.Empty : "s")} and everything they have delivered "
                      + "stay where they are, at the top level.",
                "Remove")) return;

        if (FeedAccount() is { } account)
        {
            foreach (var feed in App.Feeds.Under(heading))
            {
                Mailbox.Protocols.FeedReceiver.MoveToHeading(account, feed, string.Empty);
            }
        }

        App.Feeds.RemoveCategory(heading);
        if (FeedAccount() is { } store) Mailbox.Protocols.FeedReceiver.RemoveEmptyHeading(store, heading);

        _feedModule?.Reload();
        shell.Refresh();
        shell.StatusRight = $"“{heading}” removed; its feeds are at the top level.";
    }

    private async Task CopyToClipboardAsync(ShellViewModel shell, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try
        {
            await clipboard.SetTextAsync(text);
            shell.StatusRight = "Copied.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Log.Debug($"Feeds: the clipboard would not take it — {ex.Message}");
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
        // A picker is a desktop window a headless run cannot answer, so the harness names the
        // file and a reader is always asked — the same bargain the calendar's export makes.
        var chosen = HarnessOpenPath("opml");
        if (chosen is null)
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

            chosen = files.FirstOrDefault()?.TryGetLocalPath();
        }

        if (chosen is null) return;
        var name = System.IO.Path.GetFileName(chosen);

        try
        {
            var entries = Opml.Read(await File.ReadAllTextAsync(chosen));

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

            Log.Info($"Feeds: imported {added} of {entries.Count} from {name}.");

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

        // The same bargain as the import: a harness names the file, a reader is always asked.
        var path = HarnessSavePath("opml");
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Feeds",
                SuggestedFileName = "feeds.opml",
                DefaultExtension = "opml",
                FileTypeChoices = [new FilePickerFileType("Outline files") { Patterns = ["*.opml"] }],
            });

            path = file?.TryGetLocalPath();
        }

        if (path is null) return;

        try
        {
            var text = Opml.Write("Mailbox subscriptions",
                App.Feeds.All.Select(f => new OpmlEntry(f.Name, f.Url, f.Category, f.SiteUrl)),
                DateTimeOffset.Now);

            await File.WriteAllTextAsync(path, text);

            shell.StatusRight = $"{App.Feeds.All.Count} feed{(App.Feeds.All.Count == 1 ? string.Empty : "s")} "
                + $"written to {System.IO.Path.GetFileName(path)}.";
            Log.Info($"Feeds: exported {App.Feeds.All.Count} to {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Confirm.TellAsync(this, "Export Feeds", $"That file could not be written.\n\n{ex.Message}");
        }
    }
}
