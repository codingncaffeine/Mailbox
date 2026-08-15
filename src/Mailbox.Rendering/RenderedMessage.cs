namespace Mailbox.Rendering;

/// <summary>
/// A remote resource a message asked for, and did not get.
/// </summary>
/// <remarks>
/// Counted during the sanitizing walk rather than by a separate detector, so the tracker report
/// and the blocker cannot disagree about what was in the message. See §11.
/// </remarks>
public sealed record BlockedResource(string Url, string Host, BlockedResourceKind Kind);

public enum BlockedResourceKind
{
    /// <summary>An <c>&lt;img&gt;</c>, which is what a tracking pixel is.</summary>
    Image,

    /// <summary>A background or other resource named by a stylesheet.</summary>
    Style,
}

/// <summary>
/// The colours and type the document is rendered in.
/// </summary>
/// <remarks>
/// Passed in rather than chosen here: a colour named in a library is one the theme engine
/// cannot reach, and the reading pane has to follow the theme like everything else. The
/// renderer is handed values already resolved from tokens.
/// </remarks>
public sealed record RenderStyle(
    string Background,
    string Foreground,
    string Link,
    string Quote,
    string FontFamily,
    double FontSize)
{
    /// <summary>Plain values, for tests that care about the markup rather than the palette.</summary>
    public static RenderStyle Plain { get; } =
        new("#FFFFFF", "#000000", "#0000EE", "#767676", "serif", 14);
}

/// <summary>
/// The block the reference's Memo style prints above a message.
/// </summary>
/// <remarks>
/// Part of the document rather than of the pane, and hidden until the page is printed. The
/// header on screen is Avalonia chrome and the engine cannot see it, so printing from the pane
/// would otherwise produce a page of body text with nothing saying who sent it.
/// </remarks>
public sealed record PrintHeader(string From, string Sent, string To, string Subject)
{
    public string? Cc { get; init; }
}

/// <summary>What the caller can vary about a render.</summary>
public sealed record RenderOptions
{
    public RenderStyle Style { get; init; } = RenderStyle.Plain;

    /// <summary>What a printed copy shows above the message, or null for no print header.</summary>
    public PrintHeader? PrintHeader { get; init; }

    /// <summary>
    /// Data URIs for remote resources the caller has already fetched, keyed by the URL as it
    /// appeared in the message.
    /// </summary>
    /// <remarks>
    /// This is how "allow once" and the per-sender allow list work, and why nothing here needs
    /// a network stack: the fetch belongs to the application, which owns an HttpClient with no
    /// cookies and no referer, and hands the bytes back to be inlined the same way a
    /// <c>cid:</c> part is. See §11 — the document that reaches the engine has no remote URL
    /// left in it either way.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Inlined { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The largest resource that will be inlined, in bytes. A part above this is dropped
    /// rather than turned into several megabytes of base64 in the document.
    /// </summary>
    public int MaxInlineBytes { get; init; } = 8 * 1024 * 1024;
}

/// <summary>A message turned into a document with nothing left in it to fetch.</summary>
public sealed record RenderedMessage(
    string Html,
    IReadOnlyList<BlockedResource> Blocked,
    bool WasHtml)
{
    /// <summary>Distinct hosts the message tried to reach, for the tracker detail.</summary>
    public IReadOnlyList<string> Hosts =>
        [.. Blocked.Select(b => b.Host).Distinct(StringComparer.OrdinalIgnoreCase).Order()];

    public int BlockedImages => Blocked.Count(b => b.Kind == BlockedResourceKind.Image);

    public bool HasRemoteContent => Blocked.Count > 0;
}
