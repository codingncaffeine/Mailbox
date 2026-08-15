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

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments { get; }

    [GeneratedRegex(@"url\(\s*(?<quote>['""]?)(?<url>[^)'""]*)\k<quote>\s*\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlFunction { get; }

    [GeneratedRegex(@"@(?<name>[a-z-]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AtRule { get; }

    /// <summary>
    /// Scrubs a stylesheet or the contents of one <c>style</c> attribute.
    /// </summary>
    /// <param name="css">The declarations as the message wrote them.</param>
    /// <param name="rewrite">
    /// How a <c>url()</c> becomes something safe: the resolved URL, or null to drop the
    /// declaration's reference entirely.
    /// </param>
    internal static string Scrub(string css, Func<string, string?> rewrite)
    {
        if (string.IsNullOrWhiteSpace(css)) return string.Empty;

        var text = Comments.Replace(css, " ");

        // An at-rule that fetches takes its whole block with it, or its declarations would be
        // left loose in the sheet and applied to everything.
        foreach (var name in ForbiddenAtRules) text = RemoveAtRule(text, name);

        text = UrlFunction.Replace(text, match =>
        {
            var resolved = rewrite(match.Groups["url"].Value.Trim());
            return resolved is null ? "none" : $"url('{resolved.Replace("'", "%27", StringComparison.Ordinal)}')";
        });

        return RemoveForbiddenDeclarations(text);
    }

    /// <summary>
    /// Drops an at-rule and, where it has one, the block after it.
    /// </summary>
    private static string RemoveAtRule(string css, string name)
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
            index = SkipRule(css, at);
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
    private static string RemoveForbiddenDeclarations(string css)
    {
        var result = new StringBuilder(css.Length);

        foreach (var declaration in css.Split(';'))
        {
            if (ForbiddenProperties.Any(bad =>
                    declaration.Contains(bad, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // A value naming a script scheme is dropped whatever property it is on.
            if (UrlSafety.IsDangerousScheme(declaration)) continue;

            if (result.Length > 0) result.Append(';');
            result.Append(declaration);
        }

        return result.ToString();
    }
}
