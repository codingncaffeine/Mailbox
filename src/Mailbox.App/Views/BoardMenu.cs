using Avalonia.Controls;
using Mailbox.Controls.Ribbon;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// Save to Board: the boards, with a tick against the ones this article is already on.
/// </summary>
/// <remarks>
/// The same shape as the Categorize menu — a set, a tick, and a way to the dialog that manages
/// the set — because it is the same gesture, and a reader who has learnt one has learnt this.
/// What it writes is different: a board is a collection of its own, not a colour the mail
/// module offers on every message it draws.
/// <para>
/// A menu is a surface no screenshot can prove anything about, so this carries the same harness
/// door the categorize menu does: <c>MAILBOX_BOARD</c> names one and it is pressed without
/// anything being drawn, which makes the claim "the article is on the board afterwards"
/// checkable by reading the store back.
/// </para>
/// </remarks>
internal static class BoardMenu
{
    /// <summary>
    /// Opens the menu at <paramref name="anchor"/>, or — under the harness — presses one of its
    /// entries and returns without drawing anything.
    /// </summary>
    /// <param name="subject">What the article is called, for the log line.</param>
    /// <param name="messageIds">What is being saved. Usually one; the ribbon may hand over more.</param>
    /// <param name="changed">Run after the store has been written, so the pane can redraw.</param>
    /// <param name="newBoard">Asks for a name and saves onto the board it makes.</param>
    /// <param name="manage">Opens the Boards dialog, or null where the caller has nowhere to open one.</param>
    public static void Show(
        MailRepository mail,
        Control anchor,
        string subject,
        IReadOnlyCollection<long> messageIds,
        DateTimeOffset now,
        Action changed,
        Action newBoard,
        Action? manage)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(newBoard);

        if (messageIds.Count == 0) return;

        var boards = mail.Boards();
        var on = OnBoards(mail, messageIds);

        if (Environment.GetEnvironmentVariable("MAILBOX_BOARD") is { Length: > 0 } posed)
        {
            Pose(mail, posed.Trim(), subject, messageIds, on, now, changed);
            return;
        }

        var flyout = new MenuFlyout();

        if (boards.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "No boards yet", IsEnabled = false });
        }

        foreach (var board in boards)
        {
            var has = on.Contains(board.Id);
            var item = new MenuItem
            {
                Header = board.Count > 0 ? $"{board.Name}  ({board.Count})" : board.Name,
                Icon = has ? Tick() : new RibbonArtwork("bookmark", 16),
            };

            var chosen = board;
            item.Click += (_, _) =>
            {
                Toggle(mail, chosen, messageIds, has, now);
                changed();
            };

            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        var make = new MenuItem { Header = "New Board…", Icon = new RibbonArtwork("add", 16) };
        make.Click += (_, _) => newBoard();
        flyout.Items.Add(make);

        var all = new MenuItem { Header = "Manage Boards…", Icon = new RibbonArtwork("settings", 16) };
        if (manage is null) all.IsEnabled = false;
        else all.Click += (_, _) => manage();
        flyout.Items.Add(all);

        Log.Info($"Boards: the item is on {(on.Count == 0 ? "no board" : $"{on.Count} board(s)")}.");
        Log.Debug($"Boards: the item is “{subject}”.");
        flyout.ShowAt(anchor, showAtPointer: true);
    }

    /// <summary>
    /// Saves onto a board, or takes off one already carried.
    /// </summary>
    /// <remarks>
    /// A toggle rather than two entries, because the tick beside the name is what says which of
    /// the two a press will do — and because it is what the Categorize menu next to it does.
    /// </remarks>
    private static void Toggle(
        MailRepository mail, Board board, IReadOnlyCollection<long> messageIds, bool has, DateTimeOffset now)
    {
        if (has)
        {
            var removed = mail.RemoveFromBoard(messageIds, board.Id);
            Log.Info($"Boards: {removed} article(s) taken off “{board.Name}”.");
            return;
        }

        var saved = mail.SaveToBoard(messageIds, board.Id, now);
        Log.Info($"Boards: {saved} article(s) saved to “{board.Name}”.");
    }

    /// <summary>
    /// The boards every one of these messages is already on.
    /// </summary>
    /// <remarks>
    /// Every one, not any one: with more than one row selected, a tick has to mean "all of these
    /// are on it", or pressing it would take some off and put others on in the same gesture.
    /// </remarks>
    private static HashSet<long> OnBoards(MailRepository mail, IReadOnlyCollection<long> messageIds)
    {
        var carried = mail.BoardsFor(messageIds);
        var shared = new HashSet<long>();
        var first = true;

        // The first row seeds the set and every later one narrows it.
        foreach (var id in messageIds)
        {
            var here = (carried.GetValueOrDefault(id) ?? []).Select(b => b.Id).ToHashSet();

            if (first)
            {
                shared.UnionWith(here);
                first = false;
                continue;
            }

            shared.IntersectWith(here);
        }

        return shared;
    }

    /// <summary>
    /// The harness's way at the menu: <c>MAILBOX_BOARD=&lt;name&gt;</c> makes the board if it is
    /// not there and toggles the selection onto it, which is what pressing its entry does.
    /// </summary>
    private static void Pose(
        MailRepository mail,
        string wanted,
        string subject,
        IReadOnlyCollection<long> messageIds,
        HashSet<long> on,
        DateTimeOffset now,
        Action changed)
    {
        var board = mail.BoardNamed(wanted) ?? mail.AddBoard(wanted, now);
        var has = on.Contains(board.Id);

        Toggle(mail, board, messageIds, has, now);
        changed();

        Log.Info($"Harness: “{subject}” {(has ? "taken off" : "saved to")} board “{board.Name}”; "
            + $"it now holds {mail.BoardMessages(board.Id).Count} article(s).");
    }

    private static Control Tick() => new TextBlock
    {
        Text = IconGlyphs.GetOrEmpty("mark-complete", 16),
        FontFamily = IconFont.Family,
        FontSize = 12,
    };
}
