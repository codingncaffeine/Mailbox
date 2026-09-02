using Mailbox.Core.Diagnostics;
using Mailbox.Security;
using Mailbox.Security.OpenPgp;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// Everything the pane needs settled before it may draw a message, once it is settled.
/// </summary>
/// <param name="Opened">What opening an encrypted message came to.</param>
/// <param name="Protected">The header fields carried inside the cryptography.</param>
/// <param name="Carrier">The message the reader is actually shown — the envelope, or what was inside it.</param>
/// <param name="Signature">What checking the signature came to, inner or outer.</param>
internal sealed record MessageCrypto(
    DecryptionReport Opened,
    ProtectedHeaders? Protected,
    MimeMessage Carrier,
    SignatureReport Signature);

/// <summary>
/// The reading pane's OpenPGP work when it is GnuPG doing it, which is the one kind that may
/// wait on a person.
/// </summary>
/// <remarks>
/// <b>Why this is the only asynchronous crypto path.</b> The pane settles a message's
/// cryptography before it draws anything, deliberately: the signature's verdict is what decides
/// whether the display-name warning fires, so a message rendered first and settled later would
/// stand with a spoofed From unwarned for the length of the check. For the keyring kept here that
/// costs local disk and some arithmetic — bounded, and paid only on a signed message — and
/// <see cref="ReadingPaneBody.SignatureOf"/> says why staying synchronous is the better trade.
/// <para>
/// Delegating to GnuPG breaks that trade. It is a subprocess, and the whole point of the
/// arrangement is that <c>gpg-agent</c> may stop and ask the reader for a passphrase through
/// their own pinentry — an unbounded wait on a human being. Running that on the dispatcher would
/// freeze the application for as long as somebody took to type, which is not a stall but a hang.
/// </para>
/// <para>
/// So the rule is kept and the wait is moved: nothing is drawn until the crypto has settled, and
/// the settling happens off the dispatcher. What the reader sees meanwhile is a pane that says
/// it is opening the message — never a header whose warnings have not been decided. The answer
/// is checked against a generation on the way back, so a reader who moves on before their agent
/// answers does not have the message they left behind painted over the one they are reading.
/// </para>
/// </remarks>
public sealed partial class ReadingPaneBody
{
    /// <summary>The crypto settled for the message on show, or null while it is still being settled.</summary>
    private MessageCrypto? _crypto;

    /// <summary>
    /// Which message the crypto in flight belongs to, as a number that only goes up.
    /// </summary>
    /// <remarks>
    /// The same discipline the engine's loads are under, and for the same reason: a pinentry
    /// somebody leaves open is a resolution that can land minutes later, long after they have
    /// selected something else.
    /// </remarks>
    private long _cryptoGeneration;

    /// <summary>
    /// Whether this message's cryptography has to be settled by GnuPG before it can be drawn.
    /// </summary>
    /// <remarks>
    /// Only when the reader has turned the delegation on, only when OpenPGP is on at all, and
    /// only for a message that is actually signed or encrypted — so ordinary mail, which is
    /// almost all of it, takes exactly the path it took before and pays nothing.
    /// </remarks>
    private static bool NeedsGnuPg(MimeMessage message)
        => App.Security.OpenPgp
           && App.Security.OpenPgpThroughGnuPg
           && (PgpDecryption.IsEncrypted(message) || PgpVerification.IsSigned(message));

    /// <summary>
    /// Settles a message's cryptography through GnuPG and draws it, if it is still the one on show.
    /// </summary>
    private async Task ResolveThroughGnuPgAsync(MimeMessage message, long generation)
    {
        var settled = await Task.Run(() => SettleAsync(message));

        // The reader has moved on. Their agent answering for a message they have left is not a
        // reason to paint it over the one in front of them.
        if (generation != _cryptoGeneration || !ReferenceEquals(_message, message)) return;

        _crypto = settled;
        Refresh();
    }

    /// <summary>
    /// The same composition <see cref="Refresh"/> makes, made off the dispatcher.
    /// </summary>
    /// <remarks>
    /// Deliberately the same order and the same helpers: decrypt, read the covered headers, hide
    /// the legacy copy of them, build the carrier, and only then judge the signature — against
    /// whichever message the reader will actually be shown. Two places computing this differently
    /// is how a pane ends up verifying one thing and rendering another.
    /// </remarks>
    private async Task<MessageCrypto> SettleAsync(MimeMessage message)
    {
        var agent = new GnuPgAgent();
        var opened = DecryptionReport.Unencrypted;

        if (PgpDecryption.IsEncrypted(message))
        {
            opened = await GnuPgReading.OpenAsync(message, agent);
        }

        var covered = Covered(message, opened);
        if (opened.Opened && covered is not null) HeaderProtection.HideLegacyDisplay(covered.Rendered);

        var carrier = opened.Opened
            ? AsMessage(message, covered?.Rendered ?? opened.Content!, covered)
            : message;

        // A signature carried inside the packet is the packet's own and is already judged; only
        // a message that carried none is asked about its outer layer.
        var signature = opened.Signature is { State: not SignatureState.None } enclosed
            ? enclosed
            : await GnuPgReading.VerifyAsync(carrier, agent);

        Log.Info(
            $"Harness: reading crypto through GnuPG — encryption {opened.State}, signature {signature.State}"
            + (signature.Signer.Length > 0 ? $" by {signature.Signer}" : string.Empty)
            + (opened.Detail.Length > 0 ? $"; {opened.Detail}" : string.Empty) + ".");

        return new MessageCrypto(opened, covered, carrier, signature);
    }

    /// <summary>
    /// What the pane shows while GnuPG is being asked.
    /// </summary>
    /// <remarks>
    /// No header and no body: the header's own warnings are among the things the crypto decides,
    /// so drawing it early is the thing this whole arrangement exists to avoid. One sentence
    /// saying what is being waited for, because the wait may be a passphrase prompt on another
    /// window and a pane that simply sat blank would look broken.
    /// </remarks>
    private void ShowOpening()
    {
        _rendered = null;
        Carried = null;
        Protected = null;
        HeaderSubject = null;
        HeaderFrom = null;
        HeaderChanged?.Invoke(this, EventArgs.Empty);

        ShowText(
            "Opening this message with GnuPG…\n\n"
            + "If it asks for your passphrase, answer the prompt on your desktop.");
    }
}
