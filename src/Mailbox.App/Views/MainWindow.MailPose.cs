using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Store.Lists;

namespace Mailbox.App.Views;

/// <summary>
/// The mail module's own poses: which arrangement the list is in, and what the list and the
/// folder pane actually hold once it is.
/// </summary>
/// <remarks>
/// A capture proves a list was drawn. It does not prove which rows are in it, what order they
/// came out in, which group each fell into, or that the count beside a folder is the count in
/// its store — and those are the claims the mail module is made of. The status bar was the only
/// read-back the shell had, and it reports two numbers about one folder.
/// <para>
/// So the two dumps here write what is on screen in a form that can be compared against the
/// store with a query rather than an eye. They read <c>VisibleRows</c> and <c>Folders</c> — what
/// the list and the pane draw — never the store, because reading the store and reporting it as
/// the view's contents would prove the store agrees with itself.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Sets the arrangement by name: <c>MAILBOX_ARRANGE=Subject</c>, or several in order.
    /// </summary>
    /// <remarks>
    /// Through the shell's own <c>Arrangement</c> setter, which is exactly what the menu item
    /// behind the "By Date" label does — see <c>ArrangeFlyout</c>. Matched on the label the menu
    /// writes as well as the enum's own name, so a pose can say "Flag: Due Date" the way the
    /// menu says it.
    /// </remarks>
    private static void PoseArrange(ShellViewModel shell, string spec)
    {
        foreach (var name in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Arrangements.All
                .Cast<Arrangement?>()
                .FirstOrDefault(a => string.Equals(a!.Value.ToString(), name, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(Arrangements.Label(a.Value), name, StringComparison.OrdinalIgnoreCase));

            if (match is not { } arrangement)
            {
                Log.Info($"Harness: arrange — no arrangement named “{name}”. "
                         + $"They are: {string.Join(", ", Arrangements.All.Select(Arrangements.Label))}.");
                continue;
            }

            shell.Arrangement = arrangement;

            Log.Info($"Harness: arranged by {Arrangements.Label(arrangement)} — label “{shell.ArrangementLabel}”, "
                     + $"{(shell.SortDescending ? "descending" : "ascending")}, "
                     + $"{shell.VisibleRows.OfType<GroupHeaderRow>().Count()} group(s) over "
                     + $"{shell.VisibleRows.OfType<MessageRow>().Count()} row(s).");
        }
    }

    /// <summary>
    /// What the attachment strip drew, once it has been shown.
    /// </summary>
    /// <remarks>
    /// Here rather than in the pane's own dump because the strip is the shell's control, filled
    /// from what the pane decided it was showing — an encrypted message's attachments are inside
    /// it, so the strip is handed <c>Carried</c> after the pane has opened anything it had to.
    /// Reading it from inside the pane found no strip at all and reported a message with four
    /// attachments as having a hidden one.
    /// </remarks>
    private void LogAttachmentStrip()
    {
        if (!ReadingPaneBody.DumpRequested || !Mailbox.App.Theming.WindowCapture.IsRequested) return;

        // The logical tree, not the visual one: the chips are built and added the moment the strip
        // is shown, and the visual tree under them does not exist until they have been measured —
        // which under a capture is after this runs. Read visually, a strip holding four chips
        // reported none.
        var chips = _attachments.GetSelfAndLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .ToList();

        Log.Info($"Harness: attachment strip — {(_attachments.IsVisible ? "shown" : "hidden")}, "
                 + $"{chips.Count} drawn item(s)"
                 + (chips.Count > 0 ? $": {string.Join(" · ", chips)}" : string.Empty));
    }

    /// <summary>
    /// The folder a pose names, which may say which account's it means:
    /// <c>Inbox</c>, or <c>you@example.com/Inbox</c>.
    /// </summary>
    /// <remarks>
    /// A seeded store has three accounts and every one of them has an Inbox, a Sent Items and a
    /// Deleted Items. Matching on the name alone therefore reaches the first account's and no
    /// other, which makes the role folders of the second and third accounts unposeable — and the
    /// same trap rule 3 warns about for reading a write back, one step earlier: a pose that
    /// silently opened the wrong account's Inbox reported "nothing happened" about mail that was
    /// never on screen.
    /// </remarks>
    private static FolderNode? FolderNamed(ShellViewModel shell, string wanted)
    {
        // "unified:Inbox" names one of the All Accounts folders, which otherwise cannot be told
        // from the six others with the same name.
        if (wanted.StartsWith("unified:", StringComparison.OrdinalIgnoreCase))
        {
            var name = wanted["unified:".Length..];
            return shell.Folders.FirstOrDefault(
                f => f.Kind == FolderNodeKind.Unified && f.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        var slash = wanted.LastIndexOf('/');
        if (slash <= 0) return shell.Folders.FirstOrDefault(f => f.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        var address = wanted[..slash];
        var folder = wanted[(slash + 1)..];

        return shell.Folders.FirstOrDefault(
            f => f.Name.Contains(folder, StringComparison.OrdinalIgnoreCase)
                 && shell.FolderOf(f) is { } where
                 && where.Account.Account.Address.Contains(address, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Writes the list as it stands: every group header with its count, and every row under it.
    /// </summary>
    /// <remarks>
    /// <c>VisibleRows</c> rather than <c>Messages</c>, for the reason <c>PoseSort</c> gives —
    /// <c>Messages</c> is the folder's mail in the order it was read, and the list is what came
    /// out of the arrangement. The two disagree the moment anything is grouped, and reporting the
    /// former would call the store's order the list's.
    /// <para>
    /// Each row carries the marks a screenshot cannot be queried about: whether it is unread,
    /// flagged, has an attachment, what its conversation depth is, and which categories are on
    /// it. The received stamp is written twice — the sortable form, and the label the row
    /// actually draws — because the date wording is itself a claim the audit checks and the two
    /// go wrong independently.
    /// </para>
    /// </remarks>
    private static void PoseListDump(ShellViewModel shell)
    {
        var rows = shell.VisibleRows;

        Log.Info($"Harness: list — view “{shell.CurrentView.Name}”, arranged by "
                 + $"{Arrangements.Label(shell.Arrangement)} {(shell.SortDescending ? "descending" : "ascending")}, "
                 + $"conversations {(shell.ShowAsConversations ? "on" : "off")}, filter {shell.Filter}, "
                 + $"unread-only {(shell.UnreadOnly ? "on" : "off")}; "
                 + $"{rows.OfType<GroupHeaderRow>().Count()} group(s), "
                 + $"{rows.OfType<MessageRow>().Count()} row(s) drawn of {shell.Messages.Count} in the folder.");

        var index = 0;
        foreach (var row in rows)
        {
            switch (row)
            {
                case GroupHeaderRow group:
                    Log.Info($"Harness: list group — “{group.Header}” ({group.Count}"
                             + $"{(group.IsCollapsed ? ", collapsed" : string.Empty)})");
                    break;

                // The folded head of a conversation, which is neither a header nor a row and
                // which a dump that matched only those two reported as though the thread's other
                // messages had vanished.
                case ConversationRow conversation:
                    Log.Info($"Harness: list thread {index:D3} — “{conversation.Newest.Subject}” "
                             + $"({conversation.Count} message(s), key “{conversation.Newest.ThreadKey}”, "
                             + $"{(conversation.IsExpanded ? "expanded" : "collapsed")}"
                             + $"{(conversation.IsSplit ? ", split across folders" : string.Empty)}) "
                             + $"newest {conversation.Newest.Received.ToLocalTime():yyyy-MM-dd HH:mm} "
                             + $"from {conversation.Newest.From}");
                    index++;
                    break;

                case MessageRow message:
                {
                    var marks = new List<string>();
                    if (message.IsUnread) marks.Add("unread");
                    if (message.IsFlagged) marks.Add("flagged");
                    if (message.HasAttachment) marks.Add("attachment");
                    if (message.Importance != 1) marks.Add(message.Importance == 2 ? "high" : "low");
                    if (message.IsSnoozed) marks.Add("snoozed");
                    if (message.IsHeaderOnly) marks.Add("header-only");
                    if (message.HasCategories) marks.Add("categories " + string.Join("+", message.CategoryNames));
                    if (message.Depth > 0) marks.Add($"depth {message.Depth}");
                    if (message.Address.Length > 0) marks.Add(message.Address);
                    if (message.HasFolderLabel) marks.Add("in " + message.FolderLabel);

                    // Local, because the label beside it is local: a stamp printed in the store's
                    // own offset next to a label converted to the reader's zone reads as a
                    // seven-hour bug on any machine that is not on UTC.
                    Log.Info($"Harness: list row {index:D3} — {message.Received.ToLocalTime():yyyy-MM-dd HH:mm} "
                             + $"“{message.ReceivedLabel}”  {message.From}  “{message.Subject}”  "
                             + $"{message.SizeBytes}B  [{(marks.Count > 0 ? string.Join(", ", marks) : "-")}]");
                    index++;
                    break;
                }
            }
        }

        Log.Info($"Harness: list selection — {(shell.SelectedRow switch
        {
            MessageRow chosen => $"row “{chosen.Subject}”",
            GroupHeaderRow header => $"the “{header.Header}” header",
            _ => "nothing",
        })}; the pane is showing "
                 + $"{(shell.SelectedMessage is { } shown ? $"“{shown.Subject}”" : "nothing")}.");
    }

    /// <summary>
    /// Writes the folder pane as it stands, each row beside what its own store says.
    /// </summary>
    /// <remarks>
    /// The pane's count and the store's count on one line, because "unread counts against the
    /// store" is a claim about the two agreeing and a dump of either alone cannot be held to it.
    /// A row whose numbers differ is marked, so a disagreement is a grep rather than a join.
    /// <para>
    /// Headings, favourites, search folders and the unified mailbox's rows stand for no single
    /// store folder and say so; a favourite is the same folder as its row in the tree below and
    /// is expected to carry the same count.
    /// </para>
    /// </remarks>
    private static void PoseFolderDump(ShellViewModel shell)
    {
        Log.Info($"Harness: folder pane — {shell.Folders.Count} row(s); "
                 + $"open: {(shell.SelectedFolder is { } open ? $"“{open.Name}”" : "nothing")}.");

        foreach (var node in shell.Folders)
        {
            // The pane indents by 14 per level and keeps no depth of its own, so the margin is
            // where the level is: reading it back is reading what was actually drawn.
            var depth = (int)Math.Round(node.IndentMargin.Left / 14);
            var where = shell.FolderOf(node);

            var against = where is { } target
                ? $"store {target.Account.Mail.GetFolder(target.Folder.Id)?.Unread ?? -1} unread of "
                  + $"{target.Account.Mail.GetFolder(target.Folder.Id)?.Total ?? -1}"
                  + (shell.IsFavourite(target.Account, target.Folder) ? ", favorite" : string.Empty)
                  + $", {target.Account.Account.Address}"
                : node.Kind == FolderNodeKind.Folder ? "no store folder — a heading" : $"no store folder — {node.Kind}";

            var disagrees = where is { } check
                            && check.Account.Mail.GetFolder(check.Folder.Id) is { } folder
                            && folder.Unread != node.Unread;

            Log.Info($"Harness: folder — {new string(' ', depth * 2)}“{node.Name}” "
                     + $"[{node.Kind}, depth {depth}] pane {node.Unread} unread "
                     + $"(shows “{node.UnreadDisplay}”, {node.Weight}); {against}"
                     + (disagrees ? "  ← DISAGREES" : string.Empty));
        }
    }
}
