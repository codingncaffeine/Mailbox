using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Settings;

namespace Mailbox.Security.Tls;

/// <summary>What is wrong with a certificate, as far as the reader has to care.</summary>
/// <remarks>
/// Separated because they are not the same size of problem. A name that does not match on a
/// chain that is otherwise perfectly good is what shared hosting looks like — the certificate is
/// the hosting company's own hostname and the customer's domain is a CNAME at it — and a reader
/// who understands that is making a reasonable decision. A chain that does not build is a
/// different thing entirely: nobody has vouched for that key at all.
/// </remarks>
[Flags]
public enum CertificateFault
{
    None = 0,

    /// <summary>The certificate is valid, for somebody else. Shared hosting's own shape.</summary>
    NameMismatch = 1,

    /// <summary>Expired, or not yet valid.</summary>
    Expired = 2,

    /// <summary>The chain does not reach a root this machine trusts — self-signed, or a private CA.</summary>
    UntrustedRoot = 4,

    /// <summary>The chain says this certificate was revoked, or could not be asked.</summary>
    Revoked = 8,

    /// <summary>No certificate was offered at all.</summary>
    Absent = 16,

    /// <summary>Something the chain reported that does not fit the others.</summary>
    Other = 32,
}

/// <summary>A certificate as the dialog has to describe it.</summary>
/// <remarks>
/// Flattened out of <see cref="X509Certificate2"/> deliberately: what is shown to a reader making
/// a trust decision should be a fixed set of facts taken once, not a live handle to a certificate
/// that a page could go on asking questions of.
/// </remarks>
public sealed record CertificateFacts(
    string Subject,
    string Issuer,
    string Fingerprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    IReadOnlyList<string> Names)
{
    /// <summary>The fingerprint in pairs, which is how every other tool prints one.</summary>
    public string PrettyFingerprint => string.Join(
        ' ', Enumerable.Range(0, Fingerprint.Length / 2).Select(i => Fingerprint.Substring(i * 2, 2)));

    /// <summary>The common name out of the subject, which is what a person calls the certificate.</summary>
    public string CommonName
    {
        get
        {
            foreach (var part in Subject.Split(',', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return part[3..];
            }

            return Subject;
        }
    }

    /// <summary>Everything the certificate says it is for, for the dialog's own line.</summary>
    public string NamesLine => Names.Count > 0 ? string.Join(", ", Names) : CommonName;

    public static CertificateFacts Read(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var names = new List<string>();
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                names.AddRange(san.EnumerateDnsNames());
            }
        }

        return new CertificateFacts(
            certificate.Subject,
            certificate.Issuer,
            Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            certificate.NotBefore,
            certificate.NotAfter,
            names);
    }
}

/// <summary>A connection this application turned away, and what it would take to allow it.</summary>
public sealed record CertificateRefusal(string Host, int Port, CertificateFacts Certificate, CertificateFault Faults)
{
    /// <summary>How the host is filed — one decision per host and port, not one per account.</summary>
    public string Key => $"{Host.ToLowerInvariant()}:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// What is wrong, in the order a reader should weigh it. The plainest sentence available,
    /// because a person deciding whether to trust a stranger's key should not have to decode
    /// "RemoteCertificateNameMismatch".
    /// </summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var said = new List<string>();

            if (Faults.HasFlag(CertificateFault.Absent)) said.Add("The server offered no certificate at all.");

            if (Faults.HasFlag(CertificateFault.NameMismatch))
            {
                said.Add($"The certificate is for {Certificate.NamesLine}, not for {Host}.");
            }

            if (Faults.HasFlag(CertificateFault.Expired))
            {
                said.Add(Certificate.NotAfter < DateTimeOffset.Now
                    ? $"It expired on {Certificate.NotAfter.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}."
                    : $"It is not valid until {Certificate.NotBefore.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}.");
            }

            if (Faults.HasFlag(CertificateFault.UntrustedRoot))
            {
                said.Add("Nobody this machine trusts has vouched for it — it is self-signed, or signed by an authority that is not installed here.");
            }

            if (Faults.HasFlag(CertificateFault.Revoked)) said.Add("It has been revoked.");
            if (Faults.HasFlag(CertificateFault.Other)) said.Add("The certificate could not be verified.");

            return said;
        }
    }

    /// <summary>
    /// True when the only thing wrong is the name, and everything else about the certificate is
    /// in order.
    /// </summary>
    /// <remarks>
    /// Worth telling apart, and worth saying to the reader: it is what a shared host looks like,
    /// and it is a much smaller thing to accept than a key nobody has vouched for. It does not
    /// make the decision — nothing here does that on somebody's behalf — it only says which
    /// question is being asked.
    /// </remarks>
    public bool NameOnly => Faults == CertificateFault.NameMismatch;
}

/// <summary>
/// Which server certificates this reader has agreed to, and the one place that decides.
/// </summary>
/// <remarks>
/// <b>It is a vault, not a callback, and the reason is the same as the passphrase prompt's.</b>
/// The .NET validation callback is synchronous, on whatever thread is connecting, and it must
/// answer immediately; a dialog is asynchronous and belongs to the UI thread. Blocking either on
/// the other is a deadlock. So nothing is asked from inside it — the callback answers out of what
/// it already knows, **records the certificate it turned away**, and the connection fails. The
/// caller then asks the reader properly and tries again.
/// <para>
/// <b>What is remembered is a fingerprint, not a permission.</b> "Ignore certificate errors for
/// this host" is the wrong thing to store: it is a standing waiver that covers the certificate
/// the reader looked at and every certificate that ever replaces it, including one from somebody
/// who has since got in the way. A pin covers exactly the key that was shown, so the day the
/// server's certificate changes — a renewal, or an attack — the question is asked again.
/// </para>
/// <para>
/// Kept in the settings file rather than the keyring, because a fingerprint is not a secret: it
/// is a public value whose whole purpose is to be compared, and a trust decision a reader cannot
/// find and revoke is not one they really made.
/// </para>
/// </remarks>
public sealed class CertificateTrust(SettingsStore? settings = null)
{
    /// <summary>Where the pins live. One key, a map of host:port to fingerprint.</summary>
    public const string SettingKey = "security.tls.pinned";

    private readonly Dictionary<string, string> _pinned = Load(settings);
    private readonly Dictionary<string, CertificateRefusal> _refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Every certificate turned away since this ran, for the caller that has to ask.</summary>
    public IReadOnlyList<CertificateRefusal> Refused
    {
        get { lock (_gate) return [.. _refused.Values]; }
    }

    /// <summary>What was turned away for one host, or null when nothing was.</summary>
    public CertificateRefusal? RefusalFor(string host, int port)
    {
        lock (_gate) return _refused.GetValueOrDefault($"{host.ToLowerInvariant()}:{port}");
    }

    /// <summary>Every host with a pin against it, for the page that lists them.</summary>
    public IReadOnlyList<(string Host, string Fingerprint)> Pins
    {
        get { lock (_gate) return [.. _pinned.Select(p => (p.Key, p.Value))]; }
    }

    /// <summary>
    /// The callback a client is given, bound to the host it was told to connect to.
    /// </summary>
    /// <remarks>
    /// Bound to the name the <em>caller</em> asked for rather than reading it back off the
    /// connection: the whole question is whether the server that answered is the one that was
    /// asked for, and a check that takes the server's word for which host this is answers nothing.
    /// </remarks>
    public RemoteCertificateValidationCallback For(string host, int port)
        => (_, certificate, chain, errors) => Allows(host, port, certificate, chain, errors);

    /// <summary>Whether this connection may go ahead, and the record of it when it may not.</summary>
    public bool Allows(
        string host, int port, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        // The ordinary case, and the one that has to stay cheap: a chain that verifies against a
        // root this machine trusts, for the name that was asked for.
        if (errors == SslPolicyErrors.None) return true;

        if (certificate is null)
        {
            Remember(new CertificateRefusal(host, port, Empty, CertificateFault.Absent));
            return false;
        }

        // Only what is made here is disposed. The certificate the TLS stack hands a validation
        // callback belongs to the connection, and closing it leaves the caller holding an invalid
        // handle — which is a fault that surfaces somewhere else entirely, long after this
        // returned an answer that looked right.
        var given = certificate as X509Certificate2;
        var full = given ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

        CertificateFacts facts;
        try
        {
            facts = CertificateFacts.Read(full);
        }
        finally
        {
            if (given is null) full.Dispose();
        }

        var faults = Classify(errors, chain, facts);

        var key = $"{host.ToLowerInvariant()}:{port.ToString(CultureInfo.InvariantCulture)}";

        lock (_gate)
        {
            // The pin is on the certificate, so a server that has renewed since is a server that
            // has to be asked about again — which is the entire point of pinning the key rather
            // than waiving the check.
            if (_pinned.TryGetValue(key, out var pinned)
                && string.Equals(pinned, facts.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        Remember(new CertificateRefusal(host, port, facts, faults));
        Log.Warn(
            $"{host}:{port} was refused: its certificate is {facts.CommonName} "
            + $"({facts.Fingerprint[..16]}…) and {string.Join(" ", DescribeFaults(faults))}");

        return false;
    }

    /// <summary>
    /// Records that the reader agreed to this certificate, and only this certificate.
    /// </summary>
    public void Pin(CertificateRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        lock (_gate)
        {
            _pinned[refusal.Key] = refusal.Certificate.Fingerprint;
            _refused.Remove(refusal.Key);
            Save();
        }

        Log.Info($"{refusal.Host}:{refusal.Port} is now trusted for the certificate {refusal.Certificate.Fingerprint[..16]}….");
    }

    /// <summary>Takes a pin off, so the host is judged on its merits again.</summary>
    public bool Forget(string hostAndPort)
    {
        lock (_gate)
        {
            if (!_pinned.Remove(hostAndPort.ToLowerInvariant())) return false;
            Save();
            return true;
        }
    }

    /// <summary>Forgets what was turned away, without agreeing to any of it.</summary>
    public void ClearRefusals()
    {
        lock (_gate) _refused.Clear();
    }

    private void Remember(CertificateRefusal refusal)
    {
        lock (_gate) _refused[refusal.Key] = refusal;
    }

    /// <summary>
    /// What the chain complained about, in this application's own words.
    /// </summary>
    /// <remarks>
    /// The chain's status is read as well as the policy errors, because
    /// <see cref="SslPolicyErrors.RemoteCertificateChainErrors"/> is one flag covering half a
    /// dozen quite different failures — expired, revoked, and nobody-vouched-for-this are the same
    /// bit — and telling a reader "the chain had errors" tells them nothing they can act on.
    /// </remarks>
    internal static CertificateFault Classify(SslPolicyErrors errors, X509Chain? chain, CertificateFacts facts)
    {
        var faults = CertificateFault.None;

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) faults |= CertificateFault.NameMismatch;
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) faults |= CertificateFault.Absent;

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            var named = false;

            foreach (var status in chain?.ChainStatus ?? [])
            {
                if (status.Status == X509ChainStatusFlags.NoError) continue;
                named = true;

                faults |= status.Status switch
                {
                    X509ChainStatusFlags.NotTimeValid or X509ChainStatusFlags.CtlNotTimeValid
                        => CertificateFault.Expired,
                    X509ChainStatusFlags.Revoked => CertificateFault.Revoked,
                    X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation
                        => CertificateFault.Revoked,
                    X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain
                        or X509ChainStatusFlags.ExplicitDistrust
                        => CertificateFault.UntrustedRoot,
                    _ => CertificateFault.Other,
                };
            }

            // A chain error with nothing in the chain to say what it was. The dates are the one
            // thing that can still be checked here, and they are the commonest answer.
            if (!named)
            {
                var now = DateTimeOffset.Now;
                faults |= facts.NotAfter < now || facts.NotBefore > now
                    ? CertificateFault.Expired
                    : CertificateFault.Other;
            }
        }

        return faults == CertificateFault.None ? CertificateFault.Other : faults;
    }

    private static IEnumerable<string> DescribeFaults(CertificateFault faults)
    {
        if (faults.HasFlag(CertificateFault.NameMismatch)) yield return "is for another name;";
        if (faults.HasFlag(CertificateFault.Expired)) yield return "is out of date;";
        if (faults.HasFlag(CertificateFault.UntrustedRoot)) yield return "reaches no trusted root;";
        if (faults.HasFlag(CertificateFault.Revoked)) yield return "is revoked or cannot be checked;";
        if (faults.HasFlag(CertificateFault.Other)) yield return "could not be verified;";
    }

    private static readonly CertificateFacts Empty =
        new(string.Empty, string.Empty, string.Empty, DateTimeOffset.MinValue, DateTimeOffset.MinValue, []);

    private static Dictionary<string, string> Load(SettingsStore? settings)
    {
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settings?.GetString(SettingKey) is not { Length: > 0 } text) return pins;

        try
        {
            if (JsonNode.Parse(text) is JsonObject map)
            {
                foreach (var (host, fingerprint) in map)
                {
                    if (fingerprint?.GetValue<string>() is { Length: > 0 } value) pins[host] = value;
                }
            }
        }
        catch (JsonException ex)
        {
            // A settings file somebody has edited by hand. Trusting nothing is the safe way to be
            // wrong here, and it says so rather than starting up as if there were no pins.
            Log.Warn($"The list of trusted certificates could not be read, so none are trusted: {ex.Message}");
        }

        return pins;
    }

    private void Save()
    {
        if (settings is null) return;

        var map = new JsonObject();
        foreach (var (host, fingerprint) in _pinned) map[host] = fingerprint;
        settings.Set(SettingKey, map.ToJsonString());
    }
}
