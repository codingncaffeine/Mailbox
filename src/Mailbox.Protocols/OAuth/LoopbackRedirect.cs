using System.Net;
using System.Net.Sockets;
using System.Text;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Protocols.OAuth;

/// <summary>
/// The other end of the browser round trip: a web server that exists for one request.
/// </summary>
/// <remarks>
/// RFC 8252 §7.3. A native application cannot receive a redirect any other way — a custom URI
/// scheme is claimed by whichever program registered it last, and a server-hosted redirect means
/// operating infrastructure, which the no-hosted-services stance rules out. So the application listens on the loopback
/// interface for as long as the sign-in takes and stops.
/// <para>
/// Three things this is careful about. It binds the loopback address itself and never a wildcard,
/// so nothing off this machine can reach it. It answers exactly one <em>matching</em> request and
/// then closes, so a code cannot be replayed at it — a browser fetching <c>/favicon.ico</c> is
/// answered 404 and the wait goes on, which is the difference between working and appearing to
/// hang. And the page it returns is a constant: the query it was called with is never echoed into
/// it, because the parameters on that URL come from outside and one of them is about to be
/// exchanged for a credential.
/// </para>
/// </remarks>
public sealed class LoopbackRedirect : IDisposable
{
    private const string Path = "/mailbox-oauth/";

    private readonly HttpListener _listener = new();
    private bool _stopped;

    private LoopbackRedirect(int port, string advertisedHost)
    {
        Port = port;
        RedirectUri = new Uri($"http://{advertisedHost}:{port}{Path}");

        // The literal address is always listened for. Where the redirect URI says "localhost" —
        // which is what a Microsoft registration matches against — the browser resolves the name
        // itself, and on a machine whose hosts file puts ::1 first that is the address it will
        // knock at, so that spelling is registered as well and answers both.
        //
        // The name rather than "[::1]": HttpListener's managed implementation refuses a bracketed
        // IPv6 literal in a prefix outright ("Invalid port in prefix"), so a redirect URI naming
        // localhost is the one case where the bind goes through a name.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}{Path}");
        if (advertisedHost is "localhost") _listener.Prefixes.Add($"http://localhost:{port}{Path}");
    }

    /// <summary>The port the sign-in is listening on.</summary>
    public int Port { get; }

    /// <summary>What the authorization server is asked to send the browser back to.</summary>
    public Uri RedirectUri { get; }

    /// <summary>
    /// Opens a listener on a free port.
    /// </summary>
    /// <param name="advertisedHost">
    /// How the redirect URI names this machine. <c>127.0.0.1</c> is the specification's own
    /// recommendation and the default; a provider whose registration will only match
    /// <c>localhost</c> gets that instead, and is listened for on both loopback addresses.
    /// </param>
    public static LoopbackRedirect Open(string advertisedHost = "127.0.0.1")
    {
        // Asking the operating system for port 0 and then handing the number to HttpListener is
        // the only way to get an unused one: HttpListener has no way to be told "any port", and
        // a fixed port would collide with whatever else is on the machine and, worse, would let
        // another program sit on it first.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var redirect = new LoopbackRedirect(port, advertisedHost);
        redirect._listener.Start();
        return redirect;
    }

    /// <summary>
    /// Waits for the browser to come back, and hands over what it was called with.
    /// </summary>
    /// <remarks>
    /// The wait ends when the redirect arrives, when the caller cancels, or when the timeout runs
    /// out — a sign-in the user abandoned by closing the tab has no other ending, and leaving a
    /// listener open for the life of the process would be one.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);

        // HttpListener has no cancellable accept, so the token stops the listener instead, which
        // makes the pending GetContextAsync throw and unwinds the wait.
        using var registration = deadline.Token.Register(Stop);

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (_stopped)
            {
                Log.Info($"The sign-in listener closed before the browser came back: {ex.GetType().Name}.");
                cancellation.ThrowIfCancellationRequested();
                throw new TimeoutException("The sign-in was not completed.");
            }

            var query = Parse(context.Request.Url);

            // A request with nothing on it is not the redirect. Browsers ask for icons, and a
            // probe from anything else on the machine would otherwise end the wait and leave the
            // real redirect with nowhere to land.
            if (query.Count == 0)
            {
                await RespondAsync(context, HttpStatusCode.NotFound, "Not found.").ConfigureAwait(false);
                continue;
            }

            await RespondAsync(context, HttpStatusCode.OK, Page).ConfigureAwait(false);
            Stop();
            return query;
        }
    }

    private static Dictionary<string, string> Parse(Uri? url)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (url is null) return found;

        var query = url.Query.TrimStart('?');
        if (query.Length == 0) return found;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;

            // Query values are form-encoded: a space travels as '+', and UnescapeDataString
            // alone leaves every one in place — a provider's error_description read
            // "The+sign-in+was+refused." on screen. A literal plus arrives as %2B and is safe.
            var name = Uri.UnescapeDataString(pair[..equals]);
            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));

            // First wins. A duplicated parameter is how a request smuggles a second value past
            // whichever end of a pipeline reads the other one.
            found.TryAdd(name, value);
        }

        return found;
    }

    private static async Task RespondAsync(HttpListenerContext context, HttpStatusCode status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;

        // Nothing here should ever be framed, sniffed or cached: it is a page containing an
        // authorization code in its own URL, sitting on a port anything local can reach.
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        try
        {
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.OutputStream.Close();
        }
        catch (Exception ex)
        {
            // The browser having gone away is not a failure of the sign-in: the code is already
            // in hand by this point.
            Log.Info($"The sign-in page could not be written back to the browser: {ex.Message}");
        }
    }

    private void Stop()
    {
        if (_stopped) return;
        _stopped = true;

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception ex)
        {
            Log.Info($"Closing the sign-in listener: {ex.Message}");
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// What the browser shows when the sign-in is done. A constant, and no script.
    /// </summary>
    private const string Page =
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Signed in</title>
          <style>
            body { font-family: system-ui, sans-serif; margin: 4rem auto; max-width: 26rem;
                   color: #201f1e; }
            h1 { font-size: 1.3rem; font-weight: 600; }
            p { color: #605e5c; line-height: 1.5; }
          </style>
        </head>
        <body>
          <h1>Signed in</h1>
          <p>You can close this tab and go back to Mailbox.</p>
        </body>
        </html>
        """;
}
