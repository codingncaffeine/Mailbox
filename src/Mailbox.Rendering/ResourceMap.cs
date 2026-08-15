using MimeKit;

namespace Mailbox.Rendering;

/// <summary>
/// What a message's own parts can supply, keyed by the ids its markup refers to them by.
/// </summary>
/// <remarks>
/// A <c>cid:</c> reference points into the MIME tree, and we are holding the tree, so resolving
/// one is a lookup rather than a fetch. This is the reason the design in §11 works: the markup
/// that reaches the engine has its inline images already in it.
/// </remarks>
public sealed class ResourceMap
{
    private readonly Dictionary<string, MimePart> _byContentId =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, MimePart> _byFileName =
        new(StringComparer.OrdinalIgnoreCase);

    private ResourceMap()
    {
    }

    public static ResourceMap From(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var map = new ResourceMap();

        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (Trim(part.ContentId) is { Length: > 0 } id) map._byContentId[id] = part;

            // Some senders reference a related part by file name rather than by Content-Id.
            // It is not in the standard and it is common enough to be worth honouring.
            if (part.FileName is { Length: > 0 } name) map._byFileName[name] = part;
        }

        return map;
    }

    /// <summary>
    /// The part a <c>cid:</c> URL names, or null if the message does not carry it.
    /// </summary>
    public MimePart? Resolve(string url)
    {
        if (!url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return null;

        var id = Uri.UnescapeDataString(url[4..]).Trim();
        if (id.Length == 0) return null;

        if (_byContentId.TryGetValue(id, out var part)) return part;
        return _byFileName.TryGetValue(id, out var named) ? named : null;
    }

    /// <summary>
    /// A part as a <c>data:</c> URI, or null when it is too big to be worth inlining.
    /// </summary>
    /// <remarks>
    /// Base64 costs a third again on top of the bytes, and the document is handed to the engine
    /// as a string, so a very large inline image is paid for twice over. A limit is cheaper
    /// than a reading pane that stalls on one message.
    /// </remarks>
    public static string? DataUri(MimePart part, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(part);

        using var buffer = new MemoryStream();
        part.Content?.DecodeTo(buffer);

        if (buffer.Length == 0 || buffer.Length > maxBytes) return null;

        var type = part.ContentType?.MimeType ?? "application/octet-stream";
        return $"data:{type};base64,{Convert.ToBase64String(buffer.ToArray())}";
    }

    /// <summary>Content-Id arrives wrapped in angle brackets as often as not.</summary>
    private static string Trim(string? contentId)
        => contentId?.Trim().TrimStart('<').TrimEnd('>').Trim() ?? string.Empty;
}
