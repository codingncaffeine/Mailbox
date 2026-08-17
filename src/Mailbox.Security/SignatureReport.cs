namespace Mailbox.Security;

/// <summary>What checking a signature came to, in the terms the reading pane says it in.</summary>
/// <remarks>
/// One vocabulary for both S/MIME and OpenPGP. The reader is being told what happened to their
/// message, and which algorithm carried it is not that: a bar that reads differently for the two
/// would teach them to compare wordings rather than states. §19's rules are rules about a message,
/// so the two verifiers answer in the same words and differ only in how they arrive at them.
/// </remarks>
public enum SignatureState
{
    /// <summary>Nothing was signed. Most mail.</summary>
    None,

    /// <summary>Signed, checked, and everything held: the signer is who the message says sent it.</summary>
    Valid,

    /// <summary>
    /// The maths held but the signer is not the sender, or the key is not one for this message.
    /// </summary>
    /// <remarks>
    /// Its own state on purpose. A client that folds this into "valid" is telling a reader that a
    /// message from an impostor is signed by the person it names, which is the whole of the attack
    /// (§19); one that folds it into "invalid" teaches them to ignore the word.
    /// </remarks>
    Mismatched,

    /// <summary>Signed and it does not hold: the maths failed, or the chain would not build.</summary>
    Invalid,

    /// <summary>Signed in a way this cannot check — an algorithm or a shape it does not know.</summary>
    Unknown,
}

/// <summary>What one signature came to, and why.</summary>
/// <param name="Signer">Who the certificate or key says signed it, as an address.</param>
/// <param name="Detail">One sentence a reader can act on. Empty when there is nothing to say.</param>
public sealed record SignatureReport(SignatureState State, string Signer, string Detail)
{
    public static readonly SignatureReport Unsigned = new(SignatureState.None, string.Empty, string.Empty);

    /// <summary>True only for a signature that passed every check there is.</summary>
    public bool Trustworthy => State == SignatureState.Valid;
}
