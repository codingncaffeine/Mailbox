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
