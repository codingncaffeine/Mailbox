using System.Diagnostics;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Security.OpenPgp;

/// <summary>What came of asking GnuPG for its keys.</summary>
public sealed record GnuPgImportResult(int Public, int Secret, string? Problem)
{
    public bool Worked => Problem is null;

    public int Total => Public + Secret;

    /// <summary>One sentence for the page that asked.</summary>
    public string Summary => Problem
        ?? (Total == 0
            ? "GnuPG had no keys that were not already here."
            : $"Imported {Describe(Public, "public key")} and {Describe(Secret, "secret key")} from GnuPG.");

    private static string Describe(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

/// <summary>
/// Bringing a reader's existing keys over from GnuPG.
/// </summary>
/// <remarks>
/// <b>Why this is a subprocess and not a file read.</b> Mailbox keeps its ring beside
/// <c>pim.db</c> rather than pointing at <c>~/.gnupg</c>, and the reason is not preference:
/// MimeKit's <c>GnuPGContext</c> reads <c>pubring.gpg</c> and <c>secring.gpg</c>, and GnuPG 2.1
/// and later write <c>pubring.kbx</c> and <c>private-keys-v1.d</c>. Pointing at the directory
/// finds nothing on any current system and says nothing about why. So the keys are asked for from
/// the one program that can certainly read them, and imported.
/// <para>
/// <b>The secret export is deliberately not <c>--batch</c>.</b> GnuPG's agent may want the key's
/// passphrase, and the right place to ask for it is GnuPG's own pinentry on the reader's own
/// desktop — not a dialog of ours that would then be holding somebody's GnuPG passphrase. What
/// arrives is still encrypted with that passphrase, which is exactly right: the vault asks for it
/// when the key is next used, and nothing here ever learns it.
/// </para>
/// </remarks>
public static class GnuPgImport
{
    private const string Tool = "gpg";

    /// <summary>How long GnuPG is given, which has to cover somebody typing into pinentry.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    /// <summary>Whether GnuPG is on this machine at all.</summary>
    public static bool IsAvailable => Probe();

    private static bool? _available;

    /// <summary>
    /// Asks GnuPG for its keys and puts them in the ring.
    /// </summary>
    /// <param name="ring">The ring to import into — Mailbox's own, beside <c>pim.db</c>.</param>
    /// <param name="secretsToo">
    /// Whether to ask for the secret halves as well. Public keys alone are enough to check
    /// signatures and to encrypt to other people; the secret half is what signs and decrypts, and
    /// asking for it is what may summon pinentry.
    /// </param>
    public static async Task<GnuPgImportResult> RunAsync(
        PgpContext ring, bool secretsToo = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ring);

        if (!IsAvailable)
        {
            return new GnuPgImportResult(0, 0, "GnuPG is not installed, so there is nothing to import from.");
        }

        var publicKeys = 0;
        var secretKeys = 0;

        var exported = await ExportAsync(["--export"], cancellationToken).ConfigureAwait(false);
        if (exported.Problem is { } wrong) return new GnuPgImportResult(0, 0, wrong);

        if (exported.Bytes.Length > 0)
        {
            using var stream = new MemoryStream(exported.Bytes);
            var (added, _) = ring.Take(stream, cancellationToken);
            publicKeys = added;
        }

        if (!secretsToo) return new GnuPgImportResult(publicKeys, 0, null);

        var secrets = await ExportAsync(["--export-secret-keys"], cancellationToken).ConfigureAwait(false);
        if (secrets.Problem is { } refused)
        {
            // The public half is already in and is worth keeping; the reader is told what did not
            // come with it rather than the whole thing being reported as a failure.
            return new GnuPgImportResult(publicKeys, 0,
                $"The public keys were imported. The secret keys were not: {refused}");
        }

        if (secrets.Bytes.Length > 0)
        {
            using var stream = new MemoryStream(secrets.Bytes);
            var (_, added) = ring.Take(stream, cancellationToken);
            secretKeys = added;
        }

        Log.Info($"Imported {publicKeys} public and {secretKeys} secret key(s) from GnuPG.");
        return new GnuPgImportResult(publicKeys, secretKeys, null);
    }

    /// <summary>
    /// Runs one export and hands back the bytes.
    /// </summary>
    /// <remarks>
    /// Binary rather than armoured, and read as bytes rather than as text: armour is base64 in a
    /// text envelope, and reading a binary keyring through a <see cref="StreamReader"/> is how a
    /// key comes back subtly corrupted with no error anywhere.
    /// </remarks>
    private static async Task<(byte[] Bytes, string? Problem)> ExportAsync(
        string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(Tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return ([], "GnuPG would not start.");

            using var buffer = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
            var complaining = process.StandardError.ReadToEndAsync(cancellationToken);

            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            patience.CancelAfter(Patience);

            try
            {
                await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* it exited on its own between the timeout and here */ }
                return ([], "GnuPG did not answer. If it asked for a passphrase, it may be waiting out of sight.");
            }

            await reading.ConfigureAwait(false);
            var error = await complaining.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return ([], Tidy(error) is { Length: > 0 } said ? said : $"GnuPG exited {process.ExitCode}.");
            }

            return (buffer.ToArray(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>
    /// GnuPG's last complaint, on one line.
    /// </summary>
    /// <remarks>
    /// Its stderr is chatty and mostly progress; the last line is the one that says what went
    /// wrong. Control characters are stripped, this being text from another program that ends up
    /// in a log.
    /// </remarks>
    private static string Tidy(string error)
    {
        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return string.Empty;

        var last = new string([.. lines[^1].Where(c => !char.IsControl(c))]).Trim();
        return last.Length > 200 ? last[..200] + "…" : last;
    }

    private static bool Probe()
    {
        if (_available is { } known) return known;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(Tool, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null) return (_available = false).Value;
            process.WaitForExit(2000);
            return (_available = true).Value;
        }
        catch (Exception ex)
        {
            Log.Info($"No GnuPG on this machine: {ex.Message}");
            return (_available = false).Value;
        }
    }
}
