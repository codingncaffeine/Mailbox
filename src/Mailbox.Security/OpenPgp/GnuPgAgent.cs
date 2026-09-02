using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Security.OpenPgp;

/// <summary>What one call to GnuPG produced, and what its status output said about it.</summary>
/// <param name="Output">What it wrote, when it worked.</param>
/// <param name="Problem">Why it did not, in a sentence, or null when it did.</param>
/// <param name="Status">The status lines, which are the machine-readable half of the answer.</param>
public sealed record GnuPgResult(byte[] Output, string? Problem, IReadOnlyList<string> Status)
{
    public bool Worked => Problem is null;

    /// <summary>Whether a status line beginning with this keyword was written.</summary>
    public bool Said(string keyword)
        => Status.Any(line => line.StartsWith(keyword + " ", StringComparison.Ordinal)
                              || string.Equals(line, keyword, StringComparison.Ordinal));

    /// <summary>What followed a status keyword, or null when it was never written.</summary>
    public string? After(string keyword)
    {
        foreach (var line in Status)
        {
            if (line.StartsWith(keyword + " ", StringComparison.Ordinal)) return line[(keyword.Length + 1)..];
            if (string.Equals(line, keyword, StringComparison.Ordinal)) return string.Empty;
        }

        return null;
    }

    public static GnuPgResult Failed(string why) => new([], why, []);
}

/// <summary>
/// The reader's own GnuPG, asked to do the things only a secret key can do.
/// </summary>
/// <remarks>
/// <b>Why delegate rather than hold the keys.</b> This application keeps an OpenPGP ring of its
/// own beside the mail stores, and for a reader with no GnuPG that is the whole story. For a
/// reader who already uses PGP it is a second, parallel world: their key material is copied
/// rather than used, their passphrase is typed into this application rather than into their
/// agent, and a revocation or a new subkey published elsewhere never reaches the copy. Handing
/// the private-key operations to <c>gpg</c> ends all three — the keyring stays the one the rest
/// of their system uses, <c>gpg-agent</c> holds the passphrase and prompts through their own
/// pinentry, and nothing here ever sees it.
/// <para>
/// <b>The status stream is the answer, not the exit code.</b> GnuPG writes a machine-readable
/// account of what it did to <c>--status-fd</c>, and it is the only place several things are
/// said at all: whether a decryption's integrity check passed, whether a signature was good and
/// whose key made it, whether a key has expired. Reading the exit code alone is how a client
/// ends up reporting a modified message as an intact one — which is the failure
/// <see cref="PgpContext"/> exists to prevent on the other path, and it is prevented the same way
/// here.
/// </para>
/// <para>
/// <b>Nothing interactive except pinentry.</b> Every call is <c>--batch</c>, so GnuPG never asks
/// this process a question it cannot answer; the one prompt that does happen is the agent's own,
/// on the reader's desktop, which is the point of the arrangement.
/// </para>
/// </remarks>
public sealed class GnuPgAgent(string? home = null, TimeSpan? patience = null)
{
    private const string Tool = "gpg";

    /// <summary>
    /// How long GnuPG is given. Long, because the wait is somebody typing a passphrase into
    /// pinentry rather than a computation.
    /// </summary>
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Which keyring to use, or null for the reader's own — which is the point, and the default.
    /// </summary>
    /// <remarks>
    /// Only set by the tests and the harness, which must never touch a real one: a test that
    /// signed with somebody's actual key would be a test that asked their agent for a passphrase.
    /// </remarks>
    private readonly string? _home = home;

    private readonly TimeSpan _patience = patience ?? DefaultPatience;

    private static bool? _available;

    /// <summary>Whether GnuPG is on this machine at all.</summary>
    public static bool IsAvailable => _available ??= Probe();

    /// <summary>What to say when it is not.</summary>
    public const string Missing =
        "GnuPG is not installed, so its keys and its agent cannot be used. "
        + "Install gnupg, or switch this off to use the keys kept here.";

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Tool, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null) return false;
            process.WaitForExit(TimeSpan.FromSeconds(10));
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ---- What the keyring holds ----------------------------------------------------------------

    /// <summary>
    /// The addresses this machine can sign as — the user ids on keys whose secret half is here.
    /// </summary>
    /// <remarks>
    /// Secret keys rather than every key: signing needs the secret half, and offering to sign as
    /// an address whose key is only public is offering a button that fails at send time.
    /// </remarks>
    public async Task<IReadOnlyList<string>> SignersAsync(CancellationToken cancellationToken = default)
        => Addresses(await RunAsync(["--list-secret-keys", "--with-colons"], null, cancellationToken));

    /// <summary>The addresses a message can be encrypted to, which is every key here.</summary>
    public async Task<IReadOnlyList<string>> RecipientsAsync(CancellationToken cancellationToken = default)
        => Addresses(await RunAsync(["--list-keys", "--with-colons"], null, cancellationToken));

    /// <summary>
    /// The addresses out of a colon-delimited listing.
    /// </summary>
    /// <remarks>
    /// <c>--with-colons</c> rather than the human listing, because the human one is localised and
    /// its layout is explicitly not a stable interface. Field 1 says what the record is and field
    /// 10 carries the user id; a uid is <c>Name (comment) &lt;address&gt;</c>, so the address is
    /// what stands between the angle brackets. A record with no angle brackets is a user id
    /// somebody wrote without an address, and belongs to nobody a message can be sent to.
    /// </remarks>
    internal static IReadOnlyList<string> Addresses(GnuPgResult result)
    {
        if (!result.Worked) return [];

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in Encoding.UTF8.GetString(result.Output).Split('\n'))
        {
            var fields = line.Split(':');
            if (fields.Length < 10) continue;
            if (fields[0] is not ("uid" or "pub" or "sec")) continue;

            // Revoked, expired, disabled or invalid: field 2 carries the validity, and a key the
            // owner has revoked is not one to offer.
            if (fields[1].Length > 0 && "redi".Contains(fields[1][0], StringComparison.Ordinal)) continue;

            var uid = fields[9];
            var open = uid.LastIndexOf('<');
            var close = uid.LastIndexOf('>');
            if (open < 0 || close <= open) continue;

            var address = uid[(open + 1)..close].Trim();
            if (address.Length > 0 && seen.Add(address)) found.Add(address);
        }

        return found;
    }

    // ---- The private-key operations --------------------------------------------------------------

    /// <summary>
    /// Signs, detached and armoured — the <c>application/pgp-signature</c> half of RFC 3156's
    /// <c>multipart/signed</c>.
    /// </summary>
    /// <param name="content">Exactly the bytes the signature covers, canonicalised already.</param>
    /// <param name="signer">The address to sign as.</param>
    public async Task<GnuPgResult> SignAsync(
        byte[] content, string signer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var result = await RunAsync(
            [
                "--armor", "--detach-sign",
                // Pinned rather than left to the reader's gpg.conf, for the reason the other path
                // pins it: SHA-1 is still several clients' default and has not been defensible
                // for a decade.
                "--digest-algo", "SHA256",
                "--local-user", signer,
            ],
            content,
            cancellationToken);

        if (!result.Worked) return result;

        // The exit code is not enough. GnuPG says SIG_CREATED when it actually made one, and a
        // run that produced no signature but exited zero would otherwise become an empty
        // signature part that every recipient reports as broken.
        return result.Said("SIG_CREATED")
            ? result
            : GnuPgResult.Failed("GnuPG did not make a signature.");
    }

    /// <summary>
    /// Encrypts to everybody named, armoured — the payload of a <c>multipart/encrypted</c>.
    /// </summary>
    /// <param name="signAs">
    /// An address to sign the plaintext as at the same time, or null for encryption alone. One
    /// call rather than two: a signature made inside the encryption is the one that says the
    /// message was written by the person it is from, rather than merely forwarded by them.
    /// </param>
    public async Task<GnuPgResult> EncryptAsync(
        byte[] content,
        IReadOnlyList<string> recipients,
        string? signAs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(recipients);

        if (recipients.Count == 0) return GnuPgResult.Failed("There is nobody to encrypt to.");

        var arguments = new List<string> { "--armor", "--encrypt" };

        // Every recipient must resolve to exactly one key, and GnuPG must not pick a different
        // one: without this a name that matches two keys is quietly encrypted to whichever it
        // liked, and a name that matches none can fall through to a key server.
        foreach (var recipient in recipients)
        {
            arguments.Add("--recipient");
            arguments.Add(recipient);
        }

        // Trust is the reader's business and they keep it in GnuPG, but a key they have not
        // signed is still a key they deliberately imported — refusing to encrypt to it in batch
        // is how this reads as "encryption is broken". What the key is worth is reported on the
        // way in, by the signature check, rather than decided here.
        arguments.Add("--trust-model");
        arguments.Add("always");

        if (signAs is { Length: > 0 })
        {
            arguments.Add("--sign");
            arguments.Add("--digest-algo");
            arguments.Add("SHA256");
            arguments.Add("--local-user");
            arguments.Add(signAs);
        }

        var result = await RunAsync(arguments, content, cancellationToken);
        if (!result.Worked) return result;

        return result.Output.Length > 0
            ? result
            : GnuPgResult.Failed("GnuPG produced no ciphertext.");
    }

    /// <summary>
    /// Decrypts, and refuses to hand back anything whose integrity was not proven.
    /// </summary>
    /// <remarks>
    /// The whole reason this is not simply "run gpg and take stdout". An OpenPGP packet may carry
    /// no modification detection at all, and older ones routinely do not; a packet whose
    /// plaintext is released without that check is the EFAIL family, and it is the exact failure
    /// the library path was rewritten to close. GnuPG performs the check and says so — GOODMDC
    /// for the classic construction, a non-zero AEAD algorithm in DECRYPTION_INFO for the modern
    /// one — and neither present means the plaintext does not leave this method.
    /// </remarks>
    public async Task<GnuPgResult> DecryptAsync(byte[] ciphertext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var result = await RunAsync(["--decrypt"], ciphertext, cancellationToken);
        if (!result.Worked) return result;

        if (!result.Said("DECRYPTION_OKAY"))
        {
            return GnuPgResult.Failed("GnuPG could not decrypt this message.");
        }

        if (!IsIntegrityProven(result))
        {
            return new GnuPgResult(
                [],
                "This message carries no modification detection code, so there is no way to tell "
                + "whether it was altered in transit. It has not been shown.",
                result.Status);
        }

        return result;
    }

    /// <summary>
    /// Whether GnuPG proved the ciphertext had not been altered.
    /// </summary>
    /// <remarks>
    /// Two constructions, and only these two count. GOODMDC is the modification detection code of
    /// RFC 4880's symmetrically-encrypted-and-integrity-protected packet. DECRYPTION_INFO's third
    /// field is the AEAD algorithm of the newer construction, where integrity is part of the
    /// cipher itself and there is no separate code to report; a zero there means the packet was
    /// not AEAD, which throws the question back to GOODMDC.
    /// </remarks>
    internal static bool IsIntegrityProven(GnuPgResult result)
    {
        if (result.Said("GOODMDC")) return true;

        if (result.After("DECRYPTION_INFO") is { } info)
        {
            var fields = info.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 3
                && int.TryParse(fields[2], CultureInfo.InvariantCulture, out var aead)
                && aead != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks a detached signature against what it covers.
    /// </summary>
    /// <param name="content">The bytes as they travelled, canonicalised.</param>
    /// <param name="signature">The armoured signature beside them.</param>
    public async Task<GnuPgResult> VerifyAsync(
        byte[] content, byte[] signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(signature);

        // The signature has to be a file: --verify takes the signature as an argument and the
        // signed data on standard input, and there is only one standard input.
        var file = Path.Combine(Path.GetTempPath(), $"mailbox-sig-{Guid.NewGuid():N}.asc");
        try
        {
            await File.WriteAllBytesAsync(file, signature, cancellationToken);
            return await RunAsync(["--verify", file, "-"], content, cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A signature left in the temporary directory is public information; failing to
                // delete it is not worth failing the check over.
            }
        }
    }

    // ---- The subprocess --------------------------------------------------------------------------

    /// <summary>
    /// Runs GnuPG once, with the status stream captured separately from what it produced.
    /// </summary>
    /// <remarks>
    /// Three streams and all three matter: standard output is the product, the status stream is
    /// what GnuPG says it did, and standard error is what it would have told a person. The status
    /// stream is put on file descriptor 2 alongside standard error and told apart by its
    /// <c>[GNUPG:]</c> prefix — a third descriptor cannot be handed to a child through
    /// <see cref="ProcessStartInfo"/> at all, and standard output is the one place it must not go,
    /// because that is where the plaintext is.
    /// </remarks>
    private async Task<GnuPgResult> RunAsync(
        IReadOnlyList<string> arguments, byte[]? input, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return GnuPgResult.Failed(Missing);

        var start = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("--batch");
        start.ArgumentList.Add("--no-tty");
        start.ArgumentList.Add("--yes");
        start.ArgumentList.Add("--status-fd");
        start.ArgumentList.Add("2");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        if (_home is { Length: > 0 }) start.Environment["GNUPGHOME"] = _home;

        try
        {
            using var process = Process.Start(start);
            if (process is null) return GnuPgResult.Failed("GnuPG would not start.");

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation.CancelAfter(_patience);

            var output = new MemoryStream();
            var copying = process.StandardOutput.BaseStream.CopyToAsync(output, cancellation.Token);
            var reading = process.StandardError.ReadToEndAsync(cancellation.Token);

            if (input is { Length: > 0 })
            {
                await process.StandardInput.BaseStream.WriteAsync(input, cancellation.Token);
            }

            process.StandardInput.Close();

            await copying;
            var errors = await reading;
            await process.WaitForExitAsync(cancellation.Token);

            var status = new List<string>();
            var said = new List<string>();
            foreach (var line in errors.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("[GNUPG:] ", StringComparison.Ordinal)) status.Add(trimmed[9..]);
                else if (trimmed.Length > 0) said.Add(trimmed);
            }

            if (process.ExitCode != 0)
            {
                Log.Debug($"GnuPG exited {process.ExitCode}: {string.Join(" / ", said)}");
                return new GnuPgResult([], Explain(status, said), status);
            }

            return new GnuPgResult(output.ToArray(), null, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GnuPgResult.Failed("GnuPG did not answer in time.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warn("GnuPG could not be run.", ex);
            return GnuPgResult.Failed($"GnuPG could not be run: {ex.Message}");
        }
    }

    /// <summary>
    /// Why it failed, in the reader's terms.
    /// </summary>
    /// <remarks>
    /// The status stream first, because it names the case; what GnuPG wrote for a person second,
    /// because it is at least specific; and a bare mention of the exit code last, because a
    /// failure with nothing said is still better reported as a failure.
    /// </remarks>
    internal static string Explain(IReadOnlyList<string> status, IReadOnlyList<string> said)
    {
        foreach (var line in status)
        {
            if (line.StartsWith("INV_RECP ", StringComparison.Ordinal))
            {
                var fields = line.Split(' ');
                var who = fields.Length > 2 ? fields[2] : "somebody";
                return $"There is no usable key for {who} in GnuPG.";
            }

            if (line.StartsWith("NO_SECKEY ", StringComparison.Ordinal))
            {
                return "This message is encrypted to a key GnuPG does not have the secret half of.";
            }

            // NODATA: nothing GnuPG recognised as OpenPGP was in what it was given. Worth its
            // own sentence, because GnuPG's own words for it are "Unknown system error".
            if (line.StartsWith("NODATA", StringComparison.Ordinal))
            {
                return "There is no OpenPGP data here for GnuPG to read.";
            }

            if (line.StartsWith("KEYEXPIRED", StringComparison.Ordinal)) return "That key has expired.";
            if (line.StartsWith("KEYREVOKED", StringComparison.Ordinal)) return "That key has been revoked.";
            if (line.StartsWith("MISSING_PASSPHRASE", StringComparison.Ordinal)
                || line.StartsWith("BAD_PASSPHRASE", StringComparison.Ordinal))
            {
                return "GnuPG did not get the passphrase for that key.";
            }

            if (line.StartsWith("CANCELED", StringComparison.Ordinal)
                || line.StartsWith("ERROR ", StringComparison.Ordinal) && line.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            {
                return "The passphrase prompt was cancelled.";
            }
        }

        var last = said.LastOrDefault(l => l.Length > 0);
        return last is { Length: > 0 } ? $"GnuPG refused: {last}" : "GnuPG refused, and said nothing about why.";
    }
}
