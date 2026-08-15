using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using MailKit.Security;

namespace Mailbox.Protocols;

/// <summary>What the server said about itself in its greeting: implementation, extensions, mechanisms.</summary>
public sealed record SieveCapabilities
{
    /// <summary>The server's own name, from IMPLEMENTATION.</summary>
    public string Implementation { get; init; } = string.Empty;

    /// <summary>The Sieve extensions it will accept in a <c>require</c>, lower-cased.</summary>
    public IReadOnlySet<string> Extensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SASL mechanisms on offer, upper-cased.</summary>
    public IReadOnlySet<string> Mechanisms { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool StartTls { get; init; }

    /// <summary>The protocol version, "1.0" for RFC 5804.</summary>
    public string Version { get; init; } = string.Empty;
}

/// <summary>A script on the server, and whether it is the one that runs.</summary>
public sealed record SieveScriptInfo(string Name, bool Active);

/// <summary>The server said NO (or BYE): the response code and the words it sent.</summary>
public sealed class ManageSieveException(string message, string? code = null) : Exception(message)
{
    /// <summary>The parenthesised response code, "AUTH-TOO-WEAK", "QUOTA/MAXSCRIPTS", "NONEXISTENT" — or null.</summary>
    public string? Code { get; } = code;
}

/// <summary>
/// A ManageSieve client (RFC 5804): enough of the protocol to put a script on the server and
/// make it the active one, and to take it down again.
/// </summary>
/// <remarks>
/// MailKit has no ManageSieve, so this is written to the RFC over a socket: capabilities from
/// the greeting, STARTTLS, PLAIN or LOGIN authentication, LISTSCRIPTS, GETSCRIPT, PUTSCRIPT,
/// SETACTIVE, DELETESCRIPT, CHECKSCRIPT, LOGOUT. Strings on the wire are quoted or literals
/// (<c>{n+}</c> from the client, <c>{n}</c> from the server); responses end in a line beginning
/// OK, NO or BYE, with an optional response code in parentheses and an optional message. The
/// port is 4190 by convention and the host is the IMAP server's unless the account says
/// otherwise; STARTTLS is the norm, and refused-in-the-clear is the default when TLS is
/// wanted and not offered.
/// </remarks>
public sealed class ManageSieveClient : IAsyncDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private TcpClient? _tcp;
    private Stream? _stream;
    private byte[] _buffer = new byte[8192];
    private int _bufferStart;
    private int _bufferEnd;

    /// <summary>What the server advertised — after STARTTLS, what it advertised over TLS.</summary>
    public SieveCapabilities Capabilities { get; private set; } = new();

    public bool IsConnected => _stream is not null;

    /// <summary>Opens the connection, reads the greeting and, when asked, upgrades to TLS.</summary>
    public async Task ConnectAsync(ServerSettings server, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(server);

        _tcp = new TcpClient { NoDelay = true };
        using (cancellation.Register(() => { try { _tcp.Close(); } catch { /* closing */ } }))
        {
            await _tcp.ConnectAsync(server.Host, server.Port, cancellation).ConfigureAwait(false);
        }

        _stream = _tcp.GetStream();

        if (server.Security == SecureSocketOptions.SslOnConnect)
        {
            await UpgradeAsync(server.Host, cancellation).ConfigureAwait(false);
        }

        await ReadGreetingAsync(cancellation).ConfigureAwait(false);

        var wantTls = server.Security is SecureSocketOptions.StartTls or SecureSocketOptions.StartTlsWhenAvailable or SecureSocketOptions.Auto;
        if (wantTls && Capabilities.StartTls)
        {
            await SendLineAsync("STARTTLS", cancellation).ConfigureAwait(false);
            var response = await ReadResponseAsync(cancellation).ConfigureAwait(false);
            response.ThrowIfNotOk("STARTTLS");
            await UpgradeAsync(server.Host, cancellation).ConfigureAwait(false);

            // The capabilities are sent again once the connection is private, and only these count.
            await ReadGreetingAsync(cancellation).ConfigureAwait(false);
        }
        else if (server.Security is SecureSocketOptions.StartTls && !Capabilities.StartTls)
        {
            throw new ManageSieveException("The server does not offer STARTTLS, and the account asks for it.");
        }
    }

    /// <summary>Authenticates with SASL PLAIN, or LOGIN when PLAIN is not on offer.</summary>
    public async Task AuthenticateAsync(string userName, string password, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(password);

        if (Capabilities.Mechanisms.Contains("PLAIN") || Capabilities.Mechanisms.Count == 0)
        {
            var initial = Convert.ToBase64String(Utf8.GetBytes("\0" + userName + "\0" + password));
            await SendLineAsync($"AUTHENTICATE \"PLAIN\" {Quote(initial)}", cancellation).ConfigureAwait(false);
            var response = await FinishAuthenticationAsync([], cancellation).ConfigureAwait(false);
            response.ThrowIfNotOk("AUTHENTICATE");
            return;
        }

        if (Capabilities.Mechanisms.Contains("LOGIN"))
        {
            await SendLineAsync("AUTHENTICATE \"LOGIN\"", cancellation).ConfigureAwait(false);
            var answers = new Queue<string>([
                Convert.ToBase64String(Utf8.GetBytes(userName)),
                Convert.ToBase64String(Utf8.GetBytes(password)),
            ]);
            var response = await FinishAuthenticationAsync(answers, cancellation).ConfigureAwait(false);
            response.ThrowIfNotOk("AUTHENTICATE");
            return;
        }

        throw new ManageSieveException(
            $"The server offers no authentication this client speaks ({string.Join(", ", Capabilities.Mechanisms)}).");
    }

    /// <summary>The scripts on the server, the active one marked.</summary>
    public async Task<IReadOnlyList<SieveScriptInfo>> ListScriptsAsync(CancellationToken cancellation)
    {
        await SendLineAsync("LISTSCRIPTS", cancellation).ConfigureAwait(false);
        var response = await ReadResponseAsync(cancellation).ConfigureAwait(false);
        response.ThrowIfNotOk("LISTSCRIPTS");

        var scripts = new List<SieveScriptInfo>();
        foreach (var line in response.Lines)
        {
            var (name, rest) = ReadQuoted(line);
            if (name is null) continue;
            scripts.Add(new SieveScriptInfo(name, rest.Trim().Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)));
        }

        return scripts;
    }

    /// <summary>The text of a script.</summary>
    public async Task<string> GetScriptAsync(string name, CancellationToken cancellation)
    {
        await SendLineAsync($"GETSCRIPT {Quote(name)}", cancellation).ConfigureAwait(false);
        var response = await ReadResponseAsync(cancellation).ConfigureAwait(false);
        response.ThrowIfNotOk("GETSCRIPT");
        return response.Literal ?? string.Empty;
    }

    /// <summary>Stores a script under a name, replacing one of that name. The server checks it first and says NO if it will not run.</summary>
    public async Task PutScriptAsync(string name, string script, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(script);

        var bytes = Utf8.GetBytes(script);
        await SendAsync($"PUTSCRIPT {Quote(name)} {{{bytes.Length}+}}\r\n", cancellation).ConfigureAwait(false);
        await _stream!.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        await SendAsync("\r\n", cancellation).ConfigureAwait(false);
        var response = await ReadResponseAsync(cancellation).ConfigureAwait(false);
        response.ThrowIfNotOk("PUTSCRIPT");
    }

    /// <summary>Asks the server whether a script would run, without storing it. Throws with the server's words when it would not.</summary>
    public async Task CheckScriptAsync(string script, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(script);

        var bytes = Utf8.GetBytes(script);
        await SendAsync($"CHECKSCRIPT {{{bytes.Length}+}}\r\n", cancellation).ConfigureAwait(false);
        await _stream!.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        await SendAsync("\r\n", cancellation).ConfigureAwait(false);
        var response = await ReadResponseAsync(cancellation).ConfigureAwait(false);
        response.ThrowIfNotOk("CHECKSCRIPT");
    }

    /// <summary>Makes a script the one that runs; an empty name makes none run.</summary>
    public async Task SetActiveAsync(string name, CancellationToken cancellation)
    {
        await SendLineAsync($"SETACTIVE {Quote(name)}", cancellation).ConfigureAwait(false);
        var response = await ReadResponseAsync(cancellation).ConfigureAwait(false);
        response.ThrowIfNotOk("SETACTIVE");
    }

    /// <summary>Removes a script. The server refuses while it is the active one.</summary>
    public async Task DeleteScriptAsync(string name, CancellationToken cancellation)
    {
        await SendLineAsync($"DELETESCRIPT {Quote(name)}", cancellation).ConfigureAwait(false);
        var response = await ReadResponseAsync(cancellation).ConfigureAwait(false);
        response.ThrowIfNotOk("DELETESCRIPT");
    }

    /// <summary>Says goodbye. Errors here are nobody's concern.</summary>
    public async Task LogoutAsync(CancellationToken cancellation)
    {
        if (_stream is null) return;
        try
        {
            await SendLineAsync("LOGOUT", cancellation).ConfigureAwait(false);
            await ReadResponseAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ManageSieveException)
        {
            // The server may drop the line first; that is a goodbye too.
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
        return ValueTask.CompletedTask;
    }

    // ---- The wire --------------------------------------------------------------------------------

    private async Task UpgradeAsync(string host, CancellationToken cancellation)
    {
        var ssl = new SslStream(_stream!, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.None,
        }, cancellation).ConfigureAwait(false);
        _stream = ssl;

        // Anything buffered before the handshake belonged to the plain connection and is gone.
        _bufferStart = _bufferEnd = 0;
    }

    /// <summary>The greeting — or the re-greeting after STARTTLS: capability lines, then OK.</summary>
    private async Task ReadGreetingAsync(CancellationToken cancellation)
    {
        var response = await ReadResponseAsync(cancellation).ConfigureAwait(false);
        response.ThrowIfNotOk("greeting");

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mechanisms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var implementation = string.Empty;
        var version = string.Empty;
        var startTls = false;

        foreach (var line in response.Lines)
        {
            var (name, rest) = ReadQuoted(line);
            if (name is null) continue;
            var (value, _) = ReadQuoted(rest.TrimStart());

            switch (name.ToUpperInvariant())
            {
                case "IMPLEMENTATION": implementation = value ?? string.Empty; break;
                case "SIEVE": foreach (var e in (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)) extensions.Add(e.ToLowerInvariant()); break;
                case "SASL": foreach (var m in (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)) mechanisms.Add(m.ToUpperInvariant()); break;
                case "STARTTLS": startTls = true; break;
                case "VERSION": version = value ?? string.Empty; break;
            }
        }

        Capabilities = new SieveCapabilities
        {
            Implementation = implementation,
            Extensions = extensions,
            Mechanisms = mechanisms,
            StartTls = startTls,
            Version = version,
        };
    }

    /// <summary>
    /// After AUTHENTICATE: the server may send challenges (strings on their own line) before its
    /// verdict; each is answered from the queue, or with an empty string once it is empty.
    /// </summary>
    private async Task<Response> FinishAuthenticationAsync(Queue<string> answers, CancellationToken cancellation)
    {
        while (true)
        {
            var line = await ReadLineAsync(cancellation).ConfigureAwait(false);
            if (line is null) throw new ManageSieveException("The server closed the connection during authentication.");

            if (IsVerdict(line))
            {
                var response = await ReadVerdictAsync(line, [], cancellation).ConfigureAwait(false);
                return response;
            }

            // A challenge: quoted, or a literal we have to swallow.
            if (line.StartsWith('{'))
            {
                var length = LiteralLength(line);
                await ReadLiteralAsync(length, cancellation).ConfigureAwait(false);
            }

            var answer = answers.Count > 0 ? answers.Dequeue() : string.Empty;
            await SendLineAsync(Quote(answer), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Lines up to and including the OK / NO / BYE line, and any literal the response carried.</summary>
    private async Task<Response> ReadResponseAsync(CancellationToken cancellation)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await ReadLineAsync(cancellation).ConfigureAwait(false);
            if (line is null) throw new ManageSieveException("The server closed the connection.");

            if (IsVerdict(line))
            {
                return await ReadVerdictAsync(line, lines, cancellation).ConfigureAwait(false);
            }

            if (line.StartsWith('{'))
            {
                // A literal in the body — GETSCRIPT's script. Its text is the response's literal.
                var literal = await ReadLiteralAsync(LiteralLength(line), cancellation).ConfigureAwait(false);
                lines.Add(line);
                var verdictLine = await ReadLineAsync(cancellation).ConfigureAwait(false);
                if (verdictLine is null) throw new ManageSieveException("The server closed the connection.");
                var response = await ReadVerdictAsync(verdictLine, lines, cancellation).ConfigureAwait(false);
                return response with { Literal = literal };
            }

            lines.Add(line);
        }
    }

    private async Task<Response> ReadVerdictAsync(string line, List<string> lines, CancellationToken cancellation)
    {
        var status = line.Length >= 2 && line.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ? "OK"
            : line.StartsWith("NO", StringComparison.OrdinalIgnoreCase) ? "NO" : "BYE";
        var rest = line[status.Length..].TrimStart();

        string? code = null;
        if (rest.StartsWith('('))
        {
            var close = rest.IndexOf(')');
            if (close > 0)
            {
                code = rest[1..close].Trim();
                rest = rest[(close + 1)..].TrimStart();
            }
        }

        string? message = null;
        if (rest.StartsWith('{'))
        {
            message = await ReadLiteralAsync(LiteralLength(rest), cancellation).ConfigureAwait(false);
        }
        else if (rest.StartsWith('"'))
        {
            (message, _) = ReadQuoted(rest);
        }
        else if (rest.Length > 0)
        {
            message = rest;
        }

        return new Response(status, code, message, lines, null);
    }

    private static bool IsVerdict(string line)
        => line.StartsWith("OK", StringComparison.OrdinalIgnoreCase) && (line.Length == 2 || line[2] == ' ')
           || line.StartsWith("NO", StringComparison.OrdinalIgnoreCase) && (line.Length == 2 || line[2] == ' ')
           || line.StartsWith("BYE", StringComparison.OrdinalIgnoreCase) && (line.Length == 3 || line[3] == ' ');

    private static int LiteralLength(string line)
    {
        var close = line.IndexOf('}');
        var inner = close > 1 ? line[1..close].TrimEnd('+') : string.Empty;
        return int.TryParse(inner, out var length) && length >= 0
            ? length
            : throw new ManageSieveException($"The server sent a literal this client cannot read: {line}");
    }

    private async Task<string> ReadLiteralAsync(int length, CancellationToken cancellation)
    {
        var bytes = new byte[length];
        var got = 0;
        while (got < length)
        {
            if (_bufferEnd > _bufferStart)
            {
                var take = Math.Min(length - got, _bufferEnd - _bufferStart);
                Array.Copy(_buffer, _bufferStart, bytes, got, take);
                _bufferStart += take;
                got += take;
                continue;
            }

            if (!await FillAsync(cancellation).ConfigureAwait(false))
            {
                throw new ManageSieveException("The server closed the connection inside a literal.");
            }
        }

        // The line break the literal is followed by belongs to the protocol, not the text;
        // ReadLineAsync will find it, empty, and skip it.
        return Utf8.GetString(bytes);
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellation)
    {
        while (true)
        {
            for (var i = _bufferStart; i < _bufferEnd; i++)
            {
                if (_buffer[i] != (byte)'\n') continue;

                var end = i > _bufferStart && _buffer[i - 1] == (byte)'\r' ? i - 1 : i;
                var line = Utf8.GetString(_buffer, _bufferStart, end - _bufferStart);
                _bufferStart = i + 1;

                // The empty line after a literal is not a line of the response.
                if (line.Length == 0) return await ReadLineAsync(cancellation).ConfigureAwait(false);
                return line;
            }

            if (!await FillAsync(cancellation).ConfigureAwait(false)) return null;
        }
    }

    private async Task<bool> FillAsync(CancellationToken cancellation)
    {
        if (_bufferStart > 0)
        {
            Array.Copy(_buffer, _bufferStart, _buffer, 0, _bufferEnd - _bufferStart);
            _bufferEnd -= _bufferStart;
            _bufferStart = 0;
        }

        if (_bufferEnd == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);

        var read = await _stream!.ReadAsync(_buffer.AsMemory(_bufferEnd), cancellation).ConfigureAwait(false);
        if (read <= 0) return false;
        _bufferEnd += read;
        return true;
    }

    private Task SendLineAsync(string line, CancellationToken cancellation) => SendAsync(line + "\r\n", cancellation);

    private async Task SendAsync(string text, CancellationToken cancellation)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        var bytes = Utf8.GetBytes(text);
        await _stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        await _stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>A quoted string as the protocol writes one.</summary>
    internal static string Quote(string text)
        => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>The quoted string at the start of a line, and what follows it — or (null, line) when there is none.</summary>
    internal static (string? Value, string Remainder) ReadQuoted(string line)
    {
        if (line.Length == 0 || line[0] != '"') return (null, line);

        var builder = new StringBuilder();
        for (var i = 1; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                builder.Append(line[++i]);
                continue;
            }

            if (c == '"') return (builder.ToString(), line[(i + 1)..]);
            builder.Append(c);
        }

        return (builder.ToString(), string.Empty);
    }

    private sealed record Response(string Status, string? Code, string? Message, IReadOnlyList<string> Lines, string? Literal)
    {
        public void ThrowIfNotOk(string command)
        {
            if (Status == "OK") return;
            var words = Message is { Length: > 0 } ? Message : $"{command} failed";
            throw new ManageSieveException(Code is { Length: > 0 } ? $"{words} ({Code})" : words, Code);
        }
    }
}
