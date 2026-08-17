using MimeKit;

namespace Mailbox.Security;

/// <summary>What opening an encrypted message came to.</summary>
/// <remarks>One vocabulary for both S/MIME and OpenPGP, for the reason <see cref="SignatureState"/>
/// is one: the reader is told what happened to their message, not which algorithm carried it.</remarks>
public enum DecryptionState
{
    /// <summary>Nothing was encrypted. Most mail.</summary>
    None,

    /// <summary>Opened: what is shown is what was inside.</summary>
    Opened,

    /// <summary>Encrypted to somebody else, or to a key this machine has not got.</summary>
    Locked,

    /// <summary>
    /// It opened, and nothing vouches for what came out: the packet carries no integrity
    /// protection, or the protection it carries does not hold.
    /// </summary>
    /// <remarks>
    /// OpenPGP's own state, and the reason §19 puts this feature behind a subclass. A packet with
    /// no modification detection code, or one whose code fails, is a packet an attacker may have
    /// rewritten a byte at a time — the EFAIL family, and rPGP shipped the same class of bug into
    /// 2026. <b>Nothing decrypted this way is released</b>, so there is content here and it is not
    /// in the report. S/MIME never reaches this state: CMS carries no equivalent, and its
    /// authenticated modes are handled by the library that implements them.
    /// </remarks>
    Unprotected,

    /// <summary>Encrypted, and it would not open — malformed, or an algorithm this cannot do.</summary>
    Failed,
}

/// <summary>What was inside, and what to say about it.</summary>
/// <param name="Content">The decrypted entity, or null when there is nothing to show.</param>
/// <param name="Signature">
/// What a signature carried <em>inside</em> the encrypted packet came to. Unsigned for content
/// that carried none, and for S/MIME, whose signature is a layer of its own rather than part of
/// the encryption.
/// </param>
public sealed record DecryptionReport(
    DecryptionState State,
    MimeEntity? Content,
    string Detail,
    SignatureReport? Signature = null)
{
    public static readonly DecryptionReport Unencrypted = new(DecryptionState.None, null, string.Empty);

    /// <summary>True when there is decrypted content, which is rendered on its own terms.</summary>
    public bool Opened => State == DecryptionState.Opened && Content is not null;
}
