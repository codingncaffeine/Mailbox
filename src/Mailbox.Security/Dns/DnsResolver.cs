using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Mailbox.Security.Dns;

/// <summary>Somewhere a TXT record can be asked for.</summary>
/// <remarks>
/// An interface so the thing that verifies a signature can be tested without a network, and so
/// the reading pane can be handed something that resolves nothing at all. The rule stands: no key
/// discovery on the path that draws a message.
/// </remarks>
public interface ITxtLookup
{
    Task<DnsAnswer> TxtAsync(string name, CancellationToken cancellation = default);
}

/// <summary>A lookup that answers nothing, for every path that must not resolve.</summary>
public sealed class NoLookup : ITxtLookup
{
    public static NoLookup Instance { get; } = new();

    public Task<DnsAnswer> TxtAsync(string name, CancellationToken cancellation = default)
        => Task.FromResult(DnsAnswer.Empty);
}

/// <summary>
/// A resolver of our own, asking the system's nameservers for TXT records and nothing else.
/// </summary>
/// <remarks>
/// The no-network render design is the reason this exists rather than a library or the platform's own resolver. Verifying
/// a signature means looking up a name the *sender* chose, so the lookup is an action a stranger
/// caused; it therefore has to be ours, on our schedule, off the render path, and bounded in
/// what it will do. What it will do is: one question, to the resolvers already configured on
/// this machine, with a timeout, a size cap and a cache.
/// <para>
/// It never falls back to a public resolver. A resolver of last resort would send the domains
/// this person corresponds with to a third party they never chose, which is a worse privacy
/// failure than not verifying a signature.
/// </para>
/// </remarks>
public sealed class DnsResolver : ITxtLookup, IDisposable
{
    /// <summary>Larger than any TXT answer, and small enough to bound a hostile one.</summary>
    private const int MaxResponseBytes = 8 * 1024;

    /// <summary>What a resolver advertising EDNS0 may send us in one datagram.</summary>
    private const int MaxDatagramBytes = 4096;

    /// <summary>Enough for a key that has been split; past this the answer is not a key.</summary>
    private const int MaxCachedEntries = 512;

    private readonly IReadOnlyList<IPEndPoint> _servers;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public DnsResolver(IReadOnlyList<IPEndPoint>? servers = null, TimeSpan? timeout = null)
    {
        _servers = servers ?? SystemNameservers();
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Whether there is anywhere to ask. False on a machine with no resolver configured.</summary>
    public bool CanResolve => _servers.Count > 0;

    /// <summary>The nameservers this will ask, for the log and for a diagnostic.</summary>
    public IReadOnlyList<IPEndPoint> Servers => _servers;

    /// <summary>
    /// Asks for a name's TXT records, from the cache where one is still good.
    /// </summary>
    /// <remarks>
    /// A failure is a failure, never an exception thrown at the caller: a signature that cannot
    /// be checked is not a signature that failed, and the difference matters to what the reader
    /// is told. The answer says which it was.
    /// </remarks>
    public async Task<DnsAnswer> TxtAsync(string name, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(name) || _servers.Count == 0) return DnsAnswer.Empty;

        var key = DnsWire.Normalize(name);

        if (_cache.TryGetValue(key, out var cached) && !cached.Expired) return cached.Answer;

        DnsAnswer answer;

        try
        {
            answer = await AskAsync(key, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Every way this fails — no route, a timeout, a malformed answer — means the same
            // thing to the caller: nothing was learned. Which is not the same as a failed check.
            return new DnsAnswer(DnsResponseCode.ServerFailure, [], 0);
        }

        Remember(key, answer);
        return answer;
    }

    private async Task<DnsAnswer> AskAsync(string name, CancellationToken cancellation)
    {
        // The identifier is the only unpredictable thing in a query an off-path forger has to
        // guess, so it comes from the cryptographic generator rather than from Random.
        var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        var query = DnsWire.Query(id, name);

        Exception? last = null;

        foreach (var server in _servers)
        {
            try
            {
                var (bytes, truncated) = await OverUdpAsync(server, query, cancellation);

                // Truncated means the answer did not fit a datagram, which a split key often
                // does not. TCP is the specified way to ask again rather than a workaround.
                if (truncated) bytes = await OverTcpAsync(server, query, cancellation);

                return DnsWire.ReadResponse(bytes, id, name);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new DnsProtocolException("There was no nameserver to ask.");
    }

    private async Task<(byte[] Bytes, bool Truncated)> OverUdpAsync(
        IPEndPoint server, byte[] query, CancellationToken cancellation)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(_timeout);

        await socket.ConnectAsync(server, deadline.Token);
        await socket.SendAsync(query, SocketFlags.None, deadline.Token);

        var buffer = new byte[MaxDatagramBytes];
        var read = await socket.ReceiveAsync(buffer, SocketFlags.None, deadline.Token);

        if (read < 12) throw new DnsProtocolException("The datagram is too short to be a response.");

        // Bit 9 of the flags word: the answer did not fit.
        var truncated = (buffer[2] & 0x02) != 0;
        return (buffer[..read], truncated);
    }

    /// <summary>
    /// The same question over TCP, where the message is preceded by its own length.
    /// </summary>
    private async Task<byte[]> OverTcpAsync(
        IPEndPoint server, byte[] query, CancellationToken cancellation)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(_timeout);

        await socket.ConnectAsync(server, deadline.Token);

        var framed = new byte[query.Length + 2];
        framed[0] = (byte)(query.Length >> 8);
        framed[1] = (byte)query.Length;
        query.CopyTo(framed, 2);

        await socket.SendAsync(framed, SocketFlags.None, deadline.Token);

        var header = new byte[2];
        await ReadExactlyAsync(socket, header, deadline.Token);

        var length = (header[0] << 8) | header[1];

        // The length is a number the server chose, so it is checked before it is allocated.
        if (length is < 12 or > MaxResponseBytes)
        {
            throw new DnsProtocolException("The response length is not one worth reading.");
        }

        var body = new byte[length];
        await ReadExactlyAsync(socket, body, deadline.Token);
        return body;
    }

    private static async Task ReadExactlyAsync(
        Socket socket, byte[] buffer, CancellationToken cancellation)
    {
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await socket.ReceiveAsync(
                buffer.AsMemory(filled), SocketFlags.None, cancellation);

            if (read == 0) throw new DnsProtocolException("The connection closed mid-response.");
            filled += read;
        }
    }

    /// <summary>
    /// Caches an answer for as long as the zone said to, and no longer.
    /// </summary>
    /// <remarks>
    /// A failure is cached briefly as well. Without that, a domain whose nameservers are down
    /// costs a timeout for every message it ever sent, which is felt as the application hanging.
    /// </remarks>
    private void Remember(string name, DnsAnswer answer)
    {
        // Not an eviction policy so much as a ceiling. The set of domains one person is signed
        // mail by is small, and a store that grows without bound is the bug worth avoiding.
        if (_cache.Count >= MaxCachedEntries) _cache.Clear();

        var lifetime = answer.Resolved && answer.Records.Count > 0
            ? TimeSpan.FromSeconds(Math.Clamp(answer.Ttl, 60, 86400))
            : TimeSpan.FromMinutes(5);

        _cache[name] = new Cached(answer, DateTimeOffset.UtcNow + lifetime);
    }

    /// <summary>
    /// The nameservers this machine already uses, out of <c>/etc/resolv.conf</c>.
    /// </summary>
    /// <remarks>
    /// Read rather than asked for through the platform, because .NET exposes no way to enumerate
    /// them on Linux. A machine running a local stub resolver — which most now do — publishes
    /// the stub here, so this follows whatever the desktop is already configured to trust.
    /// </remarks>
    public static IReadOnlyList<IPEndPoint> SystemNameservers(string path = "/etc/resolv.conf")
    {
        var servers = new List<IPEndPoint>();

        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.AsSpan().Trim();
                if (line.IsEmpty || line[0] is '#' or ';') continue;

                if (!line.StartsWith("nameserver", StringComparison.OrdinalIgnoreCase)) continue;

                var value = line["nameserver".Length..].Trim();
                if (value.IsEmpty) continue;

                // A scope suffix on a link-local address is not part of the address.
                var percent = value.IndexOf('%');
                if (percent >= 0) value = value[..percent];

                if (IPAddress.TryParse(value, out var address))
                {
                    servers.Add(new IPEndPoint(address, 53));
                }
            }
        }
        catch (Exception)
        {
            // No resolv.conf, or one we may not read. Nothing to resolve with is a state this
            // handles, so there is nothing to report here that CanResolve does not say.
        }

        return servers;
    }

    public void Dispose()
    {
        _disposed = true;
        _cache.Clear();
    }

    private readonly record struct Cached(DnsAnswer Answer, DateTimeOffset Until)
    {
        public bool Expired => DateTimeOffset.UtcNow >= Until;
    }
}
