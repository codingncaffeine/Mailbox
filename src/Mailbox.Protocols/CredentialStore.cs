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
            cancellation);

        if (!result.Ok) Log.Warn($"Could not save the {purpose} password: {result.Error}");
        return result.Ok;
    }

    public async Task<string?> LoadAsync(string account, string purpose,
        CancellationToken cancellation = default)
    {
        var result = await RunAsync(
            ["lookup", "service", ServiceAttribute, "account", account, "purpose", purpose],
            input: null,
            cancellation);

        // secret-tool exits non-zero when nothing matches, which is not an error here.
        return result.Ok && result.Output.Length > 0 ? result.Output.TrimEnd('\n') : null;
    }

    public async Task<bool> DeleteAsync(string account, string purpose,
        CancellationToken cancellation = default)
    {
        var result = await RunAsync(
            ["clear", "service", ServiceAttribute, "account", account, "purpose", purpose],
            input: null,
            cancellation);

        return result.Ok;
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

    private static async Task<(bool Ok, string Output, string Error)> RunAsync(
        string[] arguments, string? input, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return (false, string.Empty, "Could not start secret-tool.");

            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellation);
            var error = await process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);

            return (process.ExitCode == 0, output, error);
        }
        catch (Exception ex)
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
