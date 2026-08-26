using Avalonia.Controls;
using Mailbox.App.ViewModels;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Rules;
using Mailbox.Core.Settings;
using Mailbox.Rendering;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// The Quick Steps half of the shell: running a step over the selection, its first-time setup,
/// and the ribbon following the list as it changes.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Runs a Quick Step over the selection — after its first-time setup, when a folder or an
    /// address is still to be chosen. Each action runs in order over the rows the step was
    /// pressed on; the respond actions open their windows last, so a Reply &amp; Delete replies to
    /// the message before the row it came from has gone.
    /// </summary>
    private async Task RunQuickStepAsync(ShellViewModel shell, QuickStep step, IReadOnlyList<MessageRow> rows)
    {
        if (step.NeedsSetup)
        {
            var setup = new QuickStepSetupDialog(step, shell.CurrentAccountForCategories());
            await setup.ShowDialog(this);
            if (setup.Result is not { } ready) return;
            step = ready;
            App.QuickSteps.Upsert(step);
        }

        // NewMessage and RunCommand act without a row — the second because the command it
        // presses decides for itself, exactly as it would from the ribbon.
        if (rows.Count == 0 && step.Actions.Any(a => a.Kind is not (QuickStepKind.NewMessage or QuickStepKind.RunCommand)))
        {
            shell.StatusRight = "Select a message first.";
            return;
        }

        var account = shell.CurrentAccountForCategories();
        var acted = 0;
        var respond = new List<QuickStepAction>();

        // The message as it is now, before an action moves the selection: a Reply & Delete
        // replies to what was selected, not to whatever the list settles on after the delete.
        var original = _openMessage;

        // One press, one step to take back. Every action below goes through the shell command
        // that records itself, so without this a Move-and-mark-read step would want two presses
        // of Ctrl+Z — and the reader has no way of knowing that it was two.
        using var undo = shell.Undo.Batch($"Quick Step “{step.Name}”");

        foreach (var action in step.Actions)
        {
            try
            {
                switch (action.Kind)
                {
                    case QuickStepKind.MoveToFolder:
                    case QuickStepKind.CopyToFolder:
                    {
                        if (account is null) break;
                        var target = ResolveFolder(account, action);
                        if (target is null)
                        {
                            shell.StatusRight = $"The folder “{action.FolderName}” is not in {account.Account.Address}.";
                            break;
                        }

                        if (action.Kind == QuickStepKind.MoveToFolder) shell.MoveToFolder([.. rows.Select(r => r.Id)], shell.NodeFor(account, target.Id) ?? throw new InvalidOperationException("Folder is not in the pane."));
                        else shell.CopyTo(rows, target);
                        acted++;
                        break;
                    }

                    case QuickStepKind.Delete:
                        shell.Delete(rows, permanently: false);
                        acted++;
                        break;

                    case QuickStepKind.PermanentlyDelete:
                        await ConfirmPermanentDeleteAsync(shell, rows);
                        acted++;
                        break;

                    case QuickStepKind.MarkAsRead:
                        shell.SetRead(rows, read: true);
                        acted++;
                        break;

                    case QuickStepKind.MarkAsUnread:
                        shell.SetRead(rows, read: false);
                        acted++;
                        break;

                    case QuickStepKind.SetImportance:
                        shell.SetImportance(rows, action.Level ?? 1);
                        acted++;
                        break;

                    case QuickStepKind.Categorize:
                        shell.AssignCategories(rows, action.Values);
                        acted++;
                        break;

                    case QuickStepKind.ClearCategories:
                        shell.ClearCategories(rows);
                        acted++;
                        break;

                    case QuickStepKind.FlagMessage:
                        shell.FlagForFollowUp(rows, action.Level is { } days
                            ? new DateTimeOffset(DateTime.Today.AddDays(days).AddHours(17))
                            : null);
                        acted++;
                        break;

                    case QuickStepKind.ClearFlags:
                        shell.ClearFollowUpFlag(rows);
                        acted++;
                        break;

                    case QuickStepKind.MarkComplete:
                        shell.MarkFollowUpComplete(rows);
                        acted++;
                        break;

                    case QuickStepKind.AlwaysMoveFromSender:
                    {
                        if (account is null || _openMessage?.From.Mailboxes.FirstOrDefault() is not { } from) break;
                        var name = from.Name is { Length: > 0 } ? from.Name : from.Address;
                        await AlwaysMoveAsync(shell, account, new RuleCondition(RuleConditionKind.From) { Values = [from.Address] }, name);
                        acted++;
                        break;
                    }

                    // Any catalogue command as an action — which is how a plugin's command
                    // becomes part of a step (§13), and how an unplaced addition can. Through
                    // the shell's own dispatcher, so it means here what it means anywhere. A
                    // step's own command is refused: a step that runs a step is a loop wearing
                    // a gallery button.
                    case QuickStepKind.RunCommand:
                    {
                        if (action.Values.FirstOrDefault() is not { Length: > 0 } commandId) break;

                        var target = new CommandId(commandId);
                        if (App.QuickSteps.FindByCommand(target) is not null)
                        {
                            Log.Warn($"Quick Step “{step.Name}”: “{commandId}” is a Quick Step itself, and a step does not run a step.");
                            break;
                        }

                        RunCommand(target);
                        acted++;
                        break;
                    }

                    case QuickStepKind.NewMessage:
                    case QuickStepKind.Forward:
                    case QuickStepKind.Reply:
                    case QuickStepKind.ReplyAll:
                    case QuickStepKind.ForwardAsAttachment:
                        respond.Add(action);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Quick Step “{step.Name}” could not {action.Describe()}.", ex);
                shell.StatusRight = $"Quick Step “{step.Name}” could not {action.Describe().ToLowerInvariant()}.";
            }
        }

        // The windows last, on the message as it was: a Reply & Delete has deleted the row by
        // now, so the reply is built from the message the pane was showing when the step ran.
        foreach (var action in respond) OpenRespondWindow(shell, action, original);

        if (acted > 0 && respond.Count == 0)
        {
            shell.StatusRight = $"Quick Step “{step.Name}” applied to {rows.Count} message{(rows.Count == 1 ? "" : "s")}.";
        }
    }

    /// <summary>The folder a step's action names in this account: by id, then by name.</summary>
    private static Folder? ResolveFolder(OpenAccount account, QuickStepAction action)
    {
        if (action.FolderId is { } id && account.Mail.GetFolder(id) is { } byId && byId.AccountId == account.Account.Id) return byId;
        if (action.FolderName is { Length: > 0 } name)
        {
            return account.Mail.Folders(account.Account.Id)
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>The respond actions: a compose window, or the inline reply, on the message the step ran on.</summary>
    private void OpenRespondWindow(ShellViewModel shell, QuickStepAction action, MimeMessage? original)
    {
        switch (action.Kind)
        {
            case QuickStepKind.NewMessage:
            {
                var compose = new ComposeWindow(App.Commands, App.Accounts, App.Contacts);
                if (shell.CurrentAddress is { Length: > 0 } address && !App.MailOptions.AlwaysUseDefaultAccount) compose.SendFromAccount(address);
                compose.ComposeFromMailto(new Mailbox.Core.Compose.MailtoLink(action.Values, [], [], action.Subject ?? string.Empty, string.Empty));
                compose.Queued += (_, e) => OnQueued(e);
                compose.Closed += (_, _) => shell.Refresh();
                compose.Show(this);
                break;
            }

            case QuickStepKind.Reply:
                if (original is not null) Respond(shell, ReplyKind.Reply, original);
                break;

            case QuickStepKind.ReplyAll:
                if (original is not null) Respond(shell, ReplyKind.ReplyAll, original);
                break;

            case QuickStepKind.Forward:
                if (original is not null) Respond(shell, ReplyKind.Forward, original, action.Values);
                break;

            case QuickStepKind.ForwardAsAttachment:
                if (original is not null) ForwardAsAttachment(shell, original, action.Values);
                break;
        }
    }

    /// <summary>
    /// Forward as an attachment: a new message carrying the whole original as a
    /// <c>message/rfc822</c> part, its subject prefixed, in a compose window.
    /// </summary>
    private void ForwardAsAttachment(ShellViewModel shell, MimeMessage original, IReadOnlyList<string> to)
    {
        var subject = original.Subject ?? string.Empty;
        var draft = new ReplyDraft
        {
            To = to,
            Subject = subject.StartsWith("FW:", StringComparison.OrdinalIgnoreCase) ? subject : "FW: " + subject,
            Attachments =
            [
                new CarriedPart(
                    (subject.Length > 0 ? subject : "message") + ".eml",
                    "message/rfc822",
                    new MessagePart { Message = original, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) }),
            ],
        };

        var compose = new ComposeWindow(App.Commands, App.Accounts, App.Contacts);
        if (shell.CurrentAddress is { Length: > 0 } address) compose.SendFromAccount(address);
        compose.Prefill(draft, ReplyKind.Forward);
        compose.Queued += (_, e) => OnQueued(e);
        compose.Closed += (_, _) => shell.Refresh();
        compose.Show(this);
    }

    /// <summary>Ctrl+Shift+1 to 9: the Quick Step with that shortcut, if any.</summary>
    private bool RunQuickStepShortcut(ShellViewModel shell, Avalonia.Input.Key key)
    {
        var digit = key switch
        {
            >= Avalonia.Input.Key.D1 and <= Avalonia.Input.Key.D9 => key - Avalonia.Input.Key.D0,
            >= Avalonia.Input.Key.NumPad1 and <= Avalonia.Input.Key.NumPad9 => key - Avalonia.Input.Key.NumPad0,
            _ => 0,
        };
        if (digit == 0) return false;

        var step = App.QuickSteps.All.FirstOrDefault(s => s.Shortcut == digit);
        if (step is null) return false;

        _ = RunQuickStepAsync(shell, step, SelectedRows());
        return true;
    }
}
