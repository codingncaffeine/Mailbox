using System.Text;
using Mailbox.Core.Diagnostics;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Utils;

namespace Mailbox.Security;

/// <summary>
/// Header protection, RFC 9788: the header fields inside the cryptography rather than beside it.
/// </summary>
/// <remarks>
/// The last of §19's eight blockers, and the one that is a pair — what this application sends and
/// what it reads have to land together, because a client that writes a message it cannot itself
/// unwrap has built half a feature. Three bugs in the same family are what it closes:
/// <b>CVE-2024-49393</b>, <b>-49394</b> and <b>-49395</b>, all Mutt and NeoMutt, all 2024 — an
/// unsigned <c>To</c> and <c>Cc</c>, an unprotected <c>In-Reply-To</c> that puts a reply under a
/// message it does not answer, and a <c>Bcc</c> that leaks.
/// <para>
/// The mechanism is one parameter and one header field. The <b>cryptographic payload</b> — the
/// innermost part under the signature and the encryption — carries a copy of the message's own
/// header fields in its MIME header section, and says so with <c>hp="clear"</c> or
/// <c>hp="cipher"</c> on its Content-Type. For an encrypted message the outer header section is then
/// reduced by a <see cref="HeaderConfidentiality"/> policy, and every field left outside is recorded
/// inside as an <c>HP-Outer</c> line, so a reader can tell what the composer meant to hide from what
/// merely happens to be missing.
/// </para>
/// <para>
/// <b>What is not sent:</b> inline PGP, and the older scheme RFC 8551 described (a whole
/// <c>message/rfc822</c> as the payload), which §4.10 says an MUA must not generate. Both are read —
/// see <see cref="Read"/> — because refusing to understand deployed mail is not a security property.
/// </para>
/// </remarks>
public static class HeaderProtection
{
    /// <summary>The Content-Type parameter that says the payload carries protected header fields.</summary>
    public const string Parameter = "hp";

    /// <summary>Signed with header protection, nothing meant to be confidential.</summary>
    public const string Clear = "clear";

    /// <summary>Signed with header protection and encrypted, with some of it hidden.</summary>
    public const string Cipher = "cipher";

    /// <summary>The header field recording what the composer left outside the encryption.</summary>
    public const string OuterField = "HP-Outer";

    /// <summary>The Content-Type parameter marking a part that carries a legacy display element.</summary>
    public const string LegacyDisplayParameter = "hp-legacy-display";

    /// <summary>The class on the <c>div</c> a legacy display element lives in, in an HTML part.</summary>
    public const string LegacyDisplayClass = "header-protection-legacy-display";

    /// <summary>
    /// The parameter the scheme that came before RFC 9788 used. Read, never written.
    /// </summary>
    /// <remarks>
    /// Autocrypt's "protected headers" — what Thunderbird, Enigmail and several others have been
    /// sending for years, and the scheme RFC 9788 grew out of. It is the same shape with a different
    /// word for the parameter and no <c>HP-Outer</c>, so it is read as header protection whose intent
    /// is inferred from the message rather than stated by its composer (§4.11).
    /// </remarks>
    private const string LegacyParameter = "protected-headers";

    /// <summary>The one value that parameter took.</summary>
    private const string LegacyVersion = "v1";

    /// <summary>
    /// The user-facing fields, in the order a legacy display element lists them.
    /// </summary>
    /// <remarks>
    /// RFC 9787 §1.1.2's list, with the subject first because it is the one a reader is looking for
    /// and the one every policy hides. A field not named here is never written into the body of a
    /// message for the benefit of a client that cannot read it properly: the element is a courtesy,
    /// and a courtesy that reprints a message's whole header section is a nuisance.
    /// </remarks>
    private static readonly string[] UserFacing =
    [
        "Subject", "From", "To", "Cc", "Date", "Reply-To", "Followup-To", "Sender",
        "Resent-From", "Resent-To", "Resent-Cc", "Resent-Date", "Resent-Sender",
    ];

    // ---- The way out ---------------------------------------------------------------------------

    /// <summary>
    /// Builds the cryptographic payload for a message: its own header fields, inside it.
    /// </summary>
    /// <remarks>
    /// <b>Nothing the message holds is touched.</b> The payload is a copy, and what the outer header
    /// section has to become is handed back as a plan rather than done, because the cryptography can
    /// still fail after this — a locked key, most often, which the caller answers and then runs the
    /// whole thing again. A message half-covered by a first attempt would be signed twice over on the
    /// second, and one whose subject had already been replaced would go out as <c>[...]</c> if the
    /// reader gave up and sent it in the clear.
    /// </remarks>
    /// <param name="message">The message as the writer built it. Read, never written.</param>
    /// <param name="body">What the writer wrote, which becomes the payload's content.</param>
    /// <param name="encrypting">
    /// Whether the cryptography about to be applied includes encryption. §2.1.1 makes this a MUST in
    /// both directions: <c>hp="cipher"</c> may not be written on a message that is not encrypted, and
    /// may not be omitted from one that is.
    /// </param>
    public static HeaderProtectionPlan Cover(
        MimeMessage message,
        MimeEntity body,
        bool encrypting,
        HeaderConfidentiality policy = HeaderConfidentiality.Baseline,
        bool legacy = true)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(body);

        var payload = Copy(body);
        var carried = Carried(message);
        var rewrites = new List<HeaderRewrite>();

        // A body that arrived claiming protection of its own is not protected, it is a body with
        // header fields drawn on it. Whatever this builds must be the only claim in the payload.
        payload.Headers.RemoveAll(OuterField);
        payload.ContentType.Parameters.Remove(Parameter);

        // The legacy display element first: it is content, and it goes in before the header fields
        // it is a copy of, so a part that ends up carrying both is assembled in one place.
        if (encrypting && legacy) Decorate(payload, Shown(carried, policy));

        foreach (var field in carried)
        {
            payload.Headers.Add(field.Name, field.Value);

            if (!encrypting) continue;

            var outside = policy.Outside(field.Name, field.Value);
            rewrites.Add(new HeaderRewrite(field.Name, field.Value, outside));

            // A field with no HP-Outer line is one the composer is saying was removed from the
            // outside altogether (§2.2), so a removal is recorded by writing nothing rather than by
            // writing something that means nothing.
            if (outside is not null) payload.Headers.Add(OuterField, field.Name + ": " + Ascii(outside));
        }

        payload.ContentType.Parameters[Parameter] = encrypting ? Cipher : Clear;
        return new HeaderProtectionPlan(payload, rewrites);
    }

    /// <summary>
    /// The message's own header fields, as they will go on the wire.
    /// </summary>
    /// <remarks>
    /// Everything but three kinds of field. <b>Structural</b> ones (<c>MIME-Version</c> and every
    /// <c>Content-</c>) belong to the part they are attached to and would break the payload's own
    /// framing (RFC 9787 §1.1.1). <b>An HP-Outer</b> that arrived on the outside of a message is not
    /// a claim anybody may make from there, and copying it in would forge one. And <b>Bcc</b>, which
    /// is the interesting one: §11.4 offers the choice and calls leaving it out the most
    /// privacy-preserving answer, and it is the right one here because the sender hides Bcc on the
    /// way to the transport anyway — so a copy inside the encryption would be visible to every
    /// recipient of a message from which it is otherwise absent. A Bcc'd reader can still tell what
    /// they are, their own address appearing in no To or Cc.
    /// </remarks>
    private static List<ProtectedField> Carried(MimeMessage message)
    {
        var fields = new List<ProtectedField>();

        foreach (var header in message.Headers)
        {
            if (header.Id is HeaderId.MimeVersion or HeaderId.Bcc or HeaderId.ResentBcc) continue;
            if (header.Field.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            if (header.Field.Equals(OuterField, StringComparison.OrdinalIgnoreCase)) continue;

            fields.Add(new ProtectedField(header.Field, Wire(header)));
        }

        return fields;
    }

    /// <summary>The user-facing fields this policy hides, in the order they are shown.</summary>
    private static List<ProtectedField> Shown(
        IReadOnlyList<ProtectedField> carried, HeaderConfidentiality policy)
    {
        var shown = new List<ProtectedField>();

        foreach (var name in UserFacing)
        {
            foreach (var field in carried)
            {
                if (!field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (policy.Outside(field.Name, field.Value) is { } outside
                    && string.Equals(outside, field.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                shown.Add(field);
            }
        }

        return shown;
    }

    /// <summary>
    /// Writes the hidden fields into the body, for a client that can decrypt and knows none of this.
    /// </summary>
    /// <remarks>
    /// §5.2.2 and §5.2.3. The element goes only into a <b>main body part</b> — never into an
    /// attachment, which has to arrive as it was sent — and only into text, no legacy client having
    /// ever drawn anything else as the body of a message.
    /// </remarks>
    private static void Decorate(MimeEntity payload, List<ProtectedField> shown)
    {
        if (shown.Count == 0) return;

        foreach (var part in MainBodyParts(payload))
        {
            // Twice would show it twice. A part already marked is one something else has decorated,
            // and adding to it is not this method's business.
            if (part.ContentType.Parameters[LegacyDisplayParameter] is not null) continue;

            var lines = new StringBuilder();
            foreach (var field in shown) lines.Append(field.Name).Append(": ").Append(field.Value).Append('\n');

            if (part.IsHtml)
            {
                part.Text = Injected(part.Text, lines.ToString());
            }
            else
            {
                part.Text = lines.Append('\n').Append(part.Text).ToString();
            }

            part.ContentType.Parameters[LegacyDisplayParameter] = "1";
        }
    }

    /// <summary>The element as the first thing in an HTML body, per §5.2.3.1.</summary>
    /// <remarks>
    /// A tag search rather than a parse, and it is enough for what it has to do: the document is
    /// this application's own — <c>EmailHtml</c> wrote it a moment ago — so the opening
    /// <c>&lt;body&gt;</c> is where the RFC's step 4 says to look. A document with no body element
    /// gets the element in front of it, which is where a browser would put the content anyway.
    /// </remarks>
    private static string Injected(string html, string lines)
    {
        var element = "<div class=\"" + LegacyDisplayClass + "\">\n<pre>"
            + Escape(lines.TrimEnd('\n')) + "</pre></div>\n";

        var body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (body < 0) return element + html;

        var open = html.IndexOf('>', body);
        return open < 0 ? element + html : html[..(open + 1)] + "\n" + element + html[(open + 1)..];
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// The parts of a payload a reader would see as the message.
    /// </summary>
    /// <remarks>
    /// RFC 9787 §7.1: the first child of every multipart, except a <c>multipart/alternative</c>,
    /// where every child is a rendering of the same thing and so every child is a main body part.
    /// Anything marked as an attachment is not one however it is reached.
    /// </remarks>
    private static IEnumerable<TextPart> MainBodyParts(MimeEntity entity)
    {
        switch (entity)
        {
            case MultipartAlternative alternative:
                foreach (var child in alternative)
                {
                    foreach (var part in MainBodyParts(child)) yield return part;
                }

                break;

            case Multipart multipart when multipart.Count > 0:
                foreach (var part in MainBodyParts(multipart[0])) yield return part;
                break;

            case TextPart text when (text.IsPlain || text.IsHtml)
                && text.ContentDisposition?.IsAttachment != true:
                yield return text;
                break;

            default:
                break;
        }
    }

    /// <summary>An independent copy of an entity, so covering one leaves the original alone.</summary>
    private static MimeEntity Copy(MimeEntity entity)
    {
        using var stream = new MemoryStream();
        entity.WriteTo(stream);
        stream.Position = 0;

        return MimeEntity.Load(stream);
    }

    /// <summary>
    /// A header field's value as it will appear on the wire, unfolded onto one line.
    /// </summary>
    /// <remarks>
    /// The wire form rather than the decoded one, because this is what gets signed and what an
    /// <c>HP-Outer</c> line has to be able to hold: a header field on the wire is ASCII, anything
    /// else in it having been encoded by whatever wrote it (RFC 2047). The decoded value is the
    /// fallback for a field that somehow is not, and the library encodes it again on the way out.
    /// </remarks>
    private static string Wire(Header header)
    {
        var raw = header.RawValue;

        foreach (var b in raw)
        {
            if (b > 0x7F) return Header.Unfold(header.Value).Trim();
        }

        return Header.Unfold(Encoding.ASCII.GetString(raw)).Trim();
    }

    /// <summary>
    /// A value fit for an <c>HP-Outer</c> line: ASCII, so the field name in front of it survives.
    /// </summary>
    /// <remarks>
    /// The line is <c>HP-Outer: Subject: what it said</c> — a field name, a colon and a value, all
    /// inside one header field's value. Handed something with a non-ASCII character in it, the
    /// library would encode the <em>whole</em> line as one encoded word, field name included, and a
    /// reader splitting on the first colon would find nothing there. So the value is encoded on its
    /// own, before it is put behind the name.
    /// </remarks>
    private static string Ascii(string value)
    {
        foreach (var c in value)
        {
            if (c > 0x7F)
            {
                return Encoding.ASCII.GetString(
                    Rfc2047.EncodeText(FormatOptions.Default, Encoding.UTF8, value));
            }
        }

        return value;
    }

    // ---- The way in ----------------------------------------------------------------------------

    /// <summary>
    /// Reads the header fields a protected message carries, or null when it carries none.
    /// </summary>
    /// <remarks>
    /// RFC 9788 §4.1 and §4.2.1. Three schemes are recognised and told apart, because what may be
    /// concluded from them differs: this document's own, which the composer stated; the parameter the
    /// scheme before it used; and the wrapped <c>message/rfc822</c> of RFC 8551, whose intent has to
    /// be guessed from the shape of the envelope. §4.10 is plain about what that guess is worth —
    /// an intervening transport agent could have wrapped a signed message in encryption and changed
    /// what a reader believes was confidential — so the two inferred kinds are marked as inferred.
    /// </remarks>
    /// <param name="envelope">The message as it arrived, which is what the outer header section is.</param>
    /// <param name="content">
    /// What came out of the cryptography: the decrypted entity, or the message's own body when it was
    /// signed and not encrypted.
    /// </param>
    /// <param name="encrypted">Whether an encryption layer was opened to get at that content.</param>
    public static ProtectedHeaders? Read(MimeMessage envelope, MimeEntity content, bool encrypted)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(content);

        if (Payload(content) is not { } payload) return null;

        var stated = payload.ContentType.Parameters[Parameter]?.Trim();

        if (Is(stated, Cipher)) return Stated(payload, HeaderProtectionIntent.Cipher);
        if (Is(stated, Clear)) return Stated(payload, HeaderProtectionIntent.Clear);

        var intent = encrypted ? HeaderProtectionIntent.Cipher : HeaderProtectionIntent.Clear;

        // The scheme that came before this one: the same header fields in the same place, under
        // another name, and with no protected record of what was left outside.
        if (Is(payload.ContentType.Parameters[LegacyParameter]?.Trim(), LegacyVersion))
        {
            return new ProtectedHeaders(
                intent, Stated: false, payload, Fields(payload.Headers), Outside(envelope, intent));
        }

        // RFC 8551's own scheme, which §4.10 says to identify precisely and never to generate: the
        // payload is one whole message, and the part to render is that message's body rather than
        // the payload itself. All four of §4.10.1's conditions, the fourth being that neither the
        // wrapper nor what it wraps says anything about hp — one that does is a message from some
        // other scheme again, and guessing at it is how a reader ends up trusting a guess.
        if (payload is MessagePart { Message: { } wrapped }
            && wrapped.Body is { } inner
            && !Layer(inner)
            && inner.ContentType.Parameters[Parameter] is null)
        {
            return new ProtectedHeaders(
                intent, Stated: false, inner, Fields(wrapped.Headers), Outside(envelope, intent));
        }

        return null;
    }

    /// <summary>Whether an entity is a cryptographic layer rather than content.</summary>
    private static bool Layer(MimeEntity entity)
        => entity is MultipartSigned or MultipartEncrypted or ApplicationPkcs7Mime;

    private static ProtectedHeaders Stated(MimeEntity payload, HeaderProtectionIntent intent)
    {
        var fields = new List<ProtectedField>();
        var outer = new List<ProtectedField>();

        foreach (var header in payload.Headers)
        {
            if (Structural(header)) continue;

            if (!header.Field.Equals(OuterField, StringComparison.OrdinalIgnoreCase))
            {
                fields.Add(new ProtectedField(header.Field, header.Value));
                continue;
            }

            // §2.2: only an encrypted message's payload may say anything about what was left
            // outside it. On a signed-only message the line is noise at best.
            if (intent == HeaderProtectionIntent.Cipher && Recorded(header.Value) is { } record)
            {
                outer.Add(record);
            }
        }

        return new ProtectedHeaders(intent, Stated: true, payload, fields, outer);
    }

    /// <summary>One <c>HP-Outer</c> line split at its first colon, per §4.2.1's step 4.i.a.</summary>
    private static ProtectedField? Recorded(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return null;

        var name = value[..colon].Trim();
        var rest = value[(colon + 1)..].TrimStart();

        return name.Length == 0 ? null : new ProtectedField(name, Decoded(rest));
    }

    /// <summary>
    /// The recorded value as the protected copy of it would read, so the two can be compared.
    /// </summary>
    /// <remarks>
    /// An <c>HP-Outer</c> line holds the wire form, encoded words and all, while the protected field
    /// beside it has already been decoded by the parser. Comparing the two without this would call
    /// every non-ASCII field confidential — the safe way to be wrong, and still wrong.
    /// </remarks>
    private static string Decoded(string value)
        => value.Contains("=?", StringComparison.Ordinal)
            ? Rfc2047.DecodeText(Encoding.ASCII.GetBytes(value))
            : value;

    /// <summary>The non-structural fields of a header section, in the order they appear.</summary>
    private static List<ProtectedField> Fields(HeaderList headers)
    {
        var fields = new List<ProtectedField>();

        foreach (var header in headers)
        {
            if (Structural(header)) continue;
            if (header.Field.Equals(OuterField, StringComparison.OrdinalIgnoreCase)) continue;

            fields.Add(new ProtectedField(header.Field, header.Value));
        }

        return fields;
    }

    /// <summary>
    /// What the message's actual outer header section says, for the schemes that record nothing.
    /// </summary>
    /// <remarks>
    /// §4.10.2's last adjustment. Confidentiality has to be inferred from the unprotected header
    /// section, which means an intervening agent that added a field could make the reader believe it
    /// was never confidential. Nothing can be done about that from here; what can be done is not
    /// pretending the two schemes are the same one, which is what <c>Stated</c> is for.
    /// </remarks>
    private static List<ProtectedField> Outside(MimeMessage envelope, HeaderProtectionIntent intent)
    {
        var fields = new List<ProtectedField>();
        if (intent != HeaderProtectionIntent.Cipher) return fields;

        foreach (var header in envelope.Headers)
        {
            if (Structural(header)) continue;
            fields.Add(new ProtectedField(header.Field, header.Value));
        }

        return fields;
    }

    private static bool Structural(Header header)
        => header.Id == HeaderId.MimeVersion
            || header.Field.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The cryptographic payload inside what the cryptography handed back.
    /// </summary>
    /// <remarks>
    /// The MIME layers, unwrapped: a <c>multipart/signed</c>'s content is its first part, and
    /// anything else is the payload already. Two shapes stop here rather than being guessed at — an
    /// unopened <c>multipart/encrypted</c>, which means a caller handed over something it had not
    /// decrypted, and S/MIME's opaque <c>signed-data</c>, which cannot be unwrapped without a
    /// certificate store. That second one is the same limitation the verifier states in the same
    /// words: a message signed that way is one Mailbox cannot check yet, and its header fields are
    /// out of reach for exactly the same reason.
    /// </remarks>
    private static MimeEntity? Payload(MimeEntity content) => content switch
    {
        MultipartEncrypted => null,
        ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.SignedData } => null,
        MultipartSigned signed => signed.Count > 0 ? Payload(signed[0]) : null,
        _ => content,
    };

    private static bool Is(string? value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The message as its own protected header fields describe it, over whichever body is wanted.
    /// </summary>
    /// <remarks>
    /// What both §4.4.4 and §6.2 make a MUST: <b>a reply is addressed from the protected header
    /// fields and from nothing else.</b> The attack it stops is a machine in the middle taking a copy
    /// of an encrypted message and replaying it with its own address added to the outer Cc — a reader
    /// who answers that has encrypted the whole conversation to the attacker, because their client
    /// had a key for them and no reason not to use it.
    /// <para>
    /// The protected From is used here even where the pane draws the envelope's, which §4.4.4 says in
    /// as many words: the two questions are different, and the answer to "who do I reply to" is the
    /// one the author signed. Anything the payload does not carry falls back to the envelope's, that
    /// being what an unprotected field is.
    /// </para>
    /// <para>
    /// The body is the caller's choice because the two callers want different ones: the reading pane
    /// renders the payload, and a reply quotes the envelope — a reply must not carry decrypted
    /// content out in the clear (RFC 9787 §5.4), and the envelope's body is ciphertext, which quotes
    /// as nothing at all.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>A protected message's header section is the protected one, and only that.</b> A field the
    /// payload does not carry is a field the author did not write, however plainly the outside of the
    /// message says otherwise — §4 says not to render what is only on the outside, and §4.4.4 makes
    /// it a MUST for recipients. Falling back to the envelope field by field is the replay attack
    /// above, arriving through the back door: Mallory does not have to alter the Cc that is there,
    /// only add one that is not.
    /// </para>
    /// </remarks>
    public static MimeMessage Addressed(MimeMessage envelope, ProtectedHeaders? covered, MimeEntity? body)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // With no protection, every field is the envelope's, which is what an ordinary message is.
        var outer = covered is null;

        var message = new MimeMessage
        {
            Subject = covered?.Value("Subject") ?? (outer ? envelope.Subject : null) ?? string.Empty,

            // The one field that falls back even under protection. §4's rule about not rendering
            // what is only on the outside is a SHOULD, and a payload that carries no date of its own
            // would otherwise be quoted and printed as the year zero. Recipients and identifiers get
            // no such latitude — those are the MUSTs, and they are what an attacker would add.
            Date = When(covered) ?? envelope.Date,
        };

        if (body is not null) message.Body = body;

        Addresses(message.From, covered?.Value("From"), envelope.From, outer);
        Addresses(message.To, covered?.Value("To"), envelope.To, outer);
        Addresses(message.Cc, covered?.Value("Cc"), envelope.Cc, outer);
        Addresses(message.ReplyTo, covered?.Value("Reply-To"), envelope.ReplyTo, outer);

        // Threading is drawn from these two, and an unprotected In-Reply-To is how a message is made
        // to appear under one it does not answer — CVE-2024-49394.
        Identifier(message, HeaderId.MessageId, covered?.Value("Message-ID"), outer ? envelope.MessageId : null);
        Identifier(message, HeaderId.InReplyTo, covered?.Value("In-Reply-To"), outer ? envelope.InReplyTo : null);

        foreach (var reference in References(covered, envelope, outer)) message.References.Add(reference);

        return message;
    }

    /// <summary>Fills an address list from the protected value, or the envelope's if there is no protection.</summary>
    private static void Addresses(
        InternetAddressList into, string? covered, InternetAddressList envelope, bool outer)
    {
        if (covered is { Length: > 0 } && InternetAddressList.TryParse(covered, out var parsed))
        {
            into.AddRange(parsed);
            return;
        }

        if (outer) into.AddRange(envelope);
    }

    /// <summary>The date the author wrote inside, or none — the envelope's is not evidence of it.</summary>
    private static DateTimeOffset? When(ProtectedHeaders? covered)
        => covered?.Value("Date") is { Length: > 0 } inside && DateUtils.TryParse(inside, out var date)
            ? date
            : null;

    /// <summary>
    /// Copies a message identifier across as a header field rather than through a parser.
    /// </summary>
    /// <remarks>
    /// The properties for these parse what they are handed and throw at what they cannot read. A
    /// message that arrived with nonsense in one of them is not a reason to fail: the field is set as
    /// text, and threading makes of it what it can.
    /// </remarks>
    private static void Identifier(MimeMessage message, HeaderId id, string? covered, string? envelope)
    {
        // A protected copy is a header field's own value and comes with its angle brackets on; the
        // envelope's came through a property that takes them off, so they go back on here.
        var value = covered?.Trim()
            ?? (envelope is { Length: > 0 } identifier ? "<" + identifier + ">" : null);

        if (value is { Length: > 0 }) message.Headers[id] = value;
    }

    private static IEnumerable<string> References(
        ProtectedHeaders? covered, MimeMessage envelope, bool outer)
    {
        if (covered?.Value("References") is { Length: > 0 } inside)
        {
            return MimeUtils.EnumerateReferences(inside);
        }

        return outer ? envelope.References : [];
    }

    // ---- The legacy display element ------------------------------------------------------------

    /// <summary>Whether anything in this payload carries a legacy display element.</summary>
    /// <remarks>
    /// §4.5.3.1 identifies one by the parameter, and only inside a cryptographic payload: the same
    /// parameter on an ordinary message means nothing, and honouring it there would let anybody hide
    /// the first paragraph of a message from the person reading it. So the caller's answer to "did
    /// this come out of an encryption layer" is what turns any of this on.
    /// </remarks>
    public static bool CarriesLegacyDisplay(MimeEntity payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload is Multipart multipart)
        {
            foreach (var child in multipart)
            {
                if (CarriesLegacyDisplay(child)) return true;
            }

            return false;
        }

        if (payload is MessagePart { Message.Body: { } inner }) return CarriesLegacyDisplay(inner);

        return payload is TextPart { IsPlain: true } or TextPart { IsHtml: true }
            && payload.ContentType.Parameters[LegacyDisplayParameter] is not null;
    }

    /// <summary>
    /// Takes the legacy display element out of every plain-text part that carries one.
    /// </summary>
    /// <remarks>
    /// §4.5.3 is a MUST: a reader of this application is shown the protected header fields where
    /// header fields belong, so the copy of them in the body is not shown at all. The rule for text
    /// is §4.5.3.2's — everything up to and including the first blank line — and it is applied here
    /// because a plain-text body is rendered as the text it is. <b>An HTML part is not done here</b>:
    /// its element is a <c>div</c> with a known class, and the place that drops markup by name is
    /// the sanitizer, which the RFC itself names as where this belongs.
    /// <para>
    /// The part is rewritten, which is why this is only ever called on the decrypted copy: it is not
    /// the message the store holds, nothing else is looking at it, and its signature has already
    /// been judged by the time anything is drawn.
    /// </para>
    /// </remarks>
    public static void HideLegacyDisplay(MimeEntity payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        switch (payload)
        {
            case Multipart multipart:
                foreach (var child in multipart) HideLegacyDisplay(child);
                break;

            case MessagePart { Message.Body: { } inner }:
                HideLegacyDisplay(inner);
                break;

            case TextPart { IsPlain: true } text
                when text.ContentType.Parameters[LegacyDisplayParameter] is not null:
                Undecorate(text);
                break;

            default:
                break;
        }
    }

    private static void Undecorate(TextPart text)
    {
        var content = text.Text;
        var blank = Blank(content);

        // No blank line means no element that was written the way the RFC writes one. Leaving the
        // content alone shows a reader a copy of their own subject; emptying the part on a guess
        // would lose the message.
        if (blank < 0)
        {
            Log.Warn("A part marked as carrying a legacy display element has no blank line in it.");
            return;
        }

        text.Text = content[blank..];
        text.ContentType.Parameters.Remove(LegacyDisplayParameter);
    }

    /// <summary>Where the content after the first entirely blank line starts, or -1 if there is none.</summary>
    private static int Blank(string content)
    {
        var start = 0;

        while (start < content.Length)
        {
            var newline = content.IndexOf('\n', start);
            if (newline < 0) return -1;

            if (content[start..newline].TrimEnd('\r').Length == 0) return newline + 1;
            start = newline + 1;
        }

        return -1;
    }
}

/// <summary>
/// What one header field becomes outside the encryption. A null <paramref name="Value"/> means it is
/// removed from there altogether.
/// </summary>
public sealed record HeaderRewrite(string Name, string Was, string? Value)
{
    /// <summary>Whether this rewrite keeps anything from somebody watching the message travel.</summary>
    public bool Hides => Value is null || !string.Equals(Value, Was, StringComparison.Ordinal);
}

/// <summary>
/// A covered payload, and what its message's outer header section has to become.
/// </summary>
/// <remarks>
/// Handed back rather than done, so that a message is only reduced once the cryptography that makes
/// the reduction safe has actually worked. See <see cref="HeaderProtection.Cover"/>.
/// </remarks>
public sealed record HeaderProtectionPlan(MimeEntity Payload, IReadOnlyList<HeaderRewrite> Outer)
{
    /// <summary>The fields this plan keeps from the outside, obscured or removed.</summary>
    public IReadOnlyList<string> Confidential => [.. Outer.Where(r => r.Hides).Select(r => r.Name)];

    /// <summary>Applies the reduction to the message the payload was built from.</summary>
    /// <remarks>
    /// Every field is rewritten where it stands rather than removed and added again, so the outer
    /// header section keeps the order it was written in — a message whose subject has moved to the
    /// bottom of its own header section is a message that says something about the client that sent
    /// it.
    /// </remarks>
    public void ApplyTo(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        foreach (var rewrite in Outer)
        {
            if (rewrite.Value is null)
            {
                message.Headers.RemoveAll(rewrite.Name);
                continue;
            }

            for (var i = 0; i < message.Headers.Count; i++)
            {
                if (!message.Headers[i].Field.Equals(rewrite.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(message.Headers[i].Value, rewrite.Value, StringComparison.Ordinal))
                {
                    message.Headers[i].SetValue(Encoding.ASCII, rewrite.Value);
                }
            }
        }
    }
}
