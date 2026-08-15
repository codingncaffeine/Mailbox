using MailKit.Net.Smtp;
using MimeKit;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>What happened to one attempt at sending.</summary>
public sealed record SendResult(bool Sent, string? Error = null, bool WorthRetrying = false)
{
    public static SendResult Ok() => new(true);

    /// <summary>The server said no, and will say no again. Do not queue another attempt.</summary>
    public static SendResult Rejected(string error) => new(false, error);

    /// <summary>Something transient. Try again later.</summary>
    public static SendResult Deferred(string error) => new(false, error, WorthRetrying: true);
}

/// <summary>
/// Sends over SMTP, and drains the outbox.
/// </summary>
/// <remarks>
/// Sending is a queue rather than a call. A message handed to this class has already been
/// written to the store, so the failure modes are all recoverable: the process can die
/// mid-send and the message is still queued, and a server that defers gets tried again rather
/// than losing the mail.
/// <para>
/// The distinction that matters is permanent versus temporary. A 5xx means the message will
/// never be accepted and retrying it forever is a way of never telling the user; a 4xx or a
/// dropped connection means try later. Getting that backwards either loses mail or hides a
/// bad address.
/// </para>
/// </remarks>
public sealed class SmtpSender(MailRepository repository)
{
    private readonly MailRepository _repository = repository;

    /// <summary>Lets a test supply a fake session. Null uses MailKit.</summary>
    public Func<ISmtpSession>? SessionFactory { get; set; }

    /// <summary>
    /// Whether a message that went is filed in Sent Items.
    /// </summary>
    /// <remarks>
    /// On, as it is everywhere. It was not done at all until session 4: the sender marked the
    /// outbox row sent and stopped, and Sent Items stayed empty for as long as anyone used the
    /// application — the Options row that governs it was a checkbox over a feature that did not
    /// exist. Off is for the person who genuinely does not want a copy, which is a real
    /// preference and the reason the reference offers it.
    /// </remarks>
    public bool FileSentCopies { get; set; } = true;

    /// <summary>How long to wait before each retry. After the last, the item is failed.</summary>
    /// <summary>
    /// Puts a copy of what went into Sent Items, already read.
    /// </summary>
    /// <remarks>
    /// The bytes as they went, verbatim, which is §4's rule for every message the store holds
    /// and the only version that matches what the recipient has. Marked read because the person
    /// wrote it. A failure here is logged and not raised: the message has been sent, and an
    /// exception now would report a delivery that succeeded as one that did not.
    /// </remarks>
    private void FileInSent(long accountId, MimeMessage message, byte[] raw, DateTimeOffset now)
    {
        try
        {
            if (_repository.FolderWithRole(accountId, FolderRole.Sent) is not { } sentFolder) return;

            var summary = MessageMapper.ToSummary(message, null, raw.Length, now) with { IsRead = true };
            _repository.AddMessage(sentFolder.Id, summary, raw);
        }
        catch (Exception ex)
        {
            Log.Warn("A sent message could not be filed in Sent Items.", ex);
        }
    }

    internal static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
    ];

    public async Task<SendResult> SendAsync(AccountConnection account, MimeMessage message,
        CancellationToken cancellation = default)
    {
        var client = SessionFactory?.Invoke() ?? new MailKitSmtpSession();

        try
        {
            await client.ConnectAsync(account.Outgoing, cancellation);

            if (account.Outgoing.UserName.Length > 0)
            {
                await client.AuthenticateAsync(account.Outgoing, cancellation);
            }

            await client.SendAsync(message, cancellation);
            return SendResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"SMTP send failed for {account.Address}.", ex);
            return Classify(ex);
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(CancellationToken.None);
            client.Dispose();
        }
    }

    /// <summary>
    /// Whether an exception means "never" or "not now". Anything unrecognised is treated as
    /// temporary: holding a message and telling the user beats discarding one that would have
    /// gone through on the next attempt.
    /// </summary>
    internal static SendResult Classify(Exception ex) => ex switch
    {
        SmtpCommandException { StatusCode: >= SmtpStatusCode.CommandUnrecognized } permanent
            => SendResult.Rejected(Describe(permanent)),

        SmtpCommandException transient => SendResult.Deferred(transient.Message),

        MailKit.Security.AuthenticationException
            => SendResult.Rejected("The server rejected the username or password."),

        MailKit.Security.SslHandshakeException
            => SendResult.Rejected(
                "The secure connection could not be established. The server's certificate may " +
                "not be trusted, or it may expect a different kind of encryption on this port."),

        System.Net.Sockets.SocketException
            => SendResult.Deferred("Could not reach the server."),

        TimeoutException => SendResult.Deferred("The server did not answer in time."),

        _ => SendResult.Deferred(ex.Message),
    };

    /// <summary>
    /// Says which address the server objected to when it named one. "Mailbox unavailable" is
    /// not useful; "the server would not accept a@example.com" is.
    /// </summary>
    private static string Describe(SmtpCommandException ex) => ex.ErrorCode switch
    {
        SmtpErrorCode.RecipientNotAccepted =>
            $"The server would not accept {ex.Mailbox?.Address ?? "a recipient"}: {ex.Message}",
        SmtpErrorCode.SenderNotAccepted =>
            $"The server would not accept {ex.Mailbox?.Address ?? "the sender"} as the " +
            $"sender: {ex.Message}",
        SmtpErrorCode.MessageNotAccepted => $"The server would not accept the message: {ex.Message}",
        _ => ex.Message,
    };

    // ---- The outbox -------------------------------------------------------------------------

    /// <summary>
    /// Queues a message for sending, and returns its outbox id. The time is a parameter rather
    /// than read from the clock so that retry scheduling can be reasoned about and tested;
    /// everything else in this class takes the same treatment.
    /// </summary>
    public long Queue(long accountId, MimeMessage message, DateTimeOffset? now = null)
    {
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        return QueueRaw(accountId, buffer.ToArray(), now);
    }

    internal long QueueRaw(long accountId, byte[] raw, DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var blobId = _repository.StoreBlob(raw);
        _repository.Store.Execute(
            """
            INSERT INTO outbox (account_id, blob_id, state, queued_utc, next_try_utc)
            VALUES ($account, $blob, 'queued', $now, $now)
            """,
            ("$account", accountId), ("$blob", blobId),
            ("$now", stamp.ToUnixTimeSeconds()));

        return _repository.Store.LastInsertId;
    }

    /// <summary>
    /// Sends everything due for an account. Items not yet due, held, or already sent are left
    /// alone, so calling this on a timer is safe.
    /// </summary>
    public async Task<int> DrainAsync(AccountConnection account, DateTimeOffset now,
        CancellationToken cancellation = default)
    {
        var due = _repository.DueOutbox(account.AccountId, now);
        var sent = 0;

        foreach (var item in due)
        {
            cancellation.ThrowIfCancellationRequested();

            var raw = _repository.LoadBlob(item.BlobId);
            if (raw is null)
            {
                _repository.FailOutbox(item.Id, "The queued message could not be read back.");
                continue;
            }

            _repository.SetOutboxState(item.Id, OutboxState.Sending);

            using var stream = new MemoryStream(raw);
            var message = await MimeMessage.LoadAsync(stream, cancellation);
            var result = await SendAsync(account, message, cancellation);

            if (result.Sent)
            {
                _repository.SetOutboxState(item.Id, OutboxState.Sent);
                if (FileSentCopies) FileInSent(item.AccountId, message, raw, now);
                sent++;
            }
            else if (result.WorthRetrying && item.Attempts + 1 < Backoff.Length)
            {
                _repository.DeferOutbox(
                    item.Id, now + Backoff[item.Attempts], result.Error ?? "Deferred");
            }
            else
            {
                _repository.FailOutbox(item.Id, result.Error ?? "Could not be sent.");
            }
        }

        return sent;
    }
}
