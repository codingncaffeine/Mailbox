using System.Buffers.Binary;
using System.Text;
using Mailbox.Security.Dns;

namespace Mailbox.Tests;

/// <summary>
/// The wire format, against bytes built by hand.
/// </summary>
/// <remarks>
/// Every length in a response is a number the answering machine chose, and the name being asked
/// about came out of a message a stranger sent — so the malformed cases are the point of this
/// file rather than an afterthought. A parser that reads past its buffer is the bug this class
/// of code exists to not have.
/// </remarks>
public class DnsWireTests
{
    // ---- Building responses ----------------------------------------------------------------

    /// <summary>A name as length-prefixed labels.</summary>
    private static byte[] Name(string name)
    {
        var bytes = new List<byte>();

        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
        return [.. bytes];
    }

    /// <summary>A TXT record's RDATA: each string length-prefixed.</summary>
    private static byte[] Text(params string[] strings)
    {
        var bytes = new List<byte>();

        foreach (var s in strings)
        {
            bytes.Add((byte)s.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(s));
        }

        return [.. bytes];
    }

    private static byte[] Response(
        ushort id, string name, IEnumerable<byte[]> answers, int rcode = 0, uint ttl = 300,
        ushort type = 16)
    {
        var records = answers.ToList();
        var bytes = new List<byte>();

        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(0x8180 | rcode));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)records.Count);
        bytes.AddRange(header);

        // The question, echoed.
        bytes.AddRange(Name(name));
        bytes.AddRange([0, 16, 0, 1]);

        foreach (var data in records)
        {
            bytes.AddRange(Name(name));

            var fixedPart = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), ttl);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)data.Length);

            bytes.AddRange(fixedPart);
            bytes.AddRange(data);
        }

        return [.. bytes];
    }

    // ---- Queries ---------------------------------------------------------------------------

    [Fact]
    public void AQueryAsksTheNameGiven()
    {
        var query = DnsWire.Query(0x1234, "sel._domainkey.example.com");

        Assert.Equal(0x12, query[0]);
        Assert.Equal(0x34, query[1]);

        // Recursion desired, and exactly one question.
        Assert.Equal(0x01, query[2]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));

        Assert.Contains("_domainkey", Encoding.ASCII.GetString(query), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("example..com")]
    [InlineData("exa mple.com")]
    [InlineData("exämple.com")]
    public void ANameThatIsNotOneIsRefusedRatherThanSent(string name)
        => Assert.Throws<ArgumentException>(() => DnsWire.Query(1, name));

    [Fact]
    public void ALabelTooLongIsRefused()
        => Assert.Throws<ArgumentException>(
            () => DnsWire.Query(1, new string('a', 64) + ".example.com"));

    [Fact]
    public void ANameTooLongIsRefused()
    {
        var name = string.Join('.', Enumerable.Repeat(new string('a', 60), 5));
        Assert.Throws<ArgumentException>(() => DnsWire.Query(1, name));
    }

    // ---- Reading answers --------------------------------------------------------------------

    [Fact]
    public void ATextRecordIsRead()
    {
        var response = Response(7, "example.com", [Text("v=spf1 -all")]);
        var answer = DnsWire.ReadResponse(response, 7, "example.com");

        Assert.Equal(DnsResponseCode.NoError, answer.Code);
        Assert.Equal(["v=spf1 -all"], answer.Records);
        Assert.Equal(300, answer.Ttl);
    }

    /// <summary>
    /// A key long enough to be worth having does not fit one character-string, so it is
    /// published as several and means nothing until they are joined. RFC 6376 §3.6.2.2.
    /// </summary>
    [Fact]
    public void ARecordSplitAcrossStringsIsJoined()
    {
        var response = Response(7, "sel._domainkey.example.com",
            [Text("v=DKIM1; k=rsa; p=AAAA", "BBBBCCCC", "DDDD")]);

        var answer = DnsWire.ReadResponse(response, 7, "sel._domainkey.example.com");

        Assert.Equal(["v=DKIM1; k=rsa; p=AAAABBBBCCCCDDDD"], answer.Records);
    }

    [Fact]
    public void SeveralRecordsUnderOneNameAreAllRead()
    {
        var response = Response(7, "example.com", [Text("first"), Text("second")]);
        var answer = DnsWire.ReadResponse(response, 7, "example.com");

        Assert.Equal(["first", "second"], answer.Records);
    }

    /// <summary>The shortest TTL is what the cache may keep the answer for.</summary>
    [Fact]
    public void TheTtlIsRead()
        => Assert.Equal(60, DnsWire.ReadResponse(
            Response(7, "example.com", [Text("x")], ttl: 60), 7, "example.com").Ttl);

    [Fact]
    public void ANameThatDoesNotExistSaysSo()
    {
        var answer = DnsWire.ReadResponse(
            Response(7, "nope.example.com", [], rcode: 3), 7, "nope.example.com");

        Assert.Equal(DnsResponseCode.NameError, answer.Code);
        Assert.Empty(answer.Records);
        Assert.False(answer.Resolved);
    }

    /// <summary>
    /// A resolver that followed a CNAME puts it in the answer section beside what was asked for.
    /// Stepping over it is the whole of the handling it wants.
    /// </summary>
    [Fact]
    public void AnAnswerOfAnotherTypeIsSteppedOver()
    {
        var response = Response(7, "example.com", [Name("elsewhere.example.com")], type: 5);
        var answer = DnsWire.ReadResponse(response, 7, "example.com");

        Assert.Empty(answer.Records);
        Assert.Equal(DnsResponseCode.NoError, answer.Code);
    }

    // ---- What must be refused ----------------------------------------------------------------

    /// <summary>
    /// The identifier and the echoed question are what an off-path forger has to guess rather
    /// than merely beat to the socket.
    /// </summary>
    [Fact]
    public void AResponseCarryingAnotherIdentifierIsRefused()
        => Assert.Throws<DnsProtocolException>(() => DnsWire.ReadResponse(
            Response(7, "example.com", [Text("x")]), 8, "example.com"));

    [Fact]
    public void AResponseAnsweringAnotherNameIsRefused()
        => Assert.Throws<DnsProtocolException>(() => DnsWire.ReadResponse(
            Response(7, "evil.example", [Text("x")]), 7, "example.com"));

    [Fact]
    public void AQueryPresentedAsAResponseIsRefused()
    {
        var response = Response(7, "example.com", [Text("x")]);
        response[2] = 0x01;

        Assert.Throws<DnsProtocolException>(() => DnsWire.ReadResponse(response, 7, "example.com"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(11)]
    public void AResponseTooShortToHaveAHeaderIsRefused(int length)
        => Assert.Throws<DnsProtocolException>(
            () => DnsWire.ReadResponse(new byte[length], 7, "example.com"));

    [Fact]
    public void AnAnswerClaimingMoreDataThanItHoldsIsRefused()
    {
        var response = Response(7, "example.com", [Text("x")]);

        // The last two bytes before the RDATA are its declared length.
        response[^3] = 0xFF;

        Assert.Throws<DnsProtocolException>(() => DnsWire.ReadResponse(response, 7, "example.com"));
    }

    [Fact]
    public void ATextStringClaimingMoreBytesThanItHoldsIsRefused()
    {
        var response = Response(7, "example.com", [Text("hello")]);

        // The character-string's own length prefix, made to overrun its record.
        response[^6] = 0x40;

        Assert.Throws<DnsProtocolException>(() => DnsWire.ReadResponse(response, 7, "example.com"));
    }

    /// <summary>
    /// A compression pointer that points at itself would loop for as long as it is followed.
    /// The parse ends instead.
    /// </summary>
    [Fact]
    public void ANamePointingAtItselfIsRefused()
    {
        var response = new byte[]
        {
            0, 7, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0,
            0xC0, 12,
        };

        Assert.Throws<DnsProtocolException>(() => DnsWire.ReadResponse(response, 7, "example.com"));
    }

    [Fact]
    public void ACompressionPointerLeavingTheResponseIsRefused()
    {
        var response = new byte[]
        {
            0, 7, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0,
            0xC0, 0xFF,
        };

        Assert.Throws<DnsProtocolException>(() => DnsWire.ReadResponse(response, 7, "example.com"));
    }

    [Fact]
    public void ALabelRunningPastTheResponseIsRefused()
    {
        var response = new byte[]
        {
            0, 7, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0,
            40, (byte)'a', (byte)'b',
        };

        Assert.Throws<DnsProtocolException>(() => DnsWire.ReadResponse(response, 7, "example.com"));
    }

    [Fact]
    public void AResponseEchoingNoQuestionIsRefused()
    {
        var response = Response(7, "example.com", [Text("x")]);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), 0);

        Assert.Throws<DnsProtocolException>(() => DnsWire.ReadResponse(response, 7, "example.com"));
    }

    // ---- resolv.conf --------------------------------------------------------------------------

    [Fact]
    public void NameserversAreReadFromResolvConf()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        File.WriteAllText(path,
            """
            # a comment
            search example.com
            nameserver 192.0.2.1
            nameserver 2001:db8::1%eth0
            options edns0
            """);

        try
        {
            var servers = DnsResolver.SystemNameservers(path);

            Assert.Equal(2, servers.Count);
            Assert.Equal("192.0.2.1", servers[0].Address.ToString());
            Assert.Equal(53, servers[0].Port);
            Assert.Equal("2001:db8::1", servers[1].Address.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMachineWithNoResolvConfResolvesNothing()
    {
        var servers = DnsResolver.SystemNameservers(
            Path.Combine(Path.GetTempPath(), "mailbox-no-such-resolv.conf"));

        Assert.Empty(servers);
        using var resolver = new DnsResolver(servers);
        Assert.False(resolver.CanResolve);
    }

    /// <summary>Nothing to ask means an empty answer rather than an attempt or an exception.</summary>
    [Fact]
    public async Task AResolverWithNoServersAsksNothing()
    {
        using var resolver = new DnsResolver([]);

        var answer = await resolver.TxtAsync("example.com", TestContext.Current.CancellationToken);
        Assert.Empty(answer.Records);
    }
}
