using System.Text.RegularExpressions;
using Mailbox.Theming.Icons;

namespace Mailbox.Tests;

/// <summary>
/// Every icon a view asks for by name is a name the glyph map has.
/// </summary>
/// <remarks>
/// <see cref="IconGlyphs.GetOrEmpty"/> answers an empty string for a name it does not know, which
/// is deliberate — one missing glyph must not take a window down — and is exactly why a wrong name
/// is invisible: the control draws, sized and positioned, with nothing in it. Commands are already
/// swept for this, because their icons are data. A view that asks for one in a string literal was
/// not, and three had been asking for names that were never in the map: the silhouette in the
/// contact form's photograph box, the pin on Map It, and the pencil beside "Add your own notes
/// here". All three drew blank, in every theme, since the day they were written.
/// </remarks>
public class IconLiteralTests
{
    /// <summary>
    /// A name asked for in code: <c>GetOrEmpty("x")</c>, <c>IconGlyphs.Get("x")</c>, or a local
    /// <c>Glyph("x")</c> helper — which is how most surfaces spell it.
    /// </summary>
    private static readonly Regex Asked = new(
        @"(?:IconGlyphs\.Get|GetOrEmpty|\bGlyph)\(\s*""(?<name>[a-z0-9][a-z0-9-]*)""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryIconNameAskedForInCodeIsInTheGlyphMap()
    {
        var faults = new List<string>();

        foreach (var (path, text) in SourceFiles())
        {
            foreach (Match match in Asked.Matches(text))
            {
                var name = match.Groups["name"].Value;
                if (IconGlyphs.Has(name)) continue;

                var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                faults.Add($"{Path.GetFileName(path)}:{line} asks for the '{name}' icon, "
                           + "which is not in the glyph map — add it to tools/generate-icons.py and regenerate.");
            }
        }

        Assert.True(faults.Count == 0, string.Join("\n", faults.OrderBy(f => f, StringComparer.Ordinal)));
    }

    private static IEnumerable<(string Path, string Text)> SourceFiles()
    {
        var root = RepoRoot();
        var start = Path.Combine(root, "src");

        foreach (var path in Directory.EnumerateFiles(start, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
            if (relative.Contains("bin") || relative.Contains("obj")) continue;

            // The map itself spells every name it has, in a shape this pattern does not match, and
            // the generator is Python; neither is a caller.
            if (Path.GetFileName(path) == "IconGlyphs.cs") continue;

            yield return (path, File.ReadAllText(path));
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mailbox.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root is not above the test binary.");
    }
}
