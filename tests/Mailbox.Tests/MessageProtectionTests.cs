using Mailbox.Security;
using Mailbox.Security.OpenPgp;
using Mailbox.Security.Smime;
using MimeKit;
using MimeKit.Cryptography;

namespace Mailbox.Tests;

/// <summary>
/// Signing and encrypting on the way out — checked by reading the result back in.
/// </summary>
/// <remarks>
/// <b>Every round trip here goes back through this application's own reader</b> rather than through
/// the library that wrote it, which is what makes these tests worth having. The reader refuses a
/// packet with no integrity protection and refuses a signature whose signer is not the sender (§19),
/// so a message that comes back <see cref="DecryptionState.Opened"/> and
/// <see cref="SignatureState.Valid"/> is one this application would agree to open and believe. A
/// client that will not read its own outgoing mail is a real failure mode and this is what would
/// catch it.
/// </remarks>
public class MessageProtectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mailbox-protect-" + Guid.NewGuid().ToString("n"));

    // ---- OpenPGP -------------------------------------------------------------------------------

    [Fact]
    public void AnOpenPgpSignedMessageComesBackAsSignedByItsSender()
    {
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);
        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);

        var report = MessageProtection.Apply(
            message, Protection.Sign, null, mine, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);
        Assert.True(report.MaySend);

        var arrived = PgpKeys.Reload(message);
        Assert.True(PgpVerification.IsSigned(arrived));

        var signature = PgpVerification.Verify(arrived, mine);
        Assert.True(signature.State == SignatureState.Valid, $"{signature.State}: {signature.Detail}");
        Assert.Equal(PgpKeys.Reader.Address, signature.Signer);
    }

    [Fact]
    public void AnOpenPgpEncryptedMessageOpensWithTheIntegrityCheckThisApplicationInsistsOn()
    {
        // The one that would fail silently if MimeKit wrote a packet with no modification detection
        // code: our own reader refuses that outright (§19), so "it opened" is the assertion that
        // what went out was protected as well as encrypted.
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);
        using var theirs = Ring("theirs", PgpKeys.Other, PgpKeys.Reader);

        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);
        var report = MessageProtection.Apply(
            message, Protection.Encrypt, null, mine, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var arrived = PgpKeys.Reload(message);
        Assert.True(PgpDecryption.IsEncrypted(arrived));

        var opened = PgpDecryption.Open(arrived, theirs, TestContext.Current.CancellationToken);
        Assert.True(opened.State == DecryptionState.Opened, $"{opened.State}: {opened.Detail}");
        Assert.Contains("The quiet part.", Text(opened.Content), StringComparison.Ordinal);
    }

    [Fact]
    public void SignedAndEncryptedTogetherIsBothWhenItArrives()
    {
        // OpenPGP's ordinary shape rather than an exotic one: the signature is inside the
        // encryption, which is the form PgpContext.Open was built to judge.
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);
        using var theirs = Ring("theirs", PgpKeys.Other, PgpKeys.Reader);

        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);
        var report = MessageProtection.Apply(
            message, Protection.Sign | Protection.Encrypt, null, mine, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var arrived = PgpKeys.Reload(message);
        var opened = PgpDecryption.Open(arrived, theirs, TestContext.Current.CancellationToken);

        Assert.True(opened.State == DecryptionState.Opened, $"{opened.State}: {opened.Detail}");
        Assert.NotNull(opened.Signature);
        Assert.True(
            opened.Signature.State == SignatureState.Valid,
            $"{opened.Signature.State}: {opened.Signature.Detail}");
        Assert.Equal(PgpKeys.Reader.Address, opened.Signature.Signer);
    }

    [Fact]
    public void AnEncryptedMessageIsReadableByItsOwnAuthor()
    {
        // The copy in Sent Items is the one that went out. A client whose users cannot read their
        // own sent mail has made encryption something to avoid.
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);

        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);
        MessageProtection.Apply(
            message, Protection.Encrypt, null, mine, TestContext.Current.CancellationToken);

        var opened = PgpDecryption.Open(
            PgpKeys.Reload(message), mine, TestContext.Current.CancellationToken);

        Assert.True(opened.State == DecryptionState.Opened, $"{opened.State}: {opened.Detail}");
    }

    // ---- S/MIME --------------------------------------------------------------------------------

    [Fact]
    public void AnSmimeSignedMessageComesBackAsSignedByItsSender()
    {
        using var context = new TemporarySecureMimeContext();
        var me = SmimeKeys.Generate("work@example.net").Hold(context).Trust(context);
        var them = SmimeKeys.Generate("b.other@example.net").Trust(context);

        var message = Message(from: me.Address, to: them.Address);
        var report = MessageProtection.Apply(
            message, Protection.Sign, context, null, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var arrived = PgpKeys.Reload(message);
        Assert.True(SmimeVerification.IsSigned(arrived));

        var signature = SmimeVerification.Verify(arrived, context);
        Assert.True(signature.State == SignatureState.Valid, $"{signature.State}: {signature.Detail}");
        Assert.Equal(me.Address, signature.Signer);
    }

    [Fact]
    public void AnSmimeEncryptedMessageOpensAgain()
    {
        using var context = new TemporarySecureMimeContext();
        var me = SmimeKeys.Generate("work@example.net").Hold(context).Trust(context);
        var them = SmimeKeys.Generate("b.other@example.net").Hold(context).Trust(context);

        var message = Message(from: me.Address, to: them.Address);
        var report = MessageProtection.Apply(
            message, Protection.Encrypt, context, null, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var arrived = PgpKeys.Reload(message);
        Assert.True(SmimeDecryption.IsEncrypted(arrived));

        var opened = SmimeDecryption.Open(arrived, context);
        Assert.True(opened.State == DecryptionState.Opened, $"{opened.State}: {opened.Detail}");
        Assert.Contains("The quiet part.", Text(opened.Content), StringComparison.Ordinal);
    }

    // ---- Choosing between them -----------------------------------------------------------------

    [Fact]
    public void TheAlgorithmThatHasEverybodysKeysIsTheOneUsed()
    {
        // S/MIME goes first where both work, so this is the case that proves the choice is a real
        // one: the certificate store can sign for the writer and has nothing for the recipient, and
        // the message goes out in the algorithm that can carry it rather than not at all.
        using var certificates = new TemporarySecureMimeContext();
        SmimeKeys.Generate(PgpKeys.Reader.Address).Hold(certificates).Trust(certificates);

        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);
        using var theirs = Ring("theirs", PgpKeys.Other, PgpKeys.Reader);

        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);
        var report = MessageProtection.Apply(
            message, Protection.Encrypt, certificates, mine, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var arrived = PgpKeys.Reload(message);
        Assert.True(PgpDecryption.IsEncrypted(arrived));
        Assert.False(SmimeDecryption.IsEncrypted(arrived));
        Assert.Equal(
            DecryptionState.Opened,
            PgpDecryption.Open(arrived, theirs, TestContext.Current.CancellationToken).State);
    }

    [Fact]
    public void SMimeIsPreferredWhenBothCouldCarryIt()
    {
        using var certificates = new TemporarySecureMimeContext();
        SmimeKeys.Generate(PgpKeys.Reader.Address).Hold(certificates).Trust(certificates);
        SmimeKeys.Generate(PgpKeys.Other.Address).Hold(certificates).Trust(certificates);

        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);

        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);
        var report = MessageProtection.Apply(
            message, Protection.Encrypt, certificates, mine, TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionState.Applied, report.State);
        Assert.True(SmimeDecryption.IsEncrypted(PgpKeys.Reload(message)));
    }

    // ---- What stops a message ------------------------------------------------------------------

    [Fact]
    public void ARecipientWithNoKeyStopsTheMessageAndIsNamed()
    {
        // The refusal that matters most. A client that sends this in the clear because one address
        // had no key has done the exact thing its user pressed the button to prevent.
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);

        var message = Message(from: PgpKeys.Reader.Address, to: "nobody@example.org");
        var before = message.Body;

        var report = MessageProtection.Apply(
            message, Protection.Encrypt, null, mine, TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionState.NoKey, report.State);
        Assert.False(report.MaySend);
        Assert.Contains("nobody@example.org", report.Detail, StringComparison.Ordinal);

        // And the message is exactly as it came in, so a caller holding a refusal still holds
        // something it could save.
        Assert.Same(before, message.Body);
    }

    [Fact]
    public void AWriterWithNoKeyOfTheirOwnCannotSign()
    {
        using var theirs = Ring("theirs", PgpKeys.Other);

        var message = Message(from: "nobody@example.org", to: PgpKeys.Other.Address);
        var report = MessageProtection.Apply(
            message, Protection.Sign, null, theirs, TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionState.NoKey, report.State);
        Assert.Contains("nobody@example.org", report.Detail, StringComparison.Ordinal);
        Assert.Contains("cannot be signed", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherAlgorithmSwitchedOnIsSaidPlainly()
    {
        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);
        var report = MessageProtection.Apply(
            message, Protection.Sign, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionState.Failed, report.State);
        Assert.False(report.MaySend);
    }

    [Fact]
    public void NothingAskedForIsNothingDone()
    {
        using var mine = Ring("mine", PgpKeys.Reader);

        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);
        var before = message.Body;

        var report = MessageProtection.Apply(
            message, Protection.None, null, mine, TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionState.None, report.State);
        Assert.True(report.MaySend);
        Assert.Same(before, message.Body);
    }

    // ---- Drafts, which are not messages (§19) --------------------------------------------------

    [Fact]
    public void ADraftIsNeverSigned()
    {
        // A signature is a statement, and it is made when a person decides to send something — not
        // every few minutes by an autosave, over fields a mailto: link may have filled in.
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);

        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);
        var before = message.Body;

        var report = MessageProtection.ApplyToDraft(
            message, Protection.Sign, null, mine, TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionState.None, report.State);
        Assert.Same(before, message.Body);
        Assert.False(PgpVerification.IsSigned(PgpKeys.Reload(message)));
    }

    [Fact]
    public void ADraftIsEncryptedToItsAuthorAndNobodyElse()
    {
        // The recipient field is the part an attacker gets to choose, so it is the part a draft
        // ignores. The author can read their own draft back; the address in the To line cannot.
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);
        using var theirs = Ring("theirs", PgpKeys.Other, PgpKeys.Reader);

        var message = Message(from: PgpKeys.Reader.Address, to: PgpKeys.Other.Address);
        var report = MessageProtection.ApplyToDraft(
            message, Protection.Sign | Protection.Encrypt, null, mine, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var saved = PgpKeys.Reload(message);
        var author = PgpDecryption.Open(saved, mine, TestContext.Current.CancellationToken);
        Assert.Equal(DecryptionState.Opened, author.State);

        var refused = PgpDecryption.Open(saved, theirs, TestContext.Current.CancellationToken);
        Assert.Equal(DecryptionState.Locked, refused.State);
        Assert.Null(refused.Content);

        // Encrypted, and still not signed: the draft rule takes the signature off and leaves the
        // encryption on rather than refusing the whole thing.
        Assert.NotNull(author.Signature);
        Assert.Equal(SignatureState.None, author.Signature.State);
    }

    // ---- The passphrase, which is why a key can be here and shut ---------------------------------

    [Fact]
    public void AKeyNobodyHasUnlockedIsLockedRatherThanBroken()
    {
        // Nothing asks for a passphrase from inside the cryptography — see PassphraseVault for why
        // that cannot work — so the first attempt refuses and says which key it wanted.
        var vault = new PassphraseVault();
        using var mine = Ring("mine", PgpKeys.Sender, vault.For, PgpKeys.Other);

        var message = Message(from: PgpKeys.Sender.Address, to: PgpKeys.Other.Address);
        var report = MessageProtection.Apply(
            message, Protection.Sign, null, mine, TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionState.Locked, report.State);
        Assert.False(report.MaySend);

        var wanted = Assert.Single(vault.Wanted);
        Assert.Equal(PgpKeys.Sender.Address, wanted.Address);
        Assert.Equal(PgpKeys.Sender.Signing.KeyId, wanted.KeyId);

        // The fingerprint as a person would compare it, which is the part a dialog shows.
        Assert.Equal(40 + 9, wanted.Fingerprint.Length);
    }

    [Fact]
    public void OnceTheKeyIsUnlockedTheSameMessageGoes()
    {
        var vault = new PassphraseVault();
        using var mine = Ring("mine", PgpKeys.Sender, vault.For, PgpKeys.Other);

        var message = Message(from: PgpKeys.Sender.Address, to: PgpKeys.Other.Address);
        Assert.Equal(
            ProtectionState.Locked,
            MessageProtection.Apply(message, Protection.Sign, null, mine, TestContext.Current.CancellationToken).State);

        var wanted = Assert.Single(vault.Wanted);
        Assert.True(PassphraseVault.Opens(PgpKeys.Sender.Signing, PgpKeys.Passphrase));
        vault.Remember(wanted.KeyId, PgpKeys.Passphrase);

        var report = MessageProtection.Apply(
            message, Protection.Sign, null, mine, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var signature = PgpVerification.Verify(PgpKeys.Reload(message), mine);
        Assert.True(signature.State == SignatureState.Valid, $"{signature.State}: {signature.Detail}");
    }

    [Fact]
    public void AWrongPassphraseIsNotFiledAsARightOne()
    {
        // The dialog asks the vault before it keeps anything, so a mistyped passphrase is refused
        // by the box that took it rather than by a send that then has to be explained.
        Assert.False(PassphraseVault.Opens(PgpKeys.Sender.Signing, "not it"));
        Assert.True(PassphraseVault.Opens(PgpKeys.Sender.Signing, PgpKeys.Passphrase));
    }

    [Fact]
    public void AnAnswerGivenOnceIsGoneAfterItIsUsed()
    {
        var vault = new PassphraseVault();
        var key = PgpKeys.Sender.Signing;

        Assert.Null(vault.For(key));
        vault.Once(key.KeyId, PgpKeys.Passphrase);

        Assert.Equal(PgpKeys.Passphrase, vault.For(key));

        vault.Clear();
        Assert.Null(vault.For(key));
    }

    // ---- The material --------------------------------------------------------------------------

    private PgpContext Ring(string name, PgpIdentity mine, params PgpIdentity[] theirs)
        => PgpKeys.Ring(Path.Combine(_root, name), mine, null, theirs);

    private PgpContext Ring(
        string name, PgpIdentity mine, Func<Org.BouncyCastle.Bcpg.OpenPgp.PgpSecretKey, string?> passphrase,
        params PgpIdentity[] theirs)
        => PgpKeys.Ring(Path.Combine(_root, name), mine, passphrase, theirs);

    private static MimeMessage Message(string from, string to)
    {
        var message = new MimeMessage { Subject = "The figures", Date = DateTimeOffset.Now };
        message.From.Add(new MailboxAddress(string.Empty, from));
        message.To.Add(new MailboxAddress(string.Empty, to));
        message.Body = new TextPart("plain") { Text = "The quiet part.\n" };
        return message;
    }

    private static string Text(MimeEntity? entity)
        => entity is TextPart text ? text.Text : string.Empty;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
