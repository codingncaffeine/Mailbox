using System.Diagnostics;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Protocols;

/// <summary>Somewhere to keep a password.</summary>
public interface ICredentialStore
{
    /// <summary>Whether this store can actually be used on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>A word for the user describing where passwords are going.</summary>
    string Description { get; }

    Task<bool> SaveAsync(string account, string purpose, string secret,
        CancellationToken cancellation = default);

    Task<string?> LoadAsync(string account, string purpose,
        CancellationToken cancellation = default);

    Task<bool> DeleteAsync(string account, string purpose,
        CancellationToken cancellation = default);
}

/// <summary>
/// Passwords in the desktop's own keyring, over the Secret Service API.
/// </summary>
/// <remarks>
/// Driven through <c>secret-tool</c> rather than by speaking D-Bus directly. The wire protocol
/// involves a session, a negotiated transport encryption and prompt handling, all of which
/// differ subtly between GNOME Keyring and KWallet's Secret Service shim; <c>secret-tool</c>
/// is part of libsecret, ships wherever the service does, and gets those differences right.
/// <para>
/// There is deliberately no file-based fallback. A password on disk in a config directory is
/// worse than being told plainly that no keyring is running, because the user would never find
/// out it had happened.
/// </para>
/// </remarks>
public sealed class SecretServiceStore : ICredentialStore
{
    private const string Tool = "secret-tool";

    /// <summary>Identifies our entries in a keyring shared with everything else on the desktop.</summary>
    private const string ServiceAttribute = "mailbox";

    private bool? _available;

    public bool IsAvailable => _available ??= Probe();

    public string Description => "the desktop keyring";

    public async Task<bool> SaveAsync(string account, string purpose, string secret,
        CancellationToken cancellation = default)
    {
        // The secret goes over stdin, never as an argument: arguments are visible to every
        // process on the machine through /proc.
        var result = await RunAsync(
            ["store", "--label", $"Mailbox — {purpose} — {account}",
             "service", ServiceAttribute, "account", account, "purpose", purpose],
            input: secret,
            cancellation).ConfigureAwait(false);

        if (!result.Ok) Log.Warn($"Could not save the {purpose} password: {result.Error}");
        return result.Ok;
    }

    public async Task<string?> LoadAsync(string account, string purpose,
        CancellationToken cancellation = default)
    {
        var result = await RunAsync(
            ["lookup", "service", ServiceAttribute, "account", account, "purpose", purpose],
            input: null,
            cancellation).ConfigureAwait(false);

        // secret-tool exits non-zero when nothing matches, which is not an error here.
        return result.Ok && result.Output.Length > 0 ? result.Output.TrimEnd('\n') : null;
    }

    public async Task<bool> DeleteAsync(string account, string purpose,
        CancellationToken cancellation = default)
    {
        var result = await RunAsync(
            ["clear", "service", ServiceAttribute, "account", account, "purpose", purpose],
            input: null,
            cancellation).ConfigureAwait(false);

        return result.Ok;
    }

    /// <summary>
    /// The same lookup, done synchronously, for the one caller that is legitimately synchronous.
    /// </summary>
    /// <remarks>
    /// The store's key has to be in hand before the first database is opened, and that happens in
    /// a start-up that runs before there is a dispatcher to await on. Written as its own
    /// synchronous call rather than by blocking on the asynchronous one: running a program and
    /// reading its output is synchronous by nature, the task was the wrapper, and unwrapping a
    /// task by waiting on it is the shape this codebase has a sweep test against.
    /// <para>
    /// Nothing else should use these. Everything that runs once the interface is up has a
    /// dispatcher to await on and should.
    /// </para>
    /// </remarks>
    public string? LoadAtStartup(string account, string purpose)
    {
        var result = Run(
            ["lookup", "service", ServiceAttribute, "account", account, "purpose", purpose],
            input: null);

        return result.Ok && result.Output.Length > 0 ? result.Output.TrimEnd('\n') : null;
    }

    /// <summary>The same save, synchronously, for the same one caller.</summary>
    public bool SaveAtStartup(string account, string purpose, string secret)
        => Run(
            ["store", "--label", $"Mailbox — {purpose} — {account}",
             "service", ServiceAttribute, "account", account, "purpose", purpose],
            input: secret).Ok;

    /// <summary>The same clear, synchronously, for the same one caller.</summary>
    public bool DeleteAtStartup(string account, string purpose)
        => Run(
            ["clear", "service", ServiceAttribute, "account", account, "purpose", purpose],
            input: null).Ok;

    /// <summary>Runs secret-tool and waits for it. No tasks anywhere in here.</summary>
    private static (bool Ok, string Output, string Error) Run(IReadOnlyList<string> arguments, string? input)
    {
        try
        {
            var start = new ProcessStartInfo(Tool)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return (false, string.Empty, "secret-tool would not start");

            if (input is not null) process.StandardInput.Write(input);
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit((int)Patience.TotalMilliseconds))
            {
                // A keyring that never answers must not be able to stop the application.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It finished between the wait and the kill.
                }

                return (false, string.Empty, "the keyring did not answer");
            }

            return (process.ExitCode == 0, output, error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    private bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Tool, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return false;
            process.WaitForExit(2000);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"No Secret Service available: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// How long the keyring is given to answer before this gives up on it.
    /// </summary>
    /// <remarks>
    /// A keyring that never answers must not be able to stop the application. It happens: a
    /// locked wallet whose unlock prompt cannot be shown, or a Secret Service that has gone away
    /// mid-session, leaves <c>secret-tool</c> waiting for something that is never coming. Ten
    /// seconds is far longer than the lookup takes and far shorter than a person will wait before
    /// deciding the program has hung.
    /// </remarks>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static async Task<(bool Ok, string Output, string Error)> RunAsync(
        string[] arguments, string? input, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return (false, string.Empty, "Could not start secret-tool.");

            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            patience.CancelAfter(Patience);

            // Both pipes drained at once. Reading one to its end and then the other is the
            // textbook subprocess deadlock: a child that fills the pipe nobody is reading blocks
            // writing to it, while this blocks reading the other, and neither ever moves again.
            var output = process.StandardOutput.ReadToEndAsync(patience.Token);
            var error = process.StandardError.ReadToEndAsync(patience.Token);

            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), patience.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            try
            {
                await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
                return (process.ExitCode == 0,
                    await output.ConfigureAwait(false),
                    await error.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // The keyring did not answer. Killed rather than left behind, and reported as a
                // failure rather than as an empty password, which would look like a password that
                // was simply not set.
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* it exited on its own between the timeout and here */ }

                return (false, string.Empty,
                    $"the desktop keyring did not answer within {Patience.TotalSeconds:0} seconds");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}

/// <summary>
/// Passwords held for the lifetime of the process and nowhere else.
/// </summary>
/// <remarks>
/// Used when no keyring is running, and by tests. The user is told that this is what is
/// happening: mail will work until Mailbox is closed and the password will be asked for again,
/// which is a nuisance but an honest one.
/// </remarks>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<(string Account, string Purpose), string> _secrets = [];

    public bool IsAvailable => true;

    public string Description => "this session only";

    public Task<bool> SaveAsync(string account, string purpose, string secret,
        CancellationToken cancellation = default)
    {
        _secrets[(account, purpose)] = secret;
        return Task.FromResult(true);
    }

    public Task<string?> LoadAsync(string account, string purpose,
        CancellationToken cancellation = default)
        => Task.FromResult(_secrets.GetValueOrDefault((account, purpose)));

    public Task<bool> DeleteAsync(string account, string purpose,
        CancellationToken cancellation = default)
        => Task.FromResult(_secrets.Remove((account, purpose)));
}

/// <summary>Picks the best store available, and says which one that was.</summary>
public static class Credentials
{
    /// <summary>Purpose strings, so incoming and outgoing passwords do not collide.</summary>
    public const string Incoming = "incoming";
    public const string Outgoing = "outgoing";

    /// <summary>
    /// An OAuth refresh token, which is one credential for the whole account rather than one per
    /// direction: the same sign-in covers collecting and sending.
    /// </summary>
    public const string OAuthRefresh = "oauth-refresh";

    public static ICredentialStore Best()
    {
        var keyring = new SecretServiceStore();
        if (keyring.IsAvailable)
        {
            Log.Info("Passwords will be kept in the desktop keyring.");
            return keyring;
        }

        Log.Warn(
            "No desktop keyring found, so passwords will be kept in memory for this session " +
            "only. Install libsecret (secret-tool) and run a keyring to have them remembered.");

        return new InMemoryCredentialStore();
    }
}
