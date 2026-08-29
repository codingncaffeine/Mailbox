using System.Net.Sockets;
using System.Text;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Core;

/// <summary>
/// One running Mailbox per session, and a way to hand a new launch's command line to it.
/// </summary>
/// <remarks>
/// The reference — and §10 — require that a <c>mailto:</c> click reach the mail client the reader
/// already has open rather than start a second copy. A Unix domain socket under the session's
/// runtime directory is the primary's address: a second launch connects, hands over its command
/// line and exits, and the primary opens the compose window. Keyed to the session
/// (<c>WAYLAND_DISPLAY</c> / <c>DISPLAY</c>), so two logins on one machine are two instances.
/// <para>
/// A stale socket left by a crash is detected by the connect failing and removed before the new
/// primary binds, so a hard exit does not lock the reader out of their own mail client. The
/// handoff is delivered on a background thread; the caller marshals it to the UI thread, which
/// keeps this free of any windowing dependency and testable on its own.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly string _path;
    private Socket? _listener;
    private CancellationTokenSource? _stop;

    /// <summary>Uses the session's socket path. A test passes its own so runs do not collide.</summary>
    public SingleInstance(string? path = null) => _path = path ?? SocketPath();

    /// <summary>
    /// Hands the command line to an already-running instance, if there is one.
    /// </summary>
    /// <returns>
    /// True if a primary was found and given the args — the caller should exit. False if this is
    /// the primary, in which case any stale socket has been cleared and <see cref="Listen"/>
    /// should be called once the shell is up.
    /// </returns>
    public bool TryHandOff(IReadOnlyList<string> args)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(_path));

            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args) + "\0");
            socket.Send(payload);
            Log.Info("Another Mailbox is running; handed it the command line.");
            return true;
        }
        catch (SocketException)
        {
            // No one listening. A file left by a crash would make the bind fail, so remove it now.
            TryDeleteSocketFile();
            return false;
        }
    }

    /// <summary>
    /// Becomes the primary: listens for a later launch's command line and raises it. The callback
    /// runs on a background thread — the caller marshals to the UI thread.
    /// </summary>
    public void Listen(Action<IReadOnlyList<string>> onCommandLine)
    {
        ArgumentNullException.ThrowIfNull(onCommandLine);

        try
        {
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_path));
            _listener.Listen(4);
        }
        catch (Exception ex)
        {
            // Losing the race to bind, or a directory that will not allow it: the application
            // still runs, it just will not receive a handoff. Not worth failing the launch over.
            Log.Warn($"Single-instance socket could not be opened ({ex.Message}); handoff is off.");
            return;
        }

        _stop = new CancellationTokenSource();
        _ = AcceptAsync(onCommandLine, _stop.Token);
        Log.Info($"Listening for a second launch on {_path}.");
    }

    private async Task AcceptAsync(Action<IReadOnlyList<string>> onCommandLine, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested && _listener is { } listener)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("A single-instance connection could not be accepted.", ex);
                continue;
            }

            using (connection)
            {
                try
                {
                    var text = await ReadAllAsync(connection, cancellation);
                    var args = text.TrimEnd('\0').Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (args.Length > 0) onCommandLine(args);
                }
                catch (Exception ex)
                {
                    Log.Warn("A handed-over command line could not be read.", ex);
                }
            }
        }
    }

    private static async Task<string> ReadAllAsync(Socket connection, CancellationToken cancellation)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();

        while (true)
        {
            var read = await connection.ReceiveAsync(buffer, cancellation);
            if (read == 0) break;

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (builder.ToString().Contains('\0')) break;
        }

        return builder.ToString();
    }

    /// <summary>The session's socket path — one per display, so two sessions do not collide.</summary>
    /// <remarks>
    /// In a <c>mailbox/</c> directory of its own rather than loose in the runtime directory,
    /// because the hardened launcher mounts the runtime directory read-only and carves out
    /// exactly this subdirectory — a socket bound at the top level cannot be created there,
    /// and the second instance's handoff would silently die.
    /// </remarks>
    private static string SocketPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtime)) runtime = Path.GetTempPath();

        var directory = Path.Combine(runtime, "mailbox");
        try
        {
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // A read-only runtime directory (an over-tight sandbox, an odd session). The bind
            // below will fail on the missing directory and say handoff is off — the app runs.
        }

        var session = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")
                      ?? Environment.GetEnvironmentVariable("DISPLAY")
                      ?? "session";

        var safe = new string([.. session.Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
        return Path.Combine(directory, $"mailbox-{safe}.sock");
    }

    private void TryDeleteSocketFile()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove a stale single-instance socket at {_path}.", ex);
        }
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _listener?.Dispose();
        _listener = null;
        TryDeleteSocketFile();
    }
}
