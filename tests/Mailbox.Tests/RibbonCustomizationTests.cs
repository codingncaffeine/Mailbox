using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

public class RibbonTreeTests
{
    private static RibbonLayout Shipped => DefaultRibbonLayouts.Mail;

    [Fact]
    public void ReadsEveryTabButTheBackstage()
    {
        var tree = RibbonTree.From(Shipped);

        Assert.Equal(["home", "sendreceive", "folder", "view", "help"], tree.Tabs.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(tree.Tabs, t => t.Id == "file");
    }

    [Fact]
    public void ReadsTheSimplifiedClustersAsGroups()
    {
        var home = RibbonTree.From(Shipped).Tabs.Single(t => t.Id == "home");

        Assert.Equal("Move & Delete", home.Groups[1].Label);
        Assert.Equal(
            ["mail.delete", "mail.archive", "mail.move"],
            home.Groups[1].Commands.Select(c => c.Value).ToArray());
    }

    [Fact]
    public void AnUneditedTreeRebuildsTheShippedRibbon()
    {
        var applied = RibbonTree.From(Shipped).ApplyTo(Shipped);

        Assert.Equal(
            Shipped.Tabs.Select(t => t.Id).ToArray(),
            applied.Tabs.Select(t => t.Id).ToArray());

        foreach (var (tab, bar) in Shipped.Simplified)
        {
            Assert.Equal(
                bar.Flatten().Select(i => i.Command.Value).ToArray(),
                applied.SimplifiedRows[tab].Select(i => i.Command.Value).ToArray());
        }
    }

    /// <summary>
    /// The File tab is not in the tree, so it has to survive being applied — it is how the
    /// Backstage is reached, and losing it would strand every account setting behind it.
    /// </summary>
    [Fact]
    public void TheBackstageTabSurvivesAndStaysLeftmost()
    {
        var tree = RibbonTree.From(Shipped);
        tree.Tabs.Reverse();

        var applied = tree.ApplyTo(Shipped);

        Assert.True(applied.Tabs[0].IsBackstage);
        Assert.Equal("file", applied.Tabs[0].Id);
    }

    [Fact]
    public void AnUntickedTabLeavesTheRibbon()
    {
        var tree = RibbonTree.From(Shipped);
        tree.Tabs.Single(t => t.Id == "view").IsVisible = false;

        var applied = tree.ApplyTo(Shipped);

        Assert.DoesNotContain(applied.Tabs, t => t.Id == "view");
        Assert.False(applied.SimplifiedRows.ContainsKey("view"));

        // Still in the tree, or re-ticking it would be impossible.
        Assert.Contains(tree.Tabs, t => t.Id == "view");
    }

    [Fact]
    public void RenamingATabRenamesItOnTheRibbon()
    {
        var tree = RibbonTree.From(Shipped);
        tree.Tabs.Single(t => t.Id == "home").Label = "Mail";

        Assert.Equal("Mail", tree.ApplyTo(Shipped).FindTab("home")!.Label);
    }

    /// <summary>
    /// A command carries its authored shape wherever it goes. New Email is a split button on the
    /// bar, and moving it to another group must not quietly flatten it into a plain one.
    /// </summary>
    [Fact]
    public void AMovedCommandKeepsTheShapeItWasAuthoredWith()
    {
        var tree = RibbonTree.From(Shipped);
        var home = tree.Tabs.Single(t => t.Id == "home");

        home.Groups[0].Commands.Clear();
        home.Groups[2].Commands.Insert(0, new CommandId("mail.new"));

        var respond = tree.ApplyTo(Shipped).Simplified["home"].Groups
            .Single(g => g.Id == "respond");

        Assert.Equal(RibbonItemKind.SplitButton, respond.Items[0].Kind);
    }

    /// <summary>
    /// A command the shipped bar never places has no shape to inherit, so it becomes what the
    /// Simplified bar is made of: a small labelled button.
    /// </summary>
    [Fact]
    public void ANewlyPlacedCommandBecomesASmallLabelledButton()
    {
        var tree = RibbonTree.From(Shipped);
        tree.Tabs.Single(t => t.Id == "home").Groups[0].Commands.Add(new CommandId("mail.snooze"));

        var item = tree.ApplyTo(Shipped).Simplified["home"].Groups[0].Items[^1];

        Assert.Equal(RibbonItemSize.Small, item.Size);
        Assert.Equal(RibbonItemKind.Button, item.Kind);
        Assert.True(item.ShowLabel);
    }

    [Fact]
    public void CustomIdsAreUniqueAcrossTheWholeTree()
    {
        var tree = RibbonTree.From(Shipped);

        var first = tree.NextTabId();
        tree.Tabs.Add(new RibbonTreeTab { Id = first, Label = "New Tab", IsCustom = true });

        Assert.NotEqual(first, tree.NextTabId());

        var group = tree.NextGroupId();
        tree.Tabs[0].Groups.Add(new RibbonTreeGroup { Id = group, Label = "New Group", IsCustom = true });

        Assert.NotEqual(group, tree.NextGroupId());
    }

    [Fact]
    public void ResettingOneTabLeavesTheOthersEdited()
    {
        var tree = RibbonTree.From(Shipped);
        tree.Tabs.Single(t => t.Id == "home").Groups.Clear();
        tree.Tabs.Single(t => t.Id == "view").Label = "Display";

        Assert.True(tree.ResetTab(Shipped, "home"));

        Assert.NotEmpty(tree.Tabs.Single(t => t.Id == "home").Groups);
        Assert.Equal("Display", tree.Tabs.Single(t => t.Id == "view").Label);
    }

    [Fact]
    public void ResettingACustomTabRemovesIt()
    {
        var tree = RibbonTree.From(Shipped);
        var id = tree.NextTabId();
        tree.Tabs.Add(new RibbonTreeTab { Id = id, Label = "New Tab", IsCustom = true });

        Assert.True(tree.ResetTab(Shipped, id));
        Assert.DoesNotContain(tree.Tabs, t => t.Id == id);
    }

    [Fact]
    public void ATabAddedByALaterBuildAppearsInAnOlderDocument()
    {
        var tree = RibbonTree.From(Shipped);
        tree.Tabs.RemoveAll(t => t.Id == "view");

        tree.Reconcile(Shipped);

        Assert.Contains(tree.Tabs, t => t.Id == "view");
    }

    [Fact]
    public void DiffersFromReportsOnlyRealEdits()
    {
        var tree = RibbonTree.From(Shipped);
        Assert.False(tree.DiffersFrom(Shipped));

        tree.Tabs[0].Groups[0].Label = "Compose";
        Assert.True(tree.DiffersFrom(Shipped));
    }
}

public class RibbonCustomizationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mailbox-ribbon-" + Guid.NewGuid().ToString("n"));

    private static RibbonLayout Shipped => DefaultRibbonLayouts.Mail;

    private string At(string name)
    {
        Directory.CreateDirectory(_directory);
        return System.IO.Path.Combine(_directory, name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NothingSavedMeansTheShippedRibbon()
    {
        var store = new RibbonCustomization(At("ribbon.json"));

        Assert.False(store.IsCustomized);
        Assert.Same(Shipped, store.Apply(Shipped));
    }

    [Fact]
    public void SavedEditsSurviveAReopen()
    {
        var path = At("ribbon.json");
        var tree = RibbonTree.From(Shipped);
        tree.Tabs.Single(t => t.Id == "home").Groups[0].Label = "Compose";
        tree.Tabs.Single(t => t.Id == "help").IsVisible = false;

        new RibbonCustomization(path).Save(tree, Shipped);

        var reopened = new RibbonCustomization(path);
        Assert.True(reopened.IsCustomized);

        var applied = reopened.Apply(Shipped);
        Assert.Equal("Compose", applied.Simplified["home"].Groups[0].Label);
        Assert.DoesNotContain(applied.Tabs, t => t.Id == "help");
        Assert.True(applied.IsUserModified);
    }

    /// <summary>
    /// Undoing every edit by hand should leave a user following the shipped ribbon as later
    /// builds change it, rather than pinned to a copy of what it looked like today.
    /// </summary>
    [Fact]
    public void SavingAnUneditedTreeDeletesTheDocument()
    {
        var path = At("ribbon.json");
        var store = new RibbonCustomization(path);

        var tree = RibbonTree.From(Shipped);
        tree.Tabs[0].Label = "Mail";
        store.Save(tree, Shipped);
        Assert.True(store.IsCustomized);

        store.Save(RibbonTree.From(Shipped), Shipped);
        Assert.False(store.IsCustomized);
    }

    [Fact]
    public void ResetDiscardsEverything()
    {
        var path = At("ribbon.json");
        var store = new RibbonCustomization(path);

        var tree = RibbonTree.From(Shipped);
        tree.Tabs[0].Label = "Mail";
        store.Save(tree, Shipped);

        store.Reset();

        Assert.False(store.IsCustomized);
        Assert.Same(Shipped, store.Apply(Shipped));
    }

    [Fact]
    public void ExportCarriesTheToolbarAndImportReadsItBack()
    {
        var path = At("exported.json");
        var tree = RibbonTree.From(Shipped);
        tree.Tabs[0].Label = "Mail";

        RibbonCustomization.Export(path, tree, [new CommandId("mail.reply")], MailboxModule.Mail);

        var imported = RibbonCustomization.Import(path);

        Assert.Equal("Mail", imported.Tree.Tabs[0].Label);
        Assert.Equal(["mail.reply"], imported.QuickAccess!.Select(c => c.Value).ToArray());
    }

    /// <summary>The stored document is the ribbon only; the toolbar lives in the settings file.</summary>
    [Fact]
    public void TheStoredDocumentCarriesNoToolbar()
    {
        var path = At("ribbon.json");
        var tree = RibbonTree.From(Shipped);
        tree.Tabs[0].Label = "Mail";

        new RibbonCustomization(path).Save(tree, Shipped);

        Assert.Null(RibbonCustomization.Import(path).QuickAccess);
    }

    /// <summary>
    /// The file is meant to be editable by hand, so it is allowed to be wrong: a bad command id
    /// costs one button, not the ribbon.
    /// </summary>
    [Fact]
    public void AMalformedCommandIdIsDroppedRatherThanThrowing()
    {
        var path = At("ribbon.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "tabs": [
                {
                  "id": "home",
                  "label": "Home",
                  "visible": true,
                  "groups": [
                    { "id": "new", "label": "New", "commands": ["mail.new", "NOT AN ID", 7] }
                  ]
                }
              ]
            }
            """);

        var applied = new RibbonCustomization(path).Apply(Shipped);

        Assert.Equal(
            ["mail.new"],
            applied.Simplified["home"].Groups[0].Items.Select(i => i.Command.Value).ToArray());
    }

    /// <summary>
    /// A customization is a statement about one module's ribbon, and is applied to that one only.
    /// </summary>
    /// <remarks>
    /// Tab ids repeat across the layouts — every module has a <c>home</c>, most of them a
    /// <c>sendreceive</c>, a <c>view</c> and a <c>help</c> — so a document applied blind rewrote
    /// the Calendar's Home row with Mail's clusters and gave every module a Folder tab holding
    /// nothing. One press of Add on the Options page was enough to do it to all six other modules.
    /// </remarks>
    [Fact]
    public void ACustomizationOfOneModuleLeavesTheOtherModulesAlone()
    {
        var path = At("ribbon.json");
        var store = new RibbonCustomization(path);

        var tree = RibbonTree.From(Shipped);
        tree.Tabs.Single(t => t.Id == "home").Groups[0].Commands.Add(new CommandId("mail.workoffline"));
        store.Save(tree, Shipped);

        Assert.True(store.IsCustomized);
        Assert.Contains(
            "mail.workoffline",
            store.Apply(Shipped).Simplified["home"].Groups[0].Items.Select(i => i.Command.Value));

        foreach (var other in new[]
                 {
                     DefaultRibbonLayouts.Calendar,
                     DefaultRibbonLayouts.People,
                     TasksRibbonLayout.Build(),
                     NotesRibbonLayout.Build(),
                     JournalRibbonLayout.Build(),
                     FeedsRibbonLayout.Build(),
                 })
        {
            Assert.Same(other, store.Apply(other));
        }
    }

    /// <summary>The document names the ribbon it describes, so it can be applied to that one.</summary>
    [Fact]
    public void TheStoredDocumentNamesItsModule()
    {
        var path = At("ribbon.json");
        var tree = RibbonTree.From(Shipped);
        tree.Tabs[0].Label = "Mail";

        new RibbonCustomization(path).Save(tree, Shipped);

        Assert.Equal(MailboxModule.Mail, RibbonCustomization.Import(path).Module);
    }

    /// <summary>
    /// A document written before the module was recorded came out of the only editor there was.
    /// </summary>
    [Fact]
    public void ADocumentWithNoModuleIsTakenAsTheMailRibbon()
    {
        var path = At("ribbon.json");
        File.WriteAllText(
            path,
            """
            {
              "version": 1,
              "tabs": [
                {
                  "id": "home",
                  "label": "Renamed",
                  "visible": true,
                  "groups": [
                    { "id": "new", "label": "New", "commands": ["mail.new"] }
                  ]
                }
              ]
            }
            """);

        var store = new RibbonCustomization(path);

        Assert.Equal("Renamed", store.Apply(Shipped).Tabs.Single(t => t.Id == "home").Label);
        Assert.Same(DefaultRibbonLayouts.Calendar, store.Apply(DefaultRibbonLayouts.Calendar));
    }

    [Fact]
    public void ACorruptDocumentFallsBackToTheShippedRibbon()
    {
        var path = At("ribbon.json");
        File.WriteAllText(path, "{ this is not json");

        var tree = new RibbonCustomization(path).Load(Shipped);

        Assert.Equal(RibbonTree.From(Shipped).Tabs.Count, tree.Tabs.Count);
        Assert.False(tree.DiffersFrom(Shipped));
        Assert.True(File.Exists(path));
    }
}
