using Mailbox.Protocols;

namespace Mailbox.Tests;

public class ServerProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServerSettings Server => new("smtp.example.com", 587);

    private static ServerProbe Probe(FakeSmtp server) => new(() => server);

    [Fact]
    public async Task AServerThatOffersALoginHasNothingToSay()
    {
        var result = await Probe(new FakeSmtp()).CheckSendingAsync(Server, Ct);

        Assert.True(result.IsClear);
        Assert.True(result.Reached);
        Assert.Equal(string.Empty, result.Explanation);
    }

    /// <summary>
    /// The failure this exists for. A mailbox with SMTP AUTH switched off connects fine and
    /// offers nothing to authenticate with, and a client that finds out at the first send
    /// reports it as a bad password.
    /// </summary>
    [Fact]
    public async Task AServerOfferingNoLoginIsExplainedRatherThanCalledAFailure()
    {
        var result = await Probe(new FakeSmtp { Advertises = [] }).CheckSendingAsync(Server, Ct);

        Assert.True(result.Reached);
        Assert.False(result.CanAuthenticate);
        Assert.False(result.IsClear);

        Assert.Contains("no way to sign in", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("Receiving will work", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatCannotBeReachedIsADifferentSentence()
    {
        var server = new FakeSmtp { FailOnConnect = new IOException("Network is unreachable.") };

        var result = await Probe(server).CheckSendingAsync(Server, Ct);

        Assert.False(result.Reached);
        Assert.False(result.IsClear);
        Assert.Contains("Could not reach smtp.example.com", result.Explanation, StringComparison.Ordinal);
        Assert.False(result.NothingListening);
    }

    /// <summary>
    /// A port on this machine with nothing behind it — Proton Mail Bridge not running — through
    /// the real MailKit clients rather than a fake, because what is being checked is the shape
    /// of the exception MailKit lets out of a refused connect.
    /// </summary>
    [Fact]
    public async Task ARefusedConnectionSaysNothingIsListening()
    {
        var port = ClosedLoopbackPort();
        var server = new ServerSettings("127.0.0.1", port, MailKit.Security.SecureSocketOptions.Auto);

        var receiving = await new ServerProbe().CheckReceivingAsync(server, MailProtocolKind.Imap, Ct);
        Assert.False(receiving.Reached);
        Assert.True(receiving.NothingListening);

        var sending = await new ServerProbe().CheckSendingAsync(server, Ct);
        Assert.False(sending.Reached);
        Assert.True(sending.NothingListening);
        Assert.Contains(ServerProbe.NothingListeningSentence, sending.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The positive control for the test above: the same probe against a port that IS listening
    /// reaches it, so "nothing listening" is a reading of the port and not of the probe.
    /// </summary>
    [Fact]
    public async Task AListeningPortIsNotCalledRefused()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var accepting = AnswerOnceAsync(listener, "* OK [CAPABILITY IMAP4rev1] ready\r\n");
            var server = new ServerSettings("127.0.0.1", port, MailKit.Security.SecureSocketOptions.None);

            var receiving = await new ServerProbe().CheckReceivingAsync(server, MailProtocolKind.Imap, Ct);

            Assert.True(receiving.Reached, receiving.Explanation);
            Assert.False(receiving.NothingListening);
            await accepting;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void OnlyARefusalIsCalledNothingListening()
    {
        Assert.True(ServerProbe.NothingListening(
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
        Assert.False(ServerProbe.NothingListening(
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));
        Assert.False(ServerProbe.NothingListening(new System.Net.Sockets.SocketException()));
        Assert.False(ServerProbe.NothingListening(new IOException("Connection refused")));
    }

    /// <summary>A loopback port that was free a moment ago and has nothing listening on it now.</summary>
    private static int ClosedLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Takes one connection, says a greeting, and holds it until the client goes.</summary>
    private static async Task AnswerOnceAsync(System.Net.Sockets.TcpListener listener, string greeting)
    {
        using var client = await listener.AcceptTcpClientAsync(Ct);
        await using var stream = client.GetStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(greeting), Ct);

        var buffer = new byte[1024];
        try
        {
            while (await stream.ReadAsync(buffer, Ct) > 0)
            {
                // A client that says LOGOUT on the way out gets its BYE and OK; anything else is
                // read and dropped until it closes.
                var said = System.Text.Encoding.ASCII.GetString(buffer);
                if (said.Contains("LOGOUT", StringComparison.Ordinal))
                {
                    var tag = said.Split(' ')[0];
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                        $"* BYE\r\n{tag} OK LOGOUT completed\r\n"), Ct);
                    break;
                }
            }
        }
        catch (IOException)
        {
            // The client closed first, which is the ordinary way for this to end.
        }
    }

    /// <summary>Nothing is offered to the server: what is read is the greeting.</summary>
    [Fact]
    public async Task NoCredentialIsSentAndNothingIsDelivered()
    {
        var server = new FakeSmtp();

        await Probe(server).CheckSendingAsync(Server, Ct);

        Assert.Equal(0, server.Authentications);
        Assert.Empty(server.Sent);
        Assert.False(server.IsConnected);
    }
}
