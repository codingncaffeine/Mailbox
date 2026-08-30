namespace Mailbox.Rendering;

/// <summary>
/// A remote resource a message asked for, and did not get.
/// </summary>
/// <remarks>
/// Counted during the sanitizing walk rather than by a separate detector, so the tracker report
/// and the blocker cannot disagree about what was in the message.
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

    /// <summary>
    /// Whether this document holds decrypted content, and so gets a CSS context of its own.
    /// </summary>
    /// <remarks>
    /// The design's second blocker, and CVE-2026-0818: decrypted plaintext was read out of a client
    /// through the cascade rather than through a fetch. Two things follow, and both are here
    /// rather than at the call site so that neither can be forgotten: the decrypted entity is
    /// rendered <em>alone</em> — never spliced into the message it arrived in — and its stylesheet
    /// refuses animations, transitions and style or container queries as well as the at-rules
    /// every message's does.
    /// </remarks>
    public bool Isolated { get; init; }

    /// <summary>
    /// Whether this document is a cryptographic payload, and so may carry a legacy display element.
    /// </summary>
    /// <remarks>
    /// RFC 9788 §4.5.3: an encrypted message may hold a copy of the header fields its composer kept
    /// off the outside, written into the body so that a client which cannot read them where they
    /// belong still shows them somewhere. This one can read them, so the copy must not be drawn.
    /// <para>
    /// It is a flag rather than something read off the part itself, because the same markup in
    /// ordinary mail means nothing: honouring it there would be a way for anybody to hide the first
    /// paragraph of a message from the person reading it. What turns it on is having opened an
    /// encryption layer to get at this document at all.
    /// </para>
    /// </remarks>
    public bool HideLegacyDisplay { get; init; }

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
    /// <c>cid:</c> part is. The document that reaches the engine has no remote URL
    /// left in it either way.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Inlined { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The largest resource that will be inlined, in bytes. A part above this is dropped
    /// rather than turned into several megabytes of base64 in the document.
    /// </summary>
    public int MaxInlineBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// The sanitized body alone, with no document around it.
    /// </summary>
    /// <remarks>
    /// For a reply, which loads the original into the editor under its own words: a second
    /// <c>&lt;html&gt;</c> inside the message would be nonsense, and the print stylesheet and the
    /// content policy belong to the reading pane's document, not to a quotation. Everything the
    /// sanitizer does still happens — that is the point of coming through here.
    /// </remarks>
    public bool Fragment { get; init; }

    /// <summary>
    /// Leave every link inert: the anchor keeps its text and loses its destination.
    /// </summary>
    /// <remarks>
    /// For a message in the Junk folder, when the Junk Options dialog asks for it. The one
    /// thing junk wants is a click, and a link that goes nowhere is the cheapest way to make
    /// sure it does not get one — the text stays, so the reader can still see where it
    /// pointed and decide the message was filed wrongly.
    /// </remarks>
    public bool DisableLinks { get; init; }
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
