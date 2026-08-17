using Mailbox.Security;
using Mailbox.Security.OpenPgp;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Opening an OpenPGP message, and §19's first crypto blocker: <b>a packet nothing vouches for is
/// not opened at all</b>.
/// </summary>
/// <remarks>
/// Two of these are the whole reason <see cref="PgpContext"/> exists. MimeKit drains BouncyCastle's
/// data stream and hands back the plaintext without ever asking whether the packet was integrity
/// protected — so a packet written with no modification detection code, and a packet whose code
/// fails, both come out as ordinary mail with nothing said. RFC 9580 §13.7 says otherwise, and rPGP
/// shipped the same class of bug into 2026.
/// </remarks>
public class PgpDecryptionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mailbox-pgp-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public void AnEncryptedMessageOpensToWhatWasInside()
    {
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = PgpKeys.Encrypted(PgpKeys.Sender, PgpKeys.Content("The quiet part."));
        var report = PgpDecryption.Open(message, context, TestContext.Current.CancellationToken);

        Assert.True(report.State == DecryptionState.Opened, report.Detail);
        Assert.True(report.Opened);
        var text = Assert.IsType<TextPart>(report.Content);
        Assert.StartsWith("The quiet part.", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void APacketWithNoIntegrityProtectionIsRefused()
    {
        // §19's first blocker, and the one MimeKit does not make: this packet decrypts perfectly
        // well. Nothing about it is malformed and nothing throws. What it has not got is anything
        // saying it was not rewritten in transit, which is what EFAIL was built out of — so the
        // plaintext exists here and is not handed over.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = PgpKeys.Encrypted(PgpKeys.Sender, PgpKeys.Content(), integrity: false);
        var report = PgpDecryption.Open(message, context, TestContext.Current.CancellationToken);

        Assert.Equal(DecryptionState.Unprotected, report.State);
        Assert.Null(report.Content);
        Assert.False(report.Opened);

        // Which of the two refusals fired, so this cannot pass by failing somewhere else: there was
        // nothing to check, rather than a check that did not hold.
        Assert.Contains("carries nothing to show", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void APacketAlteredAfterEncryptionIsRefused()
    {
        // The other half of the same finding: the check exists, it fails, and BouncyCastle's
        // Verify() *returns false rather than throwing* — so a caller that does not look at the
        // answer never learns anything went wrong.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var whole = PgpKeys.Encrypted(PgpKeys.Sender, PgpKeys.Content());
        var altered = PgpKeys.Encrypted(PgpKeys.Sender, PgpKeys.Content(), corrupt: 2048);

        Assert.Equal(
            DecryptionState.Opened,
            PgpDecryption.Open(whole, context, TestContext.Current.CancellationToken).State);

        var report = PgpDecryption.Open(altered, context, TestContext.Current.CancellationToken);

        Assert.Equal(DecryptionState.Unprotected, report.State);
        Assert.Null(report.Content);

        // The packet *has* a modification detection code — it was written with one — so this is
        // Verify() answering false, which is the call MimeKit never makes.
        Assert.Contains("was altered after it was encrypted", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageEncryptedToSomebodyElseStaysShut()
    {
        // Not an error to report loudly: mail encrypted to a key this computer has not got is
        // ordinary, and the reader is told that rather than shown a failure.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = PgpKeys.Encrypted(PgpKeys.Other, PgpKeys.Content("Not for you."));
        var report = PgpDecryption.Open(message, context, TestContext.Current.CancellationToken);

        Assert.Equal(DecryptionState.Locked, report.State);
        Assert.Null(report.Content);
        Assert.NotEmpty(report.Detail);
    }

    [Fact]
    public void AnOrdinaryMessageIsNotEncrypted()
    {
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = new MimeMessage { Subject = "Ordinary" };
        message.From.Add(new MailboxAddress("A. Person", "a.person@example.com"));
        message.Body = new TextPart("plain") { Text = "Hello." };

        Assert.False(PgpDecryption.IsEncrypted(message));
        Assert.Equal(
            DecryptionState.None,
            PgpDecryption.Open(message, context, TestContext.Current.CancellationToken).State);
    }

    [Fact]
    public void AnEncryptedPartBuriedInsideIsNotThisMessageBeingEncrypted()
    {
        // The same rule as the signature's, for the same reason: a part the reader never sees is
        // not a claim about the message. Reporting it as encrypted lends the envelope a padlock it
        // has not earned, and rendering it would put attacker-chosen markup where the plaintext goes.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var encrypted = PgpKeys.Encrypted(PgpKeys.Sender, PgpKeys.Content()).Body!;

        var message = new MimeMessage { Subject = "Not encrypted at all" };
        message.From.Add(new MailboxAddress("A. Person", "a.person@example.com"));
        message.Body = new Multipart("related") { new TextPart("plain") { Text = "Hello." }, encrypted };

        var arrived = PgpKeys.Reload(message);

        Assert.False(PgpDecryption.IsEncrypted(arrived));
        Assert.Equal(
            DecryptionState.None,
            PgpDecryption.Open(arrived, context, TestContext.Current.CancellationToken).State);
    }

    [Fact]
    public void AMultipartEncryptedCarryingSomebodyElsesSchemeIsNotOurs()
    {
        // multipart/encrypted is not OpenPGP's alone. A part declaring another protocol is left to
        // whatever handles that one rather than fed to this.
        using var context = PgpKeys.Context(_root, PgpKeys.Sender);

        var message = PgpKeys.Encrypted(PgpKeys.Sender, PgpKeys.Content());
        message.Body!.ContentType.Parameters["protocol"] = "application/something-else";

        var arrived = PgpKeys.Reload(message);

        Assert.False(PgpDecryption.IsEncrypted(arrived));
    }

    [Fact]
    public void AKeyThatIsHereAndShutIsToldApartFromOneThatIsNotHere()
    {
        // Both come back Locked, and only one of them is something a reader can do anything about.
        // What tells them apart is the vault: it records every key it was asked for and had no
        // answer to, which is what puts an Unlock button on the bar rather than a dead end.
        var vault = new PassphraseVault();
        using var shut = PgpKeys.Ring(Path.Combine(_root, "shut"), PgpKeys.Sender, vault.For);

        var message = PgpKeys.Encrypted(PgpKeys.Sender, PgpKeys.Content());
        var locked = PgpDecryption.Open(message, shut, TestContext.Current.CancellationToken);

        Assert.Equal(DecryptionState.Locked, locked.State);
        Assert.Null(locked.Content);
        Assert.Equal(PgpKeys.Sender.Address, Assert.Single(vault.Wanted).Address);

        // Answered, and the same message opens — which is the whole of what the button does.
        vault.Remember(vault.Wanted[0].KeyId, PgpKeys.Passphrase);
        var opened = PgpDecryption.Open(message, shut, TestContext.Current.CancellationToken);
        Assert.True(opened.State == DecryptionState.Opened, opened.Detail);

        // Whereas a message to somebody else asks for nothing, there being nothing to ask about.
        vault.Clear();
        using var stranger = PgpKeys.Ring(Path.Combine(_root, "stranger"), PgpKeys.Other, vault.For);
        var absent = PgpDecryption.Open(message, stranger, TestContext.Current.CancellationToken);

        Assert.Equal(DecryptionState.Locked, absent.State);
        Assert.Empty(vault.Wanted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
