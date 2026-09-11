using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;

namespace Mailbox.App.Views;

/// <summary>
/// The Send/Receive Progress dialog, put up so that it cannot take the keyboard.
/// </summary>
/// <remarks>
/// A run nobody pressed for — the half-hourly schedule, a server's IDLE saying mail has come —
/// opened the dialog and took the keyboard off whatever the reader was doing, a full-screen game
/// included, and when it closed the keyboard went to the shell rather than back where it had been.
/// Measured in KWin 6.7 with the reader's settings: the game was active, the dialog was mapped,
/// the dialog was activated, and the whole application was stacked above the game.
/// <para>
/// The window cannot be told not to. With focus-stealing prevention at KWin's default, a new
/// Wayland window that accepts input is always activated; xdg-shell has no request to decline it,
/// and Avalonia's Wayland backend drops <c>ShowActivated</c> on the floor anyway. A popup is never
/// activated, but KWin stacks popups above the full-screen layer, so it would have been drawn over
/// the game instead. An X11 window is different: mapped with a user time of zero it is, in KWin's
/// words, "explicitly asked not to get focus", and it gets none — measured the same way, the game
/// kept the keyboard, and kept its place above the toaster.
/// </para>
/// <para>
/// So the dialog is shown by a second <c>mailbox</c> process that is an X11 client
/// (<see cref="ProgressToasterCompanion"/>), which a Wayland process cannot be — the toolkit runs
/// one windowing backend per process. It is the same class drawn with the same theme, fed the same
/// reports; this end starts it, feeds it, and hears back Cancel All, the "don't show" box and the
/// window closing. When the shell itself is on X11 none of this is needed and the dialog is shown
/// in-process, unactivated, by the caller.
/// </para>
/// </remarks>
internal sealed class ProgressToaster
{
    /// <summary>Why the toaster could not be started, once it could not; the session stops trying.</summary>
    private static string? _unavailable;

    private readonly Process _process;
    private readonly SendReceiveTasks _tasks;

    /// <summary>
    /// What is waiting to be written to the toaster, drained by a writer of its own.
    /// </summary>
    /// <remarks>
    /// Never written from the UI thread directly. A pipe holds so much and then blocks the writer
    /// until the other end reads, and the other end is a process that may still be starting — or
    /// stuck on an X server that is not answering. A download reports once a message; enough of
    /// them before the toaster reads its first line would have stopped the shell's UI thread on a
    /// pipe write, which is the one thing a progress report must never cost.
    /// </remarks>
    private readonly System.Threading.Channels.Channel<string> _outbox =
        System.Threading.Channels.Channel.CreateUnbounded<string>(new() { SingleReader = true });

    private bool _ready;
    private bool _closing;
    private bool _finished;
    private bool _exited;
    private bool _drained;
    private int _streamsOpen = 2;

    /// <summary>Whether the log entry being passed on is a warning; its continuation lines follow it.</summary>
    private bool _entryWarns;

    private ProgressToaster(Process process, SendReceiveTasks tasks)
    {
        _process = process;
        _tasks = tasks;
    }

    /// <summary>Cancel All, pressed on the toaster.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>The "don't show this dialog box" box, ticked or cleared on the toaster.</summary>
    public event EventHandler<bool>? HideChanged;

    /// <summary>The toaster has gone: closed by the reader, closed by this end, or failed.</summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Starts a toaster for a run. Null when there is none to be had — no X display, or one that
    /// has already failed to start this session — and the run keeps to the status bar.
    /// </summary>
    /// <param name="tasks">
    /// The run's own table: sent as it stands, so a toaster that starts late still shows what the
    /// run has done, and read again once the toaster is ready to decide whether it is still worth
    /// showing.
    /// </param>
    /// <param name="anchor">A point on the screen the application is on.</param>
    public static ProgressToaster? Start(SendReceiveTasks tasks, PixelPoint anchor)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        if (_unavailable is not null) return null;

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Unavailable("there is no X display to put it on");
            return null;
        }

        if (Command() is not { } start)
        {
            Unavailable("this process cannot tell where its own program is");
            return null;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process was started");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Unavailable($"it could not be started ({ex.Message})");
            return null;
        }

        var toaster = new ProgressToaster(process, tasks);
        toaster.Listen();
        toaster.Send(ToasterMessage.Begin.For(tasks, anchor.X, anchor.Y));
        return toaster;
    }

    /// <summary>A report from the service, passed on as the shell's own table received it.</summary>
    public void Report(PollProgress progress) => Send(new ToasterMessage.Progress(progress));

    /// <summary>How the run ended.</summary>
    public void Finish(SendReceiveResult result) => Send(new ToasterMessage.Finish(result.Accounts));

    /// <summary>
    /// Takes the toaster down. The process is asked, then told: one that has not gone a few
    /// seconds later is killed, because a toaster that will not leave is exactly the window this
    /// exists to prevent.
    /// </summary>
    public void Close()
    {
        if (_closing) return;
        _closing = true;

        // Queued behind whatever is still waiting, and the queue then shut, so the writer sends
        // the last word and closes the pipe after it: the toaster reads "close", then the end.
        Send(new ToasterMessage.Close());
        _outbox.Writer.TryComplete();

        DispatcherTimer.RunOnce(
            () =>
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        Log.Warn("Progress toaster: it did not leave when asked, so it was stopped.");
                        _process.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited between the question and the answer.
                }
            },
            TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// This program again, with the switch. The apphost when there is one; the runtime and the
    /// assembly when the application was started through <c>dotnet</c> itself.
    /// </summary>
    private static ProcessStartInfo? Command()
    {
        if (Environment.ProcessPath is not { Length: > 0 } self) return null;

        var start = new ProcessStartInfo(self)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(self), "dotnet", StringComparison.Ordinal))
        {
            if (typeof(ProgressToaster).Assembly.Location is not { Length: > 0 } assembly) return null;
            start.ArgumentList.Add(assembly);
        }

        start.ArgumentList.Add(ProgressToasterCompanion.Switch);
        return start;
    }

    private void Listen()
    {
        // Both handlers run on the runtime's reader threads, where nothing may escape: an exception
        // there ends the process, and the process is the mail client.
        _process.OutputDataReceived += (_, e) =>
        {
            try
            {
                if (e.Data is null)
                {
                    StreamEnded();
                }
                else if (ToasterWire.Decode(e.Data) is { } message)
                {
                    Dispatcher.UIThread.Post(() => Handle(message));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Progress toaster: a line from it could not be handled.", ex);
            }
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            try
            {
                if (e.Data is null) StreamEnded();
                else Forward(e.Data);
            }
            catch (Exception ex)
            {
                Log.Warn("Progress toaster: a line of its log could not be passed on.", ex);
            }
        };

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Dispatcher.UIThread.Post(Exited);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var input = _process.StandardInput;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _outbox.Reader.ReadAllAsync())
                {
                    await input.WriteLineAsync(line);
                    await input.FlushAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The toaster has gone. Exited says so.
            }
            finally
            {
                try
                {
                    // The end of its input is the toaster's cue to go, whatever else happened.
                    input.Close();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // Already closed with the process.
                }
            }
        });
    }

    private void Handle(ToasterMessage message)
    {
        switch (message)
        {
            case ToasterMessage.Ready:
                _ready = true;

                // Asked for only now, because a toaster takes a moment to start and a run can be
                // over by then. One that ended cleanly has nothing to show — the dialog would have
                // closed on it already — so it is not shown at all rather than flashed.
                if (_closing) return;
                if (_tasks.IsFinished && _tasks.Errors.Count == 0)
                {
                    Close();
                    return;
                }

                Send(new ToasterMessage.Show());
                break;

            case ToasterMessage.Shown shown:
                Log.Info($"Progress toaster: shown on {shown.Backend}, unactivated and kept above.");
                break;

            case ToasterMessage.CancelAll:
                CancelRequested?.Invoke(this, EventArgs.Empty);
                break;

            case ToasterMessage.HideChanged hide:
                HideChanged?.Invoke(this, hide.Hidden);
                break;

            case ToasterMessage.Closed:
                Finished();
                break;
        }
    }

    private void Exited()
    {
        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        // A toaster that never got as far as ready will not get further next time either: no X
        // server behind DISPLAY, a sandbox without its socket. Said once, and not tried again.
        // Not when this end had asked it to leave: a toaster still starting when its run ended,
        // and stopped by the timer in Close, failed at nothing — and taking that for a failure
        // would switch the toaster off for the session because a start was slow.
        if (!_ready && !_closing) Unavailable($"it exited with code {code} before it was ready");

        _outbox.Writer.TryComplete();
        _exited = true;
        DisposeWhenDone();
        Finished();
    }

    /// <summary>Both of the toaster's output streams have reached their end.</summary>
    private void StreamEnded()
    {
        if (Interlocked.Decrement(ref _streamsOpen) == 0) Dispatcher.UIThread.Post(Drained);
    }

    private void Drained()
    {
        _drained = true;
        DisposeWhenDone();
    }

    /// <summary>
    /// Disposes the process once it has exited and everything it wrote has been read. Not on the
    /// exit alone: disposing cancels the readers, and the exit can be signalled before its last
    /// lines — which, for a toaster that failed to start, are the lines that say why.
    /// </summary>
    private void DisposeWhenDone()
    {
        if (_exited && _drained) _process.Dispose();
    }

    /// <summary>
    /// One line of the toaster's log, at the level of the entry it belongs to.
    /// </summary>
    /// <remarks>
    /// An entry that carries an exception puts the type, the message and the stack on the lines
    /// after the one that says ERR, and those are the part worth keeping — so a line that does not
    /// begin with the log's timestamp takes the level of the entry before it. Its warnings are the
    /// application's warnings; the rest — the theme it applied, the language it loaded, every half
    /// hour — is only worth the debug level.
    /// </remarks>
    private void Forward(string line)
    {
        if (line.Length == 0) return;

        if (StartsEntry(line))
        {
            _entryWarns = line.Contains(" WRN ", StringComparison.Ordinal)
                          || line.Contains(" ERR ", StringComparison.Ordinal);
        }

        if (_entryWarns) Log.Warn($"Progress toaster: {line}");
        else Log.Debug($"Progress toaster: {line}");
    }

    /// <summary>True for the first line of a log entry, which begins with its time: <c>HH:mm:ss.fff</c>.</summary>
    private static bool StartsEntry(string line)
        => line.Length > 12 && char.IsAsciiDigit(line[0]) && line[2] == ':' && line[5] == ':' && line[8] == '.';

    private void Finished()
    {
        if (_finished) return;
        _finished = true;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Queues a message; the writer sends it. Nothing once the toaster is closing or gone.</summary>
    private void Send(ToasterMessage message) => _outbox.Writer.TryWrite(ToasterWire.Encode(message));

    private static void Unavailable(string why)
    {
        _unavailable = why;
        Log.Warn($"Progress toaster: {why}, so the runs that would open it keep to the status bar this session.");
    }
}
