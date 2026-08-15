using Mailbox.Security.Dns;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;

namespace Mailbox.Security;

/// <summary>
/// Where a signing domain's public key comes from: a TXT record it published.
/// </summary>
/// <remarks>
/// The name asked about is <c>&lt;selector&gt;._domainkey.&lt;domain&gt;</c>, and both halves are
/// read out of the message's own <c>DKIM-Signature</c> header — so both are chosen by the sender.
/// They are validated as names before anything is sent, and the lookup is the bounded one in
/// <see cref="DnsResolver"/> rather than the platform's.
/// <para>
/// <see cref="DkimPublicKeyLocatorBase"/> does the parsing: a DKIM TXT record is a tag list, and
/// <c>GetPublicKey</c> knows its grammar, the <c>p=</c> tag and what an empty one means.
/// </para>
/// </remarks>
public sealed class DkimKeyLocator(ITxtLookup lookup) : DkimPublicKeyLocatorBase
{
    private readonly ITxtLookup _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));

    public override AsymmetricKeyParameter LocatePublicKey(
        string methods, string domain, string selector, CancellationToken cancellationToken = default)
        => LocatePublicKeyAsync(methods, domain, selector, cancellationToken).GetAwaiter().GetResult();

    public override async Task<AsymmetricKeyParameter> LocatePublicKeyAsync(
        string methods, string domain, string selector,
        CancellationToken cancellationToken = default)
    {
        // "dns/txt" is the only query method RFC 6376 defines. A signature naming another is
        // one we cannot check rather than one that failed.
        if (methods is { Length: > 0 }
            && !methods.Split(':').Any(m => m.Trim().Equals("dns/txt", StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException($"The signature asks for '{methods}', which is not dns/txt.");
        }

        var answer = await _lookup.TxtAsync($"{selector}._domainkey.{domain}", cancellationToken);

        // A record split into several strings has already been joined. Several *records* under
        // one name is a misconfiguration; the first that parses is the one meant.
        foreach (var record in answer.Records)
        {
            try
            {
                return GetPublicKey(record);
            }
            catch (Exception)
            {
                // Not a key, or a revoked one. Try the next before giving up on the name.
            }
        }

        throw new ParseException(
            $"No public key is published at {selector}._domainkey.{domain}.", 0, 0);
    }
}

/// <summary>What verifying a message's signatures locally came to.</summary>
/// <param name="Verdict">
/// <see cref="AuthVerdict.None"/> when the message is unsigned, and
/// <see cref="AuthVerdict.Error"/> when it is signed and the key could not be reached — neither
/// is a failure, and telling a reader a signature failed because their network was down would be
/// worse than saying nothing.
/// </param>
/// <param name="SigningDomain">The domain of the signature that passed, or of the first tried.</param>
public sealed record DkimResult(AuthVerdict Verdict, string? SigningDomain)
{
    public static DkimResult Unsigned { get; } = new(AuthVerdict.None, null);

    /// <summary>True when this says something the header-derived results do not.</summary>
    public bool WasChecked => Verdict is not AuthVerdict.None;
}

/// <summary>
/// Verifies a message's DKIM signatures against the keys their domains publish.
/// </summary>
/// <remarks>
/// The check the reference application does not show and the receiving server does for us. Doing
/// it here as well is worth the work for one reason: the <c>Authentication-Results</c> header is
/// only as trustworthy as the server that wrote it, and a POP3 account's mail may have passed
/// through machines the reader never chose. A signature checked locally is checked against the
/// bytes in the store.
/// <para>
/// <b>Never on the render path.</b> This resolves, so §19's "no key discovery to display a
/// message" applies: it runs when mail is received, on the thread already doing network work,
/// and the verdict is stored. The reading pane reads the verdict and resolves nothing.
/// </para>
/// </remarks>
public sealed class DkimVerification(ITxtLookup lookup)
{
    /// <summary>
    /// Below this an RSA key is not evidence of anything. RFC 8301 deprecates keys under 1024
    /// bits, and MimeKit's own default is the same number.
    /// </summary>
    private const int MinimumRsaKeyBits = 1024;

    private readonly DkimVerifier _verifier = new(new DkimKeyLocator(lookup))
    {
        MinimumRsaKeyLength = MinimumRsaKeyBits,
    };

    /// <summary>
    /// Verifies every signature a message carries, and reports the best outcome.
    /// </summary>
    /// <remarks>
    /// A message may be signed more than once — by its author's domain and again by a list that
    /// forwarded it. One good signature is a pass, which is what RFC 6376 §6.1 says: a verifier
    /// that treated any failure as failure would mark most mailing-list mail as forged.
    /// </remarks>
    public async Task<DkimResult> VerifyAsync(
        MimeMessage message, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var signatures = message.Headers
            .Where(h => h.Id == HeaderId.DkimSignature)
            .ToList();

        if (signatures.Count == 0) return DkimResult.Unsigned;

        var verdict = AuthVerdict.None;
        string? domain = null;

        foreach (var signature in signatures)
        {
            var signer = DomainOf(signature.Value);
            domain ??= signer;

            try
            {
                if (await _verifier.VerifyAsync(message, signature, cancellation))
                {
                    return new DkimResult(AuthVerdict.Pass, signer ?? domain);
                }

                // A signature that verified as false is a real failure, and outranks a
                // signature we could not reach a key for.
                verdict = AuthVerdict.Fail;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // No key, an unreadable one, a signature we cannot parse, an algorithm we will
                // not accept. None of these is the sender failing a check.
                if (verdict is AuthVerdict.None) verdict = AuthVerdict.Error;
            }
        }

        return new DkimResult(verdict, domain);
    }

    /// <summary>The <c>d=</c> tag, which names who signed.</summary>
    private static string? DomainOf(string signature)
    {
        foreach (var tag in signature.Split(';'))
        {
            var trimmed = tag.Trim();
            if (!trimmed.StartsWith("d=", StringComparison.OrdinalIgnoreCase)) continue;

            var value = trimmed[2..].Trim();
            if (value.Length > 0) return value;
        }

        return null;
    }
}
