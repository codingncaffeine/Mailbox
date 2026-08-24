using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Import;

/// <summary>
/// The filing half every mail importer shares: parse, skip what the folder already holds by
/// Message-ID, store with the message's own date, count. The format-specific half — walking a
/// maildir, splitting an mbox, opening an .eml — is each importer's own.
/// </summary>
/// <remarks>
/// Nothing acts on what is filed: no junk filter, no rules, no reminders. Import is furniture
/// moving in, not mail arriving — the one decision here that every importer inherits.
/// </remarks>
public sealed class MessageFiler(MailRepository mail)
{
    private readonly MailRepository _mail = mail ?? throw new ArgumentNullException(nameof(mail));
    private readonly Dictionary<long, HashSet<string>> _seen = [];

    public int Imported { get; private set; }

    public int AlreadyHere { get; private set; }

    public int Unreadable { get; private set; }

    public List<string> Notes { get; } = [];

    /// <summary>Files one message's bytes. False when it was skipped or unreadable.</summary>
    public bool File(long folderId, byte[] raw, bool read, bool flagged, DateTimeOffset? fallbackDate = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(raw);

        MimeMessage parsed;
        try
        {
            using var stream = new MemoryStream(raw);
            parsed = MimeMessage.Load(stream);
        }
        catch (Exception ex)
        {
            Unreadable++;
            if (Unreadable <= 5) Notes.Add($"Could not read {name ?? "a message"}: {ex.Message}");
            return false;
        }

        if (!_seen.TryGetValue(folderId, out var ids))
        {
            ids = _mail.MessageIdsIn(folderId);
            _seen[folderId] = ids;
        }

        var messageId = parsed.MessageId;
        if (messageId is { Length: > 0 } && ids.Contains(messageId))
        {
            AlreadyHere++;
            return false;
        }

        var received = parsed.Date != default
            ? parsed.Date
            : fallbackDate ?? DateTimeOffset.UtcNow;

        var summary = MessageMapper.ToSummary(parsed, serverUid: null, raw.Length, received) with
        {
            IsRead = read,
            IsFlagged = flagged,
        };

        if (_mail.AddMessage(folderId, summary, raw) is null)
        {
            AlreadyHere++;
            return false;
        }

        if (messageId is { Length: > 0 }) ids.Add(messageId);
        Imported++;
        return true;
    }
}
