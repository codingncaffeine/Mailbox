using System.Text.RegularExpressions;
using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// What Phase 2 proved about the Quick Access Toolbar, the Backstage, the status bar and the
/// title search box, promoted so the classes of fault it caught stay caught.
/// </summary>
/// <remarks>
/// Three of these read the source rather than the assemblies, following
/// <see cref="AuditChromeSweepTests"/>: the test project does not reference <c>Mailbox.App</c>,
/// and a reflection sweep over views would need a running Avalonia application. The toolbar's
/// own state is <c>Mailbox.Core</c> and is exercised directly.
/// </remarks>
public class AuditBackstageTests
{
    // ---- The toolbar's state ----------------------------------------------------------------

    /// <summary>
    /// Everything the customize flyout and the Options page do to the toolbar is written to the
    /// settings, and a fresh layout over the same settings reads it back.
    /// </summary>
    /// <remarks>
    /// A capture run cannot show this: <c>MAILBOX_CAPTURE</c> gives the application a scratch
    /// settings file named after its own process, so nothing a posed run writes is there for the
    /// next one to read. The harness proves the drawing half — a posed <c>ribbon.qat.commands</c>
    /// draws that toolbar — and this proves the remembering half.
    /// </remarks>
    [Fact]
    public void TheToolbarRemembersWhatWasDoneToIt()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mailbox-qat-{Guid.NewGuid():N}.json");
        try
        {
            var shipped = new[] { new CommandId("app.sendreceive.all"), new CommandId("app.undo") };

            var first = new QuickAccessLayout(new SettingsStore(file), shipped);
            Assert.Equal(shipped, first.Commands);

            first.Toggle(new CommandId("mail.new"));          // the flyout's tick: added
            first.Toggle(new CommandId("app.undo"));          // and the same tick again: removed
            first.AddSeparator();
            first.Move(new CommandId("mail.new"), -1);
            first.Placement = QuickAccessPlacement.BelowRibbon;
            first.IsVisible = false;
            first.ShowLabels = true;
            first.Modify(new CommandId("mail.new"), "Write", "flag");

            var second = new QuickAccessLayout(new SettingsStore(file), shipped);

            Assert.Equal(
                ["mail.new", "app.sendreceive.all", RibbonItem.SeparatorId.Value],
                second.Commands.Select(c => c.Value));
            Assert.Equal(QuickAccessPlacement.BelowRibbon, second.Placement);
            Assert.False(second.IsVisible);
            Assert.True(second.ShowLabels);
            Assert.Equal(new QuickAccessOverride("Write", "flag"), second.OverrideFor(new CommandId("mail.new")));

            // Reset is the flyout's last entry, and it puts the placement and the visibility back
            // as well as the commands — a toolbar reset to the shipped set but still hidden is
            // not what a reader asking for it back means.
            second.Reset();
            var third = new QuickAccessLayout(new SettingsStore(file), shipped);
            Assert.Equal(shipped, third.Commands);
            Assert.Equal(QuickAccessPlacement.AboveRibbon, third.Placement);
            Assert.True(third.IsVisible);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// An empty toolbar is a real choice and survives a restart; never having customized one is
    /// what brings the shipped set back.
    /// </summary>
    [Fact]
    public void AnEmptyToolbarIsNotTheSameAsAnUncustomizedOne()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mailbox-qat-{Guid.NewGuid():N}.json");
        try
        {
            var shipped = new[] { new CommandId("app.undo") };

            var emptied = new QuickAccessLayout(new SettingsStore(file), shipped);
            emptied.Remove(new CommandId("app.undo"));
            Assert.Empty(new QuickAccessLayout(new SettingsStore(file), shipped).Commands);

            Assert.Equal(shipped, new QuickAccessLayout(new SettingsStore(Path.Combine(
                Path.GetTempPath(), $"mailbox-qat-{Guid.NewGuid():N}.json")), shipped).Commands);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// A hand-edited settings file is allowed to be wrong: an id the format rejects costs one
    /// button, never the launch.
    /// </summary>
    [Fact]
    public void AMalformedCommandIdCostsOneButtonAndNotTheLaunch()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mailbox-qat-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsStore(file);
            settings.Set(QuickAccessLayout.CommandsKey, "mail.new,NotACommand,mail.delete");
            settings.Set(QuickAccessLayout.OverridesKey, "{ this is not json");

            var layout = new QuickAccessLayout(new SettingsStore(file), []);
            Assert.Equal(["mail.new", "mail.delete"], layout.Commands.Select(c => c.Value));
            Assert.Null(layout.OverrideFor(new CommandId("mail.new")));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Every command the customize flyout offers is a command the catalogue actually has.
    /// </summary>
    /// <remarks>
    /// The flyout skips a candidate the catalogue cannot resolve, silently — so a renamed command
    /// leaves a shorter menu and nothing says so.
    /// </remarks>
    [Fact]
    public void EveryToolbarCandidateIsACommandTheCatalogueHas()
    {
        var catalog = AuditWiringSweepTests.Registered();

        var missing = DefaultRibbonLayouts.QuickAccessCandidates
            .Where(id => !catalog.TryGet(id, out _))
            .Select(id => id.Value)
            .ToList();

        Assert.True(missing.Count == 0,
            "The toolbar's customize menu offers commands the catalogue does not have: "
            + string.Join(", ", missing));
    }

    // ---- The empty-popup class ---------------------------------------------------------------

    /// <summary>
    /// No <c>MenuFlyout</c> is left to fill itself from its own <c>Opening</c> event alone.
    /// </summary>
    /// <remarks>
    /// This is the fault Phase 2 found in the Quick Access Toolbar's customize menu, and it is
    /// invisible to everything else the audit does. The popup's presenter is built from the
    /// menu's entries when the popup is created, and that happens before <c>Opening</c> is
    /// raised: a menu that had nothing in it at that moment presents an empty popup however many
    /// entries the handler then adds. Nothing catches it — the capture cannot photograph a popup,
    /// <c>IsOpen</c> comes back true, and a read-back that lists <c>Items</c> lists all of them,
    /// because the objects exist and simply never reach the screen.
    /// <para>
    /// The rule is therefore about the code: a flyout whose entries arrive on <c>Opening</c> must
    /// also be filled where it is built. Adding a handler is fine — that is how a tick stays
    /// fresh; adding one and nothing else is not.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Menus known to be filled only on <c>Opening</c> and not yet repaired, by the variable that
    /// holds them.
    /// </summary>
    /// <remarks>
    /// Empty, and it stays empty. It held one entry — the Simplified ribbon's "…" overflow menu,
    /// the same shape the Quick Access Toolbar's customize menu was — until the ribbon lane
    /// measured that surface: <c>open=True, 13 entries, popup not presented</c> at 820 wide,
    /// beside a display-options menu in the same run that measured 214x198. Filled where it is
    /// built, it presents 202x299. Nothing is exempt now, so the next one is caught.
    /// </remarks>
    private static readonly string[] NotYetFilledBeforeShowing = [];

    [Fact]
    public void NoMenuFillsItselfOnlyFromItsOwnOpeningEvent()
    {
        var offenders = new List<string>();

        foreach (var (path, text) in Sources())
        {
            foreach (Match handler in Regex.Matches(text, @"(\w+)\.Opening \+= "))
            {
                var menu = handler.Groups[1].Value;
                if (NotYetFilledBeforeShowing.Contains(menu)) continue;

                // The same method has to fill it outside the handler too. Taken over the whole
                // file rather than the enclosing method: several of these are built by a helper
                // and filled by a named method the handler also calls.
                var fillsElsewhere = Regex.Matches(text, $@"{Regex.Escape(menu)}\.Items\.(Add|Clear)|Fill\w*\({Regex.Escape(menu)}|Populate\({Regex.Escape(menu)}|{Regex.Escape(menu)}\.ItemsSource\s*=")
                    .Select(m => m.Index)
                    .Any(at => at < handler.Index || at > EndOfLine(text, handler.Index));

                // A handler that assigns Content rather than entries is a different animal: a
                // Flyout with one child, which the presenter takes whenever it is set.
                var isContent = text.AsSpan(handler.Index, Math.Min(160, text.Length - handler.Index))
                    .IndexOf(".Content") >= 0;

                if (!fillsElsewhere && !isContent)
                {
                    offenders.Add($"{Relative(path)}: “{menu}” is only ever filled from its own Opening event.");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A menu filled only on Opening presents an empty popup:\n  " + string.Join("\n  ", offenders));
    }

    // ---- The Backstage -----------------------------------------------------------------------

    /// <summary>
    /// Every action a Backstage button or menu entry raises is handled somewhere.
    /// </summary>
    /// <remarks>
    /// The Backstage's buttons raise strings. <c>BackstageActions.RunAsync</c> switches on them
    /// and its default is to do nothing at all — so a typo, or a button added without its case,
    /// is a button that silently does nothing, which is precisely the standing rule this phase
    /// exists to hold. The shell handles a few of them first, because they act on what the shell
    /// is showing.
    /// </remarks>
    [Fact]
    public void EveryBackstageActionIsHandled()
    {
        var view = Read("src/Mailbox.App/Views/BackstageView.cs");
        var actions = Read("src/Mailbox.App/Views/BackstageActions.cs");
        var shell = Read("src/Mailbox.App/Views/MainWindow.axaml.cs");

        // Raised: BuildSection's named argument, and MenuEntry's fourth positional one.
        var raised = Regex.Matches(view, @"action: ""([a-z.]+)""\)")
            .Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(view, @"MenuEntry\(""[^""]*"", ""[^""]*"",\s*""[^""]*"",\s*""([a-z.]+)""", RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(raised);

        var handled = Regex.Matches(actions + shell, @"case ""([a-z.]+)"":")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = raised.Where(a => !handled.Contains(a)).ToList();

        Assert.True(orphans.Count == 0,
            "Backstage buttons that raise an action nothing handles: " + string.Join(", ", orphans));
    }

    /// <summary>
    /// Every rail page the Backstage lists is a page it can build, or is greyed.
    /// </summary>
    /// <remarks>
    /// The page switch falls through to a placeholder that writes "&lt;id&gt; — not built yet"
    /// into the window. Print and Save As both sat behind that placeholder once, on the most-used
    /// page the Backstage has, while every command behind them was built.
    /// </remarks>
    [Fact]
    public void EveryBackstageRailPageIsBuiltOrGreyed()
    {
        var view = Read("src/Mailbox.App/Views/BackstageView.cs");

        // Exit and Options never show a page: their rail entries raise an event and return.
        string[] notPages = ["exit", "options"];

        var built = Regex.Matches(view, @"^\s+""([a-z]+)"" => Build\w+\(\),", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var placeholders = Regex.Matches(view, @"RailItem\(""([a-z]+)"", ""[^""]+"", ""[^""]+""(, enabled: false)?\)")
            .Where(m => !m.Groups[2].Success)
            .Select(m => m.Groups[1].Value)
            .Where(id => !notPages.Contains(id) && !built.Contains(id))
            .ToList();

        Assert.True(placeholders.Count == 0,
            "Backstage rail entries a reader can press that open a placeholder: "
            + string.Join(", ", placeholders));
    }

    /// <summary>
    /// The Backstage's Account Information page describes the accounts that are open, not a
    /// fixture.
    /// </summary>
    /// <remarks>
    /// Its account picker was a pair of literals — an address and a protocol — so the page drew
    /// the same two lines whatever was in the store: byte-identical captures from a store with
    /// three accounts and a store with one, and the one it named was in neither. The page beside
    /// it, Mailbox Account, reads <c>App.Accounts</c> and differs between the two, which is what
    /// this page has to do as well.
    /// </remarks>
    [Fact]
    public void TheAccountInformationPageReadsTheAccountsRatherThanNamingOne()
    {
        var view = Read("src/Mailbox.App/Views/BackstageView.cs");
        var picker = Body(view, view.IndexOf("private Control BuildAccountPicker()", StringComparison.Ordinal));

        Assert.False(
            Regex.IsMatch(picker, @"Text = ""[^""]*@[^""]*"""),
            "The Backstage's account picker names an address in code, so it draws that address "
            + "whatever account is open.");

        Assert.True(
            picker.Contains("App.Accounts", StringComparison.Ordinal),
            "The Backstage's account picker never asks which accounts are open.");
    }

    // ---- The status bar ----------------------------------------------------------------------

    /// <summary>
    /// The status bar counts what the list is showing, and says so in the reference's wording.
    /// </summary>
    /// <remarks>
    /// Measured against a seeded store rather than asserted: an Inbox of two reads "Items: 2",
    /// the unified Inbox over three accounts of 2, 4 and 8 reads "Items: 14", and both were read
    /// from the running shell. What this holds is the shape — a count of the rows on screen and a
    /// count of the unread among them, which is what the reference's own bar carries.
    /// </remarks>
    [Fact]
    public void TheStatusBarCountsTheRowsOnScreen()
    {
        var shell = Read("src/Mailbox.App/ViewModels/ShellViewModel.cs");
        var statusLeft = shell[shell.IndexOf("public string StatusLeft", StringComparison.Ordinal)..];
        statusLeft = statusLeft[..statusLeft.IndexOf(';')];

        Assert.Contains("VisibleCount", statusLeft, StringComparison.Ordinal);
        Assert.Contains("IsUnread", statusLeft, StringComparison.Ordinal);
        Assert.Contains("Items:", statusLeft, StringComparison.Ordinal);
        Assert.Contains("Unread:", statusLeft, StringComparison.Ordinal);
    }

    // ---- The title search box ----------------------------------------------------------------

    /// <summary>
    /// The search box's three scopes are the reference's three, in its order, and each is what
    /// the search actually runs against.
    /// </summary>
    /// <remarks>
    /// Proven against the seeded store through the real box: "example" finds 2 in This Folder,
    /// 2 in Current Mailbox and 14 in All Mailboxes, which is that store's one folder, that
    /// account, and every account. This holds the wiring the numbers came from — a scope that
    /// stopped narrowing would still read "All Mailboxes" on the label.
    /// </remarks>
    [Fact]
    public void EachSearchScopeNarrowsToWhatItsLabelSays()
    {
        var shell = Read("src/Mailbox.App/ViewModels/ShellViewModel.cs");

        Assert.Contains(@"[""This Folder"", ""Current Mailbox"", ""All Mailboxes""]", shell, StringComparison.Ordinal);

        var run = Body(shell, shell.IndexOf("private void RunSearch()", StringComparison.Ordinal));

        // This Folder searches one folder of one account; Current Mailbox every folder of that
        // account; All Mailboxes every account. The folder id is the whole difference between
        // the first two, and passing it in both would make the scope a label and nothing else.
        Assert.Contains("SearchScope.ThisFolder when current.Account is not null =>", run, StringComparison.Ordinal);
        Assert.Contains("[(current.Account, current.FolderId)]", run, StringComparison.Ordinal);
        Assert.Contains("[(current.Account, null)]", run, StringComparison.Ordinal);
        Assert.Contains("_accounts.All.Select(a => (a, (long?)null))", run, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static int EndOfLine(string text, int from)
    {
        var at = text.IndexOf('\n', from);
        return at < 0 ? text.Length : at;
    }

    /// <summary>The braced block that starts at or after <paramref name="from"/>.</summary>
    private static string Body(string text, int from)
    {
        if (from < 0) return string.Empty;

        var open = text.IndexOf('{', from);
        if (open < 0) return string.Empty;

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[open..(i + 1)];
        }

        return text[open..];
    }

    private static string Read(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

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
            yield return (path, File.ReadAllText(path));
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
