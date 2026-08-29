using System.Text;

namespace Mailbox.Import;

/// <summary>One message out of an mbox: its true bytes, and what the storage flags said.</summary>
public sealed record MboxMessage(byte[] Raw, bool IsRead, bool IsFlagged);

/// <summary>
/// Reads and writes mbox — the one-big-file store Thunderbird keeps and half of Unix grew up
/// on.
/// </summary>
/// <remarks>
/// Splitting is on the <c>From </c> separator line, and unescaping is mboxrd's: a body line
/// matching <c>&gt;+From </c> loses one <c>&gt;</c>, which also reads mboxo files correctly for
/// every line mboxo can represent. The bytes handed back are the message's own — the escaping
/// is transport armour, not content — and writing puts it back, so a message round-trips
/// byte-exact.
/// <para>
/// <b>Byte-exact includes the line endings.</b> Every message that ever arrived over SMTP, IMAP
/// or POP3 ends its lines with CRLF, and that is what the store keeps; mbox files written by
/// the readers people migrate from carry it too. Rewriting them to LF is not cosmetic: it is the
/// bytes a DKIM body hash, an S/MIME signature and an OpenPGP signature are all computed over,
/// so a signed message that verified in the file it came from stops verifying the moment it is
/// read here. So each line keeps the ending it arrived with, and a file of mixed endings keeps
/// both.
/// </para>
/// Read state comes from the headers the writers actually use: <c>Status: R/O</c>,
/// <c>X-Status: F</c>, and Thunderbird's <c>X-Mozilla-Status</c> hex word (0x0001 read,
/// 0x0004 flagged, 0x0008 expunged — an expunged message is Thunderbird's deleted-not-yet-
/// compacted, and is not imported).
/// </remarks>
public static class Mbox
{
    /// <summary>Splits an mbox into messages. An empty or non-mbox file is an empty list.</summary>
    public static IReadOnlyList<MboxMessage> Read(Stream mbox)
    {
        ArgumentNullException.ThrowIfNull(mbox);

        var messages = new List<MboxMessage>();
        var current = new List<(byte[] Content, byte[] End)>();
        var inMessage = false;

        foreach (var line in Lines(mbox))
        {
            if (IsSeparator(line.Content))
            {
                Take(messages, current, ref inMessage);
                inMessage = true;
                continue;
            }

            if (!inMessage) continue;
            current.Add((Unescape(line.Content), line.End));
        }

        Take(messages, current, ref inMessage);
        return messages;
    }

    /// <summary>Whether a file starts the way an mbox does.</summary>
    public static bool Looks(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[5];
            return stream.Read(head, 0, 5) == 5 && head.AsSpan().SequenceEqual("From "u8);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Appends one message in mboxrd form: the separator line, the body with its
    /// <c>From </c>-shaped lines escaped, and the blank line the next separator needs.
    /// </summary>
    public static void Append(Stream mbox, byte[] raw, DateTimeOffset date, string? fromAddress = null)
    {
        ArgumentNullException.ThrowIfNull(mbox);
        ArgumentNullException.ThrowIfNull(raw);

        var separator = Encoding.ASCII.GetBytes(
            $"From {fromAddress ?? "MAILER-DAEMON"} {date.UtcDateTime:ddd MMM d HH:mm:ss yyyy}\n");
        mbox.Write(separator);

        foreach (var (content, end) in Lines(new MemoryStream(raw)))
        {
            if (NeedsEscape(content)) mbox.WriteByte((byte)'>');
            mbox.Write(content);

            // The line's own ending, so a CRLF message stays a CRLF message. A last line that
            // had none still needs one, or the blank line below joins onto it.
            mbox.Write(end.Length > 0 ? end : "\n"u8);
        }

        mbox.WriteByte((byte)'\n');
    }

    /// <summary>What the storage headers say about a message, read without a full parse.</summary>
    public static (bool IsRead, bool IsFlagged, bool IsExpunged) Flags(byte[] raw)
    {
        var read = false;
        var flagged = false;
        var expunged = false;

        foreach (var (content, _) in Lines(new MemoryStream(raw)))
        {
            if (content.Length == 0) break; // the headers ended

            var text = Encoding.ASCII.GetString(content);
            if (text.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
            {
                read |= text.Contains('R', StringComparison.Ordinal) || text.Contains('O', StringComparison.Ordinal);
            }
            else if (text.StartsWith("X-Status:", StringComparison.OrdinalIgnoreCase))
            {
                flagged |= text.Contains('F', StringComparison.Ordinal);
            }
            else if (text.StartsWith("X-Mozilla-Status:", StringComparison.OrdinalIgnoreCase))
            {
                var value = text["X-Mozilla-Status:".Length..].Trim();
                if (int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var bits))
                {
                    read |= (bits & 0x0001) != 0;
                    flagged |= (bits & 0x0004) != 0;
                    expunged |= (bits & 0x0008) != 0;
                }
            }
        }

        return (read, flagged, expunged);
    }

    // ---- Lines and separators ------------------------------------------------------------------

    /// <summary>
    /// The file as raw lines, each with the ending it actually carried — <c>\r\n</c>, <c>\n</c>,
    /// or nothing at all for a last line that ends the file.
    /// </summary>
    /// <remarks>
    /// The ending is handed back rather than dropped because it is part of the message: reading
    /// and rejoining on <c>\n</c> alone rewrites every CRLF message on the way in and on the way
    /// out, which is what a body hash and a signature are taken over.
    /// </remarks>
    private static IEnumerable<(byte[] Content, byte[] End)> Lines(Stream stream)
    {
        var buffer = new List<byte>(256);
        int b;
        var any = false;

        while ((b = stream.ReadByte()) >= 0)
        {
            any = true;
            if (b == '\n')
            {
                var crlf = buffer.Count > 0 && buffer[^1] == '\r';
                if (crlf) buffer.RemoveAt(buffer.Count - 1);
                yield return (buffer.ToArray(), crlf ? "\r\n"u8.ToArray() : "\n"u8.ToArray());
                buffer.Clear();
                continue;
            }

            buffer.Add((byte)b);
        }

        if (any && buffer.Count > 0) yield return (buffer.ToArray(), []);
    }

    private static bool IsSeparator(byte[] line)
        => line.Length >= 5 && line.AsSpan(0, 5).SequenceEqual("From "u8);

    /// <summary>mboxrd in: a line of <c>&gt;</c>s followed by <c>From </c> loses one of them.</summary>
    private static byte[] Unescape(byte[] line)
    {
        var quotes = 0;
        while (quotes < line.Length && line[quotes] == '>') quotes++;
        return quotes > 0 && line.Length >= quotes + 5 && line.AsSpan(quotes, 5).SequenceEqual("From "u8)
            ? line[1..]
            : line;
    }

    /// <summary>mboxrd out: any <c>&gt;*From </c> line gains one, so reading gives it back.</summary>
    private static bool NeedsEscape(byte[] line)
    {
        var quotes = 0;
        while (quotes < line.Length && line[quotes] == '>') quotes++;
        return line.Length >= quotes + 5 && line.AsSpan(quotes, 5).SequenceEqual("From "u8);
    }

    private static void Take(
        List<MboxMessage> messages, List<(byte[] Content, byte[] End)> lines, ref bool inMessage)
    {
        if (!inMessage || lines.Count == 0)
        {
            lines.Clear();
            return;
        }

        // Trailing blank lines are the format's, not the message's: one blank line precedes
        // every separator, and some writers pad with more.
        var end = lines.Count;
        while (end > 0 && lines[end - 1].Content.Length == 0) end--;

        var size = 0;
        for (var i = 0; i < end; i++) size += lines[i].Content.Length + lines[i].End.Length;

        var raw = new byte[size];
        var at = 0;
        for (var i = 0; i < end; i++)
        {
            lines[i].Content.CopyTo(raw, at);
            at += lines[i].Content.Length;
            lines[i].End.CopyTo(raw, at);
            at += lines[i].End.Length;
        }

        var (read, flagged, expunged) = Flags(raw);
        if (!expunged) messages.Add(new MboxMessage(raw, read, flagged));

        lines.Clear();
    }
}
