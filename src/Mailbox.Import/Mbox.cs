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
/// byte-exact. Read state comes from the headers the writers actually use: <c>Status: R/O</c>,
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
        var current = new List<byte[]>();
        var inMessage = false;

        foreach (var line in Lines(mbox))
        {
            if (IsSeparator(line))
            {
                Take(messages, current, ref inMessage);
                inMessage = true;
                continue;
            }

            if (!inMessage) continue;
            current.Add(Unescape(line));
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

        foreach (var line in Lines(new MemoryStream(raw)))
        {
            if (NeedsEscape(line)) mbox.WriteByte((byte)'>');
            mbox.Write(line);
            mbox.WriteByte((byte)'\n');
        }

        mbox.WriteByte((byte)'\n');
    }

    /// <summary>What the storage headers say about a message, read without a full parse.</summary>
    public static (bool IsRead, bool IsFlagged, bool IsExpunged) Flags(byte[] raw)
    {
        var read = false;
        var flagged = false;
        var expunged = false;

        foreach (var line in Lines(new MemoryStream(raw)))
        {
            if (line.Length == 0) break; // the headers ended

            var text = Encoding.ASCII.GetString(line);
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

    /// <summary>The file as raw lines without their endings, whichever ending it uses.</summary>
    private static IEnumerable<byte[]> Lines(Stream stream)
    {
        var buffer = new List<byte>(256);
        int b;
        var any = false;

        while ((b = stream.ReadByte()) >= 0)
        {
            any = true;
            if (b == '\n')
            {
                if (buffer.Count > 0 && buffer[^1] == '\r') buffer.RemoveAt(buffer.Count - 1);
                yield return buffer.ToArray();
                buffer.Clear();
                continue;
            }

            buffer.Add((byte)b);
        }

        if (any && buffer.Count > 0) yield return buffer.ToArray();
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

    private static void Take(List<MboxMessage> messages, List<byte[]> lines, ref bool inMessage)
    {
        if (!inMessage || lines.Count == 0)
        {
            lines.Clear();
            return;
        }

        // Trailing blank lines are the format's, not the message's: one blank line precedes
        // every separator, and some writers pad with more.
        var end = lines.Count;
        while (end > 0 && lines[end - 1].Length == 0) end--;

        var size = 0;
        for (var i = 0; i < end; i++) size += lines[i].Length + 1;

        var raw = new byte[size];
        var at = 0;
        for (var i = 0; i < end; i++)
        {
            lines[i].CopyTo(raw, at);
            at += lines[i].Length;
            raw[at++] = (byte)'\n';
        }

        var (read, flagged, expunged) = Flags(raw);
        if (!expunged) messages.Add(new MboxMessage(raw, read, flagged));

        lines.Clear();
    }
}
