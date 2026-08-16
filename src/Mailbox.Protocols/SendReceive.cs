using Mailbox.Core.Diagnostics;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>An account to transfer for, and the store its mail is filed in.</summary>
public sealed record TransferTarget(AccountConnection Connection, MailRepository Mail);

/// <summary>What one account's turn in a send/receive did.</summary>
public sealed record AccountRunResult(
    string Address,
    int Received,
    int Sent,
    string? Error = null)
{
    public bool Succeeded => Error is null;

    /// <summary>The store ids of what arrived in this account's Inbox, for the new-mail toast.</summary>
    public IReadOnlyList<long> Arrived { get; init; } = [];
}

/// <summary>The whole run.</summary>
public sealed record SendReceiveResult(IReadOnlyList<AccountRunResult> Accounts)
{
    public int Received => Accounts.Sum(a => a.Received);
    public int Sent => Accounts.Sum(a => a.Sent);
    public bool AllSucceeded => Accounts.All(a => a.Succeeded);

    /// <summary>A line for the status bar. Says what happened, including nothing.</summary>
    public string Summary()
    {
        if (Accounts.Count == 0) return "No accounts to check.";

        var failures = Accounts.Count(a => !a.Succeeded);
        var parts = new List<string>();

        if (Received > 0) parts.Add($"{Received} new");
        if (Sent > 0) parts.Add($"{Sent} sent");
        if (parts.Count == 0 && failures == 0) parts.Add("No new mail");
        if (failures > 0) parts.Add($"{failures} account{(failures == 1 ? "" : "s")} failed");

        return string.Join(", ", parts) + ".";
    }
}

/// <summary>
/// Runs a send/receive across every account.
/// </summary>
/// <remarks>
/// The order is send first, then receive. A reply queued a moment ago should leave before the
/// poll that might bring its own answer, and a user watching the progress dialog expects their
/// outbox to empty before the inbox fills.
/// <para>
/// One account failing never stops another. A laptop with a work account behind a VPN that is
/// down should still collect personal mail, and the run reports per account so the failure can
/// be attributed rather than reported as "send/receive failed".
/// </para>
/// </remarks>
public sealed class SendReceiveService(
    Func<MailRepository, Pop3Receiver>? receiver = null,
    Func<MailRepository, SmtpSender>? sender = null,
    Func<MailRepository, ImapSynchronizer>? synchronizer = null)
{
    // Every account has its own store, so a receiver and a sender belong to a repository rather
    // than to the service. Factories rather than instances: the service is long-lived and the
    // set of accounts is not.
    private readonly Func<MailRepository, Pop3Receiver> _receiver =
        receiver ?? (mail => new Pop3Receiver(mail));

    private readonly Func<MailRepository, SmtpSender> _sender =
        sender ?? (mail => new SmtpSender(mail));

    private readonly Func<MailRepository, ImapSynchronizer> _synchronizer =
        synchronizer ?? (mail => new ImapSynchronizer(mail));

    /// <summary>
    /// Nothing goes out and nothing is fetched while this is set, and queued mail is held
    /// rather than attempted. Outlook's Work Offline.
    /// </summary>
    public bool WorkOffline { get; private set; }

    /// <summary>Raised when the run's progress changes, for the dialog and the status bar.</summary>
    public event EventHandler<PollProgress>? Progress;

    public void SetWorkOffline(bool offline, IEnumerable<TransferTarget> targets)
    {
        if (WorkOffline == offline) return;

        WorkOffline = offline;
        foreach (var target in targets)
        {
            if (offline) target.Mail.HoldOutbox(target.Connection.AccountId);
            else target.Mail.ReleaseOutbox(target.Connection.AccountId);
        }

        Log.Info(offline ? "Working offline." : "Working online.");
    }

    public async Task<SendReceiveResult> RunAsync(
        IReadOnlyList<TransferTarget> accounts,
        DateTimeOffset now,
        CancellationToken cancellation = default)
    {
        if (WorkOffline)
        {
            Log.Info("Send/receive skipped: working offline.");
            return new SendReceiveResult([]);
        }

        var results = new List<AccountRunResult>(accounts.Count);

        foreach (var target in accounts)
        {
            cancellation.ThrowIfCancellationRequested();
            results.Add(await RunOneAsync(target, now, cancellation));
        }

        var result = new SendReceiveResult(results);
        Log.Info($"Send/receive finished: {result.Summary()}");
        return result;
    }

    private async Task<AccountRunResult> RunOneAsync(TransferTarget target,
        DateTimeOffset now, CancellationToken cancellation)
    {
        var account = target.Connection;
        var sent = 0;
        string? error = null;

        try
        {
            Progress?.Invoke(this, new PollProgress(account.Address, 0, 0, "Sending"));
            sent = await _sender(target.Mail).DrainAsync(account, now, cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Recorded and carried on with: a failure to send must not also cost the user the
            // mail waiting to be received.
            Log.Warn($"Sending failed for {account.Address}.", ex);
            error = SmtpSender.Classify(ex).Error;
        }

        var progress = new Progress<PollProgress>(p => Progress?.Invoke(this, p));

        // IMAP has folders and flags on the server; POP3 has neither, so it is the receiver that
        // downloads the inbox and the store that keeps everything else. The store being
        // authoritative (§4) is what lets these share one send path and one outbox.
        if (account.Protocol == MailProtocol.Imap)
        {
            var sync = await _synchronizer(target.Mail).SyncAsync(account, progress, cancellation);
            return new AccountRunResult(account.Address, sync.Downloaded, sent, error ?? sync.Error)
            {
                Arrived = sync.Arrived,
            };
        }

        // Delivered to the Inbox unless the account says another folder — and to the Inbox
        // again if that folder has since gone, because mail with nowhere to go is lost mail.
        var inbox = (account.Policy.DeliveryFolderId is { } chosen ? target.Mail.GetFolder(chosen) : null)
                    ?? target.Mail.FolderWithRole(account.AccountId, FolderRole.Inbox);
        if (inbox is null)
        {
            return new AccountRunResult(account.Address, 0, sent,
                error ?? "This account has no Inbox.");
        }

        var poll = await _receiver(target.Mail).PollAsync(account, inbox, progress, cancellation);

        return new AccountRunResult(account.Address, poll.Downloaded, sent, error ?? poll.Error)
        {
            Arrived = poll.Arrived,
        };
    }
}
