using MimeKit;

namespace Mailbox.Security;

/// <summary>What a writer asked for before this message goes out.</summary>
/// <remarks>
/// Two toggles rather than four states, because the reference's bar has two buttons and they are
/// independent: a message may be signed, sealed, both or neither. Which algorithm carries it is not
/// asked here — see <see cref="MessageProtection"/> for why that is the application's decision
/// rather than the writer's.
/// </remarks>
[Flags]
public enum Protection
{
    /// <summary>Neither button is down. Most mail.</summary>
    None = 0,

    /// <summary>Sign it, so a recipient can tell it came from this account unaltered.</summary>
    Sign = 1,

    /// <summary>Encrypt it, so only the recipients can read it.</summary>
    Encrypt = 2,
}

/// <summary>What came of applying it, in the terms the compose window says it in.</summary>
/// <remarks>
/// The counterpart to <see cref="SignatureState"/> and <see cref="DecryptionState"/>, and one
/// vocabulary for both algorithms for the same reason: the writer is being told what happened to
/// their message, and which algorithm was going to carry it is not that.
/// <para>
/// <b>There is no partial state.</b> A message the writer asked to encrypt either goes encrypted or
/// does not go: a client that sends it in the clear because one recipient had no key has done the
/// one thing its user was trying to prevent, and told them so afterwards.
/// </para>
/// </remarks>
public enum ProtectionState
{
    /// <summary>Nothing was asked for, so nothing was done and the message is as it was.</summary>
    None,

    /// <summary>Everything asked for was applied.</summary>
    Applied,

    /// <summary>
    /// A key is missing — the writer's own to sign with, or a recipient's to encrypt to.
    /// </summary>
    /// <remarks>Its own state because the writer can act on it: get the key, or take the toggle off.</remarks>
    NoKey,

    /// <summary>The writer's own secret key is here and would not unlock.</summary>
    Locked,

    /// <summary>The cryptography itself failed, which is not something a writer can act on.</summary>
    Failed,
}

/// <summary>What one attempt to protect an outgoing message came to.</summary>
/// <param name="Body">
/// What the message's body should become, or null when nothing was applied. Handed back rather than
/// written in place so a caller that gets a refusal still holds the message it started with.
/// </param>
/// <param name="Detail">
/// One sentence a writer can act on — who has no key, which key would not open. Empty when there is
/// nothing to say.
/// </param>
public sealed record ProtectionReport(ProtectionState State, MimeEntity? Body, string Detail)
{
    /// <summary>Nothing was asked for.</summary>
    public static readonly ProtectionReport Unprotected =
        new(ProtectionState.None, null, string.Empty);

    /// <summary>True when the message may go: either nothing was asked for, or all of it was done.</summary>
    public bool MaySend => State is ProtectionState.None or ProtectionState.Applied;
}
