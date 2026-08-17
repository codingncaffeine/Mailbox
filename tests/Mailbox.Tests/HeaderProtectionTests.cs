using System.Text;
using Mailbox.Rendering;
using Mailbox.Security;
using Mailbox.Security.OpenPgp;
using Mailbox.Security.Smime;
using MimeKit;
using MimeKit.Cryptography;

namespace Mailbox.Tests;

/// <summary>
/// Header protection, RFC 9788 — written, read back, and read against the RFC's own bytes.
/// </summary>
/// <remarks>
/// Two kinds of test, and both are needed. The round trips go out through this application's writer
/// and back in through its reader, which is what proves the pair lands together: a client that
/// protects header fields nobody can unprotect has shipped half a feature. The fixtures are lifted
/// from RFC 9788's own appendices — a payload some other implementation produced, byte for byte — and
/// they are what proves the reader is reading the standard rather than its own output.
/// </remarks>
public class HeaderProtectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mailbox-hp-" + Guid.NewGuid().ToString("n"));

    // ---- What goes out ---------------------------------------------------------------------------

    [Fact]
    public void ASignedMessageCarriesItsOwnHeaderFieldsAndHidesNothing()
    {
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);
        var message = Message();

        var report = MessageProtection.Apply(
            message, Protection.Sign, null, mine, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var arrived = PgpKeys.Reload(message);

        // Signing hides nothing, so the outer header section is exactly what it was.
        Assert.Equal("The figures", arrived.Subject);

        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(arrived, arrived.Body!, encrypted: false));

        Assert.Equal(HeaderProtectionIntent.Clear, carried.Intent);
        Assert.True(carried.Stated);
        Assert.Equal("The figures", carried.Value("Subject"));
        Assert.Equal(PgpKeys.Reader.Address, Address(carried.Value("From")));

        // §2.2: HP-Outer is for encrypted messages. A signed-only payload records none, and nothing
        // in it is confidential — the fields are visible the whole way and merely cannot be altered.
        Assert.Empty(carried.Outer);
        Assert.False(carried.Confidential("Subject"));
        Assert.Equal(HeaderFieldProtection.SignedOnly, carried.ProtectionOf("Subject", signatureHeld: true));

        // And the signature still holds over a payload that now has a header section of its own.
        var signature = PgpVerification.Verify(arrived, mine);
        Assert.True(signature.State == SignatureState.Valid, $"{signature.State}: {signature.Detail}");
    }

    [Fact]
    public void AnEncryptedMessageSaysNothingAboutItsSubjectOnTheOutside()
    {
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);
        using var theirs = Ring("theirs", PgpKeys.Other, PgpKeys.Reader);

        var message = Message();
        message.Headers.Add("Keywords", "Contract, Urgent");

        var report = MessageProtection.Apply(
            message, Protection.Sign | Protection.Encrypt, null, mine, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var arrived = PgpKeys.Reload(message);

        // What anything between here and there sees: an obscured subject, no keywords at all, and
        // the addressing left alone so the message is still deliverable.
        Assert.Equal("[...]", arrived.Subject);
        Assert.Null(arrived.Headers["Keywords"]);
        Assert.Equal(PgpKeys.Reader.Address, arrived.From.Mailboxes.First().Address);
        Assert.Equal(PgpKeys.Other.Address, arrived.To.Mailboxes.First().Address);

        var opened = PgpDecryption.Open(arrived, theirs, TestContext.Current.CancellationToken);
        Assert.True(opened.State == DecryptionState.Opened, $"{opened.State}: {opened.Detail}");

        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(arrived, opened.Content!, encrypted: true));

        Assert.Equal(HeaderProtectionIntent.Cipher, carried.Intent);
        Assert.Equal("The figures", carried.Value("Subject"));
        Assert.Equal("Contract, Urgent", carried.Value("Keywords"));

        // The two ways a field is kept: replaced outside, and absent from outside.
        Assert.True(carried.Confidential("Subject"));
        Assert.True(carried.Confidential("Keywords"));

        // And the ones that were not kept, which is most of a header section — copying To and Cc
        // unaltered is what keeps the message deliverable, and saying so is what stops a reply from
        // treating them as secrets.
        Assert.False(carried.Confidential("From"));
        Assert.False(carried.Confidential("To"));

        Assert.Equal(
            HeaderFieldProtection.SignedAndEncrypted,
            carried.ProtectionOf("Subject", signatureHeld: true));
        Assert.Equal(
            HeaderFieldProtection.SignedOnly, carried.ProtectionOf("To", signatureHeld: true));

        // §4.3.1's note: a signature that does not hold lowers the answer rather than raising a
        // second alarm about it.
        Assert.Equal(
            HeaderFieldProtection.EncryptedOnly,
            carried.ProtectionOf("Subject", signatureHeld: false));
    }

    [Fact]
    public void BccIsNeverCopiedInsideWhereEveryRecipientCouldReadIt()
    {
        // §11.4's choice, made the private way. The message is encrypted to the Bcc'd recipient —
        // they can open it — and no copy of the field goes anywhere the others can see.
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other, PgpKeys.Sender);
        using var theirs = Ring("theirs", PgpKeys.Other, PgpKeys.Reader);

        var message = Message();
        message.Bcc.Add(new MailboxAddress(string.Empty, PgpKeys.Sender.Address));

        Assert.Equal(
            ProtectionState.Applied,
            MessageProtection.Apply(
                message, Protection.Encrypt, null, mine, TestContext.Current.CancellationToken).State);

        var arrived = PgpKeys.Reload(message);
        var opened = PgpDecryption.Open(arrived, theirs, TestContext.Current.CancellationToken);
        Assert.Equal(DecryptionState.Opened, opened.State);

        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(arrived, opened.Content!, encrypted: true));

        Assert.Null(carried.Value("Bcc"));
        Assert.DoesNotContain("Bcc", carried.Outer.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHiddenFieldsAreWrittenIntoTheBodyForAClientThatKnowsNoneOfThis()
    {
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);
        using var theirs = Ring("theirs", PgpKeys.Other, PgpKeys.Reader);

        var message = Message();
        Assert.Equal(
            ProtectionState.Applied,
            MessageProtection.Apply(
                message, Protection.Encrypt, null, mine, TestContext.Current.CancellationToken).State);

        var opened = PgpDecryption.Open(
            PgpKeys.Reload(message), theirs, TestContext.Current.CancellationToken);

        var payload = Assert.IsType<TextPart>(opened.Content);
        Assert.Equal("1", payload.ContentType.Parameters[HeaderProtection.LegacyDisplayParameter]);

        // What a decryption-capable client that has never heard of RFC 9788 shows its reader: the
        // subject, above the message, because the header section it can see says [...].
        Assert.StartsWith("Subject: The figures\n\n", payload.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        // And what this one shows: the message, with the courtesy taken back out (§4.5.3, a MUST).
        Assert.True(HeaderProtection.CarriesLegacyDisplay(payload));
        HeaderProtection.HideLegacyDisplay(payload);

        Assert.Equal("The quiet part.\n", payload.Text.ReplaceLineEndings("\n"));
        Assert.Null(payload.ContentType.Parameters[HeaderProtection.LegacyDisplayParameter]);
    }

    [Fact]
    public void TheHtmlHalfGetsADivAndTheSanitizerDropsIt()
    {
        using var mine = Ring("mine", PgpKeys.Reader, PgpKeys.Other);

        var message = Message();
        message.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = "The quiet part.\n" },
            new TextPart("html") { Text = "<html><head></head><body>\n<p>The quiet part.</p>\n</body></html>" },
        };

        Assert.Equal(
            ProtectionState.Applied,
            MessageProtection.Apply(
                message, Protection.Encrypt, null, mine, TestContext.Current.CancellationToken).State);

        var opened = PgpDecryption.Open(
            PgpKeys.Reload(message), mine, TestContext.Current.CancellationToken);

        var alternative = Assert.IsType<MultipartAlternative>(opened.Content);
        var html = alternative.OfType<TextPart>().Single(p => p.IsHtml);

        Assert.Equal("1", html.ContentType.Parameters[HeaderProtection.LegacyDisplayParameter]);
        Assert.Contains(
            "<div class=\"" + HeaderProtection.LegacyDisplayClass + "\">", html.Text, StringComparison.Ordinal);

        // Both halves say the same thing, which is the rule for a multipart/alternative: every child
        // of one is a rendering of the same message, so every child is a main body part.
        var plain = alternative.OfType<TextPart>().Single(p => p.IsPlain);
        Assert.Contains("Subject: The figures", plain.Text, StringComparison.Ordinal);

        // The div is dropped where markup is dropped by name, and only for a document that came out
        // of an encryption layer.
        var hidden = MessageRenderer.RenderHtml(
            html.Text, null, new RenderOptions { Fragment = true, HideLegacyDisplay = true });

        Assert.DoesNotContain("Subject:", hidden.Html, StringComparison.Ordinal);
        Assert.Contains("The quiet part.", hidden.Html, StringComparison.Ordinal);

        var shown = MessageRenderer.RenderHtml(
            html.Text, null, new RenderOptions { Fragment = true });

        Assert.Contains("Subject:", shown.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void SmimeCarriesTheSameFieldsInItsOwnShapes()
    {
        using var context = new TemporarySecureMimeContext();
        var me = SmimeKeys.Generate("work@example.net").Hold(context).Trust(context);
        var them = SmimeKeys.Generate("b.other@example.net").Hold(context).Trust(context);

        var message = Message(from: me.Address, to: them.Address);
        var report = MessageProtection.Apply(
            message, Protection.Sign | Protection.Encrypt, context, null, TestContext.Current.CancellationToken);

        Assert.True(report.State == ProtectionState.Applied, report.Detail);

        var arrived = PgpKeys.Reload(message);
        Assert.Equal("[...]", arrived.Subject);

        var opened = SmimeDecryption.Open(arrived, context);
        Assert.True(opened.State == DecryptionState.Opened, $"{opened.State}: {opened.Detail}");

        // The payload is under the signature, which is under the encryption: the reader has to walk
        // down to it rather than read the first thing it finds.
        Assert.IsType<MultipartSigned>(opened.Content);

        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(arrived, opened.Content!, encrypted: true));

        Assert.Equal(HeaderProtectionIntent.Cipher, carried.Intent);
        Assert.Equal("The figures", carried.Value("Subject"));
        Assert.True(carried.Confidential("Subject"));
        Assert.False(carried.Confidential("From"));
    }

    [Fact]
    public void AMessageWhoseCryptographyRefusedIsLeftExactlyAsItWas()
    {
        // The reason the payload is a copy and the outer header section is a plan. A locked key is
        // the ordinary refusal — the reader is asked for the passphrase and the whole thing runs
        // again — and a message half-covered by the first attempt would go out signed twice over, or
        // in the clear with its subject already replaced by [...].
        var vault = new PassphraseVault();
        using var mine = Ring("mine", PgpKeys.Sender, vault.For, PgpKeys.Other);

        var message = Message(from: PgpKeys.Sender.Address, to: PgpKeys.Other.Address);
        var report = MessageProtection.Apply(
            message, Protection.Sign | Protection.Encrypt, null, mine, TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionState.Locked, report.State);

        Assert.Equal("The figures", message.Subject);
        var body = Assert.IsType<TextPart>(message.Body);
        Assert.Null(body.ContentType.Parameters[HeaderProtection.Parameter]);
        Assert.Null(body.Headers["Subject"]);
        Assert.DoesNotContain(HeaderProtection.OuterField, body.Headers.Select(h => h.Field));
    }

    [Fact]
    public void TheHpOuterLinesAreWrittenTheWayTheRfcWritesThem()
    {
        // The wire form, on its own, with no cryptography in the way — a reader at the other end
        // parses text, not this application's objects.
        var message = Message();
        message.Headers.Add("Comments", "Not for the file");

        var plan = HeaderProtection.Cover(message, message.Body!, encrypting: true);
        var wire = Wire(plan.Payload);

        Assert.Contains("HP-Outer: Subject: [...]", wire, StringComparison.Ordinal);
        Assert.Contains("HP-Outer: From: " + PgpKeys.Reader.Address, wire, StringComparison.Ordinal);
        Assert.Contains("Subject: The figures", wire, StringComparison.Ordinal);

        // A removal is recorded by there being no line for it at all (§2.2), which is the one place
        // the format says something by silence.
        Assert.DoesNotContain("HP-Outer: Comments", wire, StringComparison.Ordinal);
        Assert.Contains("Comments: Not for the file", wire, StringComparison.Ordinal);

        Assert.Equal(["Subject", "Comments"], plan.Confidential);

        // And the message itself is untouched until the plan is applied to it.
        Assert.Equal("The figures", message.Subject);
        plan.ApplyTo(message);
        Assert.Equal("[...]", message.Subject);
        Assert.Null(message.Headers["Comments"]);
    }

    // ---- The RFC's own bytes ---------------------------------------------------------------------

    /// <summary>
    /// RFC 9788 Appendix C.3.1.2 — the payload of a signed-and-encrypted S/MIME message under
    /// <c>hcp_baseline</c>, as the document prints it once both layers are off.
    /// </summary>
    private const string RfcPayload =
        """
        MIME-Version: 1.0
        Content-Transfer-Encoding: 7bit
        Subject: smime-signed-enc-hp-baseline
        Message-ID: <smime-signed-enc-hp-baseline@example>
        From: Alice <alice@smime.example>
        To: Bob <bob@smime.example>
        Date: Sat, 20 Feb 2021 10:09:02 -0500
        User-Agent: Sample MUA Version 1.0
        HP-Outer: Subject: [...]
        HP-Outer: Message-ID: <smime-signed-enc-hp-baseline@example>
        HP-Outer: From: Alice <alice@smime.example>
        HP-Outer: To: Bob <bob@smime.example>
        HP-Outer: Date: Sat, 20 Feb 2021 10:09:02 -0500
        HP-Outer: User-Agent: Sample MUA Version 1.0
        Content-Type: text/plain; charset="utf-8"; hp="cipher"

        This is the
        smime-signed-enc-hp-baseline
        message.

        """;

    /// <summary>RFC 9788 Appendix E.1 — a text/plain payload carrying a legacy display element.</summary>
    private const string RfcLegacyText =
        """
        Date: Fri, 21 Jan 2022 20:40:48 -0500
        From: Alice <alice@example.net>
        To: Bob <bob@example.net>
        Subject: Dinner plans
        Message-ID: <text-plain-legacy-display@lhp.example>
        MIME-Version: 1.0
        Content-Type: text/plain; charset="us-ascii"; hp-legacy-display="1";
         hp="cipher"
        HP-Outer: Date: Fri, 21 Jan 2022 20:40:48 -0500
        HP-Outer: From: Alice <alice@example.net>
        HP-Outer: To: Bob <bob@example.net>
        HP-Outer: Subject: [...]
        HP-Outer: Message-ID: <text-plain-legacy-display@lhp.example>

        Subject: Dinner plans

        Let's meet at Rama's Roti Shop at 8pm and go to the park
        from there.

        """;

    /// <summary>RFC 9788 Appendix E.2 — the same message as HTML.</summary>
    private const string RfcLegacyHtml =
        """
        Date: Fri, 21 Jan 2022 20:40:48 -0500
        From: Alice <alice@example.net>
        To: Bob <bob@example.net>
        Subject: Dinner plans
        Message-ID: <text-html-legacy-display@lhp.example>
        MIME-Version: 1.0
        Content-Type: text/html; charset="us-ascii"; hp-legacy-display="1";
         hp="cipher"
        HP-Outer: Date: Fri, 21 Jan 2022 20:40:48 -0500
        HP-Outer: From: Alice <alice@example.net>
        HP-Outer: To: Bob <bob@example.net>
        HP-Outer: Subject: [...]
        HP-Outer: Message-ID: <text-html-legacy-display@lhp.example>

        <html><head><title></title></head><body>
        <div class="header-protection-legacy-display">
        <pre>Subject: Dinner plans</pre>
        </div>
        <p>
        Let's meet at Rama's Roti Shop at 8pm and go to the park
        from there.
        </p>
        </body>
        </html>

        """;

    [Fact]
    public void TheRfcsOwnPayloadReadsAsTheRfcSaysItShould()
    {
        // Somebody else's implementation, byte for byte out of the standard. The envelope is the one
        // the appendix describes: the same message with its subject obscured.
        var envelope = new MimeMessage { Subject = "[...]" };
        envelope.From.Add(MailboxAddress.Parse("Alice <alice@smime.example>"));
        envelope.To.Add(MailboxAddress.Parse("Bob <bob@smime.example>"));

        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(envelope, Entity(RfcPayload), encrypted: true));

        Assert.True(carried.Stated);
        Assert.Equal(HeaderProtectionIntent.Cipher, carried.Intent);
        Assert.Equal("smime-signed-enc-hp-baseline", carried.Value("Subject"));
        Assert.Equal("Alice <alice@smime.example>", carried.Value("From"));
        Assert.Equal("Sample MUA Version 1.0", carried.Value("User-Agent"));

        // Six HP-Outer lines, one of which says something different from the field beside it.
        Assert.Equal(6, carried.Outer.Count);
        Assert.Equal(["Subject"], carried.ConfidentialFields);

        Assert.Equal(
            HeaderFieldProtection.SignedAndEncrypted,
            carried.ProtectionOf("Subject", signatureHeld: true));
        Assert.Equal(
            HeaderFieldProtection.SignedOnly,
            carried.ProtectionOf("Message-ID", signatureHeld: true));

        // A structural field is not a protected one: it describes the part it is attached to.
        Assert.Null(carried.Value("Content-Type"));
        Assert.Null(carried.Value("MIME-Version"));
        Assert.False(carried.FromMismatch(envelope));
    }

    [Fact]
    public void TheRfcsLegacyDisplayExamplesRenderAsTheRfcSaysTheyShould()
    {
        var text = Entity(RfcLegacyText);
        Assert.True(HeaderProtection.CarriesLegacyDisplay(text));

        HeaderProtection.HideLegacyDisplay(text);

        // §4.5.3.2, and the appendix prints the answer: the two lines of the message and nothing
        // above them.
        Assert.Equal(
            "Let's meet at Rama's Roti Shop at 8pm and go to the park\nfrom there.\n",
            Assert.IsType<TextPart>(text).Text.ReplaceLineEndings("\n"));

        var html = Entity(RfcLegacyHtml);
        Assert.True(HeaderProtection.CarriesLegacyDisplay(html));

        var rendered = MessageRenderer.RenderHtml(
            Assert.IsType<TextPart>(html).Text,
            null,
            new RenderOptions { Fragment = true, HideLegacyDisplay = true });

        Assert.DoesNotContain("Dinner plans", rendered.Html, StringComparison.Ordinal);
        // The apostrophe comes back as an entity, which is what the sanitizer does to all text.
        Assert.Contains("Roti Shop", rendered.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchemeBeforeThisOneIsReadAndItsIntentIsMarkedAsAGuess()
    {
        // What Thunderbird and Enigmail have been sending for years: the same fields in the same
        // place, an older word for the parameter, and no protected record of what was left outside —
        // so confidentiality has to be inferred from the envelope, and §4.10 is clear that the
        // inference rests on nothing an intervening agent could not have arranged.
        var payload = Entity(
            """
            Subject: The figures
            From: Alice <alice@example.net>
            To: Bob <bob@example.net>
            Content-Type: text/plain; charset="utf-8"; protected-headers="v1"

            The quiet part.

            """);

        var envelope = new MimeMessage { Subject = "..." };
        envelope.From.Add(MailboxAddress.Parse("Alice <alice@example.net>"));
        envelope.To.Add(MailboxAddress.Parse("Bob <bob@example.net>"));

        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(envelope, payload, encrypted: true));

        Assert.False(carried.Stated);
        Assert.Equal("The figures", carried.Value("Subject"));
        Assert.True(carried.Confidential("Subject"));
        Assert.False(carried.Confidential("To"));
    }

    [Fact]
    public void TheWrappedSchemeRfc8551DescribedIsReadAndItsBodyIsWhatGetsRendered()
    {
        // §4.10: identified precisely, rendered from the wrapped message's body rather than from the
        // payload, and never generated.
        var payload = Entity(
            """
            Content-Type: message/rfc822

            Subject: The figures
            From: Alice <alice@example.net>
            To: Bob <bob@example.net>
            Content-Type: text/plain; charset="utf-8"

            The quiet part.

            """);

        var envelope = new MimeMessage { Subject = "[...]" };
        envelope.From.Add(MailboxAddress.Parse("Alice <alice@example.net>"));

        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(envelope, payload, encrypted: true));

        Assert.False(carried.Stated);
        Assert.Equal("The figures", carried.Value("Subject"));
        Assert.True(carried.Confidential("Subject"));

        var rendered = Assert.IsType<TextPart>(carried.Rendered);
        Assert.Equal("The quiet part.\n", rendered.Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void AMessageWithNoneOfThisIsNotReadAsHavingIt()
    {
        var envelope = new MimeMessage { Subject = "The figures" };
        var body = new TextPart("plain") { Text = "The quiet part.\n" };

        Assert.Null(HeaderProtection.Read(envelope, body, encrypted: false));

        // Nor is the same markup in ordinary mail a legacy display element: the parameter means
        // something only inside a cryptographic payload, and this is how that stays true.
        Assert.False(HeaderProtection.CarriesLegacyDisplay(body));
    }

    // ---- A From that does not agree with itself --------------------------------------------------

    [Fact]
    public void AProtectedFromThatDisagreesWithTheEnvelopeIsAMismatch()
    {
        // §4.4, and §10.1 is the attack: a client that draws the protected From without checking it
        // against the one its transport authenticated has made header protection a way to make a
        // spoof look better than an ordinary one.
        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(Envelope("mallory@example.org"), Entity(FromPayload), encrypted: true));

        Assert.True(carried.FromMismatch(Envelope("mallory@example.org")));
        Assert.False(carried.FromMismatch(Envelope("alice@example.net")));

        // Case and A-label form are the same address, and reporting either as a spoof would make the
        // warning worthless (§4.4.5).
        Assert.False(carried.FromMismatch(Envelope("ALICE@Example.NET")));
    }

    private const string FromPayload =
        """
        Subject: The figures
        From: Alice <alice@example.net>
        Content-Type: text/plain; charset="utf-8"; hp="cipher"

        The quiet part.

        """;

    [Fact]
    public void ADomainWrittenInAnotherScriptIsTheSameDomainEitherWay()
    {
        var payload = Entity(
            """
            From: Alice <alice@münchen.example>
            Content-Type: text/plain; charset="utf-8"; hp="cipher"

            The quiet part.

            """);

        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(Envelope("alice@xn--mnchen-3ya.example"), payload, encrypted: true));

        Assert.False(carried.FromMismatch(Envelope("alice@xn--mnchen-3ya.example")));
        Assert.True(carried.FromMismatch(Envelope("alice@munchen.example")));
    }

    // ---- Answering one ---------------------------------------------------------------------------

    [Fact]
    public void AReplyIsAddressedFromInsideTheMessageAndNotFromItsOutside()
    {
        // §4.4.4 and §6.2, both MUSTs, and the attack is a replay: Mallory takes a copy of an
        // encrypted message and adds her own address to the outer Cc. A client that answers what it
        // can see has just encrypted the whole conversation to her, because it holds a key for her
        // and had no reason to think twice.
        var payload = Entity(
            """
            Subject: The figures
            From: Alice <alice@example.net>
            To: Bob <bob@example.net>
            Message-ID: <first@example.net>
            Content-Type: text/plain; charset="utf-8"; hp="cipher"

            The quiet part.

            """);

        var replayed = new MimeMessage { Subject = "[...]" };
        replayed.From.Add(MailboxAddress.Parse("Alice <alice@example.net>"));
        replayed.To.Add(MailboxAddress.Parse("Bob <bob@example.net>"));
        replayed.Cc.Add(MailboxAddress.Parse("Mallory <mallory@example.org>"));
        replayed.Body = new TextPart("plain") { Text = "unread ciphertext stands in for itself\n" };

        var carried = HeaderProtection.Read(replayed, payload, encrypted: true);
        var addressed = HeaderProtection.Addressed(replayed, carried, replayed.Body);

        Assert.Equal("The figures", addressed.Subject);
        Assert.Equal(["alice@example.net"], addressed.From.Mailboxes.Select(m => m.Address));
        Assert.Equal(["bob@example.net"], addressed.To.Mailboxes.Select(m => m.Address));
        Assert.Empty(addressed.Cc.Mailboxes);

        // Threading comes from inside too: an unprotected In-Reply-To is how a message is made to
        // appear as the answer to one it has nothing to do with — CVE-2024-49394.
        Assert.Equal("first@example.net", addressed.MessageId);

        // And the body is the caller's, which for a reply is the envelope's: what was decrypted does
        // not get quoted back out in the clear.
        Assert.Same(replayed.Body, addressed.Body);
    }

    [Fact]
    public void AMessageWithNoProtectionIsAddressedFromItsOwnHeaderSection()
    {
        var message = Message();
        message.Cc.Add(new MailboxAddress(string.Empty, "c.other@example.net"));

        var addressed = HeaderProtection.Addressed(message, null, message.Body);

        Assert.Equal("The figures", addressed.Subject);
        Assert.Equal(PgpKeys.Reader.Address, addressed.From.Mailboxes.First().Address);
        Assert.Equal(PgpKeys.Other.Address, addressed.To.Mailboxes.First().Address);
        Assert.Equal("c.other@example.net", addressed.Cc.Mailboxes.First().Address);
    }

    // ---- The policies ---------------------------------------------------------------------------

    [Fact]
    public void BaselineObscuresTheSubjectAndRemovesTheInformationalFields()
    {
        const HeaderConfidentiality baseline = HeaderConfidentiality.Baseline;

        Assert.Equal("[...]", baseline.Outside("Subject", "The figures"));
        Assert.Equal("[...]", baseline.Outside("subject", "The figures"));
        Assert.Null(baseline.Outside("Keywords", "Contract"));
        Assert.Null(baseline.Outside("Comments", "Not for the file"));

        // Everything else is left exactly as it is, addressing and dates included: this is the
        // conservative policy, and what it is conservative about is deliverability.
        Assert.Equal("Alice <alice@example.net>", baseline.Outside("From", "Alice <alice@example.net>"));
        Assert.Equal("Bob <bob@example.net>", baseline.Outside("To", "Bob <bob@example.net>"));

        Assert.True(baseline.Hides("Subject"));
        Assert.True(baseline.Hides("Keywords"));
        Assert.False(baseline.Hides("To"));
        Assert.False(baseline.Hides("Date"));
    }

    [Fact]
    public void ShyAlsoTakesTheNamesOffTheAddressesAndTheZoneOffTheDate()
    {
        const HeaderConfidentiality shy = HeaderConfidentiality.Shy;

        Assert.Equal("alice@example.net", shy.Outside("From", "Alice <alice@example.net>"));
        Assert.Equal(
            "bob@example.net, carol@example.net",
            shy.Outside("To", "Bob <bob@example.net>, Carol <carol@example.net>"));

        // §3.1.1: the address itself is never changed, only what is written around it.
        Assert.Contains("alice@example.net", shy.Outside("From", "Alice <alice@example.net>")!, StringComparison.Ordinal);

        Assert.Equal(
            "Sat, 20 Feb 2021 15:09:02 +0000", shy.Outside("Date", "Sat, 20 Feb 2021 10:09:02 -0500"));

        // A field it cannot parse is left alone rather than rewritten on a guess: an undeliverable
        // message is a worse outcome than a visible display name.
        Assert.Equal("not an address", shy.Outside("To", "not an address"));
        Assert.True(shy.Hides("Date"));
    }

    [Fact]
    public void NoConfidentialityIsAPolicyThatSaysSo()
    {
        const HeaderConfidentiality none = HeaderConfidentiality.NoConfidentiality;

        Assert.Equal("The figures", none.Outside("Subject", "The figures"));
        Assert.Equal("Contract", none.Outside("Keywords", "Contract"));
        Assert.False(none.Hides("Subject"));
    }

    [Fact]
    public void APolicyThatHidesNothingStillCoversTheHeaderFields()
    {
        // The state the RFC calls signed-only for every field: inside the signature, out in the open.
        var message = Message();
        var plan = HeaderProtection.Cover(
            message, message.Body!, encrypting: true, HeaderConfidentiality.NoConfidentiality);

        Assert.Empty(plan.Confidential);

        var carried = Assert.IsType<ProtectedHeaders>(
            HeaderProtection.Read(message, plan.Payload, encrypted: true));

        Assert.Equal("The figures", carried.Value("Subject"));
        Assert.False(carried.Confidential("Subject"));
        Assert.Equal(
            HeaderFieldProtection.SignedOnly, carried.ProtectionOf("Subject", signatureHeld: true));
    }

    // ---- The material ---------------------------------------------------------------------------

    private PgpContext Ring(string name, PgpIdentity mine, params PgpIdentity[] theirs)
        => PgpKeys.Ring(Path.Combine(_root, name), mine, null, theirs);

    private PgpContext Ring(
        string name, PgpIdentity mine, Func<Org.BouncyCastle.Bcpg.OpenPgp.PgpSecretKey, string?> passphrase,
        params PgpIdentity[] theirs)
        => PgpKeys.Ring(Path.Combine(_root, name), mine, passphrase, theirs);

    private static MimeMessage Message(string? from = null, string? to = null)
    {
        var message = new MimeMessage { Subject = "The figures", Date = DateTimeOffset.Now };
        message.From.Add(new MailboxAddress(string.Empty, from ?? PgpKeys.Reader.Address));
        message.To.Add(new MailboxAddress(string.Empty, to ?? PgpKeys.Other.Address));
        message.Body = new TextPart("plain") { Text = "The quiet part.\n" };
        return message;
    }

    private static MimeMessage Envelope(string from)
    {
        var message = new MimeMessage { Subject = "[...]" };
        message.From.Add(new MailboxAddress(string.Empty, from));
        return message;
    }

    /// <summary>One MIME entity from text, which is how a fixture out of an RFC is loaded.</summary>
    private static MimeEntity Entity(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n")));
        return MimeEntity.Load(stream);
    }

    private static string Wire(MimeEntity entity)
    {
        using var stream = new MemoryStream();
        entity.WriteTo(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? Address(string? value)
        => value is not null && InternetAddressList.TryParse(value, out var list)
            ? list.Mailboxes.FirstOrDefault()?.Address
            : null;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
