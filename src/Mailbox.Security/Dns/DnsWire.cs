using System.Buffers.Binary;
using System.Text;

namespace Mailbox.Security.Dns;

/// <summary>What a server said, once the answer has been taken apart.</summary>
/// <param name="Code">The RCODE. Zero is the only one that carries records.</param>
/// <param name="Records">
/// One entry per TXT record. A record split across several character-strings is joined, which
/// RFC 6376 §3.6.2.2 requires — a key long enough to be worth having is usually split.
/// </param>
/// <param name="Ttl">The shortest TTL among the answers, in seconds. What the cache honours.</param>
public sealed record DnsAnswer(DnsResponseCode Code, IReadOnlyList<string> Records, int Ttl)
{
    public static DnsAnswer Empty { get; } = new(DnsResponseCode.NoError, [], 0);

    /// <summary>True when the name exists and the server said so definitively.</summary>
    public bool Resolved => Code == DnsResponseCode.NoError;
}

/// <summary>The RCODEs worth telling apart. Everything else is a server problem.</summary>
public enum DnsResponseCode
{
    NoError = 0,
    FormatError = 1,
    ServerFailure = 2,

    /// <summary>The name does not exist — for DKIM, a revoked or never-published selector.</summary>
    NameError = 3,

    NotImplemented = 4,
    Refused = 5,
}

/// <summary>
/// Raised when a response cannot be trusted to be an answer to the question we asked.
/// </summary>
public sealed class DnsProtocolException(string message) : Exception(message);

/// <summary>
/// The DNS message format, and nothing else — no sockets, no policy, no cache.
/// </summary>
/// <remarks>
/// Split out because this is the part that reads bytes chosen by someone else. The name being
/// resolved comes out of a message a stranger sent, so the answer comes from wherever that name
/// points, and every length in it is a number that machine wrote. Parsing is therefore bounds-
/// checked at every step and gives up rather than guessing.
/// <para>
/// Only TXT is implemented, because only TXT is needed: a DKIM public key is published as one.
/// A resolver that can ask for anything is a larger thing to get right than a resolver that
/// asks one question.
/// </para>
/// </remarks>
public static class DnsWire
{
    /// <summary>TXT.</summary>
    internal const ushort TypeText = 16;

    /// <summary>IN.</summary>
    internal const ushort ClassInternet = 1;

    private const int HeaderBytes = 12;

    /// <summary>A name may not exceed this, encoded, per RFC 1035 §2.3.4.</summary>
    private const int MaxNameBytes = 255;

    /// <summary>One label may not exceed this.</summary>
    private const int MaxLabelBytes = 63;

    /// <summary>
    /// Compression pointers may chain. Bounded so a response that points at itself ends the
    /// parse rather than the process.
    /// </summary>
    private const int MaxPointerJumps = 16;

    /// <summary>More answers than any real TXT lookup returns.</summary>
    private const int MaxAnswers = 64;

    /// <summary>Builds a TXT query for a name.</summary>
    /// <exception cref="ArgumentException">The name is not one that can be asked about.</exception>
    public static byte[] Query(ushort id, string name)
    {
        var labels = EncodeName(name);

        var query = new byte[HeaderBytes + labels.Length + 4];
        var span = query.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span, id);

        // Recursion desired. We ask a resolver a question; we do not walk the tree ourselves.
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 1);

        labels.CopyTo(span[HeaderBytes..]);

        var tail = span[(HeaderBytes + labels.Length)..];
        BinaryPrimitives.WriteUInt16BigEndian(tail, TypeText);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], ClassInternet);

        return query;
    }

    /// <summary>
    /// Reads a response, having first satisfied itself that it answers the question asked.
    /// </summary>
    /// <remarks>
    /// The identifier and the echoed question are both checked. Neither is a defence against
    /// anyone who can read the query, and together they are what makes a blind answer from an
    /// off-path forger have to guess right rather than merely arrive first.
    /// </remarks>
    /// <exception cref="DnsProtocolException">The response is malformed or answers something else.</exception>
    public static DnsAnswer ReadResponse(ReadOnlySpan<byte> response, ushort id, string name)
    {
        if (response.Length < HeaderBytes) throw new DnsProtocolException("The response is too short.");

        if (BinaryPrimitives.ReadUInt16BigEndian(response) != id)
        {
            throw new DnsProtocolException("The response does not carry the identifier that was asked.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if ((flags & 0x8000) == 0) throw new DnsProtocolException("The response is not a response.");

        var code = (DnsResponseCode)(flags & 0x000F);
        var questions = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);

        var offset = HeaderBytes;

        // The question is echoed back. One that disagrees with ours is an answer to something
        // else, whoever sent it.
        if (questions != 1) throw new DnsProtocolException("The response echoes no single question.");

        var asked = ReadName(response, ref offset);
        if (!string.Equals(asked, Normalize(name), StringComparison.OrdinalIgnoreCase))
        {
            throw new DnsProtocolException("The response answers a different name.");
        }

        if (offset + 4 > response.Length) throw new DnsProtocolException("The question is truncated.");
        if (BinaryPrimitives.ReadUInt16BigEndian(response[offset..]) != TypeText)
        {
            throw new DnsProtocolException("The response answers a different type.");
        }

        offset += 4;

        if (code != DnsResponseCode.NoError) return new DnsAnswer(code, [], 0);

        var records = new List<string>();
        var ttl = int.MaxValue;

        for (var i = 0; i < answers && i < MaxAnswers; i++)
        {
            if (offset >= response.Length) break;

            // The owner name is read only to step over it, and may be a compression pointer.
            ReadName(response, ref offset);

            if (offset + 10 > response.Length) throw new DnsProtocolException("An answer is truncated.");

            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var @class = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
            var recordTtl = BinaryPrimitives.ReadUInt32BigEndian(response[(offset + 4)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);

            offset += 10;

            if (offset + length > response.Length)
            {
                throw new DnsProtocolException("An answer claims more data than the response holds.");
            }

            // Anything else in the answer section is a CNAME the resolver followed for us, or a
            // type we did not ask for. Stepping over it is the whole of the handling either
            // wants.
            if (type == TypeText && @class == ClassInternet)
            {
                records.Add(ReadText(response.Slice(offset, length)));
                ttl = Math.Min(ttl, (int)Math.Min(recordTtl, int.MaxValue));
            }

            offset += length;
        }

        return new DnsAnswer(code, records, records.Count == 0 ? 0 : ttl);
    }

    /// <summary>
    /// A TXT record's character-strings, joined.
    /// </summary>
    /// <remarks>
    /// Each is length-prefixed, and a record longer than 255 bytes is published as several — so
    /// a 2048-bit key arrives in pieces and means nothing until they are put back together.
    /// Read as Latin-1 because the bytes are not declared to be anything: a key is base64 and a
    /// tag list is ASCII, and decoding as UTF-8 would replace a byte rather than preserve it.
    /// </remarks>
    private static string ReadText(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder(data.Length);
        var offset = 0;

        while (offset < data.Length)
        {
            var length = data[offset++];
            if (offset + length > data.Length)
            {
                throw new DnsProtocolException("A text record claims more bytes than it holds.");
            }

            text.Append(Encoding.Latin1.GetString(data.Slice(offset, length)));
            offset += length;
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads a name, following compression pointers, and leaves the offset after the name as it
    /// appears at this position rather than after whatever a pointer led to.
    /// </summary>
    private static string ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        var name = new StringBuilder();
        var jumps = 0;
        var position = offset;
        var jumped = false;

        while (true)
        {
            if (position >= message.Length) throw new DnsProtocolException("A name runs past the response.");

            var length = message[position];

            if (length == 0)
            {
                position++;
                break;
            }

            // The top two bits set means the rest is an offset to where the name continues.
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length)
                {
                    throw new DnsProtocolException("A compression pointer runs past the response.");
                }

                if (++jumps > MaxPointerJumps)
                {
                    throw new DnsProtocolException("A name points at itself.");
                }

                var target = ((length & 0x3F) << 8) | message[position + 1];

                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }

                if (target >= message.Length)
                {
                    throw new DnsProtocolException("A compression pointer leaves the response.");
                }

                position = target;
                continue;
            }

            if ((length & 0xC0) != 0) throw new DnsProtocolException("A label uses a reserved form.");
            if (length > MaxLabelBytes) throw new DnsProtocolException("A label is too long.");
            if (position + 1 + length > message.Length)
            {
                throw new DnsProtocolException("A label runs past the response.");
            }

            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(message.Slice(position + 1, length)));

            if (name.Length > MaxNameBytes) throw new DnsProtocolException("A name is too long.");

            position += 1 + length;
        }

        if (!jumped) offset = position;
        return name.ToString();
    }

    /// <summary>
    /// A name as label bytes.
    /// </summary>
    /// <remarks>
    /// The selector and domain both come out of the message's own DKIM-Signature header, so this
    /// is where a name a stranger wrote is refused rather than sent. Nothing is escaped or
    /// rewritten to make a bad name work: a name that is not one is an argument error.
    /// </remarks>
    private static byte[] EncodeName(string name)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0) throw new ArgumentException("The name is empty.", nameof(name));

        var labels = normalized.Split('.');
        var bytes = new List<byte>(normalized.Length + 2);

        foreach (var label in labels)
        {
            if (label.Length == 0) throw new ArgumentException("The name has an empty label.", nameof(name));
            if (label.Length > MaxLabelBytes) throw new ArgumentException("A label is too long.", nameof(name));

            // ASCII only. A name with anything else in it has not been through IDNA, and
            // guessing an encoding for it would be asking about a name nobody published.
            foreach (var c in label)
            {
                if (c is < (char)0x21 or > (char)0x7E)
                {
                    throw new ArgumentException("The name is not ASCII.", nameof(name));
                }
            }

            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);

        if (bytes.Count > MaxNameBytes) throw new ArgumentException("The name is too long.", nameof(name));
        return [.. bytes];
    }

    /// <summary>A name without its root dot, which is how the wire spells it.</summary>
    internal static string Normalize(string name) => name.Trim().TrimEnd('.');
}
