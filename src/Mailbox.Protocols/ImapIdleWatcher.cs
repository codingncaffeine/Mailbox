using Mailbox.Core.Diagnostics;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>
/// Keeps one IMAP account's Inbox under IDLE, so the server says when mail arrives instead of
/// being asked on a timer.
/// </summary>
/// <remarks>
/// IDLE is a held connection that the server breaks to announce a change; the protocol requires
/// it be renewed about every twenty-nine minutes, before a middlebox times the connection out.
/// So this loops: open the Inbox, idle until the server speaks or the renewal is due, and on a
/// change raise <see cref="ChangeDetected"/> for the app to run a sync on — the watcher itself
/// never touches the store, because a sync is the same work a manual send/receive does and
/// there should be one path through it.
/// <para>
/// A dropped connection is ordinary — a laptop lid, a network blip — so it is met with a
/// backoff and a reconnect rather than an error, and the watcher goes quiet only when it is
/// asked to stop or the account cannot IDLE at all, in which case the poll timer is the fallback.
/// </para>
/// </remarks>
public sealed class ImapIdleWatcher(AccountConnection account, Func<IImapSession>? sessionFactory = null)
    : IDisposable
{
    private readonly Func<IImapSession> _sessionFactory = sessionFactory ?? (() => new MailKitImapSession());
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>How long IDLE is held before it is renewed. Under the 30-minute protocol limit.</summary>
    public TimeSpan RenewAfter { get; init; } = TimeSpan.FromMinutes(29);

    /// <summary>The wait after a failure before reconnecting, doubling to a ceiling.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Raised, off the UI thread, when the server reports the Inbox changed. The subscriber runs
    /// a sync; this carries the address so a shell watching several accounts knows which.
    /// </summary>
    public event EventHandler<string>? ChangeDetected;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Starts watching. A second call is ignored.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        var backoff = InitialBackoff;

        while (!cancellation.IsCancellationRequested)
        {
            IImapSession? session = null;
            try
            {
                session = _sessionFactory();
                await session.ConnectAsync(account.Incoming, cancellation);
                await session.AuthenticateAsync(account.Incoming, cancellation);

                if (!session.Features.HasFlag(ImapFeatures.Idle))
                {
                    Log.Info($"{account.Address} does not support IMAP IDLE; the poll timer covers it.");
                    return;
                }

                await session.OpenAsync("INBOX", cancellation);
                backoff = InitialBackoff;
                Log.Info($"Watching {account.Address} for new mail over IDLE.");

                await WatchAsync(session, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"IDLE on {account.Address} dropped; reconnecting in {backoff.TotalSeconds:0}s.", ex);
                await SafeDelay(backoff, cancellation);
                backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
            }
            finally
            {
                if (session is not null)
                {
                    try { if (session.IsConnected) await session.DisconnectAsync(CancellationToken.None); }
                    catch (Exception) { /* Already down; nothing to do. */ }
                    session.Dispose();
                }
            }
        }
    }

    private async Task WatchAsync(IImapSession session, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var changed = false;
            void OnChanged(object? _, EventArgs __) => changed = true;
            session.FolderChanged += OnChanged;

            using var renew = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            renew.CancelAfter(RenewAfter);

            try
            {
                await session.IdleAsync(renew.Token, cancellation);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // The renewal timer, not a stop. Fall through to loop and re-IDLE.
            }
            finally
            {
                session.FolderChanged -= OnChanged;
            }

            if (changed) ChangeDetected?.Invoke(this, account.Address);
        }
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken cancellation)
    {
        try { await Task.Delay(delay, cancellation); }
        catch (OperationCanceledException) { /* Stopping. */ }
    }

    /// <summary>Stops watching and lets the connection go. Waits briefly for the loop to unwind.</summary>
    public void Stop()
    {
        _stop?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* A watcher mid-reconnect; the cancellation will land. */ }
        _loop = null;
        _stop?.Dispose();
        _stop = null;
    }

    public void Dispose() => Stop();
}
