using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.App.ViewModels;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// The doors onto boards, onto a row's own buttons, and onto what the article list is holding.
/// </summary>
/// <remarks>
/// <b>Why a script and not one pose per press.</b> A board's central claim is an <em>order</em> —
/// it is read newest-<i>saved</i> first, so a piece from last year saved this morning sits above
/// this morning's headlines — and an order needs two saves of two different articles in one
/// process to say anything at all. One save per run is two boards with one thing on each. The
/// same goes for taking an article off a board, which only means anything once a board is open,
/// and for the keep-rather-than-delete fallback, which is Delete pressed over an article that is
/// already on one.
/// <para>
/// Every step goes in the way a reader's press goes in: <see cref="RunCommand"/> for the ribbon's
/// commands, so what is proved is the command rather than the repository call under it; the board
/// menu's own <c>MAILBOX_BOARD</c> door for the save; and, for the four buttons that appear on a
/// row under the pointer, the button's own <c>Click</c>. Those four had no door at all — they are
/// built per row inside a virtualising list, so nothing outside the workspace could reach one,
/// and "the row has a Delete button" was readable off the code and provable nowhere.
/// </para>
/// <para>
/// The read-back is the store — the boards table, the join with the moment each row was saved,
/// and the folder each article is still filed in — because "the bar said it worked" is the
/// evidence this audit does not accept. <c>MAILBOX_FEED_STATE=1</c> is the other half: what the
/// list is showing and what is selected, read back after every other pose has had its pass.
/// Without it a run that pressed a module-local single key had nothing to compare, the shell's
/// own after-a-keystroke line being about the mail list, which in this module is not the list
/// that moved.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this lane's doors. Called once, from the constructor.</summary>
    private void WirePhase11BDoors()
    {
        // Both at Background and both from a Background pass of their own, so they land after the
        // module switch (Normal), after MAILBOX_RUN (Normal), after MAILBOX_KEY's presses
        // (Background) and after MAILBOX_BOARD_VIEW (Background) — the state a run ends with is
        // the claim, not the state half-way through it.
        if (Environment.GetEnvironmentVariable("MAILBOX_FEED_DO") is { Length: > 0 } steps)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => Dispatcher.UIThread.Post(() => PoseFeedSteps(steps), DispatcherPriority.Background),
                DispatcherPriority.Background);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_FEED_STATE") is "1" or "true")
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => Dispatcher.UIThread.Post(ReportFeedState, DispatcherPriority.Background),
                DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Presses a list of steps in order: <c>pick:3;save:Keep;view:Keep;off;report</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>pick:&lt;n&gt;</c> — chooses the nth article showing, which is the
    /// click a capture cannot make.</description></item>
    /// <item><description><c>save:&lt;board&gt;</c> — presses Save to Board with the menu pointed
    /// at that board, making it if it is not there. A second save of the same article onto the
    /// same board takes it off again, because the menu entry is a toggle and the tick beside the
    /// name is what says which.</description></item>
    /// <item><description><c>view:&lt;board&gt;</c> — opens the board's row in the pane.</description></item>
    /// <item><description><c>off</c> — presses Take Off Board.</description></item>
    /// <item><description><c>binned</c> — presses the ribbon's Delete over whatever is selected.</description></item>
    /// <item><description><c>key:&lt;chord&gt;</c> — presses a key at the workspace, for the
    /// module-local ones that only mean anything after another step.</description></item>
    /// <item><description><c>press:&lt;n&gt;:&lt;tip&gt;</c> — presses the button on the nth row
    /// whose tooltip carries that text, which is how the four hover buttons are reached.</description></item>
    /// <item><description><c>buttons:&lt;n&gt;</c> — what the nth row's strip actually offers.</description></item>
    /// <item><description><c>enabled:&lt;command&gt;</c> — whether the bar draws that command black
    /// or greyed in the state the steps before it have made.</description></item>
    /// <item><description><c>report</c> — the boards, read back out of the store.</description></item>
    /// <item><description><c>folders</c> — the feed folder tree, read back out of the store, which
    /// is where an organise verb has to land and the one place a status line cannot say it
    /// did.</description></item>
    /// </list>
    /// </remarks>
    private void PoseFeedSteps(string steps)
    {
        if (DataContext is not ShellViewModel shell) return;
        if (shell.Module != MailboxModule.Feeds)
        {
            Log.Info("Harness: MAILBOX_FEED_DO wants the feeds module — pose MAILBOX_MODULE=feeds as well.");
            return;
        }

        var feeds = EnsureFeeds(shell);

        foreach (var step in steps.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = step.IndexOf(':');
            var verb = (colon > 0 ? step[..colon] : step).ToLowerInvariant();
            var arg = colon > 0 ? step[(colon + 1)..].Trim() : string.Empty;

            switch (verb)
            {
                case "pick" when int.TryParse(arg, System.Globalization.CultureInfo.InvariantCulture, out var nth):
                    Log.Info($"Harness: step pick {nth} — {feeds.PoseSelect(nth)}.");
                    break;

                case "save":
                    // Through the ribbon's own command, with the menu's harness door pointed at
                    // this board for the length of the press: that door is read when the menu is
                    // built, so setting it here is what names the entry being pressed.
                    Environment.SetEnvironmentVariable("MAILBOX_BOARD", arg);
                    RunCommand(new CommandId("feeds.board.save"));
                    Log.Info($"Harness: step save “{arg}” — {shell.StatusRight}");
                    break;

                case "view":
                    Log.Info(feeds.ShowBoard(arg)
                        ? $"Harness: step view “{arg}” — {feeds.Status}."
                        : $"Harness: step view — there is no board called “{arg}”.");
                    break;

                case "off":
                    RunCommand(new CommandId("feeds.board.remove"));
                    Said(shell, feeds, "off");
                    break;

                case "binned":
                    RunCommand(new CommandId("feeds.delete"));
                    Said(shell, feeds, "binned");
                    break;

                case "key":
                    // The same route MAILBOX_KEY takes. Here as a step as well because some of the
                    // module-local keys only mean anything in a state another step has to make
                    // first: Shift+B takes an article off the board you are reading, and
                    // MAILBOX_KEY presses before anything has opened one.
                    feeds.Focus();
                    PressChord(arg);
                    Said(shell, feeds, $"key {arg}");
                    break;

                case "press":
                    PressRowButton(feeds, arg);
                    break;

                case "buttons":
                    ReportRowButtons(feeds, arg);
                    break;

                case "enabled":
                    // Whether the bar draws a command black or greyed, which is a different
                    // question from whether pressing it does anything: a command that cannot act
                    // and is drawn black is a button that answers with a sentence in the status
                    // bar, and a screenshot cannot tell the two states apart at a glance.
                    RefreshCommandEnablement();
                    Log.Info($"Harness: {arg} is "
                        + (_ribbon?.ControlFor(new CommandId(arg)) is { } control
                            ? control.IsEnabled ? "drawn black" : "greyed"
                            : "not on this bar"));
                    break;

                case "menu":
                    Log.Info($"Harness: step menu {arg} — {feeds.PoseMenu(arg)}.");
                    break;

                case "report":
                    ReportBoards();
                    break;

                case "folders":
                    ReportFeedFolders();
                    break;

                default:
                    Log.Info($"Harness: “{verb}” is not a feeds step.");
                    break;
            }
        }

        Environment.SetEnvironmentVariable("MAILBOX_BOARD", null);
        ReportBoards();
    }

    /// <summary>
    /// Both halves of what a press says: the shell's own line and the module's.
    /// </summary>
    /// <remarks>
    /// Both, because they are written by different things and a run that read one of them was
    /// reading a sentence some earlier press had left there. The workspace writes its own
    /// <c>Status</c> and the shell's right-hand line is written by the window; a command that
    /// writes one and not the other looks, from whichever half is being watched, like a command
    /// that did nothing.
    /// </remarks>
    private static void Said(ShellViewModel shell, FeedsWorkspace feeds, string step)
        => Log.Info($"Harness: step {step} — module says “{feeds.Status}”, shell says “{shell.StatusRight}”.");

    /// <summary>
    /// Presses one of the buttons that appear on a row under the pointer: <c>&lt;n&gt;:&lt;tip&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Through the button's own <see cref="Button.ClickEvent"/>, which is what a pointer press
    /// ends in, and found by the tooltip because that is the only thing about one of these buttons
    /// a reader can see. Named by row rather than by a run of every button in the list: the strip
    /// is built per row, and the fourth row's Delete is not the first row's.
    /// </remarks>
    private void PressRowButton(FeedsWorkspace feeds, string spec)
    {
        var parts = spec.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var nth))
        {
            Log.Info($"Harness: “{spec}” is not <row>:<tooltip>.");
            return;
        }

        if (RowAt(feeds, nth) is not { } row)
        {
            Log.Info($"Harness: row {nth} is not drawn — the list virtualises, so only what is on screen has buttons.");
            return;
        }

        var button = row.GetVisualDescendants().OfType<Button>().FirstOrDefault(b =>
            ToolTip.GetTip(b) is string tip && tip.Contains(parts[1], StringComparison.OrdinalIgnoreCase));

        if (button is null)
        {
            Log.Info($"Harness: row {nth} has no button reading “{parts[1]}” — it offers {Tips(row)}.");
            return;
        }

        Log.Info($"Harness: pressing “{ToolTip.GetTip(button)}” on row {nth}.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>What a row's own strip offers, which is a claim about four buttons.</summary>
    private void ReportRowButtons(FeedsWorkspace feeds, string spec)
    {
        if (!int.TryParse(spec, System.Globalization.CultureInfo.InvariantCulture, out var nth)) return;

        Log.Info(RowAt(feeds, nth) is { } row
            ? $"Harness: row {nth} offers {Tips(row)}."
            : $"Harness: row {nth} is not drawn.");
    }

    private static string Tips(Visual row)
    {
        var tips = row.GetVisualDescendants().OfType<Button>()
            .Select(b => ToolTip.GetTip(b) as string)
            .Where(t => t is { Length: > 0 })
            .ToList();

        return tips.Count == 0 ? "no buttons" : $"{tips.Count} buttons: {string.Join(", ", tips.Select(t => $"“{t}”"))}";
    }

    /// <summary>
    /// The nth realised row of the article list, or null when the list has not drawn it.
    /// </summary>
    /// <remarks>
    /// Through the list's own <c>ContainerFromIndex</c> rather than by collecting
    /// <see cref="ListBoxItem"/>s out of the visual tree: the panel virtualises, so what is in the
    /// tree is the dozen rows on screen in whatever order the recycler is holding them, and the
    /// third one found is not the third article. The list is found by being the one with as many
    /// items as the workspace says it is showing — the reading pane on the right has lists of its
    /// own.
    /// </remarks>
    private static Control? RowAt(FeedsWorkspace feeds, int nth)
    {
        // A step before this one may have replaced the list's items — saving to a board redraws it
        // — and nothing has been through a layout pass since, so the containers a press needs do
        // not exist yet. Without this the first press after a save reported "row 0 is not drawn"
        // over a list a capture taken a moment later plainly shows.
        feeds.UpdateLayout();

        var lists = feeds.GetVisualDescendants().OfType<ListBox>().ToList();
        if (lists.Count == 0)
        {
            Log.Info("Harness: the article list is not in the tree yet.");
            return null;
        }

        var showing = feeds.Showing.Count();
        var list = lists.FirstOrDefault(l => l.ItemCount == showing) ?? lists[0];

        Log.Info($"Harness: the workspace holds {lists.Count} list(s) of "
            + $"[{string.Join(", ", lists.Select(l => l.ItemCount))}] against {showing} showing; "
            + $"the one chosen has realised {list.GetRealizedContainers().Count()}.");

        return list.ContainerFromIndex(nth) as Control;
    }

    /// <summary>
    /// Every board and what is on it, in the order the pane reads them, out of the store.
    /// </summary>
    /// <remarks>
    /// The moment each row was saved is printed beside it because that is what the order is by —
    /// a listing without it cannot tell "newest saved first" from "newest published first" when
    /// the two happen to agree. The folder each article is still filed in is printed for the other
    /// half of the same claim: saving does not move an article out of its feed, and deleting one
    /// that is on a board keeps it rather than taking it away.
    /// </remarks>
    private void ReportBoards()
    {
        if (App.FeedStore?.Account is not { } account)
        {
            Log.Info("Harness: boards — there is no feeds store.");
            return;
        }

        var folders = account.Mail.Folders(account.Account.Id).ToDictionary(f => f.Id);

        string Path(long folderId)
        {
            if (!folders.TryGetValue(folderId, out var folder)) return $"folder {folderId}";
            return folder.ParentId is { } up && folders.TryGetValue(up, out var parent)
                ? $"{parent.Name}/{folder.Name}"
                : folder.Name;
        }

        var boards = account.Mail.Boards();
        Log.Info($"Harness: boards — {boards.Count} in the store.");

        foreach (var board in boards)
        {
            var saved = account.Mail.BoardItems(board.Id).ToDictionary(i => i.MessageId, i => i.SavedUtc);

            Log.Info($"Harness: board “{board.Name}” (id {board.Id}, ordinal {board.Ordinal}) holds {board.Count}"
                + (board.Description.Length > 0 ? $" — {board.Description}" : string.Empty));

            foreach (var article in account.Mail.BoardMessages(board.Id))
            {
                Log.Info($"Harness:   · {article.Subject} — in {Path(article.FolderId)}, "
                    + $"published {article.Received:yyyy-MM-dd HH:mm:ss}, "
                    + $"saved {saved.GetValueOrDefault(article.Id):yyyy-MM-dd HH:mm:ss}");
            }
        }
    }

    /// <summary>
    /// The feed folder tree as the store holds it, with what is in each folder.
    /// </summary>
    /// <remarks>
    /// The organise verbs are claims about this and about nothing else. A heading is a folder, and
    /// moving a feed under one moves its whole history with it; renaming a feed renames the folder
    /// its articles are filed in; removing a heading puts what was under it back at the top. Every
    /// one of those writes a status line saying it worked, and the status line is written by the
    /// same method whether the folder moved or not.
    /// </remarks>
    private void ReportFeedFolders()
    {
        if (App.FeedStore?.Account is not { } account)
        {
            Log.Info("Harness: folders — there is no feeds store.");
            return;
        }

        var all = account.Mail.Folders(account.Account.Id);
        var root = all.FirstOrDefault(f => f.ParentId is null && f.Name == Mailbox.Protocols.FeedReceiver.RootFolder);

        if (root is null)
        {
            Log.Info("Harness: folders — there is no feeds root.");
            return;
        }

        Log.Info($"Harness: folders — under {root.Name}:");

        foreach (var child in all.Where(f => f.ParentId == root.Id).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            Log.Info($"Harness: folders   {child.Name} — {child.Total} article(s), {child.Unread} unread");

            foreach (var grandchild in all.Where(f => f.ParentId == child.Id).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                Log.Info($"Harness: folders     {child.Name}/{grandchild.Name} — "
                    + $"{grandchild.Total} article(s), {grandchild.Unread} unread");
            }
        }

        Log.Info("Harness: folders — subscriptions say "
            + string.Join("; ", App.Feeds.InOrder.Select(f => $"{f.Name} → {f.FolderPath}")));
    }

    /// <summary>What the article list is showing and what is selected, after every other pose.</summary>
    /// <remarks>
    /// The module-local single keys move a selection inside this workspace, and nothing else in a
    /// run says where they left it: the shell's own after-a-keystroke line reports the mail list,
    /// which in this module is not the list that moved. Read, kept-for-later and which boards it
    /// is on come with it, because <c>m</c>, <c>s</c>, <c>x</c> and <c>b</c> are claims about
    /// exactly those three.
    /// </remarks>
    private void ReportFeedState()
    {
        if (_feedModule is not { } feeds)
        {
            Log.Info("Harness: feeds state — the module was never opened.");
            return;
        }

        // The windows too: two of the module-local keys open one — o opens the article and ? opens
        // the list of the keys themselves — and a run cannot photograph a modal it is behind, so
        // "nothing happened" and "a window opened" are otherwise the same evidence.
        Log.Info($"Harness: feeds state — showing {feeds.Status}; "
            + $"feed “{feeds.SelectedFeed?.Name ?? "none"}”, board “{feeds.SelectedBoard?.Name ?? "none"}”; "
            + $"windows {(OwnedWindows.Count == 0 ? "none" : string.Join(", ", OwnedWindows.Select(w => $"“{w.Title}”")))}.");

        if (feeds.SelectedArticle is not { } article)
        {
            Log.Info("Harness: feeds state — nothing is selected.");
        }
        else
        {
            var on = App.FeedStore?.Account is { } account
                ? account.Mail.BoardsFor([article.Id]).GetValueOrDefault(article.Id) ?? []
                : [];

            Log.Info($"Harness: feeds state — selected “{article.Subject}”, "
                + $"{(article.IsRead ? "read" : "unread")}, "
                + $"{(article.IsFlagged ? "kept for later" : "not kept")}, "
                + $"on {(on.Count == 0 ? "no board" : string.Join(", ", on.Select(b => b.Name)))}.");
        }

        var at = 0;
        foreach (var headline in feeds.Showing.Take(4)) Log.Info($"Harness: feeds state   {at++}: {headline}");
    }
}
