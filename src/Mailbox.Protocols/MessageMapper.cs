using MimeKit;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>
/// Turns a parsed MIME message into the row the list draws.
/// </summary>
/// <remarks>
/// Separate from the protocols so it can be tested against a file rather than a server, which
/// is the only practical way to cover the mail that actually turns up: missing From, dates from
/// a machine with the wrong clock, a body that is only HTML, or only an attachment.
/// </remarks>
public static class MessageMapper
{
    /// <summary>How much of the body the list keeps for the preview line.</summary>
    private const int PreviewLength = 200;

    public static MessageSummary ToSummary(MimeMessage message, string? serverUid,
        long sizeBytes, DateTimeOffset receivedUtc, bool isRead = false, bool isFlagged = false)
    {
        // Before any part is asked for its text: a charset the platform cannot resolve is read as
        // Latin-1, which turns a Japanese or Cyrillic body into well-formed nonsense without
        // anything reporting a fault. See LegacyCodePages.
        LegacyCodePages.Register();

        var from = message.From.Mailboxes.FirstOrDefault();

        return new MessageSummary(
            Id: 0,
            FolderId: 0,
            ServerUid: serverUid,
            MessageId: string.IsNullOrWhiteSpace(message.MessageId) ? null : message.MessageId,
            FromName: from?.Name ?? string.Empty,
            FromAddress: from?.Address ?? UnknownSender(message),
            Subject: message.Subject ?? string.Empty,
            Preview: Preview(message),
            Sent: SentDate(message),
            Received: receivedUtc,
            SizeBytes: sizeBytes,
            IsRead: isRead,
            IsFlagged: isFlagged,
            HasAttachment: message.Attachments.Any())
        {
            BodyText = FullText(message),
            Importance = message.Importance switch
            {
                MessageImportance.Low => 0,
                MessageImportance.High => 2,
                _ => message.Priority switch
                {
                    MessagePriority.NonUrgent => 0,
                    MessagePriority.Urgent => 2,
                    _ => 1,
                },
            },
            To = [.. message.To.Mailboxes.Select(m => m.Address.Trim().ToLowerInvariant()).Where(a => a.Length > 0)],
            Cc = [.. message.Cc.Mailboxes.Select(m => m.Address.Trim().ToLowerInvariant()).Where(a => a.Length > 0)],
            Expires = Expiry(message),

            // A feed item's own address and picture, lifted into columns because the article
            // list wants both on every visible row. Read through the header list rather than
            // off the wire: a long address is folded into an encoded word on the way out, and
            // this is what unfolds it.
            FeedLink = message.Headers["X-Mailbox-Feed-Link"] ?? string.Empty,
            FeedImage = message.Headers["X-Mailbox-Feed-Image"] ?? string.Empty,

            // How long it takes to read, counted once here rather than on every draw of every
            // visible row. Only for a feed article: an ordinary message's length is not something
            // anybody wants told, and a mail list that announced "4 min" per letter would be
            // reporting on somebody's correspondence.
            FeedWords = message.Headers["X-Mailbox-Feed-Link"] is { Length: > 0 }
                ? Words(FullText(message))
                : 0,
        };
    }

    /// <summary>
    /// How many words a piece of text holds.
    /// </summary>
    /// <remarks>
    /// Runs of whitespace, which is close enough: the number is turned into a reading time and
    /// rounded to a minute, so the difference between one definition of a word and another
    /// disappears long before it reaches the reader.
    /// </remarks>
    private static int Words(string text)
        => text.Length == 0 ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>The message's own expiry — an Expires or Expiry-Date header that parses — or null.</summary>
    private static DateTimeOffset? Expiry(MimeMessage message)
    {
        foreach (var name in (string[])["Expires", "Expiry-Date"])
        {
            var value = message.Headers[name];
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (MimeKit.Utils.DateUtils.TryParse(value, out var when)) return when;
        }

        return null;
    }

    /// <summary>
    /// A message with no parseable From still has to show something. The envelope sender is the
    /// next best thing; failing that, say so rather than leaving the column blank, which reads
    /// as a rendering fault.
    /// </summary>
    private static string UnknownSender(MimeMessage message)
        => message.Sender?.Address ?? "unknown sender";

    /// <summary>
    /// When it was sent. A date in the future is a wrong clock somewhere upstream, and sorting
    /// by it pins that message to the top of the list forever, so it is not trusted.
    /// </summary>
    private static DateTimeOffset? SentDate(MimeMessage message)
    {
        if (message.Date == default) return null;

        return message.Date > DateTimeOffset.UtcNow.AddDays(1) ? null : message.Date;
    }

    /// <summary>
    /// The first line or two of the body. Plain text if the sender provided it; otherwise the
    /// HTML converted down, because a preview of raw markup is worse than none.
    /// </summary>
    /// <summary>
    /// The whole plain text of the message, for the search index — the preview trimmed to two
    /// hundred characters would miss a word further down, which is the point of a body search.
    /// </summary>
    internal static string FullText(MimeMessage message)
    {
        var text = message.TextBody
                   ?? (message.HtmlBody is { } html ? ToPlain(html) : null)
                   ?? string.Empty;

        // Collapse runs of whitespace so the index is not full of newline noise, but keep it
        // whole — no length cap.
        return Condense(text, int.MaxValue);
    }

    internal static string Preview(MimeMessage message)
    {
        var text = message.TextBody
                   ?? (message.HtmlBody is { } html ? ToPlain(html) : null)
                   ?? string.Empty;

        return Condense(text, PreviewLength);
    }

    /// <summary>
    /// Strips markup for the preview line only. Deliberately crude: this feeds two hundred
    /// characters of plain text into a list row, never the reading pane, which gets a real
    /// sanitiser and a renderer in Phase 4. Style and script content is dropped rather than
    /// flattened, because CSS read as prose is worse than no preview.
    /// </summary>
    private static string ToPlain(string html)
    {
        var text = new System.Text.StringBuilder(html.Length);
        var insideTag = false;
        var skipUntil = (string?)null;

        for (var i = 0; i < html.Length; i++)
        {
            if (skipUntil is not null)
            {
                if (!html.AsSpan(i).StartsWith(skipUntil, StringComparison.OrdinalIgnoreCase)) continue;

                i += skipUntil.Length - 1;
                skipUntil = null;
                insideTag = false;
                continue;
            }

            var c = html[i];
            if (c == '<')
            {
                insideTag = true;
                if (StartsTag(html, i, "script")) skipUntil = "</script";
                else if (StartsTag(html, i, "style")) skipUntil = "</style";
                continue;
            }

            if (c == '>') { insideTag = false; text.Append(' '); continue; }
            if (!insideTag) text.Append(c);
        }

        return System.Net.WebUtility.HtmlDecode(text.ToString());
    }

    private static bool StartsTag(string html, int at, string name)
        => html.AsSpan(at + 1).StartsWith(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Collapses whitespace to single spaces and trims to length.</summary>
    internal static string Condense(string text, int limit)
    {
        var builder = new System.Text.StringBuilder(Math.Min(text.Length, limit));
        var lastWasSpace = false;

        foreach (var character in text)
        {
            var isSpace = char.IsWhiteSpace(character);
            if (isSpace)
            {
                if (!lastWasSpace && builder.Length > 0) builder.Append(' ');
            }
            else
            {
                builder.Append(character);
            }

            lastWasSpace = isSpace;
            if (builder.Length >= limit) break;
        }

        return builder.ToString().TrimEnd();
    }
}
