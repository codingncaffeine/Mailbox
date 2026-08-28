using System.Text.RegularExpressions;

namespace Mailbox.Tests;

/// <summary>
/// The audit's chrome and thread-discipline sweeps, promoted so the classes of fault they caught
/// stay caught: every window wearing the application's own caption, the system frame turned off
/// in one place, nothing blocking the dispatcher, and no catch block that says nothing at all.
/// </summary>
/// <remarks>
/// These read the source rather than the assemblies. The test project does not reference
/// <c>Mailbox.App</c> — every window in the application lives there — and a reflection sweep would
/// have to construct windows, which needs a running Avalonia application. Reading the files costs
/// milliseconds and asks the question the rule is actually about: what the code says.
/// <para>
/// Comments and string literals are blanked before anything is matched, so a rule cannot be
/// tripped by prose describing it. <see cref="Scrub"/> keeps every offset, so a body's span in
/// the blanked text is the same span in the original.
/// </para>
/// </remarks>
public class AuditChromeSweepTests
{
    // ---- Sweep A: caption buttons ------------------------------------------------------------

    /// <summary>
    /// Windows that deliberately carry no caption of their own. Empty, and meant to stay that
    /// way: the Backstage-style surfaces are not windows at all — they are content inside the
    /// shell — and even an OK-only dialog gets a close button, because a modal with no way out
    /// but the keyboard is not a dialog the reference draws.
    /// </summary>
    private static readonly string[] NoCaptionByDesign = [];

    /// <summary>
    /// Every <c>Window</c> subclass in the tree hosts the application's own caption buttons,
    /// through <c>DialogChrome</c>, <c>SystemDialogChrome</c>, or by placing <c>CaptionButtons</c>
    /// in a title bar of its own.
    /// </summary>
    [Fact]
    public void EveryWindowSubclassCarriesTheApplicationsOwnCaption()
    {
        var bare = new List<string>();
        var seen = 0;

        foreach (var (path, text) in Sources())
        {
            foreach (var window in WindowClasses(text))
            {
                seen++;
                var body = Block(text, window.BodyStart);
                if (NoCaptionByDesign.Contains(window.Name)) continue;

                if (!body.Contains("DialogChrome.Apply", StringComparison.Ordinal)
                    && !body.Contains("SystemDialogChrome.Apply", StringComparison.Ordinal)
                    && !NewCaptionButtons.IsMatch(body))
                {
                    bare.Add($"{Relative(path)}: {window.Name} draws no caption of its own.");
                }
            }
        }

        Assert.True(seen >= 70, $"only {seen} window subclasses were found; the sweep is not reading the tree.");
        Assert.True(bare.Count == 0, string.Join("\n", bare));
    }

    /// <summary>
    /// A window built in place — <c>Confirm</c>, <c>Prompt</c> and the other factory dialogs —
    /// is chromed too.
    /// </summary>
    /// <remarks>
    /// Counted per file rather than per statement: every one of these is a factory whose whole
    /// job is to build one window and chrome it, so a file holding more <c>new Window</c> than
    /// chrome calls has left one wearing the desktop's title bar. That is exactly how the compose
    /// window's Word Count, Find, Replace, spelling, Symbol, Delay Delivery and Direct Replies To
    /// dialogs came to be the seven windows in the application with an operating-system frame.
    /// </remarks>
    [Fact]
    public void EveryWindowBuiltInPlaceIsGivenTheApplicationsChrome()
    {
        var faults = new List<string>();
        var built = 0;

        foreach (var (path, text) in Sources())
        {
            var made = NewWindow.Matches(text).Count;
            if (made == 0) continue;
            built += made;

            var chromed = ChromeOnAVariable.Matches(text).Count;
            if (chromed < made)
            {
                faults.Add($"{Relative(path)}: builds {made} window(s) but chromes {chromed}.");
            }
        }

        Assert.True(built >= 15, $"only {built} in-place windows were found; the sweep is not reading the tree.");
        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    /// <summary>
    /// The system frame is turned off in exactly one place, and nowhere puts it back.
    /// </summary>
    /// <remarks>
    /// A window that sets <c>ExtendClientAreaToDecorationsHint</c> for itself is a window one
    /// reordering away from wearing the desktop's title bar under the application's own caption
    /// buttons — the two-sets-of-buttons failure <c>WindowFrame</c> exists to prevent.
    /// </remarks>
    [Fact]
    public void TheSystemFrameIsTurnedOffInOnePlaceOnly()
    {
        var strays = new List<string>();

        foreach (var (path, text) in Sources())
        {
            if (Path.GetFileName(path) == "WindowFrame.cs") continue;

            foreach (Match m in FrameHints.Matches(text))
            {
                strays.Add($"{Relative(path)}:{Line(text, m.Index)}: {m.Value.Trim()}");
            }
        }

        Assert.True(strays.Count == 0,
            "the system frame is set outside WindowFrame:\n" + string.Join("\n", strays));

        var frame = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Mailbox.App", "Views", "WindowFrame.cs"));
        Assert.Contains("ExtendClientAreaToDecorationsHint = true", frame, StringComparison.Ordinal);
        Assert.Contains("WindowDecorations = WindowDecorations.None", frame, StringComparison.Ordinal);

        // The shell declares the same two in markup, because its frame exists before its code runs.
        var shell = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Mailbox.App", "Views", "MainWindow.axaml"));
        Assert.Contains("ExtendClientAreaToDecorationsHint=\"True\"", shell, StringComparison.Ordinal);
        Assert.Contains("WindowDecorations=\"None\"", shell, StringComparison.Ordinal);
    }

    /// <summary>
    /// There is one set of caption buttons and one implementation of the frame behind them.
    /// </summary>
    /// <remarks>
    /// A second copy is how one window ends up resizable from three edges. The shell is the one
    /// place allowed to change its own window state without going through a caption button: the
    /// system menu it draws for Alt+Space offers Restore, Minimize and Maximize by name, and its
    /// full-screen command is a state change too.
    /// </remarks>
    [Fact]
    public void TheCaptionAndItsFrameHaveOneImplementationEach()
    {
        var drags = new List<string>();
        var states = new List<string>();

        foreach (var (path, text) in Sources())
        {
            var file = Path.GetFileName(path);

            if (file != "WindowFrame.cs")
            {
                foreach (Match m in WindowDrag.Matches(text))
                {
                    drags.Add($"{Relative(path)}:{Line(text, m.Index)}: {m.Value}");
                }
            }

            if (file is "CaptionButtons.cs" or "WindowFrame.cs" or "MainWindow.axaml.cs") continue;

            foreach (Match m in WindowStateWrite.Matches(text))
            {
                states.Add($"{Relative(path)}:{Line(text, m.Index)}: {m.Value.Trim()}");
            }
        }

        Assert.True(drags.Count == 0,
            "the window frame is re-implemented outside WindowFrame:\n" + string.Join("\n", drags));
        Assert.True(states.Count == 0,
            "caption behaviour is re-implemented outside CaptionButtons:\n" + string.Join("\n", states));
    }

    // ---- Sweep B: the dispatcher --------------------------------------------------------------

    /// <summary>
    /// Nothing in the projects that run on the dispatcher pulls a result out of a task by
    /// blocking on it.
    /// </summary>
    /// <remarks>
    /// <c>GetAwaiter().GetResult()</c> and <c>Wait()</c> on the UI thread do not merely stall it:
    /// every continuation in the awaited chain is posted back to the dispatcher this call is
    /// holding, so the application stops for good. Compose's Save Draft did exactly that the
    /// moment a message carried an attachment, autosave included.
    /// <para>
    /// A dialog's own <c>Result</c> property is not this and is deliberately not matched — the
    /// pattern here is a task being unwrapped, not a property being read.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingOnTheDispatcherBlocksOnATask()
    {
        var blocking = new List<string>();

        foreach (var (path, text) in Sources())
        {
            if (!RunsOnTheDispatcher(path)) continue;

            foreach (Match m in BlockingWait.Matches(text))
            {
                blocking.Add($"{Relative(path)}:{Line(text, m.Index)}: {m.Value.Trim()}");
            }
        }

        Assert.True(blocking.Count == 0,
            "the UI thread is blocked on a task:\n" + string.Join("\n", blocking));
    }

    // ---- Sweep C: swallowed exceptions --------------------------------------------------------

    /// <summary>
    /// An empty catch block says why it is empty.
    /// </summary>
    /// <remarks>
    /// Empty is often right — a best-effort kill of a process that has already exited, a decoding
    /// attempt that falls through to the next reading. What is never right is being unable to tell
    /// that from a failure somebody meant to come back to. The comment is the difference, and it
    /// costs one line.
    /// </remarks>
    [Fact]
    public void EveryEmptyCatchSaysWhyItIsEmpty()
    {
        var silent = new List<string>();
        var found = 0;

        foreach (var (path, text) in Sources())
        {
            var raw = File.ReadAllText(path);

            foreach (Match m in Catch.Matches(text))
            {
                // The match eats the whitespace after the filter, so a catch with a block has
                // its opening brace right there. Anything else is `catch` inside something the
                // scrubber left behind, and is not a block to judge.
                var open = m.Index + m.Length;
                if (open >= text.Length || text[open] != '{') continue;

                var close = Close(text, open);
                if (close < 0) continue;

                found++;
                if (text[(open + 1)..close].Trim().Length > 0) continue;

                var body = raw[(open + 1)..close];
                if (body.Contains("//", StringComparison.Ordinal) || body.Contains("/*", StringComparison.Ordinal)) continue;

                silent.Add($"{Relative(path)}:{Line(text, m.Index)}: {m.Value.Trim()} swallows without a word.");
            }
        }

        Assert.True(found >= 300, $"only {found} catch blocks were found; the sweep is not reading the tree.");
        Assert.True(silent.Count == 0, string.Join("\n", silent));
    }

    // ---- The reader ---------------------------------------------------------------------------

    private static readonly Regex ClassDeclaration = new(
        @"(?<!\w)class\s+(?<name>\w+)\s*(?:<[^>]*>)?\s*:\s*(?<bases>[^\{\r\n]+)", RegexOptions.Compiled);

    private static readonly Regex NewCaptionButtons = new(@"new\s+CaptionButtons\s*\(", RegexOptions.Compiled);

    private static readonly Regex NewWindow = new(@"new\s+Window\s*[({]", RegexOptions.Compiled);

    /// <summary>A chrome call on something other than <c>this</c>: a window built in place.</summary>
    private static readonly Regex ChromeOnAVariable = new(
        @"(?:Dialog|SystemDialog)Chrome\.Apply\(\s*(?!this\b)\w+", RegexOptions.Compiled);

    private static readonly Regex FrameHints = new(
        @"\b(?:ExtendClientAreaToDecorationsHint|ExtendClientAreaChromeHints|SystemDecorations|WindowDecorations)\s*=",
        RegexOptions.Compiled);

    private static readonly Regex WindowDrag = new(@"\bBegin(?:Move|Resize)Drag\s*\(", RegexOptions.Compiled);

    private static readonly Regex WindowStateWrite = new(
        @"WindowState\s*=\s*WindowState\.(?:Minimized|Maximized)", RegexOptions.Compiled);

    private static readonly Regex BlockingWait = new(
        @"GetAwaiter\(\)\s*\.\s*GetResult\(\)|\.\s*Wait\s*\(", RegexOptions.Compiled);

    private static readonly Regex Catch = new(
        @"\bcatch\b\s*(?:\([^)]*\))?\s*(?:when\s*\([^)]*\))?\s*", RegexOptions.Compiled);

    /// <summary>The projects whose code runs on the dispatcher.</summary>
    private static bool RunsOnTheDispatcher(string path)
    {
        var project = Relative(path).Split('/')[1];
        return project is "Mailbox.App" or "Mailbox.Editor" || project.StartsWith("Mailbox.Controls.", StringComparison.Ordinal);
    }

    private record WindowClass(string Name, int BodyStart);

    private static IEnumerable<WindowClass> WindowClasses(string scrubbed)
    {
        foreach (Match m in ClassDeclaration.Matches(scrubbed))
        {
            var first = m.Groups["bases"].Value.Split(',')[0].Trim().Split('<')[0];
            if (first.EndsWith("Window", StringComparison.Ordinal))
            {
                yield return new WindowClass(m.Groups["name"].Value, m.Index + m.Length);
            }
        }
    }

    /// <summary>The brace-matched block beginning at or after <paramref name="from"/>.</summary>
    private static string Block(string text, int from)
    {
        var open = text.IndexOf('{', from);
        if (open < 0) return string.Empty;
        var close = Close(text, open);
        return close < 0 ? text[open..] : text[open..close];
    }

    private static int Close(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }

        return -1;
    }

    private static int Line(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static string Relative(string path)
        => Path.GetRelativePath(RepoRoot(), path).Replace(Path.DirectorySeparatorChar, '/');

    private static IEnumerable<(string Path, string Scrubbed)> Sources()
    {
        var root = Path.Combine(RepoRoot(), "src");
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj")) continue;
            yield return (path, Scrub(File.ReadAllText(path)));
        }
    }

    /// <summary>
    /// The file with every comment and every string literal replaced by spaces, character for
    /// character, so a rule about code cannot be tripped by prose that describes it — and a match
    /// found here is at the same offset in the original.
    /// </summary>
    private static string Scrub(string text)
    {
        var outp = text.ToCharArray();
        var i = 0;

        void Blank(int from, int to)
        {
            for (var j = from; j < to && j < outp.Length; j++) if (outp[j] != '\n' && outp[j] != '\r') outp[j] = ' ';
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                Blank(i, end);
                i = end;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                Blank(i, end);
                i = end;
            }
            else if (c == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
            {
                // A raw string literal: opened and closed by runs of three quotes or more.
                var fence = 0;
                while (i + fence < text.Length && text[i + fence] == '"') fence++;
                var quotes = new string('"', fence);
                var end = text.IndexOf(quotes, i + fence, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + fence;
                Blank(i, end);
                i = end;
            }
            else if (c == '@' && i + 1 < text.Length && text[i + 1] == '"')
            {
                var j = i + 2;
                while (j < text.Length)
                {
                    if (text[j] == '"')
                    {
                        if (j + 1 < text.Length && text[j + 1] == '"') { j += 2; continue; }
                        j++;
                        break;
                    }

                    j++;
                }

                Blank(i, j);
                i = j;
            }
            else if (c is '"' or '\'')
            {
                var j = i + 1;
                while (j < text.Length && text[j] != c)
                {
                    if (text[j] == '\\') j++;
                    if (j >= text.Length || text[j] == '\n') break;
                    j++;
                }

                Blank(i, Math.Min(j + 1, text.Length));
                i = Math.Min(j + 1, text.Length);
            }
            else
            {
                i++;
            }
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
