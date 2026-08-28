using System.Text.RegularExpressions;

namespace Mailbox.Tests;

/// <summary>
/// The prose rules, held by a test rather than by anybody remembering them.
/// </summary>
/// <remarks>
/// Three of these were found by hand and are the kind of thing that comes back the moment nobody
/// is looking: the reference product's name written into a comment, a user-visible string that
/// dates itself against an internal schedule, and a citation to a document only this repository's
/// owner can read. Each is cheap to check over the whole tree, so each is checked here.
/// </remarks>
public class AuditProseSweepTests
{
    /// <summary>Where the sweep starts, and what it never walks into.</summary>
    private static readonly string[] Roots =
        ["src", "tests", "tools", "packaging", "assets", ".github", "docs"];

    private static readonly string[] SkippedDirectories =
        ["bin", "obj", "out", "artifacts", "specs", ".git", ".vs", ".idea"];

    private static readonly string[] TextExtensions =
    [
        ".cs", ".axaml", ".xaml", ".md", ".json", ".sh", ".py", ".yml", ".yaml",
        ".desktop", ".csproj", ".props", ".targets", ".txt", ".slnx", ".xml", ".editorconfig",
    ];

    /// <summary>Just the source the shell is written in.</summary>
    private static readonly string[] SourceExtensions = [".cs", ".axaml", ".xaml"];

    /// <summary>Source, plus the project files and the prose that ships beside it.</summary>
    private static readonly string[] ShippedExtensions =
        [".cs", ".axaml", ".xaml", ".csproj", ".md", ".sh", ".py"];

    /// <summary>
    /// Files the sweep skips, and why.
    /// </summary>
    /// <remarks>
    /// The internal working documents are gitignored and never leave the machine, so they are not
    /// bound by the rule the tracked tree is. This file is skipped because it has to spell the
    /// forbidden word in order to look for it.
    /// </remarks>
    private static bool IsExemptFile(string path)
    {
        var name = Path.GetFileName(path);
        return name == "AuditProseSweepTests.cs"
            || name.StartsWith("PLAN", StringComparison.Ordinal)
            || name.StartsWith("HANDOFF", StringComparison.Ordinal)
            || name.StartsWith("AUDIT", StringComparison.Ordinal)
            || name.StartsWith("MISSING-BROKEN", StringComparison.Ordinal)
            || name.StartsWith("USER-REQUESTS", StringComparison.Ordinal)
            || name.StartsWith("FEEDS-PLAN", StringComparison.Ordinal);
    }

    // ---- The name-nowhere rule ----------------------------------------------------------

    /// <summary>
    /// The reference product's name, and the only two shapes it is allowed to appear in: a DNS
    /// label (always lowercase, always followed by a dot and more of a hostname), and the
    /// registered message type the desktop entry claims. Prose capitalises the product, which is
    /// what makes the two tellable apart by machine.
    /// </summary>
    private static readonly Regex ExemptForms = new(
        @"[A-Za-z0-9-]*\.?outlook\.[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?|vnd\.ms-outlook",
        RegexOptions.Compiled);

    private static readonly Regex ProductName = new("outlook", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The reference's name appears nowhere it is not forced to. A hostname, the consumer mail
    /// domain and the registered message type cannot be written any other way; a comment, a
    /// doc comment, a test method name and a user-visible string all can, and say what the thing
    /// <i>is</i> instead.
    /// </summary>
    [Fact]
    public void TheReferenceProductIsNamedOnlyWhereNothingElseWillDo()
    {
        var offences = new List<string>();

        foreach (var (path, text) in TextFiles())
        {
            var stripped = ExemptForms.Replace(text, string.Empty);
            foreach (Match hit in ProductName.Matches(stripped))
            {
                offences.Add($"{Relative(path)}: …{Around(stripped, hit.Index)}…");
            }
        }

        Assert.True(
            offences.Count == 0,
            "The reference product is named outside the exemption:" + Environment.NewLine
            + string.Join(Environment.NewLine, offences));
    }

    // ---- Internal numbering never reaches a reader -----------------------------------------

    /// <summary>An ordinary or interpolated string literal, one line at a time.</summary>
    private static readonly Regex StringLiteral = new(
        "\"(?:[^\"\\\\\\n]|\\\\.)*\"", RegexOptions.Compiled);

    private static readonly Regex NamesAPhase = new(@"\bPhase\s+\d+", RegexOptions.Compiled);

    /// <summary>
    /// No string a reader can see dates itself against the project's own schedule.
    /// </summary>
    /// <remarks>
    /// The rule the compose availability table learned first, applied to the whole shell: a phase
    /// number ages silently. The phase lands, the button stays blocked for some other reason, and
    /// the sentence is now false while looking maintained. Say what is missing, never when it
    /// arrives.
    /// </remarks>
    [Fact]
    public void NoStringAReaderCanSeeNamesAPhase()
    {
        var offences = new List<string>();

        foreach (var (path, text) in TextFiles(SourceExtensions))
        {
            if (!IsUnderSrc(path)) continue;

            foreach (Match literal in StringLiteral.Matches(text))
            {
                if (NamesAPhase.IsMatch(literal.Value)) offences.Add($"{Relative(path)}: {literal.Value}");
            }
        }

        Assert.True(
            offences.Count == 0,
            "A string names a phase rather than what is missing:" + Environment.NewLine
            + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// No string a reader can see cites a section of a document they do not have.
    /// </summary>
    [Fact]
    public void NoStringAReaderCanSeeCitesAnInternalSection()
    {
        var offences = new List<string>();

        foreach (var (path, text) in TextFiles(SourceExtensions))
        {
            if (!IsUnderSrc(path)) continue;

            foreach (Match literal in StringLiteral.Matches(text))
            {
                if (literal.Value.Contains('§')) offences.Add($"{Relative(path)}: {literal.Value}");
            }
        }

        Assert.True(
            offences.Count == 0,
            "A string cites a section of an internal document:" + Environment.NewLine
            + string.Join(Environment.NewLine, offences));
    }

    // ---- The internal documents are not referred to at all ----------------------------------

    private static readonly string[] InternalDocuments =
        ["PLAN.md", "HANDOFF", "USER-REQUESTS", "FEEDS-PLAN", "AUDIT-PLAN", "MISSING-BROKEN"];

    /// <summary>
    /// Nothing that ships names one of the working documents. They are gitignored because they
    /// are not ours to publish, and a comment pointing at one is a dangling reference for
    /// everybody who ever reads the repository.
    /// </summary>
    [Fact]
    public void NothingThatShipsNamesAnInternalDocument()
    {
        var offences = new List<string>();

        foreach (var (path, text) in TextFiles(ShippedExtensions))
        {
            var relative = Relative(path);
            if (relative.StartsWith(".gitignore", StringComparison.Ordinal)) continue;

            foreach (var name in InternalDocuments)
            {
                if (text.Contains(name, StringComparison.Ordinal)) offences.Add($"{relative} names {name}");
            }
        }

        Assert.True(
            offences.Count == 0,
            "An internal working document is named in a file that ships:" + Environment.NewLine
            + string.Join(Environment.NewLine, offences));
    }

    // ---- Walking the tree ------------------------------------------------------------------

    private static bool IsUnderSrc(string path)
        => path.Contains(
            $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static IEnumerable<(string Path, string Text)> TextFiles(string[]? extensions = null)
    {
        var wanted = extensions ?? TextExtensions;
        var root = RepoRoot();

        foreach (var directory in Roots)
        {
            var start = Path.Combine(root, directory);
            if (!Directory.Exists(start)) continue;

            foreach (var path in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories))
            {
                if (IsSkipped(root, path)) continue;
                if (!wanted.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                if (IsExemptFile(path)) continue;

                yield return (path, File.ReadAllText(path));
            }
        }

        // The README is the one file that lives at the root and asserts things.
        var readme = Path.Combine(root, "README.md");
        if (wanted.Contains(".md", StringComparer.OrdinalIgnoreCase) && File.Exists(readme))
        {
            yield return (readme, File.ReadAllText(readme));
        }
    }

    private static bool IsSkipped(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative
            .Split(Path.DirectorySeparatorChar)
            .Any(segment => SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static string Around(string text, int index)
    {
        var from = Math.Max(0, index - 45);
        var to = Math.Min(text.Length, index + 45);
        return text[from..to].Replace('\n', ' ').Replace('\r', ' ');
    }

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot(), path);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mailbox.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
