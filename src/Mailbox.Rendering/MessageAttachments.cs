using MimeKit;
using MimeKit.Tnef;
using MimeKit.Cryptography;

namespace Mailbox.Rendering;

/// <summary>One thing attached to a message.</summary>
public sealed record Attachment(string Name, string MimeType, long Size, MimeEntity Part)
{
    /// <summary>
    /// True for something the message carried inside a <c>winmail.dat</c> rather than as a
    /// part of its own.
    /// </summary>
    public bool FromTnef { get; init; }

    /// <summary>True for a whole message carried inside this one, as forwarding produces.</summary>
    public bool IsMessage => Part is MessagePart;

    /// <summary>The size as a reader reads it.</summary>
    public string Describe() => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        _ => $"{Size / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>
    /// The name to offer a save dialog, with what a file system will not take removed.
    /// </summary>
    /// <remarks>
    /// A file name is text a stranger wrote, and it arrives with the message rather than from
    /// anywhere trusted. Only the last segment is kept — a sender writing <c>../../.bashrc</c>
    /// gets <c>.bashrc</c> suggested, in the directory the reader picked, and nowhere else.
    /// Backslash counts as a separator too: it is a legal character on this platform, but a
    /// Windows-shaped path is what a Windows sender's client puts there.
    /// <para>
    /// Then the characters a file system will not take, and the control characters that would
    /// let a name misrepresent itself in a dialog. A name left with nothing meaningful in it is
    /// replaced rather than offered empty.
    /// </para>
    /// </remarks>
    public string SafeName
    {
        get
        {
            var segment = Name.Split(['/', '\\']).LastOrDefault(part => part.Trim().Length > 0)
                          ?? string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string([.. segment.Where(
                c => !invalid.Contains(c) && !char.IsControl(c))]).Trim();

            // "." and ".." name a directory rather than a file.
            return clean.Length == 0 || clean.All(c => c == '.') ? "attachment" : clean;
        }
    }

    /// <summary>Writes the attachment's bytes, decoded.</summary>
    /// <remarks>
    /// A carried message is written as the RFC822 it is, so what lands on disk is a
    /// <c>.eml</c> any mail client can open — including this one, once import lands.
    /// </remarks>
    public void SaveTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        switch (Part)
        {
            case MessagePart carried:
                carried.Message?.WriteTo(destination);
                break;

            case MimePart part:
                part.Content?.DecodeTo(destination);
                break;
        }
    }
}

/// <summary>
/// What a message has attached, including what it hid inside a <c>winmail.dat</c>.
/// </summary>
/// <remarks>
/// TNEF is Exchange's own attachment format, and mail sent from the reference talking to an
/// Exchange server still arrives that way. To every other client the message looks like it has
/// one useless attachment called <c>winmail.dat</c> containing the several real ones. Unpacking
/// it here means the strip shows the attachments the sender actually sent — which is the whole
/// of what the format costs anyone who is not Exchange.
/// </remarks>
public static class MessageAttachments
{
    public static IReadOnlyList<Attachment> List(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var found = new List<Attachment>();
        var machinery = Machinery(message);

        foreach (var entity in message.BodyParts)
        {
            if (machinery.Contains(entity)) continue;

            switch (entity)
            {
                case TnefPart tnef:
                    found.AddRange(FromTnef(tnef));
                    break;

                // A whole message carried inside this one — what forwarding as an attachment
                // produces, and what a bounce puts the message it is bouncing into. It is not a
                // MimePart and has no content of its own, so it is listed and saved separately;
                // matching only MimePart left it invisible, and a forwarded message showed an
                // empty strip rather than the mail it came with.
                case MessagePart carried:
                    found.Add(Describe(carried));
                    break;

                case MimePart part when IsAttachment(part):
                    found.Add(Describe(part, fromTnef: false));
                    break;
            }
        }

        return found;
    }

    /// <summary>
    /// The parts that are how a message is signed or sealed rather than anything in it.
    /// </summary>
    /// <remarks>
    /// A <c>multipart/encrypted</c> is two parts, both of them plumbing: one saying the protocol is
    /// version 1 and one holding the ciphertext. A <c>multipart/signed</c> is the message followed
    /// by a detached signature. None of the four is a thing a reader attached, and offering the
    /// ciphertext as <c>attachment.octet-stream</c> invites somebody to save the one file in the
    /// message they can do nothing with.
    /// <para>
    /// Found by walking the tree rather than by content type, because the ciphertext part is an
    /// ordinary <c>application/octet-stream</c> and only its place in a <c>multipart/encrypted</c>
    /// says what it is. The signed part's <em>content</em> is walked on: its attachments are the
    /// message's own, and they are exactly the ones the signature covers.
    /// </para>
    /// </remarks>
    private static HashSet<MimeEntity> Machinery(MimeMessage message)
    {
        var skip = new HashSet<MimeEntity>();
        if (message.Body is { } body) Walk(body);
        return skip;

        void Walk(MimeEntity entity)
        {
            switch (entity)
            {
                case MultipartEncrypted encrypted:
                    foreach (var child in encrypted) skip.Add(child);
                    break;

                // S/MIME wraps the whole message in one part instead of two, and MimeKit names it
                // smime.p7m — a file the reader can do nothing with either.
                case ApplicationPkcs7Mime pkcs7:
                    skip.Add(pkcs7);
                    break;

                // The first part is what was signed and the rest is the signature over it.
                case MultipartSigned signed:
                    for (var i = 1; i < signed.Count; i++) skip.Add(signed[i]);
                    if (signed.Count > 0) Walk(signed[0]);
                    break;

                case Multipart multipart:
                    foreach (var child in multipart) Walk(child);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a part is something the reader would call an attachment.
    /// </summary>
    /// <remarks>
    /// An inline image referenced by the markup is not: it is already on screen, and listing it
    /// again turns every newsletter into a message with nine attachments.
    /// </remarks>
    private static bool IsAttachment(MimePart part)
    {
        if (part.IsAttachment) return true;

        // No disposition at all, but a file name and not something the body refers to.
        return part.ContentDisposition is null
               && part.FileName is { Length: > 0 }
               && part.ContentId is null;
    }

    private static IEnumerable<Attachment> FromTnef(TnefPart tnef)
    {
        List<MimeEntity> extracted;

        try
        {
            extracted = [.. tnef.ExtractAttachments()];
        }
        catch (Exception)
        {
            // A winmail.dat we cannot read is still a file the reader may want to keep, so it
            // is offered as itself rather than dropped.
            return [Describe(tnef, fromTnef: false)];
        }

        return extracted.OfType<MimePart>().Select(part => Describe(part, fromTnef: true));
    }

    /// <summary>
    /// A carried message, named the way a reader would name it.
    /// </summary>
    /// <remarks>
    /// Such a part rarely carries a file name, so the subject becomes one. Its size is the size
    /// of the RFC822 it serializes to, which is what saving it writes — a decoded length would
    /// be a number that matches no file.
    /// </remarks>
    private static Attachment Describe(MessagePart carried)
    {
        var subject = carried.Message?.Subject;

        var name = FileNameOf(carried)
                   ?? (string.IsNullOrWhiteSpace(subject) ? "message.eml" : subject.Trim() + ".eml");

        return new Attachment(name, "message/rfc822", Measure(carried), carried);
    }

    /// <summary>The name a part gives itself, from either place one can be written.</summary>
    private static string? FileNameOf(MimeEntity entity)
    {
        if (entity.ContentDisposition?.FileName is { Length: > 0 } disposition) return disposition;
        return entity.ContentType?.Name is { Length: > 0 } name ? name : null;
    }

    private static long Measure(MessagePart carried)
    {
        try
        {
            using var counter = new MemoryStream();
            carried.Message?.WriteTo(counter);
            return counter.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static Attachment Describe(MimePart part, bool fromTnef)
    {
        var name = part.FileName is { Length: > 0 } fileName
            ? fileName
            : $"attachment.{Extension(part)}";

        return new Attachment(name, part.ContentType?.MimeType ?? "application/octet-stream",
            Measure(part), part)
        {
            FromTnef = fromTnef,
        };
    }

    /// <summary>
    /// The decoded size, which is what a reader means by "how big is it".
    /// </summary>
    /// <remarks>
    /// Base64 is a third larger than the bytes it carries, so the encoded length would report
    /// every attachment as bigger than the file that comes out of it.
    /// </remarks>
    private static long Measure(MimePart part)
    {
        try
        {
            using var counter = new MemoryStream();
            part.Content?.DecodeTo(counter);
            return counter.Length;
        }
        catch (Exception)
        {
            return part.Content?.Stream is { CanSeek: true } stream ? stream.Length : 0;
        }
    }

    private static string Extension(MimePart part) =>
        part.ContentType?.MediaSubtype is { Length: > 0 } subtype ? subtype : "bin";
}
