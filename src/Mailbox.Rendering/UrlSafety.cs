namespace Mailbox.Rendering;

/// <summary>
/// Which URLs may appear in a rendered message, and in what role.
/// </summary>
/// <remarks>
/// An allowlist, not a blocklist. The set of schemes a rendering engine might act on is not
/// knowable — <c>javascript:</c>, <c>vbscript:</c>, <c>data:text/html</c>, <c>jar:</c> and
/// <c>view-source:</c> have all been someone's vulnerability — so anything not recognised is
/// dropped rather than passed through.
/// </remarks>
internal static class UrlSafety
{
    /// <summary>Schemes a link may use. A click is routed out to the desktop, not followed.</summary>
    private static readonly string[] LinkSchemes = ["http", "https", "mailto", "tel", "ftp", "ftps"];

    /// <summary>
    /// Schemes that are worth naming as dangerous even where the allowlist would already have
    /// dropped them, so a value can be rejected without being parsed as a URL first.
    /// </summary>
    private static readonly string[] DangerousSchemes =
    [
        "javascript:", "vbscript:", "livescript:", "mocha:", "jar:", "view-source:", "chrome:",
        "resource:", "about:",
    ];

    /// <summary>True for a value naming a scheme that must never survive sanitizing.</summary>
    internal static bool IsDangerousScheme(string value)
    {
        // Compared with whitespace and control characters removed: "java\tscript:" is the
        // oldest trick in this family, and browsers have historically stripped them before
        // resolving the scheme.
        var squashed = new string([.. value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c))]);

        return DangerousSchemes.Any(
            scheme => squashed.Contains(scheme, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a link may keep its <c>href</c>.</summary>
    internal static bool IsSafeLink(string url)
    {
        if (IsDangerousScheme(url)) return false;

        var trimmed = url.Trim();

        // Fragments and relative references cannot leave the document, which has no base.
        if (trimmed.StartsWith('#')) return true;

        var colon = trimmed.IndexOf(':');
        if (colon < 0) return true;

        // A colon later than the first slash belongs to a path, not a scheme.
        var slash = trimmed.IndexOf('/');
        if (slash >= 0 && slash < colon) return true;

        var scheme = trimmed[..colon];
        return LinkSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a resource reference is one we produced by inlining.</summary>
    internal static bool IsInlinedImage(string url)
        => url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a reference is to somewhere on the network.</summary>
    internal static bool IsRemote(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("//", StringComparison.Ordinal);

    /// <summary>The host a remote reference names, for the tracker report.</summary>
    internal static string HostOf(string url)
    {
        var absolute = url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;

        return Uri.TryCreate(absolute, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host
            : "unknown";
    }
}
