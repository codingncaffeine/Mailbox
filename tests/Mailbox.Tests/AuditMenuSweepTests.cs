using System.Text.RegularExpressions;

namespace Mailbox.Tests;

/// <summary>
/// The rules that came out of walking every context menu in the tree, promoted so the class of
/// fault stays caught: a menu goes through the probe that lets a posed run read it back, and no
/// menu fills itself only when it opens.
/// </summary>
/// <remarks>
/// A popup is not a window in the application's window list, so the in-process capture
/// photographs the shell behind it and an empty menu looks exactly like a success — two shipped
/// that way. Every show site now goes through <c>MenuProbe.Show</c> (or the ribbon's own
/// <c>OpenMenuUnder</c>, whose host records it), which is what lets a posed run log every menu's
/// entries, greyed states and presenter size the moment it opens. A raw <c>ShowAt</c> added
/// later would silently fall out of that read-back, and a menu filled from its own
/// <c>Opening</c> event presents nothing at all — the presenter is created and measured before
/// the event is raised.
/// </remarks>
public class AuditMenuSweepTests
{
    /// <summary>
    /// The two harness doors that re-show an attached flyout on purpose, measuring whether the
    /// click alone opened it — the deliberate second ask the QAT door is built on.
    /// </summary>
    private static readonly string[] RawShowAtByDesign =
    [
        "Mailbox.App/Views/MainWindow.axaml.cs",
        "Mailbox.App/Views/MainWindow.Phase12BPose.cs",
    ];

    private static readonly Regex ShowAt = new(@"\.ShowAt\(", RegexOptions.Compiled);

    private static readonly Regex OpeningFill = new(
        @"\.Opening\s*\+=", RegexOptions.Compiled);

    /// <summary>
    /// Every menu in the App project is shown through <see cref="ShowAt"/>'s one wrapper, so a
    /// posed run reads back what opened. The ribbon control's own <c>OpenMenuUnder</c> is the
    /// other sanctioned path; its hosts record through the same probe.
    /// </summary>
    [Fact]
    public void EveryMenuIsShownThroughTheProbe()
    {
        var strays = new List<string>();

        foreach (var (path, text) in Sources("Mailbox.App"))
        {
            var relative = Relative(path);
            if (relative.EndsWith("MenuProbe.cs", StringComparison.Ordinal)) continue;
            if (RawShowAtByDesign.Any(allowed => relative.EndsWith(allowed, StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (Match m in ShowAt.Matches(text))
            {
                strays.Add($"{relative}:{Line(text, m.Index)}: a raw ShowAt — "
                           + "route it through MenuProbe.Show so a posed run can read it back.");
            }
        }

        Assert.True(strays.Count == 0, string.Join(Environment.NewLine, strays));
    }

    /// <summary>
    /// The two doors on the allow list still carry exactly the raw calls they are allowed —
    /// so the list cannot quietly become a hole new call sites hide in.
    /// </summary>
    [Fact]
    public void TheAllowedRawShowAtsAreExactlyTheTwoDoors()
    {
        var count = 0;

        foreach (var (path, text) in Sources("Mailbox.App"))
        {
            var relative = Relative(path);
            if (!RawShowAtByDesign.Any(allowed => relative.EndsWith(allowed, StringComparison.Ordinal)))
            {
                continue;
            }

            count += ShowAt.Matches(text).Count;
        }

        Assert.Equal(2, count);
    }

    /// <summary>
    /// The files allowed to touch a flyout's <c>Opening</c> at all, each for a reason that has
    /// been checked: the toolbar's customize flyout and the bar's overflow both refill there
    /// <em>after</em> filling at build time — the headless suite holds the build-time half — and
    /// the collapsed-group flyout lazily builds <c>Flyout.Content</c>, which is not an items
    /// presenter and measures after it is set. A new file joining this list needs the same
    /// justification written here.
    /// </summary>
    private static readonly string[] OpeningByDesign =
    [
        "Mailbox.App/Views/QuickAccessFlyout.cs",
        "Mailbox.Controls.Ribbon/RibbonView.cs",
    ];

    /// <summary>
    /// No menu fills itself only from its own <c>Opening</c> event — the presenter is created
    /// and measured before the event is raised, so such a menu opens holding nothing.
    /// </summary>
    [Fact]
    public void NoMenuFillsItselfOnlyWhenItOpens()
    {
        var strays = new List<string>();

        foreach (var (path, text) in Sources("Mailbox.App", "Mailbox.Controls.Common",
                     "Mailbox.Controls.Ribbon"))
        {
            var relative = Relative(path);
            if (OpeningByDesign.Any(allowed => relative.EndsWith(allowed, StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (Match m in OpeningFill.Matches(text))
            {
                strays.Add($"{relative}:{Line(text, m.Index)}: a flyout filled from its own "
                           + "Opening event presents nothing — build it full, then show it.");
            }
        }

        Assert.True(strays.Count == 0, string.Join(Environment.NewLine, strays));
    }

    // ---- Plumbing ----------------------------------------------------------------------------

    private static IEnumerable<(string Path, string Text)> Sources(params string[] projects)
    {
        var root = Path.Combine(RepoRoot(), "src");

        foreach (var project in projects)
        {
            var directory = Path.Combine(root, project);
            if (!Directory.Exists(directory)) continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj")) continue;
                yield return (path, File.ReadAllText(path));
            }
        }
    }

    private static string RepoRoot()
    {
        var here = AppContext.BaseDirectory;
        while (here is not null && !File.Exists(Path.Combine(here, "Mailbox.slnx")))
        {
            here = Path.GetDirectoryName(here);
        }

        return here ?? throw new InvalidOperationException("Mailbox.slnx was not found above the test assembly.");
    }

    private static string Relative(string path)
        => Path.GetRelativePath(Path.Combine(RepoRoot(), "src"), path).Replace('\\', '/');

    private static int Line(string text, int offset)
        => text.AsSpan(0, offset).Count('\n') + 1;
}
