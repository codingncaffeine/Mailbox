using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

public class ComposeRibbonTests
{
    private static CommandCatalog Catalog() => CommandCatalogTests.Everything();

    private static RibbonLayout Compose => DefaultRibbonLayouts.Compose;

    private static IEnumerable<CommandId> PlacedAnywhere =>
        Compose.PlacedCommands
            .Concat(Compose.SimplifiedRows.SelectMany(r => r.Value)
                .Where(i => !i.IsSentinel)
                .Select(i => i.Command))
            .Distinct();

    /// <summary>The reference's compose window, left to right.</summary>
    [Fact]
    public void TabsAreInTheReferenceOrder()
        => Assert.Equal(
            ["file", "message", "insert", "options", "formattext", "review", "help"],
            Compose.Tabs.Select(t => t.Id));

    [Fact]
    public void FileIsTheOnlyBackstageTabAndComesFirst()
    {
        Assert.True(Compose.Tabs[0].IsBackstage);
        Assert.Single(Compose.Tabs, t => t.IsBackstage);
    }

    [Fact]
    public void EveryPlacedCommandExistsInTheCatalogue()
    {
        var catalog = Catalog();
        foreach (var id in PlacedAnywhere)
        {
            Assert.True(catalog.TryGet(id, out _), $"Compose ribbon places unknown command '{id}'.");
        }
    }

    [Fact]
    public void EveryTabWithGroupsHasASimplifiedRow()
    {
        foreach (var tab in Compose.Tabs.Where(t => t.Groups.Count > 0))
        {
            Assert.True(Compose.SimplifiedRows.ContainsKey(tab.Id),
                $"Tab '{tab.Id}' has classic groups but no Simplified row.");
        }
    }

    [Fact]
    public void CollapsePrioritiesAreDistinctWithinATab()
    {
        foreach (var tab in Compose.Tabs.Where(t => t.Groups.Count > 1))
        {
            var duplicates = tab.Groups
                .GroupBy(g => g.CollapsePriority)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0,
                $"Tab '{tab.Id}' reuses collapse priority {string.Join(", ", duplicates)}.");
        }
    }

    // ---- The Simplified rows, against the captures ---------------------------------------

    private static string[] Row(string tab) =>
        Compose.SimplifiedRows[tab]
            .Where(i => !i.IsSentinel)
            .Select(i => i.Command.Value)
            .ToArray();

    /// <summary>
    /// Paste leads and Editor closes the row, with the three formatting clusters between them
    /// in the order the capture shows.
    /// </summary>
    [Fact]
    public void MessageRowMatchesTheCapture()
    {
        var row = Row("message");

        Assert.Equal("compose.paste", row[0]);
        Assert.Equal("review.editor", row[^1]);

        // Bold, Italic, Underline stay adjacent and in that order.
        var bold = Array.IndexOf(row, "format.bold");
        Assert.Equal("format.italic", row[bold + 1]);
        Assert.Equal("format.underline", row[bold + 2]);

        // Attach File, Link, Signature are one cluster, in that order.
        var attach = Array.IndexOf(row, "compose.attach.file");
        Assert.Equal("insert.link", row[attach + 1]);
        Assert.Equal("compose.signature", row[attach + 2]);

        // The two importance buttons sit either side of Follow Up.
        var high = Array.IndexOf(row, "compose.importance.high");
        Assert.Equal("compose.importance.low", row[high + 1]);
        Assert.Equal("item.followup", row[high + 2]);
    }

    [Fact]
    public void InsertRowMatchesTheCapture()
    {
        var row = Row("insert");

        Assert.Equal("compose.attach.file", row[0]);
        Assert.Equal("insert.symbol", row[^1]);

        // The illustrations run, in capture order.
        var pictures = Array.IndexOf(row, "insert.pictures");
        Assert.Equal(
            ["insert.pictures", "insert.stockimages", "insert.onlinepictures", "insert.shapes",
             "insert.icons", "insert.models3d", "insert.smartart", "insert.chart"],
            row.Skip(pictures).Take(8));
    }

    [Fact]
    public void OptionsRowMatchesTheCapture()
        => Assert.Equal(
            ["options.themes", "options.colors", "options.fonts", "options.effects",
             "options.pagecolor", "options.voting", "compose.properties"],
            Row("options"));

    [Fact]
    public void ReviewRowMatchesTheCapture()
        => Assert.Equal(
            ["review.spelling", "review.editor", "review.thesaurus", "review.wordcount",
             "mail.readaloud", "review.smartlookup", "review.language", "review.accessibility"],
            Row("review"));

    [Fact]
    public void FormatTextRowOpensWithPasteAndClosesWithZoom()
    {
        var row = Row("formattext");

        Assert.Equal("compose.paste", row[0]);
        Assert.Equal("format.zoom", row[^1]);

        // Strikethrough, subscript and superscript only appear on this tab, not on Message.
        Assert.Contains("format.strikethrough", row);
        Assert.DoesNotContain("format.strikethrough", Row("message"));
    }

    // ---- Availability --------------------------------------------------------------------

    /// <summary>
    /// A button on the ribbon with no entry here is a button whose status nobody decided. That
    /// is the failure this whole table exists to prevent.
    /// </summary>
    [Fact]
    public void EveryPlacedCommandHasAnAvailabilityEntry()
    {
        var missing = PlacedAnywhere
            .Where(id => ComposeAvailability.For(id) is null)
            .Select(id => id.Value)
            .ToList();

        Assert.True(missing.Count == 0,
            "Placed on the compose ribbon with no recorded status:\n" + string.Join("\n", missing));
    }

    /// <summary>The other direction: a status for something no longer on the ribbon is stale.</summary>
    [Fact]
    public void EveryAvailabilityEntryIsActuallyPlaced()
    {
        var placed = PlacedAnywhere.ToHashSet();

        var orphans = ComposeAvailability.All
            .Where(s => !placed.Contains(s.Command))
            .Select(s => s.Command.Value)
            // The window's own actions are not on the ribbon: Send sits beside the address
            // fields, and the rest are on the Quick Access Toolbar.
            .Where(v => v is not ("compose.send" or "compose.save" or "compose.discard"
                or "compose.previous" or "compose.next"))
            .ToList();

        Assert.True(orphans.Count == 0,
            "Recorded status for something not on the compose ribbon:\n" + string.Join("\n", orphans));
    }

    [Fact]
    public void EveryStatusSaysSomethingUseful()
        => Assert.All(ComposeAvailability.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Note));
            Assert.EndsWith(".", s.Note.TrimEnd(), StringComparison.Ordinal);
        });

    /// <summary>
    /// A blocked note has to name what is blocking it, not merely that something is: a phase, an
    /// explicit statement that nothing is planned, an open decision — or, since Phase 5, a gap in
    /// the editor.
    /// </summary>
    /// <remarks>
    /// That fourth category is new and is the honest consequence of the survey: the editor is a
    /// dependency rather than ours, so some of these are blocked on something no phase of this
    /// project will deliver. Saying "Phase 5" about them would be a lie with a date on it.
    /// </remarks>
    [Fact]
    public void EveryBlockedNoteNamesItsBlocker()
        => Assert.All(
            ComposeAvailability.All.Where(s => s.State == ComposeCommandState.Blocked),
            s => Assert.True(
                s.Note.Contains("Phase ", StringComparison.Ordinal)
                || s.Note.Contains("Not planned", StringComparison.Ordinal)
                || s.Note.Contains("No decision", StringComparison.Ordinal)
                || s.Note.Contains("The editor does not", StringComparison.Ordinal),
                $"'{s.Command}' is blocked but does not say by what: {s.Note}"));

    /// <summary>
    /// A tripwire, in the manner of the migration count: what works is a number somebody has to
    /// change on purpose. It has been wrong in both directions — commands recorded as working
    /// that quietly were not, and commands still recorded as blocked for a phase that had
    /// already delivered them.
    /// </summary>
    [Fact]
    public void TheWorkingCountIsWhatSomebodyLastDecidedItWas()
    {
        // 54 after the three the table was stale about: Address Book and Plain Text had been
        // running for some time with the table still calling them blocked, and All Apps now
        // opens the installed plugins here as it does in the shell.
        Assert.Equal(54, ComposeAvailability.WorkingCount);
        Assert.Equal(41, ComposeAvailability.BlockedCount);
        Assert.Equal(95, ComposeAvailability.All.Count);
    }

    /// <summary>
    /// The Format Text tab is the point of the editor, so none of it may quietly go back to
    /// being blocked on "the editor", which no longer means anything — there is one.
    /// </summary>
    [Fact]
    public void TheFormattingCommandsWork()
    {
        CommandId[] formatting =
        [
            ComposeCommands.Bold.Id, ComposeCommands.Italic.Id, ComposeCommands.Underline.Id,
            ComposeCommands.Strikethrough.Id, ComposeCommands.Font.Id,
            ComposeCommands.FontSize.Id, ComposeCommands.GrowFont.Id,
            ComposeCommands.ShrinkFont.Id, ComposeCommands.FontColor.Id,
            ComposeCommands.Highlight.Id, ComposeCommands.Bullets.Id,
            ComposeCommands.Numbering.Id, ComposeCommands.MultilevelList.Id,
            ComposeCommands.IncreaseIndent.Id, ComposeCommands.DecreaseIndent.Id,
            ComposeCommands.Align.Id, ComposeCommands.LineSpacing.Id,
            ComposeCommands.FormatPainter.Id, ComposeCommands.Table.Id,
            ComposeCommands.Pictures.Id, ComposeCommands.Link.Id,
            MailCommands.Undo.Id, ViewCommands.Redo.Id,
        ];

        Assert.All(formatting, id => Assert.True(
            ComposeAvailability.Works(id),
            $"'{id}' formats the document and should work: "
            + $"{ComposeAvailability.For(id)?.Note}"));
    }

    [Fact]
    public void NoCommandIsDeclaredTwice()
        => Assert.Equal(
            ComposeCommands.All.Count,
            ComposeCommands.All.Select(c => c.Id).Distinct().Count());

    /// <summary>
    /// The compose window is its own host, so its commands do not have to avoid the main
    /// window's KeyTips — but they must not collide with each other's ids.
    /// </summary>
    [Fact]
    public void ComposeCommandsDoNotCollideWithTheMainWindowsIds()
    {
        var mail = MailCommands.All.Select(c => c.Id).ToHashSet();
        var view = ViewCommands.All.Select(c => c.Id).ToHashSet();

        foreach (var command in ComposeCommands.All)
        {
            Assert.DoesNotContain(command.Id, mail);
            Assert.DoesNotContain(command.Id, view);
        }
    }
}
