using System.Text.RegularExpressions;
using Mailbox.App.Views;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The door list and the switch that implements the doors say the same thing.
/// </summary>
/// <remarks>
/// The audit's inventory of harness doors was, for a while, whatever a script could parse out of
/// <c>MainWindow</c>'s <c>MAILBOX_PEEK</c> switch — and it got it wrong twice in one session,
/// both times silently. A fixed line window truncated the switch and dropped nine real doors; a
/// nested switch contributed <c>MAILBOX_PROGRESS_STATE</c>'s values as if they were doors of
/// their own. Either way the batch runner then reported on a list that was not the truth.
/// <para>
/// <see cref="HarnessDoors.All"/> is the list now. This is what keeps it honest: add a case and
/// forget the list, or leave a name behind after removing a case, and the build says so.
/// </para>
/// </remarks>
public class AuditDoorInventoryTests
{
    [Fact]
    public void TheDoorListMatchesTheSwitchThatImplementsIt()
    {
        var cases = CaseLabelsOfThePeekSwitch();

        Assert.True(cases.Count > 80, $"only {cases.Count} case labels parsed — the sweep is not reading the switch");

        var listed = HarnessDoors.All.ToHashSet(StringComparer.Ordinal);

        var missing = cases.Where(c => !listed.Contains(c)).ToList();
        var stale = HarnessDoors.All.Where(k => !cases.Contains(k)).ToList();

        Assert.True(
            missing.Count == 0,
            $"the shell answers MAILBOX_PEEK={string.Join(", ", missing)} and HarnessDoors.All does not "
            + "name them — a door missing from the inventory is a surface nobody audits.");

        Assert.True(
            stale.Count == 0,
            $"HarnessDoors.All names {string.Join(", ", stale)} and the switch no longer answers them — "
            + "a door in the inventory that opens onto nothing sends the next reader hunting a bug "
            + "that is not there.");
    }

    /// <summary>
    /// The case labels of the peek switch, at its own brace depth.
    /// </summary>
    /// <remarks>
    /// Depth matters: <c>MAILBOX_PEEK=newkey</c> parses name/address/pass in a switch of its own,
    /// and <c>MAILBOX_PEEK=progress</c> switches again on <c>MAILBOX_PROGRESS_STATE</c>. Labels
    /// nested inside a case are that pose's arguments, not doors, and counting them was one of
    /// the two ways the old parse went wrong.
    /// </remarks>
    private static HashSet<string> CaseLabelsOfThePeekSwitch()
    {
        var text = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Mailbox.App", "Views", "MainWindow.axaml.cs"));

        var anchor = text.IndexOf(
            "GetEnvironmentVariable(\"MAILBOX_PEEK\")?.ToLowerInvariant()", StringComparison.Ordinal);
        Assert.True(anchor >= 0, "the MAILBOX_PEEK switch was not found — has it been renamed?");

        var open = text.IndexOf('{', anchor);
        var depth = 0;
        var end = text.Length;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) { end = i; break; }
        }

        var labels = new HashSet<string>(StringComparer.Ordinal);
        var body = text[open..end];
        depth = 0;
        var lineStart = 0;

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}') depth--;
            else if (body[i] == '\n')
            {
                var match = CaseLabel.Match(body[lineStart..i]);
                if (match.Success && depth == 1) labels.Add(match.Groups[1].Value);
                lineStart = i + 1;
            }
        }

        return labels;
    }

    private static readonly Regex CaseLabel = new(@"^\s+case ""([a-z0-9]+)"":", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
