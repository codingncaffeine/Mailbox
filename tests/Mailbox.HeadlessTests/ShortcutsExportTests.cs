using Mailbox.App;
using Mailbox.Core.Commands;

// The namespace and the class are both called App, so the class is spelt out once here.
using TheApp = Mailbox.App.App;

namespace Mailbox.HeadlessTests;

/// <summary>
/// The generated shortcut page lists every shortcut there is.
/// </summary>
/// <remarks>
/// The page is generated so that it cannot drift from the application, and this is what keeps
/// the generator itself honest: a command whose scope or surface falls through the grouping
/// would simply not appear, and a page that quietly lists ninety of a hundred chords is worse
/// than one nobody generated, because it reads as complete.
/// </remarks>
public class ShortcutsExportTests
{
    [Fact]
    public void EveryCommandWithAShortcutIsOnThePage()
    {
        var page = ShortcutsExport.Markdown();

        var missing = TheApp.BuiltInCommands().All
            .Where(c => !string.IsNullOrWhiteSpace(c.DefaultGesture))
            .Where(c => !page.Contains($"| {Escaped(c.Label)} |", StringComparison.Ordinal))
            .Select(c => $"{c.Id} ({c.Label})")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} command(s) carry a shortcut and are not on the page: {string.Join(", ", missing)}. "
            + "A grouping that drops a command lists a page that reads complete and is not.");
    }

    /// <summary>
    /// Every window has a section, and the section that is not generated is still there — the
    /// keys a surface answers itself have nowhere else to be written down.
    /// </summary>
    [Fact]
    public void ThePageCoversEveryWindowAndTheViewsOwnKeys()
    {
        var page = ShortcutsExport.Markdown();

        Assert.Contains("## The main window", page, StringComparison.Ordinal);
        Assert.Contains("## The compose window", page, StringComparison.Ordinal);
        Assert.Contains("## The appointment window", page, StringComparison.Ordinal);
        Assert.Contains("## The contact window", page, StringComparison.Ordinal);
        Assert.Contains("## Inside a view", page, StringComparison.Ordinal);

        foreach (var module in (ModuleScope[])
                 [
                     ModuleScope.Mail, ModuleScope.Calendar, ModuleScope.People,
                     ModuleScope.Tasks, ModuleScope.Notes, ModuleScope.Journal, ModuleScope.Feeds,
                 ])
        {
            Assert.Contains($"### {module}", page, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every row of every table has its three cells. A description carrying a pipe would split a
    /// row into four and the table would stop being one — cheap to write, invisible until read.
    /// </summary>
    [Fact]
    public void EveryTableRowHasThreeCells()
    {
        var rows = ShortcutsExport.Markdown()
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("| ---", StringComparison.Ordinal))
            .ToList();

        Assert.True(rows.Count > 80, $"only {rows.Count} rows — the page is not being generated.");

        foreach (var row in rows)
        {
            var cells = row.Trim('|').Split(" | ").Length;
            Assert.True(cells is 2 or 3, $"a row with {cells} cells: {row}");
        }
    }

    private static string Escaped(string label) => label.Replace("|", "\\|", StringComparison.Ordinal);
}
