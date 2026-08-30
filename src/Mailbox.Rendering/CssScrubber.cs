using System.Text;
using System.Text.RegularExpressions;

namespace Mailbox.Rendering;

/// <summary>
/// Takes the dangerous parts out of a message's CSS, and rewrites what it asks the network for.
/// </summary>
/// <remarks>
/// Dropping stylesheets outright would be safer and would make most of the mail people actually
/// receive look broken — marketing mail puts nearly everything in a <c>&lt;style&gt;</c> block.
/// So the rules are kept and scrubbed instead.
/// <para>
/// This is not a CSS parser and does not need to be. Everything dangerous in email CSS is
/// either an at-rule that pulls in something else, a property that runs code, or a
/// <c>url()</c> — and a <c>url()</c> is exactly what the image blocker already knows how to
/// deal with, so it goes through the same rewriter as an <c>img src</c>.
/// </para>
/// </remarks>
internal static partial class CssScrubber
{
    /// <summary>
    /// Properties that execute something or reach outside the document. <c>expression()</c> and
    /// <c>behavior</c> are Internet Explorer's, and are in every sanitizer for the good reason
    /// that a rendering engine nobody expected may still honour them.
    /// </summary>
    private static readonly string[] ForbiddenProperties =
    [
        "behavior", "-moz-binding", "binding", "filter", "-ms-filter", "expression",
    ];

    /// <summary>
    /// At-rules that fetch. <c>@import</c> pulls in another stylesheet and <c>@font-face</c> a
    /// font, and either is a request to a server chosen by the sender.
    /// </summary>
    private static readonly string[] ForbiddenAtRules = ["import", "font-face", "charset", "namespace"];

    /// <summary>
    /// What a document holding decrypted content refuses on top of the rest.
    /// </summary>
    /// <remarks>
    /// The design's second blocker, and CVE-2026-0818: decrypted plaintext was read out of a message
    /// through the cascade rather than through a fetch — CSS animations timed against the content,
    /// and style and container queries that branch on it. None of the three is worth anything in
    /// mail, and each is a side channel out of a document that holds a secret.
    /// </remarks>
    private static readonly string[] IsolatedAtRules = ["keyframes", "-webkit-keyframes", "container", "property", "scope"];

    /// <summary>The properties that go with them: what animates, transitions or times.</summary>
    private static readonly string[] IsolatedProperties =
    [
        "animation", "animation-name", "animation-duration", "animation-delay",
        "animation-timing-function", "animation-iteration-count", "animation-direction",
        "animation-fill-mode", "animation-play-state", "transition", "transition-property",
        "transition-duration", "transition-delay", "transition-timing-function",
        "container", "container-name", "container-type",
    ];

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments { get; }

    [GeneratedRegex(@"url\(\s*(?<quote>['""]?)(?<url>[^)'""]*)\k<quote>\s*\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlFunction { get; }

    [GeneratedRegex(@"@(?<name>[a-z-]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AtRule { get; }

    /// <summary>
    /// A reference to somewhere on the network, written any way at all.
    /// </summary>
    /// <remarks>
    /// <c>url()</c> is not the only way CSS fetches: <c>image-set("https://…" 1x)</c> takes bare
    /// strings, an escaped identifier (<c>\75 rl(…)</c>) is the same token spelled differently, and
    /// the next function to be invented will be neither. So anything remote left in a declaration
    /// after the <c>url()</c> rewriter has had it is reported and its declaration dropped, which
    /// makes the rule "no absolute remote URL survives in CSS, however it is written" rather than a
    /// list of the ways it might be written.
    /// <para>
    /// The protocol-relative half insists on a dot in the authority so that the base64 of an inline
    /// image — which may hold <c>//</c> but never <c>//a.b</c>, the alphabet having no dot — is not
    /// mistaken for one.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"https?://[^\s'""(),;{}]+|//[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+[^\s'""(),;{}]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex RemoteReference { get; }

    /// <summary>
    /// Marks a declaration that named somewhere on the network outside a <c>url()</c>.
    /// </summary>
    /// <remarks>
    /// A character no stylesheet writes, so the declaration can be found again after the
    /// <c>url()</c> pass has rewritten everything around it.
    /// </remarks>
    private const string BareRemoteMark = "\uFFFC";

    /// <summary>
    /// Scrubs a stylesheet or the contents of one <c>style</c> attribute.
    /// </summary>
    /// <param name="css">The declarations as the message wrote them.</param>
    /// <param name="rewrite">
    /// How a <c>url()</c> becomes something safe: the resolved URL, or null to drop the
    /// declaration's reference entirely.
    /// </param>
    /// <param name="isolated">
    /// True for a document that holds decrypted content, which refuses animations, transitions and
    /// style or container queries as well — see <see cref="IsolatedAtRules"/>.
    /// </param>
    internal static string Scrub(string css, Func<string, string?> rewrite, bool isolated = false)
    {
        if (string.IsNullOrWhiteSpace(css)) return string.Empty;

        var text = Comments.Replace(css, " ");

        // An at-rule that fetches takes its whole block with it, or its declarations would be
        // left loose in the sheet and applied to everything. What it named goes through the
        // rewriter on the way out, so the tracker report still names the host: @import was the
        // one way of asking for something that the report never mentioned.
        foreach (var name in ForbiddenAtRules) text = RemoveAtRule(text, name, rewrite);
        if (isolated)
        {
            foreach (var name in IsolatedAtRules) text = RemoveAtRule(text, name, rewrite);
        }

        // Before the url() pass, so that the base64 of an image this pass is about to inline
        // cannot be read as an address.
        text = MarkBareRemote(text, rewrite);

        text = UrlFunction.Replace(text, match =>
        {
            var resolved = rewrite(Unescape(match.Groups["url"].Value.Trim()));
            return resolved is null ? "none" : $"url('{resolved.Replace("'", "%27", StringComparison.Ordinal)}')";
        });

        return RemoveForbiddenDeclarations(text, isolated);
    }

    [GeneratedRegex(@"\\([0-9A-Fa-f]{1,6})[ \t\r\n\f]?")]
    private static partial Regex CssEscape { get; }

    /// <summary>
    /// A URL as the engine will read it, with CSS's numeric escapes spelled out.
    /// </summary>
    /// <remarks>
    /// <c>url(https\3a //host/x)</c> and <c>url(https://host/x)</c> are the same address to a
    /// rendering engine, and were two different things to the resolver: the first read as neither
    /// remote nor local, so it was dropped — safe — with the host left out of the tracker report,
    /// which is the half a reader is looking at when they ask who wrote to them. Decoded only for
    /// the resolver's benefit; what goes into the document is still whatever the resolver returns.
    /// </remarks>
    private static string Unescape(string url)
        => url.Contains('\\', StringComparison.Ordinal)
            ? CssEscape.Replace(url, match =>
            {
                var code = Convert.ToInt32(match.Groups[1].Value, 16);
                return code is > 0 and <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF)
                    ? char.ConvertFromUtf32(code)
                    : string.Empty;
            })
            : url;

    /// <summary>
    /// Reports every address named outside a <c>url()</c> and marks the declaration it was in.
    /// </summary>
    /// <remarks>
    /// See <see cref="RemoteReference"/>: <c>url()</c> is one spelling of "fetch this", not the
    /// only one, and a rewriter that knows only that spelling leaves the document with a live
    /// address in it and the tracker report saying the message asked for nothing.
    /// </remarks>
    private static string MarkBareRemote(string css, Func<string, string?> rewrite)
    {
        var inside = UrlFunction.Matches(css);
        if (!RemoteReference.IsMatch(css)) return css;

        return RemoteReference.Replace(css, match =>
        {
            foreach (var url in (IEnumerable<Match>)inside)
            {
                if (match.Index >= url.Index && match.Index < url.Index + url.Length) return match.Value;
            }

            // Through the same resolver an img src goes through, purely so the host is counted.
            // What it hands back is a placeholder for a picture that is not going to be drawn.
            rewrite(match.Value);
            return BareRemoteMark;
        });
    }

    /// <summary>
    /// Drops an at-rule and, where it has one, the block after it — reporting what it asked for.
    /// </summary>
    private static string RemoveAtRule(string css, string name, Func<string, string?> rewrite)
    {
        var result = new StringBuilder(css.Length);
        var index = 0;

        while (index < css.Length)
        {
            var at = css.IndexOf('@', index);
            if (at < 0)
            {
                result.Append(css, index, css.Length - index);
                break;
            }

            var match = AtRule.Match(css, at);
            if (!match.Success || match.Index != at
                || !string.Equals(match.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                result.Append(css, index, at - index + 1);
                index = at + 1;
                continue;
            }

            result.Append(css, index, at - index);

            var end = SkipRule(css, at);

            // What the rule was going to fetch, counted before it goes — once per address,
            // whether it was written as a bare string or through url(), because a count that
            // reported one @import as two resources would be a tracker report that invents.
            foreach (var reference in (IEnumerable<Match>)RemoteReference.Matches(css[at..end]))
            {
                rewrite(reference.Value);
            }

            index = end;
        }

        return result.ToString();
    }

    /// <summary>Past the end of the rule starting at <paramref name="start"/>.</summary>
    private static int SkipRule(string css, int start)
    {
        var depth = 0;

        for (var i = start; i < css.Length; i++)
        {
            switch (css[i])
            {
                case '{':
                    depth++;
                    break;

                case '}':
                    depth--;
                    if (depth <= 0) return i + 1;
                    break;

                // A statement at-rule ends at the semicolon, before any block begins.
                case ';' when depth == 0:
                    return i + 1;
            }
        }

        return css.Length;
    }

    /// <summary>
    /// Removes any declaration whose property or value is on the list, semicolon by semicolon.
    /// </summary>
    /// <remarks>
    /// Splitting on semicolons is crude and is right here: a semicolon inside a string or a
    /// <c>url()</c> would confuse it, but the <c>url()</c> pass has already run and a stray
    /// split can only ever drop more than intended, never keep something dangerous.
    /// </remarks>
    private static string RemoveForbiddenDeclarations(string css, bool isolated = false)
    {
        var result = new StringBuilder(css.Length);

        foreach (var declaration in css.Split(';'))
        {
            if (ForbiddenProperties.Any(bad =>
                    declaration.Contains(bad, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // In a document holding decrypted content, anything that animates or times is a way
            // of reading it out one step at a time.
            if (isolated && IsolatedProperties.Any(bad => NamesProperty(declaration, bad))) continue;

            // A value naming a script scheme is dropped whatever property it is on.
            if (UrlSafety.IsDangerousScheme(declaration)) continue;

            // And one that named an address outside a url(): the rewriter could not reach it, so
            // the declaration goes rather than the address staying. See MarkBareRemote.
            if (declaration.Contains(BareRemoteMark, StringComparison.Ordinal)) continue;

            if (result.Length > 0) result.Append(';');
            result.Append(declaration);
        }

        return result.ToString();
    }

    /// <summary>
    /// Whether a declaration sets exactly this property.
    /// </summary>
    /// <remarks>
    /// By its name rather than by a substring: "animation" appears inside "animation-name" and
    /// inside a class called <c>no-animation</c>, and dropping a rule because its selector said
    /// so would break mail that has nothing to do with any of this.
    /// </remarks>
    private static bool NamesProperty(string declaration, string property)
    {
        var colon = declaration.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) return false;

        var name = declaration[..colon];
        var start = name.LastIndexOfAny(['{', '}', '\n']);
        return string.Equals(name[(start + 1)..].Trim(), property, StringComparison.OrdinalIgnoreCase);
    }
}
