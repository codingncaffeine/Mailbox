using System.Text.RegularExpressions;

namespace Mailbox.Tests;

/// <summary>
/// A control that sets its own Background in code cannot be styled by state.
/// </summary>
/// <remarks>
/// One fault, three separate bugs before anybody named it. A local value beats every style
/// setter in Avalonia, so <c>Bind(button, BackgroundProperty, …)</c> silently kills that
/// control's <c>:pointerover</c> and <c>:pressed</c> rules — and the token those rules name goes
/// on being defined by every theme, referenced by nothing, and impossible to notice.
/// <list type="bullet">
/// <item>The ribbon's buttons had no hover at all. Fixed, and <c>Shell.axaml</c> records why.</item>
/// <item>The ribbon's tab strip had the same fault, found only when a door was added that could
/// photograph a hovered tab — and the first version of that door lit the wrong control, so the
/// capture was byte-identical either way.</item>
/// <item>The folder pane took the content palette's hover on chrome: invisible in two themes and
/// a near-white block on Dark Gray.</item>
/// </list>
/// This is the sweep that makes the class visible. It does not claim every hit is a bug — a
/// button that should not answer the pointer is entitled to a fixed ground — it claims that each
/// one is a decision somebody made rather than a rule nobody noticed breaking.
/// </remarks>
public class LocalBackgroundBindTests
{
    /// <summary>
    /// The buttons that bind their own Background, each reviewed against the reference and kept
    /// with its reason. The fourteenth still cannot be added without this test failing, which is
    /// the property the three bugs above all lacked.
    /// </summary>
    /// <remarks>
    /// Reviewed 29 August 2026, the question being what each button's hover and held states
    /// should be. No reference capture shows any of these thirteen hovered, and the precedent
    /// for that state of evidence is the hovered selected ribbon tab: the current paint stands
    /// until a capture — or the owner — says otherwise. What each one is:
    /// <list type="bullet">
    /// <item><c>CustomizationEditor</c> — the editors' push buttons, menu button and reorder
    /// arrows, drawn to the Options captures' flat boxes in a line.</item>
    /// <item><c>AppointmentSurface</c> — the form's Save &amp; Close tile and its pressable
    /// faces: a light face inside a line, as the reference draws them.</item>
    /// <item><c>AttachmentStrip</c> — the attachment chip, a raised face in a line.</item>
    /// <item><c>BackstageView</c> — the Backstage's big tiles, drawn on the field colour the
    /// backstage captures show.</item>
    /// <item><c>EditorOptionsDialog</c> — a selected-state marker, not a rest ground: hover
    /// must not disturb what selection is saying.</item>
    /// <item><c>OptionsWindow</c> — the dialog's own nav rail: flat rows with the selected one
    /// boxed, exactly as the Options captures draw it.</item>
    /// <item><c>ReadingPaneBody</c> — the InfoBar's action button, a raised face in a line.</item>
    /// <item><c>SendReceiveProgressDialog</c> — the dialog's push buttons, the editors' shape.</item>
    /// <item><c>UndoSendToast</c> — the toast's Undo, a raised face; the toast is this
    /// application's own surface with no reference counterpart to hover like.</item>
    /// </list>
    /// </remarks>
    private static readonly string[] KnownLocalBackgroundButtons =
    [
        "src/Mailbox.App/Options/CustomizationEditor.cs",
        "src/Mailbox.App/Views/AppointmentSurface.cs",
        "src/Mailbox.App/Views/AttachmentStrip.cs",
        "src/Mailbox.App/Views/BackstageView.cs",
        "src/Mailbox.App/Views/EditorOptionsDialog.cs",
        "src/Mailbox.App/Views/OptionsWindow.axaml.cs",
        "src/Mailbox.App/Views/ReadingPaneBody.cs",
        "src/Mailbox.App/Views/SendReceiveProgressDialog.cs",
        "src/Mailbox.App/Views/UndoSendToast.cs",
    ];

    [Fact]
    public void NoNewButtonBindsItsOwnBackground()
    {
        var found = new List<string>();

        foreach (var (path, text) in Sources())
        {
            var relative = Relative(path);

            // Locals the file declares as a Button. Only these matter: a Border has no state
            // rules to lose, and the shell binds plenty of those on purpose.
            var buttons = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Declared.Matches(text)) buttons.Add(m.Groups["name"].Value);

            foreach (Match m in LocalBind.Matches(text))
            {
                if (!buttons.Contains(m.Groups["name"].Value)) continue;
                if (KnownLocalBackgroundButtons.Contains(relative)) continue;

                found.Add($"{relative}:{Line(text, m.Index)} binds {m.Groups["name"].Value}'s "
                          + "Background in code, which kills its :pointerover and :pressed styles. "
                          + "Style it by class instead, or add the file to "
                          + "KnownLocalBackgroundButtons with a reason.");
            }
        }

        Assert.Empty(found);
    }

    /// <summary>
    /// The backlog above names files that still do it, so it cannot quietly go stale: an entry
    /// whose file has been repaired should be deleted, not left standing as cover for the next one.
    /// </summary>
    [Fact]
    public void TheBacklogNamesOnlyFilesThatStillBindALocalBackground()
    {
        var stale = new List<string>();

        foreach (var listed in KnownLocalBackgroundButtons)
        {
            var path = Path.Combine(RepoRoot(), listed.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                stale.Add($"{listed} is on the backlog and no longer exists.");
                continue;
            }

            var text = Scrub(File.ReadAllText(path));
            var buttons = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Declared.Matches(text)) buttons.Add(m.Groups["name"].Value);

            if (!LocalBind.Matches(text).Any(m => buttons.Contains(m.Groups["name"].Value)))
            {
                stale.Add($"{listed} no longer binds a button's Background — take it off the backlog.");
            }
        }

        Assert.Empty(stale);
    }

    private static readonly Regex Declared =
        new(@"\b(?:var|Button)\s+(?<name>\w+)\s*=\s*new\s+Button\b|\b(?<name>\w+)\s*=\s*new\s+Button\s*[\({]",
            RegexOptions.Compiled);

    private static readonly Regex LocalBind =
        new(@"Bind\(\s*(?<name>\w+)\s*,\s*(?:Border\.)?BackgroundProperty", RegexOptions.Compiled);

    private static int Line(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static string Relative(string path)
        => Path.GetRelativePath(RepoRoot(), path).Replace(Path.DirectorySeparatorChar, '/');

    private static IEnumerable<(string Path, string Text)> Sources()
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

    /// <summary>Comments blanked, so a comment describing this rule cannot trip it.</summary>
    private static string Scrub(string text)
    {
        var outp = text.ToCharArray();
        for (var i = 0; i < text.Length - 1; i++)
        {
            var run = 0;
            if (text[i] == '/' && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                run = (end < 0 ? text.Length : end) - i;
            }
            else if (text[i] == '/' && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                run = (end < 0 ? text.Length : end + 2) - i;
            }

            for (var j = i; j < i + run && j < text.Length; j++)
            {
                if (outp[j] != '\n') outp[j] = ' ';
            }

            if (run > 1) i += run - 1;
        }

        return new string(outp);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
