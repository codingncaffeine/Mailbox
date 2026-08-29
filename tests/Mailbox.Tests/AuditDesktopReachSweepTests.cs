using System.Text.RegularExpressions;

namespace Mailbox.Tests;

/// <summary>
/// One rule: nothing hands an address or a path to the desktop except
/// <c>Mailbox.Core.Platform.DesktopOpen</c>, which declines to do it under a posed run.
/// </summary>
/// <remarks>
/// <para>
/// Promoted from the audit, because this class of fault had already escaped into the owner's own
/// session: a headless capture pressed a contact card's Map It and opened a map in their browser,
/// on their screen, while they were working. Six call sites had grown their own copy of the
/// launch, two of them had grown a guard, and the difference between the two groups was invisible
/// to everybody including the sweep that went looking — it grepped for <c>xdg-open</c>, which
/// found seven of the eight and could not tell a guarded one from an unguarded one.
/// </para>
/// <para>
/// The rule is narrow on purpose. Starting a process is fine and the tree does it for good reasons
/// — <c>secret-tool</c>, <c>gpg</c>, the notification and sound helpers, the icon cache. What is
/// not fine is handing something to <em>whatever the desktop has registered</em>, because that
/// reaches the person running the sweep rather than the application under test. Two shapes do
/// that: naming <c>xdg-open</c>, and setting <c>UseShellExecute</c>, which on Linux goes through
/// the same tool.
/// </para>
/// <para>
/// Read from the source rather than the assemblies, like the sibling sweeps: the test project does
/// not reference <c>Mailbox.App</c>, and the question is what the code says.
/// </para>
/// </remarks>
public class AuditDesktopReachSweepTests
{
    /// <summary>
    /// The one file allowed to do it, because it is the thing that carries the guard. Adding a
    /// second entry here should take an argument, not a keystroke.
    /// </summary>
    private static readonly string[] MayReachTheDesktop =
    [
        "src/Mailbox.Core/Platform/DesktopOpen.cs",
    ];

    private static readonly Regex NamesXdgOpen = new(@"""xdg-open""", RegexOptions.Compiled);

    private static readonly Regex TurnsOnShellExecute =
        new(@"UseShellExecute\s*=\s*true", RegexOptions.Compiled);

    [Fact]
    public void NothingButTheSharedHelperHandsAnAddressToTheDesktop()
    {
        var faults = new List<string>();

        foreach (var (path, text) in Sources())
        {
            var relative = Relative(path);
            if (MayReachTheDesktop.Contains(relative)) continue;

            // The literal is searched in the unscrubbed text — it is a string, and scrubbing is
            // what blanks strings — while UseShellExecute is code and is read from the scrubbed
            // copy, so prose about it cannot trip the rule.
            foreach (Match match in NamesXdgOpen.Matches(File.ReadAllText(path)))
            {
                faults.Add(
                    $"{relative}:{Line(File.ReadAllText(path), match.Index)} names xdg-open directly. "
                    + "Call Mailbox.Core.Platform.DesktopOpen.Open instead — it declines under a posed run.");
            }

            foreach (Match match in TurnsOnShellExecute.Matches(text))
            {
                faults.Add(
                    $"{relative}:{Line(text, match.Index)} sets UseShellExecute, which on Linux hands the "
                    + "target to the desktop. Call Mailbox.Core.Platform.DesktopOpen.Open instead.");
            }
        }

        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    /// <summary>
    /// And the helper itself actually declines: the guard is what every other file is trusting it
    /// for, so a refactor that drops it must fail here rather than in somebody's browser.
    /// </summary>
    [Fact]
    public void TheSharedHelperDeclinesUnderAPosedRun()
    {
        var helper = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Mailbox.Core", "Platform", "DesktopOpen.cs"));

        Assert.Contains("MAILBOX_CAPTURE", helper, StringComparison.Ordinal);
        Assert.Contains("IsPosedRun", helper, StringComparison.Ordinal);
        Assert.Matches(@"if\s*\(IsPosedRun\)", helper);
    }

    // ---- shared with the sibling sweeps ------------------------------------------------------

    private static int Line(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static string Relative(string path)
        => Path.GetRelativePath(RepoRoot(), path).Replace(Path.DirectorySeparatorChar, '/');

    private static IEnumerable<(string Path, string Scrubbed)> Sources()
    {
        var root = Path.Combine(RepoRoot(), "src");
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj")) continue;
            yield return (path, Scrub(File.ReadAllText(path)));
        }
    }

    /// <summary>
    /// Comments and string literals blanked character for character, so a rule about code cannot
    /// be tripped by prose describing it and every offset still points where it did.
    /// </summary>
    private static string Scrub(string text)
    {
        var scrubbed = text.ToCharArray();
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') scrubbed[i++] = ' ';
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] != '\n') scrubbed[i] = ' ';
                    i++;
                }

                if (i < text.Length) { scrubbed[i++] = ' '; }
                if (i < text.Length) { scrubbed[i++] = ' '; }
            }
            else if (text[i] == '"')
            {
                scrubbed[i++] = ' ';
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        if (text[i] != '\n') scrubbed[i] = ' ';
                        i++;
                    }

                    if (i < text.Length)
                    {
                        if (text[i] != '\n') scrubbed[i] = ' ';
                        i++;
                    }
                }

                if (i < text.Length) scrubbed[i++] = ' ';
            }
            else
            {
                i++;
            }
        }

        return new string(scrubbed);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
