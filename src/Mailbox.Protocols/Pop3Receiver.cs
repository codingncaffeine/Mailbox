using MailKit;
using MailKit.Net.Pop3;
using MimeKit;
using Mailbox.Core.Diagnostics;
using Mailbox.Security;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>What a poll did.</summary>
public sealed record PollResult(
    int Downloaded,
    int AlreadyHad,
    int RemovedFromServer,
    string? Error = null)
{
    public bool Succeeded => Error is null;

    public static PollResult Failed(string error) => new(0, 0, 0, error);
}

/// <summary>Progress during a poll, for the send/receive dialog and the status bar.</summary>
public sealed record PollProgress(string Account, int Done, int Total, string Stage);

/// <summary>
/// Downloads mail over POP3.
/// </summary>
/// <remarks>
/// POP3 has no folders, no flags and no server-side state beyond "present" or "deleted", so
/// everything here is about the one thing it does give: the UIDL, a stable per-message
/// identifier. Dedupe is the whole protocol — a client that loses track of which UIDLs it has
/// seen re-downloads the mailbox on every poll, and one that deletes to avoid that empties a
/// mailbox somebody else was still reading.
/// <para>
/// So the default is to leave everything on the server and rely on the store to know what it
/// already holds.
/// </para>
/// </remarks>
public sealed class Pop3Receiver(MailRepository repository, Func<DateTimeOffset>? now = null)
{
    private readonly MailRepository _repository = repository;

    /// <summary>
    /// The clock. Injectable because "remove from the server after N days" is arithmetic on
    /// it, and a rule that deletes mail is not one to test against whatever today happens to be.
    /// </summary>
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Lets a test supply a fake session. Null uses MailKit.</summary>
    public Func<IPop3Session>? SessionFactory { get; set; }

    /// <summary>
    /// Checks each message's own DKIM signatures as it arrives, or null to check nothing.
    /// </summary>
    /// <remarks>
    /// Here rather than in the reading pane because verifying resolves a name the sender chose,
    /// and §19 does not allow a lookup on the path that draws a message. A poll is already
    /// network work on a background thread, and it is also the only moment the signing key is
    /// certain to still be published — a key checked months later may have rotated, and
    /// reporting a rotation as a forgery would be worse than not checking at all.
    /// <para>
    /// Null by default so that nothing acquires a resolver by accident. The application supplies
    /// one; every test that does not care about signatures gets no lookups and no network.
    /// </para>
    /// </remarks>
    public DkimVerification? Authentication { get; set; }

    public async Task<PollResult> PollAsync(
        AccountConnection account,
        Folder inbox,
        IProgress<PollProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var client = SessionFactory?.Invoke() ?? new MailKitPop3Session();

        try
        {
            progress?.Report(new PollProgress(account.Address, 0, 0, "Connecting"));

            await client.ConnectAsync(account.Incoming, cancellation);
            await client.AuthenticateAsync(account.Incoming, cancellation);

            return await DownloadAsync(client, account, inbox, progress, cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed poll is ordinary — a laptop lid, a captive portal, an expired password.
            // It is reported, not thrown: the send/receive runs several accounts and one
            // failing must not stop the others.
            Log.Warn($"POP3 poll failed for {account.Address}.", ex);
            return PollResult.Failed(Explain(ex));
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(CancellationToken.None);
            client.Dispose();
        }
    }

    private async Task<PollResult> DownloadAsync(
        IPop3Session client,
        AccountConnection account,
        Folder inbox,
        IProgress<PollProgress>? progress,
        CancellationToken cancellation)
    {
        var count = client.Count;
        if (count == 0) return new PollResult(0, 0, 0);

        // One UIDL call for the whole mailbox rather than one per message: on a mailbox of any
        // size the round trips cost more than the download.
        var uids = await client.GetUidsAsync(cancellation);
        var known = _repository.ServerUids(inbox.Id);

        var downloaded = 0;
        var alreadyHad = 0;
        var toRemove = new List<int>();
        var expired = Expired(inbox, account.Policy);

        for (var index = 0; index < uids.Count && downloaded < account.Policy.MaxPerPoll; index++)
        {
            cancellation.ThrowIfCancellationRequested();

            var uid = uids[index];
            if (known.Contains(uid))
            {
                alreadyHad++;
                if (ShouldRemove(account.Policy) || expired.Contains(uid)) toRemove.Add(index);
                continue;
            }

            progress?.Report(new PollProgress(
                account.Address, downloaded + 1, uids.Count - known.Count, "Receiving"));

            var message = await client.GetMessageAsync(index, cancellation);
            await StoreAsync(inbox, message, uid, cancellation);
            downloaded++;

            if (!account.Policy.LeaveOnServer) toRemove.Add(index);
        }

        var removed = await RemoveAsync(client, toRemove, cancellation);
        return new PollResult(downloaded, alreadyHad, removed);
    }

    private async Task StoreAsync(
        Folder inbox, MimeMessage message, string uid, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();

        var summary = MessageMapper.ToSummary(message, uid, raw.Length, _now());
        var id = _repository.AddMessage(inbox.Id, summary, raw);

        if (id is { } messageId)
        {
            await Arrival.RecordSignatureAsync(_repository, Authentication, messageId, message, _now(), cancellation);
        }
    }

    /// <summary>
    /// The UIDLs whose copies here are old enough that the user has asked for the server's to
    /// go.
    /// </summary>
    /// <remarks>
    /// Counted from when the message was <em>downloaded</em>, not from its own date. A message
    /// written a year ago and collected this morning is one day old as far as this rule is
    /// concerned; counting from the header would delete it off the server the moment it
    /// arrived, which is the opposite of what "leave a copy for 14 days" means.
    /// <para>
    /// Only consulted while leave-on-server is set. Without it the mail is being removed as it
    /// is downloaded anyway, and an age has nothing left to decide.
    /// </para>
    /// </remarks>
    private HashSet<string> Expired(Folder inbox, Pop3Policy policy)
    {
        if (!policy.LeaveOnServer || policy.DeleteAfterDays is not { } days) return [];

        return _repository.ServerUidsOlderThan(inbox.Id, _now().AddDays(-days));
    }

    /// <summary>
    /// Whether a message already downloaded should now be taken off the server. Only ever true
    /// when the user has asked for it; "leave on server" means leave it.
    /// </summary>
    private static bool ShouldRemove(Pop3Policy policy)
        => !policy.LeaveOnServer && !policy.DeleteWhenRemovedLocally;

    private static async Task<int> RemoveAsync(IPop3Session client, List<int> indexes,
        CancellationToken cancellation)
    {
        if (indexes.Count == 0) return 0;

        await client.DeleteAsync(indexes, cancellation);
        return indexes.Count;
    }

    /// <summary>
    /// Turns an exception into something worth showing. MailKit's own messages are accurate but
    /// assume the reader knows the protocol, and the common causes have a plain description.
    /// </summary>
    internal static string Explain(Exception ex) => ex switch
    {
        Pop3ProtocolException => "The server replied in a way Mailbox did not expect.",
        MailKit.Security.AuthenticationException =>
            "The server rejected the username or password.",
        MailKit.Security.SslHandshakeException =>
            "The secure connection could not be established. The server's certificate may not " +
            "be trusted, or it may expect a different kind of encryption on this port.",
        System.Net.Sockets.SocketException =>
            "Could not reach the server. Check the address, the port, and the network.",
        TimeoutException => "The server did not answer in time.",
        _ => ex.Message,
    };
}
