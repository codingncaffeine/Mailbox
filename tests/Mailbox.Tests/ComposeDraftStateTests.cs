using System.Text.RegularExpressions;

namespace Mailbox.Tests;

/// <summary>
/// What a compose window keeps, and when it asks before dropping it.
/// </summary>
/// <remarks>
/// These read the source rather than the assemblies, for the reason <see cref="AuditChromeSweepTests"/>
/// gives: the test project does not reference <c>Mailbox.App</c>, and exercising a compose window
/// needs a running Avalonia application.
/// <para>
/// The rule they hold is one bug's worth of scar tissue. Saving a draft used to set the same flag
/// sending does — the reasoning being that a written draft needs no keeping — but nothing ever put
/// the flag back down. So the first save, which is usually the autosave timer and needs nobody to
/// press anything, silenced the close prompt and stopped the timer for the life of the window, and
/// everything typed afterwards was dropped on close without a word. Whether there is anything
/// worth keeping is a question about unwritten changes, and it has to be asked per keystroke.
/// </para>
/// </remarks>
public class ComposeDraftStateTests
{
    /// <summary>
    /// Saving a draft clears the unwritten-changes flag and touches nothing else. Setting the
    /// sent flag here is the regression: the message has not gone anywhere, and the next
    /// keystroke makes the draft on disk stale.
    /// </summary>
    [Fact]
    public void SavingADraftDoesNotMarkTheMessageSent()
    {
        var text = Scrubbed("src/Mailbox.App/Views/ComposeSurface.cs");
        var body = Body(text, "public async Task SaveDraftAsync()");

        Assert.False(string.IsNullOrEmpty(body), "SaveDraftAsync was not found — has it been renamed?");
        Assert.DoesNotContain("_sent", body, StringComparison.Ordinal);

        // The clearing half is the part that must stay, or every close would ask.
        Assert.Matches(new Regex(@"_dirty\s*=\s*false"), body);
    }

    /// <summary>
    /// The close prompt asks about unwritten changes, not about whether a draft was ever saved.
    /// A saved draft goes stale on the next keystroke, so "a draft exists" is the wrong question.
    /// </summary>
    [Fact]
    public void ClosingAComposeWindowAsksWhenThereAreChangesItHasNotWritten()
    {
        var text = Scrubbed("src/Mailbox.App/Views/ComposeWindow.cs");
        var body = Body(text, "protected override async void OnClosing(WindowClosingEventArgs e)");

        Assert.False(string.IsNullOrEmpty(body), "OnClosing was not found — has its signature changed?");

        // The guard that closes without asking, up to its first statement.
        var guard = body[..Math.Max(body.IndexOf("base.OnClosing", StringComparison.Ordinal), 0)];
        Assert.Contains("IsDirty", guard, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two flags stay distinct: one says the message has gone, the other says there is
    /// something unwritten. A window that conflated them is what the tests above are about.
    /// </summary>
    [Fact]
    public void TheSurfaceTellsSentApartFromUnwritten()
    {
        var text = Scrubbed("src/Mailbox.App/Views/ComposeSurface.cs");

        Assert.Matches(new Regex(@"public\s+bool\s+IsSent\s*=>\s*_sent\s*;"), text);
        Assert.Matches(new Regex(@"public\s+bool\s+IsDirty\s*=>\s*_dirty\s*;"), text);
    }

    // ---- Reading the source ------------------------------------------------------------------

    /// <summary>The brace-matched body of the member whose declaration starts with <paramref name="signature"/>.</summary>
    private static string Body(string text, string signature)
    {
        var at = text.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return string.Empty;

        var open = text.IndexOf('{', at + signature.Length);
        if (open < 0) return string.Empty;

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[open..i];
        }

        return string.Empty;
    }

    /// <summary>
    /// The file with comments and string literals blanked, character for character.
    /// </summary>
    /// <remarks>
    /// Necessary rather than tidy: the comment that explains why the sent flag is not set here
    /// names the flag, and an unscrubbed read would fail on the explanation of the fix.
    /// </remarks>
    private static string Scrubbed(string relative)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
        var outp = text.ToCharArray();

        for (var i = 0; i < text.Length; i++)
        {
            var run = text[i] switch
            {
                '/' when i + 1 < text.Length && text[i + 1] == '/' => Until(text, i, "\n", 0),
                '/' when i + 1 < text.Length && text[i + 1] == '*' => Until(text, i, "*/", 2),
                '"' when i > 0 && text[i - 1] == '@' => Verbatim(text, i),
                '"' => Quoted(text, i),
                '\'' => Quoted(text, i, '\''),
                _ => 0,
            };

            for (var j = i; j < i + run && j < text.Length; j++)
            {
                if (outp[j] != '\n') outp[j] = ' ';
            }

            if (run > 1) i += run - 1;
        }

        return new string(outp);
    }

    private static int Until(string text, int from, string terminator, int include)
    {
        var end = text.IndexOf(terminator, from + 2, StringComparison.Ordinal);
        return end < 0 ? text.Length - from : end - from + include;
    }

    private static int Quoted(string text, int from, char quote = '"')
    {
        for (var i = from + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == quote) return i - from + 1;
            if (text[i] == '\n') break;
        }

        return 1;
    }

    private static int Verbatim(string text, int from)
    {
        for (var i = from + 1; i < text.Length; i++)
        {
            if (text[i] != '"') continue;
            if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
            return i - from + 1;
        }

        return 1;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
