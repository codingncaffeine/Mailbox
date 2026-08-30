using System.Text.RegularExpressions;

namespace Mailbox.Tests;

/// <summary>
/// The prose rules, held by a test rather than by anybody remembering them.
/// </summary>
/// <remarks>
/// Each of these was found by hand first and is the kind of thing that comes back the moment
/// nobody is looking: the reference product's name written into a comment, a string or a comment
/// that dates itself against an internal schedule, and a citation to a document only this
/// repository's owner can read. Each is cheap to check over the whole tree, so each is checked
/// here.
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

    /// <summary>The extensions the two comment sweeps walk: everything the text sweep does, plus the pose lists.</summary>
    private static readonly string[] SweptExtensions =
    [
        ".cs", ".axaml", ".xaml", ".md", ".json", ".sh", ".py", ".yml", ".yaml",
        ".desktop", ".csproj", ".props", ".targets", ".txt", ".slnx", ".xml", ".editorconfig", ".tsv",
    ];

    /// <summary>
    /// Files allowed bare <c>§N</c> without an anchor on the same line: the ones whose whole
    /// business is transcribing one named specification, where the document is named once at the
    /// top and every section number after it is that document's. Each must actually name its
    /// document somewhere, or it loses the exemption.
    /// </summary>
    private static bool IsSpecTranscription(string relative)
    {
        if (relative.StartsWith(Path.Combine("src", "Mailbox.Pst"), StringComparison.Ordinal)) return true;
        if (relative.StartsWith(Path.Combine("src", "Mailbox.Security"), StringComparison.Ordinal)) return true;

        string[] fixtures =
        [
            "PstSpecExamples.cs", "PstSpecExampleTests.cs", "PstLtpTests.cs",
            "HeaderProtectionTests.cs", "SmimeKeys.cs",
        ];
        return fixtures.Contains(Path.GetFileName(relative), StringComparer.Ordinal);
    }

    /// <summary>A section number that names its document on the same line.</summary>
    private static readonly Regex AnchoredSection = new(@"(\[MS-[A-Z-]+\]|RFC ?\d+)[^\n]*§", RegexOptions.Compiled);

    /// <summary>
    /// Every <c>§N</c> anywhere in the tree cites a document the reader has.
    /// </summary>
    /// <remarks>
    /// The audit found five hundred of these pointing at a gitignored working document, and every
    /// one was a dangling cross-reference for anybody reading the repository. A section number is
    /// allowed exactly two homes now: on a line that names a published document — an
    /// <c>[MS-*]</c> specification or an RFC — or in a file that transcribes one named spec from
    /// end to end. Anything else must say the rule itself, not where the rule was written down.
    /// </remarks>
    [Fact]
    public void EverySectionSignCitesADocumentTheReaderHas()
    {
        var offences = new List<string>();

        foreach (var (path, text) in TextFiles(SweptExtensions))
        {
            var relative = Relative(path);

            if (IsSpecTranscription(relative))
            {
                if (text.Contains('§') && !text.Contains("[MS-", StringComparison.Ordinal)
                    && !Regex.IsMatch(text, @"RFC ?\d"))
                {
                    offences.Add($"{relative}: uses § but never names its document");
                }

                continue;
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains('§')) continue;
                if (AnchoredSection.IsMatch(lines[i])) continue;
                offences.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offences.Count == 0,
            "A § cites a section of a document the reader does not have:" + Environment.NewLine
            + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// No comment anywhere dates itself against the project's own schedule.
    /// </summary>
    /// <remarks>
    /// The strings rule, widened to the comments once the last of them was rewritten: a phase
    /// number is a citation into a gitignored plan, and it ages into a falsehood the moment the
    /// phase lands — nine had, before the sweep. String literals are stripped first so a test
    /// may still spell the forbidden shape in order to look for it.
    /// </remarks>
    [Fact]
    public void NoCommentNamesAPhase()
    {
        var offences = new List<string>();

        foreach (var (path, text) in TextFiles(SweptExtensions))
        {
            var withoutLiterals = StringLiteral.Replace(text, string.Empty);
            var lines = withoutLiterals.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (NamesAPhase.IsMatch(lines[i])) offences.Add($"{Relative(path)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offences.Count == 0,
            "A comment names a phase rather than what it means:" + Environment.NewLine
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
