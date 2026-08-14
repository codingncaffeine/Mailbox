using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
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

    Task ConnectAsync(ServerSettings server, CancellationToken cancellation);

    Task AuthenticateAsync(ServerSettings server, CancellationToken cancellation);

    Task SendAsync(MimeMessage message, CancellationToken cancellation);

    Task DisconnectAsync(CancellationToken cancellation);
}

/// <summary>MailKit behind the POP3 seam.</summary>
public sealed class MailKitPop3Session : IPop3Session
{
    private readonly Pop3Client _client = new();

    public bool IsConnected => _client.IsConnected;

    public int Count => _client.Count;

    public Task ConnectAsync(ServerSettings server, CancellationToken cancellation)
        => _client.ConnectAsync(server.Host, server.Port, server.Security, cancellation);

    public Task AuthenticateAsync(ServerSettings server, CancellationToken cancellation)
        => _client.AuthenticateAsync(server.UserName, server.Password, cancellation);

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
    private readonly SmtpClient _client = new();

    public bool IsConnected => _client.IsConnected;

    public Task ConnectAsync(ServerSettings server, CancellationToken cancellation)
        => _client.ConnectAsync(server.Host, server.Port, server.Security, cancellation);

    public Task AuthenticateAsync(ServerSettings server, CancellationToken cancellation)
        => _client.AuthenticateAsync(server.UserName, server.Password, cancellation);

    public Task SendAsync(MimeMessage message, CancellationToken cancellation)
        => _client.SendAsync(message, cancellation);

    public Task DisconnectAsync(CancellationToken cancellation)
        => _client.DisconnectAsync(true, cancellation);

    public void Dispose() => _client.Dispose();
}
