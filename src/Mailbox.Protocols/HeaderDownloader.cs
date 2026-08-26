using Mailbox.Store;
using MimeKit;

namespace Mailbox.Protocols;

/// <summary>
/// Headers without their messages, and the messages behind the ones that are wanted.
/// </summary>
/// <remarks>
/// <b>What this is for.</b> A mailbox reached over a slow or metered line, where fetching every
/// message to find out what is in it is the expensive part. Download Headers asks the server what
/// is there — sender, subject, date, size — and writes a row for each without its body; the
/// reader marks the ones worth having, and Process Marked Headers fetches those and only those.
/// This is the reference's Send/Receive · Server group, and it works the same way here.
/// <para>
/// <b>Why it is a class of its own.</b> The synchroniser downloads whole messages on a schedule;
/// this is the same conversation with the server for a different purpose, driven by a button. It
/// shares the store's own operations — <c>AddMessage</c> for a header, <c>FillHeader</c> for the
/// message that arrives under it — so a header row is an ordinary row from the moment it lands,
/// and can be flagged, categorized and moved like any other.
/// </para>
/// </remarks>
public sealed class HeaderDownloader(MailRepository repository, Func<DateTimeOffset>? now = null)
{
    private readonly MailRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The IMAP session to use. Null takes MailKit's, which is what a real run wants.</summary>
    public Func<IImapSession>? ImapSessionFactory { get; init; }

    /// <summary>The POP3 session to use. Null takes MailKit's.</summary>
    public Func<IPop3Session>? Pop3SessionFactory { get; init; }

    /// <summary>
    /// Writes a header row for everything in the folder the store has not got.
    /// </summary>
    /// <returns>How many headers were written.</returns>
    public async Task<int> HeadersAsync(AccountConnection connection, Folder folder, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(folder);

        return connection.Protocol == MailProtocol.Imap
            ? await ImapHeadersAsync(connection, folder, cancellation)
            : await Pop3HeadersAsync(connection, folder, cancellation);
    }

    /// <summary>
    /// Fetches the message behind every header the reader has marked, across the account.
    /// </summary>
    /// <returns>How many messages arrived under their headers.</returns>
    public async Task<int> ProcessMarkedAsync(AccountConnection connection, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var marked = _repository.MarkedForDownload(connection.AccountId);
        if (marked.Count == 0) return 0;

        return connection.Protocol == MailProtocol.Imap
            ? await ImapBodiesAsync(connection, marked, cancellation)
            : await Pop3BodiesAsync(connection, marked, cancellation);
    }

    // ---- IMAP ---------------------------------------------------------------------------

    private async Task<int> ImapHeadersAsync(AccountConnection connection, Folder folder, CancellationToken cancellation)
    {
        if (folder.ImapPath is not { Length: > 0 } path) return 0;

        var session = ImapSessionFactory?.Invoke() ?? new MailKitImapSession();
        try
        {
            await session.ConnectAsync(connection.Incoming, cancellation);
            await session.AuthenticateAsync(connection.Incoming, cancellation);
            await session.OpenAsync(path, cancellation);

            // What the server has, less what is already here — by UID, which is what the store
            // keeps beside each row for exactly this comparison.
            var known = _repository.Messages(folder.Id, int.MaxValue)
                .Select(m => m.ServerUid)
                .Where(uid => uid is { Length: > 0 })
                .ToHashSet(StringComparer.Ordinal);

            var uids = await session.SearchAllAsync(cancellation);
            var wanted = uids
                .Where(uid => !known.Contains(uid.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToList();

            if (wanted.Count == 0) return 0;

            var headers = await session.FetchHeadersAsync(wanted, cancellation);
            return Write(folder, headers);
        }
        finally
        {
            if (session.IsConnected) await session.DisconnectAsync(cancellation);
            session.Dispose();
        }
    }

    private async Task<int> ImapBodiesAsync(
        AccountConnection connection,
        IReadOnlyList<(Folder Folder, MessageSummary Message)> marked,
        CancellationToken cancellation)
    {
        var session = ImapSessionFactory?.Invoke() ?? new MailKitImapSession();
        var filled = 0;

        try
        {
            await session.ConnectAsync(connection.Incoming, cancellation);
            await session.AuthenticateAsync(connection.Incoming, cancellation);

            // A folder at a time, because IMAP fetches from whichever folder is open.
            foreach (var group in marked.GroupBy(m => m.Folder.Id))
            {
                cancellation.ThrowIfCancellationRequested();

                var folder = group.First().Folder;
                if (folder.ImapPath is not { Length: > 0 } path) continue;

                await session.OpenAsync(path, cancellation);

                foreach (var (_, header) in group)
                {
                    if (!long.TryParse(header.ServerUid, out var uid)) continue;

                    var message = await session.GetMessageAsync(uid, cancellation);
                    if (message is null) continue;

                    if (Fill(header, message)) filled++;
                }
            }
        }
        finally
        {
            if (session.IsConnected) await session.DisconnectAsync(cancellation);
            session.Dispose();
        }

        return filled;
    }

    // ---- POP3 ---------------------------------------------------------------------------

    private async Task<int> Pop3HeadersAsync(AccountConnection connection, Folder folder, CancellationToken cancellation)
    {
        // POP3 has one mailbox, and it is the Inbox. Asking for headers while looking at another
        // folder would be asking the server about mail it has never had.
        if (folder.Role != FolderRole.Inbox) return 0;

        var client = Pop3SessionFactory?.Invoke() ?? new MailKitPop3Session();
        try
        {
            await client.ConnectAsync(connection.Incoming, cancellation);
            await client.AuthenticateAsync(connection.Incoming, cancellation);

            // What this account has already taken, whether it is still in the Inbox or not: the
            // seen list outlives a message that was filed, deleted or archived.
            var known = _repository.SeenUidls();
            known.UnionWith(_repository.ServerUidsInAccount(connection.AccountId));
            var uids = await client.GetUidsAsync(cancellation);

            var headers = new List<RemoteHeader>();
            for (var index = 0; index < uids.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();

                if (known.Contains(uids[index])) continue;
                if (await client.GetHeadersAsync(index, uids[index], cancellation) is { } header) headers.Add(header);
            }

            return Write(folder, headers);
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(cancellation);
            client.Dispose();
        }
    }

    private async Task<int> Pop3BodiesAsync(
        AccountConnection connection,
        IReadOnlyList<(Folder Folder, MessageSummary Message)> marked,
        CancellationToken cancellation)
    {
        var client = Pop3SessionFactory?.Invoke() ?? new MailKitPop3Session();
        var filled = 0;

        try
        {
            await client.ConnectAsync(connection.Incoming, cancellation);
            await client.AuthenticateAsync(connection.Incoming, cancellation);

            // POP3 numbers messages by position within one session, so the UIDL each row keeps
            // has to be turned back into this session's index before anything can be fetched.
            var uids = await client.GetUidsAsync(cancellation);
            var index = uids
                .Select((uid, at) => (uid, at))
                .ToDictionary(pair => pair.uid, pair => pair.at, StringComparer.Ordinal);

            foreach (var (_, header) in marked)
            {
                cancellation.ThrowIfCancellationRequested();

                if (header.ServerUid is not { Length: > 0 } uid || !index.TryGetValue(uid, out var at)) continue;

                var message = await client.GetMessageAsync(at, cancellation);
                if (message is null) continue;

                if (Fill(header, message)) filled++;
            }
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(cancellation);
            client.Dispose();
        }

        return filled;
    }

    // ---- The store ----------------------------------------------------------------------

    /// <summary>Writes each header as a row with no message under it.</summary>
    private int Write(Folder folder, IReadOnlyList<RemoteHeader> headers)
    {
        var written = 0;
        foreach (var header in headers)
        {
            var summary = new MessageSummary(
                Id: 0,
                FolderId: folder.Id,
                ServerUid: header.Uid,
                MessageId: header.MessageId,
                FromName: header.FromName,
                FromAddress: header.FromAddress,
                Subject: header.Subject,

                // The preview is the one thing a header cannot carry, and saying so is better
                // than an empty line the reader would read as an empty message.
                Preview: "Header only — the message has not been downloaded.",
                Sent: header.Sent,
                Received: header.Received,
                SizeBytes: header.Size,
                IsRead: header.IsRead,
                IsFlagged: header.IsFlagged,
                HasAttachment: false)
            {
                HeaderOnly = true,
            };

            if (_repository.AddMessage(folder.Id, summary) is not null) written++;
        }

        return written;
    }

    /// <summary>Puts a fetched message under the header that stood for it.</summary>
    private bool Fill(MessageSummary header, MimeMessage message)
    {
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();

        var filled = MessageMapper.ToSummary(message, header.ServerUid, raw.LongLength, _now(), header.IsRead, header.IsFlagged);
        return _repository.FillHeader(header.Id, filled, raw);
    }
}
