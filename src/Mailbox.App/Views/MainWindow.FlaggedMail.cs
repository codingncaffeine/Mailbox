using Mailbox.App.ViewModels;
using Mailbox.Rendering;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The flagged mail on the to-do list: what a row stands for, and what acting on one does.
/// </summary>
/// <remarks>
/// The reference's To-Do List holds tasks and flagged messages together, so every action the list
/// offers has two meanings and this is the mail one. The distinction the reference draws between
/// its two removals is exactly here: <b>Delete</b> deletes the thing, and <b>Remove from List</b>
/// takes it off the list without deleting it — which for a message means clearing its flag and
/// leaving the mail where it is, and for a task means the same as Delete, a task being nothing but
/// its own entry on the list.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Which account's store a flagged-mail row belongs to.</summary>
    private static OpenAccount? AccountOf(FlaggedMessage message)
        => App.Accounts.Find(message.Account);

    /// <summary>
    /// The tick box, and Mark Complete, on a flagged message: the follow-up is completed, which
    /// is the flag clearing and a check taking its place — and on IMAP it journals the flag.
    /// </summary>
    private void ToggleFlaggedMessage(ShellViewModel shell, TaskRow row, bool? complete = null)
    {
        if (row.Message is not { } message || AccountOf(message) is not { } account) return;

        var done = complete ?? !row.IsComplete;
        if (done) account.Mail.CompleteFollowUp([message.MessageId]);
        else account.Mail.SetFollowUp([message.MessageId], row.Task.Due?.Wall);

        AfterFlaggedChange(shell);
        shell.StatusRight = done
            ? $"“{row.Summary}” marked complete."
            : $"“{row.Summary}” put back on the list.";
        Log.Info($"Flagged mail: message {message.MessageId} in {message.Account} {(done ? "completed" : "reopened")}.");
    }

    /// <summary>
    /// Remove from List on a flagged message: the flag goes and the message stays, which is the
    /// whole of the reference's difference between this and Delete.
    /// </summary>
    private void RemoveFlaggedMessage(ShellViewModel shell, TaskRow row)
    {
        if (row.Message is not { } message || AccountOf(message) is not { } account) return;

        account.Mail.ClearFollowUp([message.MessageId]);
        AfterFlaggedChange(shell);

        shell.StatusRight = $"“{row.Summary}” taken off the list; the message is still in the folder.";
        Log.Info($"Flagged mail: flag cleared on message {message.MessageId} in {message.Account}.");
    }

    /// <summary>Delete on a flagged message: the message itself goes, as it would from the list.</summary>
    /// <remarks>
    /// <b>As it would from the list</b> is the whole of it: to Deleted Items, and undoable — the
    /// same rule the message list's own Delete follows, and for the same reason, which is that the
    /// store may hold the only copy. This used to call the repository's <c>DeleteMessages</c>,
    /// which is the <em>permanent</em> delete Empty Folder uses; a reader looking at their tasks
    /// pressed Delete on a row and the message left the folder tree with no prompt and nothing to
    /// undo. A message already sitting in Deleted Items has nowhere left to move to, and that one
    /// case is still the permanent delete — again as the list does it.
    /// </remarks>
    private void DeleteFlaggedMessage(ShellViewModel shell, TaskRow row)
    {
        if (row.Message is not { } message || AccountOf(message) is not { } account) return;

        var from = account.Mail.GetMessage(message.MessageId)?.FolderId;
        var deleted = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Deleted);

        if (deleted is null || from is null || from == deleted.Id)
        {
            account.Mail.DeleteMessages([message.MessageId]);
            shell.StatusRight = $"“{row.Summary}” permanently deleted.";
            Log.Info($"Flagged mail: message {message.MessageId} in {message.Account} permanently deleted.");
        }
        else
        {
            account.Mail.MoveMessages([message.MessageId], deleted.Id);
            shell.StatusRight = $"“{row.Summary}” moved to Deleted Items.";
            Log.Info($"Flagged mail: message {message.MessageId} in {message.Account} moved to Deleted Items.");

            var back = from.Value;
            shell.Undo.Push(
                "Delete",
                () =>
                {
                    account.Mail.MoveMessages([message.MessageId], back);
                    AfterFlaggedChange(shell);
                },
                () => DeleteFlaggedMessage(shell, row));
        }

        AfterFlaggedChange(shell);
    }

    /// <summary>Opening a flagged-mail row opens the message, as double-clicking it in the list does.</summary>
    private void OpenFlaggedMessage(ShellViewModel shell, TaskRow row)
    {
        if (row.Message is not { } message) return;
        OpenMessageWindowById(shell, message.Account, message.MessageId);
    }

    /// <summary>
    /// The message window for a row named by account and id — what a to-do row and a reminder
    /// both have in hand, neither of which has been through the reading pane's load.
    /// </summary>
    private void OpenMessageWindowById(ShellViewModel shell, string address, long messageId)
    {
        if (AccountOf(new FlaggedMessage(address, messageId, string.Empty)) is not { } account) return;

        if (account.Mail.LoadRaw(messageId) is not { } raw)
        {
            shell.StatusRight = "That message is no longer in the store.";
            return;
        }

        using var stream = new MemoryStream(raw);
        new MessageWindow(App.Themes, () => account.Mail, MimeKit.MimeMessage.Load(stream), raw).Show(this);
        Log.Info($"Flagged mail: opened message {messageId} in {address}.");
    }

    /// <summary>
    /// Reply, Reply All and Forward on a flagged-mail row — the three the Tasks bar carries
    /// because the reference's list holds mail.
    /// </summary>
    private void RespondToFlaggedMessage(ShellViewModel shell, TaskRow row, ReplyKind kind)
    {
        if (row.Message is not { } message || AccountOf(message) is not { } account) return;
        if (account.Mail.LoadRaw(message.MessageId) is not { } raw)
        {
            shell.StatusRight = "That message is no longer in the store.";
            return;
        }

        using var stream = new MemoryStream(raw);
        Respond(shell, kind, MimeKit.MimeMessage.Load(stream));
    }

    /// <summary>Everything showing to-dos reads the store again: the module, the bar, the mail list.</summary>
    private void AfterFlaggedChange(ShellViewModel shell)
    {
        _taskModule?.Reload();
        RefreshToDoTasks();
        shell.Refresh();
    }
}
