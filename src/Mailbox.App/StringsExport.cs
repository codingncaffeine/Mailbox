using System.Text;
using System.Text.RegularExpressions;
using Mailbox.Core.Localization;

namespace Mailbox.App;

/// <summary>
/// Writes the template a translator starts from: every string the application asks to have
/// translated, with where it came from.
/// </summary>
/// <remarks>
/// <b>Extracted from the source rather than kept in a list.</b> A list somebody maintains by hand
/// is a list that is wrong within a week — a string reworded and not re-listed becomes a
/// translation that silently stops applying, which is the failure nobody notices because it looks
/// exactly like a string nobody has translated yet. The calls themselves are the record.
/// <para>
/// A <c>.pot</c>, which is what <c>xgettext</c> produces and what Poedit, Weblate and every
/// translation team expect to be handed. Each entry carries the file and line it was found at, so
/// a translator can see what a string is for; that is <c>#:</c>, and their tools show it.
/// </para>
/// <para>
/// Deliberately a text scan rather than a compile-time analyser. The calls are a fixed shape —
/// <c>T("…")</c>, <c>T("…", "…")</c>, <c>Plural("…", "…", …)</c>, <c>Counted(…)</c> — with literal
/// strings, because a string built at run time cannot be translated anyway and a call site that
/// tried would be a bug this refuses to encode. What cannot be read as a literal is skipped, and
/// <see cref="Mailbox.Tests"/>'s adoption sweep is what stops that skipping quietly.
/// </para>
/// </remarks>
public static class StringsExport
{
    /// <summary>One string as it was found: what it says, and where.</summary>
    private sealed record Found(string? Context, string English, string? Plural)
    {
        public List<string> Places { get; } = [];
    }

    /// <summary>
    /// <c>T("…")</c> and <c>T("context", "…")</c>, and the plural pair with their optional context.
    /// </summary>
    /// <remarks>
    /// Qualified or not: <c>T("…")</c> reads the same whether it was written bare, as
    /// <c>Strings.T("…")</c> or through a localizer somebody held. Only a longer identifier
    /// ending in the same letters is excluded, which is what the lookbehind is for.
    /// <para>
    /// A C# literal, with its escapes left exactly as written: the catalogue quotes them again on
    /// the way out, so a tab in the source is a tab in the template. Verbatim and raw string
    /// literals are not matched on purpose — a string long enough to want one is a paragraph, and
    /// the two that exist are better handled by being made ordinary than by teaching this to read
    /// three syntaxes.
    /// </remarks>
    private static readonly Regex Simple = new(
        """(?<!\w)T\(\s*"((?:[^"\\]|\\.)*)"\s*\)""",
        RegexOptions.Compiled);

    private static readonly Regex WithContext = new(
        """(?<!\w)T\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)""",
        RegexOptions.Compiled);

    private static readonly Regex Plural = new(
        """(?<!\w)(?:Plural|Counted)\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*,""",
        RegexOptions.Compiled);

    /// <summary>Writes the template, and says how many strings it found.</summary>
    public static int Write(string? path)
    {
        var root = RepoRoot();
        if (root is null)
        {
            Console.Error.WriteLine("Could not find the source tree; run this from a checkout.");
            return 1;
        }

        var found = Collect(root);
        var target = path ?? Path.Combine(root, "assets", "locales", "mailbox.pot");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, Render(found), new UTF8Encoding(false));

        Console.WriteLine($"{found.Count} string(s) → {target}");
        return 0;
    }

    private static List<Found> Collect(string root)
    {
        // Keyed the way the catalogue keys them, so one string asked for in two files is one
        // entry with two places rather than two entries a translator has to answer twice.
        var byKey = new Dictionary<string, Found>(StringComparer.Ordinal);

        foreach (var file in Sources(root))
        {
            var text = WithoutComments(File.ReadAllText(file));
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

            void Take(Match match, string? context, string english, string? plural)
            {
                if (english.Length == 0) return;

                var key = PoCatalog.Key(context, english);
                if (!byKey.TryGetValue(key, out var entry))
                {
                    byKey[key] = entry = new Found(context, english, plural);
                }

                entry.Places.Add($"{relative}:{Line(text, match.Index)}");
            }

            foreach (Match m in Plural.Matches(text)) Take(m, null, m.Groups[1].Value, m.Groups[2].Value);
            foreach (Match m in WithContext.Matches(text)) Take(m, m.Groups[1].Value, m.Groups[2].Value, null);

            // Last, and only where a two-argument call did not already claim the position: the
            // one-argument pattern matches the tail of a two-argument one otherwise.
            foreach (Match m in Simple.Matches(text))
            {
                if (WithContext.Matches(text).Any(w => m.Index >= w.Index && m.Index < w.Index + w.Length)) continue;
                Take(m, null, m.Groups[1].Value, null);
            }
        }

        return [.. byKey.Values.OrderBy(e => e.Places.FirstOrDefault(), StringComparer.Ordinal)];
    }

    private static string Render(List<Found> found)
    {
        var built = new StringBuilder();

        built.AppendLine("# Mailbox — the strings the interface asks to have translated.");
        built.AppendLine("# Generated by `mailbox --export-strings`; do not edit by hand.");
        built.AppendLine("#");
        built.AppendLine("# To translate: copy this to <language>.po beside it, fill in the msgstr");
        built.AppendLine("# lines, and set Plural-Forms below to your language's own rule.");
        built.AppendLine(@"msgid """"");
        built.AppendLine(@"msgstr """"");
        built.AppendLine(@"""Project-Id-Version: Mailbox\n""");
        built.AppendLine(@"""MIME-Version: 1.0\n""");
        built.AppendLine(@"""Content-Type: text/plain; charset=UTF-8\n""");
        built.AppendLine(@"""Content-Transfer-Encoding: 8bit\n""");
        built.AppendLine(@"""Plural-Forms: nplurals=2; plural=(n != 1);\n""");

        foreach (var entry in found)
        {
            built.AppendLine();
            foreach (var place in entry.Places) built.AppendLine($"#: {place}");

            if (entry.Context is { Length: > 0 } context)
            {
                built.AppendLine($"msgctxt {PoCatalog.Quote(Unescape(context))}");
            }

            built.AppendLine($"msgid {PoCatalog.Quote(Unescape(entry.English))}");

            if (entry.Plural is { Length: > 0 } plural)
            {
                built.AppendLine($"msgid_plural {PoCatalog.Quote(Unescape(plural))}");
                built.AppendLine(@"msgstr[0] """"");
                built.AppendLine(@"msgstr[1] """"");
            }
            else
            {
                built.AppendLine(@"msgstr """"");
            }
        }

        return built.ToString();
    }

    /// <summary>
    /// A C# literal's escapes, undone — the catalogue quotes them again its own way.
    /// </summary>
    private static string Unescape(string literal)
    {
        if (!literal.Contains('\\', StringComparison.Ordinal)) return literal;

        var built = new StringBuilder(literal.Length);
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] != '\\' || i + 1 >= literal.Length)
            {
                built.Append(literal[i]);
                continue;
            }

            switch (literal[++i])
            {
                case 'n': built.Append('\n'); break;
                case 't': built.Append('\t'); break;
                case 'r': built.Append('\r'); break;
                case '"': built.Append('"'); break;
                case '\\': built.Append('\\'); break;
                default: built.Append('\\').Append(literal[i]); break;
            }
        }

        return built.ToString();
    }

    /// <summary>
    /// The same text with its comments blanked out, so a call written about in a comment is not
    /// read as a call.
    /// </summary>
    /// <remarks>
    /// Found the honest way: this file's own documentation shows what a call looks like, and the
    /// first run of the extractor duly offered translators three strings called "…". Blanked to
    /// spaces rather than removed, so every line number still points where it did.
    /// <para>
    /// A scanner rather than a regular expression, because <c>//</c> inside a string literal —
    /// every URL in the tree — is not a comment, and a quote inside a comment does not open a
    /// string. Verbatim and raw literals are stepped over as literals without being read, which
    /// is enough: what matters is that nothing inside them is mistaken for a comment.
    /// </remarks>
    internal static string WithoutComments(string text)
    {
        var built = new System.Text.StringBuilder(text.Length);
        var at = 0;

        while (at < text.Length)
        {
            var c = text[at];

            // A raw string literal: """ … """, which may contain anything at all.
            if (c == '"' && at + 2 < text.Length && text[at + 1] == '"' && text[at + 2] == '"')
            {
                var fence = 0;
                while (at + fence < text.Length && text[at + fence] == '"') fence++;
                var close = text.IndexOf(new string('"', fence), at + fence, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + fence;
                built.Append(text, at, end - at);
                at = end;
                continue;
            }

            // A verbatim literal: @" … ", where "" is an escaped quote.
            if (c == '@' && at + 1 < text.Length && text[at + 1] == '"')
            {
                built.Append(text, at, 2);
                at += 2;
                while (at < text.Length)
                {
                    if (text[at] == '"' && at + 1 < text.Length && text[at + 1] == '"')
                    {
                        built.Append(text, at, 2);
                        at += 2;
                        continue;
                    }

                    built.Append(text[at]);
                    if (text[at++] == '"') break;
                }

                continue;
            }

            // An ordinary literal, or a character one: both end at their own unescaped quote.
            if (c is '"' or '\'')
            {
                var quote = c;
                built.Append(text[at++]);
                while (at < text.Length)
                {
                    if (text[at] == '\\' && at + 1 < text.Length)
                    {
                        built.Append(text, at, 2);
                        at += 2;
                        continue;
                    }

                    built.Append(text[at]);
                    if (text[at++] == quote) break;
                }

                continue;
            }

            if (c == '/' && at + 1 < text.Length && text[at + 1] == '/')
            {
                while (at < text.Length && text[at] != '\n') { built.Append(' '); at++; }
                continue;
            }

            if (c == '/' && at + 1 < text.Length && text[at + 1] == '*')
            {
                var close = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 2;
                for (var i = at; i < end; i++) built.Append(text[i] == '\n' ? '\n' : ' ');
                at = end;
                continue;
            }

            built.Append(c);
            at++;
        }

        return built.ToString();
    }

    /// <summary>Every source file a string could be asked for in.</summary>
    internal static IEnumerable<string> Sources(string root)
    {
        var src = Path.Combine(root, "src");
        if (!Directory.Exists(src)) yield break;

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            // What the compiler generated, and what it built along the way, are not surfaces.
            var relative = Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    private static int Line(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    /// <summary>The checkout this is running out of, found by the file that marks its root.</summary>
    internal static string? RepoRoot()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        while (here is not null)
        {
            if (File.Exists(Path.Combine(here.FullName, "Mailbox.slnx"))) return here.FullName;
            here = here.Parent;
        }

        return null;
    }
}
