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
    private void DeleteFlaggedMessage(ShellViewModel shell, TaskRow row)
    {
        if (row.Message is not { } message || AccountOf(message) is not { } account) return;

        account.Mail.DeleteMessages([message.MessageId]);
        AfterFlaggedChange(shell);

        shell.StatusRight = $"“{row.Summary}” deleted.";
        Log.Info($"Flagged mail: message {message.MessageId} in {message.Account} deleted.");
    }

    /// <summary>Opening a flagged-mail row opens the message, as double-clicking it in the list does.</summary>
    private void OpenFlaggedMessage(ShellViewModel shell, TaskRow row)
    {
        if (row.Message is not { } message || AccountOf(message) is not { } account) return;

        if (account.Mail.LoadRaw(message.MessageId) is not { } raw)
        {
            shell.StatusRight = "That message is no longer in the store.";
            return;
        }

        using var stream = new MemoryStream(raw);
        new MessageWindow(App.Themes, () => account.Mail, MimeKit.MimeMessage.Load(stream), raw).Show(this);
        Log.Info($"Flagged mail: opened message {message.MessageId} in {message.Account}.");
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
