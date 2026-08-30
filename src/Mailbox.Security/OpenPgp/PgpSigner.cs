using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Mailbox.Security.OpenPgp;

/// <summary>How far one signature got before anybody asked what it meant.</summary>
public enum PgpSignerOutcome
{
    /// <summary>The maths holds: these bytes were signed by this key.</summary>
    Held,

    /// <summary>The maths does not hold. The message is not what was signed.</summary>
    Failed,

    /// <summary>Signed by a key this computer has not got, so there was nothing to check against.</summary>
    Unavailable,

    /// <summary>Signed in a shape or an algorithm this cannot check.</summary>
    Unreadable,
}

/// <summary>
/// One OpenPGP signature as the cryptography leaves it — before the verifier's questions are asked of it.
/// </summary>
/// <remarks>
/// Deliberately not a verdict. A signature arrives two ways — detached, in a
/// <c>multipart/signed</c>, and one-pass, inside an encrypted packet — and the library hands back
/// its own type for the first and nothing at all for the second, whose constructors are internal to
/// it. So both are reduced to this, and <see cref="PgpVerification.Judge"/> is the one place that
/// turns either into something a reader is told.
/// </remarks>
/// <param name="Ring">The key ring the signer's key came from; the user IDs are on its master key.</param>
/// <param name="Key">The key that signed, which is usually a subkey of that ring.</param>
/// <param name="Created">When the signature says it was made. The signer's own claim, never trusted as a timestamp.</param>
public sealed record PgpSigner(
    PgpSignerOutcome Outcome,
    PgpPublicKeyRing? Ring,
    PgpPublicKey? Key,
    DateTime Created)
{
    /// <summary>A signature whose key this computer has not got.</summary>
    public static readonly PgpSigner Unavailable =
        new(PgpSignerOutcome.Unavailable, null, null, default);
}
