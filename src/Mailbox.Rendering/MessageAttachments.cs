using MimeKit;
using MimeKit.Tnef;

namespace Mailbox.Rendering;

/// <summary>One thing attached to a message.</summary>
public sealed record Attachment(string Name, string MimeType, long Size, MimePart Part)
{
    /// <summary>
    /// True for something the message carried inside a <c>winmail.dat</c> rather than as a
    /// part of its own.
    /// </summary>
    public bool FromTnef { get; init; }

    /// <summary>The size as a reader reads it.</summary>
    public string Describe() => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        _ => $"{Size / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>Writes the part's decoded bytes.</summary>
    public void SaveTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Part.Content?.DecodeTo(destination);
    }
}

/// <summary>
/// What a message has attached, including what it hid inside a <c>winmail.dat</c>.
/// </summary>
/// <remarks>
/// TNEF is Exchange's own attachment format, and mail sent from an Outlook talking to an
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

        foreach (var entity in message.BodyParts)
        {
            switch (entity)
            {
                case TnefPart tnef:
                    found.AddRange(FromTnef(tnef));
                    break;

                case MimePart part when IsAttachment(part):
                    found.Add(Describe(part, fromTnef: false));
                    break;
            }
        }

        return found;
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
