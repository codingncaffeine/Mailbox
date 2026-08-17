using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using Mailbox.Protocols.OAuth;
using MimeKit;

namespace Mailbox.Protocols;

/// <summary>
/// The part of POP3 this application uses.
/// </summary>
/// <remarks>
/// A seam of our own rather than MailKit's <c>IPop3Client</c>, which carries some sixty members
/// covering the whole protocol. Depending on all of it would mean a test double had to
/// implement all of it, and the double would then be mostly members nobody calls — noise that
/// hides which five operations a poll actually performs.
/// </remarks>
public interface IPop3Session : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Messages currently on the server.</summary>
    int Count { get; }

    Task ConnectAsync(ServerSettings server, CancellationToken cancellation);

    Task AuthenticateAsync(ServerSettings server, CancellationToken cancellation);

    /// <summary>Every message's UIDL, in index order. One call for the whole mailbox.</summary>
    Task<IList<string>> GetUidsAsync(CancellationToken cancellation);

    Task<MimeMessage> GetMessageAsync(int index, CancellationToken cancellation);

    Task DeleteAsync(IList<int> indexes, CancellationToken cancellation);

    Task DisconnectAsync(CancellationToken cancellation);
}

/// <summary>The part of SMTP this application uses.</summary>
public interface ISmtpSession : IDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// The authentication mechanisms the server advertised on connecting, before anything has
    /// been sent to it.
    /// </summary>
    /// <remarks>
    /// Empty means the server offers none — which is a real configuration, not a fault, and is
    /// the one an Outlook.com account with SMTP AUTH switched off is in.
    /// </remarks>
    IReadOnlySet<string> AuthenticationMechanisms { get; }

    Task ConnectAsync(ServerSettings server, CancellationToken cancellation);

    Task AuthenticateAsync(ServerSettings server, CancellationToken cancellation);

    /// <summary>
    /// Submits a message, and hands back what the server said as it accepted it.
    /// </summary>
    /// <remarks>
    /// The response is returned rather than discarded because it is the only evidence a sender
    /// ever gets: a submission server's <c>250</c> usually carries a queue identifier, and that
    /// identifier is the thing to quote when a message was accepted here and never arrived
    /// anywhere. Thrown away, the whole question becomes unanswerable from this end.
    /// </remarks>
    Task<string> SendAsync(MimeMessage message, CancellationToken cancellation);

    Task DisconnectAsync(CancellationToken cancellation);
}

/// <summary>MailKit behind the POP3 seam.</summary>
public sealed class MailKitPop3Session : IPop3Session
{
    private readonly Pop3Client _client =
        ProtocolDiagnostics.For("pop3") is { } log ? new Pop3Client(log) : new Pop3Client();

    public bool IsConnected => _client.IsConnected;

    public int Count => _client.Count;

    public Task ConnectAsync(ServerSettings server, CancellationToken cancellation)
    {
        SaslAuthentication.UseTrust(_client, server);
        return _client.ConnectAsync(server.Host, server.Port, server.Security, cancellation);
    }

    public Task AuthenticateAsync(ServerSettings server, CancellationToken cancellation)
        => SaslAuthentication.AuthenticateAsync(_client, server, cancellation);

    public Task<IList<string>> GetUidsAsync(CancellationToken cancellation)
        => _client.GetMessageUidsAsync(cancellation);

    public Task<MimeMessage> GetMessageAsync(int index, CancellationToken cancellation)
        => _client.GetMessageAsync(index, cancellation);

    public Task DeleteAsync(IList<int> indexes, CancellationToken cancellation)
        => _client.DeleteMessagesAsync(indexes, cancellation);

    public Task DisconnectAsync(CancellationToken cancellation)
        => _client.DisconnectAsync(true, cancellation);

    public void Dispose() => _client.Dispose();
}

/// <summary>MailKit behind the SMTP seam.</summary>
public sealed class MailKitSmtpSession : ISmtpSession
{
    private readonly SmtpClient _client =
        ProtocolDiagnostics.For("smtp") is { } log ? new SmtpClient(log) : new SmtpClient();

    public bool IsConnected => _client.IsConnected;

    public IReadOnlySet<string> AuthenticationMechanisms => _client.AuthenticationMechanisms;

    public Task ConnectAsync(ServerSettings server, CancellationToken cancellation)
    {
        SaslAuthentication.UseTrust(_client, server);
        return _client.ConnectAsync(server.Host, server.Port, server.Security, cancellation);
    }

    public Task AuthenticateAsync(ServerSettings server, CancellationToken cancellation)
        => SaslAuthentication.AuthenticateAsync(_client, server, cancellation);

    public Task<string> SendAsync(MimeMessage message, CancellationToken cancellation)
        => _client.SendAsync(message, cancellation);

    public Task DisconnectAsync(CancellationToken cancellation)
        => _client.DisconnectAsync(true, cancellation);

    public void Dispose() => _client.Dispose();
}
