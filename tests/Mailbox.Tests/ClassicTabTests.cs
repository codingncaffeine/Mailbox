using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

/// <summary>
/// The Folder, View and Send/Receive tabs of the classic ribbon, against the captures they were
/// transcribed from.
/// </summary>
/// <remarks>
/// A transcription is a claim about somebody else's application, and the only way it stays true
/// is if changing it breaks something. These hold the group order, the item order and the two
/// controls that are not buttons — the Arrangement gallery and the Show as Conversations tick.
/// </remarks>
public class ClassicTabTests
{
    private static RibbonTab Tab(string id) => DefaultRibbonLayouts.Mail.Tabs.Single(t => t.Id == id);

    private static string[] Groups(string tab) => [.. Tab(tab).Groups.Select(g => g.Label)];

    private static string[] Commands(string tab, string group) =>
    [
        .. Tab(tab).Groups.Single(g => g.Label == group).Items.Select(i => i.Command.Value),
    ];

    [Fact]
    public void TheFolderTabIsTheCaptureS()
    {
        Assert.Equal(["New", "Actions", "Clean Up", "Favorites", "Properties"], Groups("folder"));

        Assert.Equal(["folder.new", "mail.searchfolder.new"], Commands("folder", "New"));
        Assert.Equal(
            ["folder.rename", "folder.copy", "folder.move", "folder.delete"],
            Commands("folder", "Actions"));
        Assert.Equal(
            ["folder.markallread", "folder.runrules", "folder.sortatoz",
             "mail.cleanup.folder", "folder.deleteall", "mail.recoverdeleted"],
            Commands("folder", "Clean Up"));
        Assert.Equal(["folder.favorites"], Commands("folder", "Favorites"));
        Assert.Equal(
            ["folder.autoarchive", "folder.permissions", "folder.properties"],
            Commands("folder", "Properties"));

        // Rename leads its group as a large button; the three that follow it are the stack.
        var actions = Tab("folder").Groups.Single(g => g.Label == "Actions");
        Assert.Equal(RibbonItemSize.Large, actions.Items[0].Size);
        Assert.All(actions.Items.Skip(1), item => Assert.Equal(RibbonItemSize.Small, item.Size));
    }

    [Fact]
    public void TheSendReceiveTabCarriesItsServerGroup()
    {
        Assert.Equal(["Send & Receive", "Download", "Server", "Preferences"], Groups("sendreceive"));

        Assert.Equal(
            ["app.sendreceive.all", "app.updatefolder", "app.sendall", "app.sendreceive.groups"],
            Commands("sendreceive", "Send & Receive"));
        Assert.Equal(["app.showprogress", "app.cancelall"], Commands("sendreceive", "Download"));
        Assert.Equal(
            ["app.downloadheaders", "app.markdownload", "app.unmarkdownload", "app.processheaders"],
            Commands("sendreceive", "Server"));
        Assert.Equal(["app.workoffline"], Commands("sendreceive", "Preferences"));

        // Each of the small three carries its own chevron, as the capture shows.
        var server = Tab("sendreceive").Groups.Single(g => g.Label == "Server");
        Assert.All(
            server.Items.Skip(1),
            item => Assert.Equal(RibbonItemKind.DropDown, item.Kind));
    }

    [Fact]
    public void TheViewTabIsTheCaptureS()
    {
        Assert.Equal(
            ["Current View", "Messages", "Arrangement", "Layout", "Window", "Immersive Reader"],
            Groups("view"));

        Assert.Equal(
            ["view.change", "view.viewsettings", "view.reset"],
            Commands("view", "Current View"));
        Assert.Equal(
            ["view.conversations", "view.conversations.settings"],
            Commands("view", "Messages"));
        Assert.Equal(
            ["view.tighterspacing", "view.folderpane", "view.readingpane", "view.todobar"],
            Commands("view", "Layout"));
        Assert.Equal(
            ["view.reminders", "view.newwindow", "view.closeall"],
            Commands("view", "Window"));
        Assert.Equal(["view.reader"], Commands("view", "Immersive Reader"));
    }

    [Fact]
    public void ShowAsConversationsIsATickAndNotAButton()
    {
        var messages = Tab("view").Groups.Single(g => g.Label == "Messages");

        Assert.Equal(RibbonItemKind.CheckBox, messages.Items[0].Kind);
        Assert.Equal(ViewCommands.ShowAsConversations.Id, messages.Items[0].Command);
    }

    /// <summary>
    /// The Arrangement gallery: eight entries in a four-wide box, in the capture's own order,
    /// with Message Preview to the left of it and three small buttons to the right.
    /// </summary>
    [Fact]
    public void TheArrangementGalleryIsFourAcrossAndTwoDeep()
    {
        var arrangement = Tab("view").Groups.Single(g => g.Label == "Arrangement");

        Assert.Equal(4, arrangement.GalleryColumns);
        Assert.Equal(ViewCommands.ArrangeBy.Id, arrangement.GalleryMore);

        var gallery = arrangement.Items.Where(i => i.InGallery).Select(i => i.Command.Value).ToArray();
        Assert.Equal(
            ["view.arrange.date", "view.arrange.from", "view.arrange.to", "view.arrange.categories",
             "view.arrange.flagstatus", "view.arrange.flagstart", "view.arrange.flagdue", "view.arrange.size"],
            gallery);

        // Message Preview leads the group, and the gallery is a run in the middle of it: the
        // three that follow are ordinary small buttons outside the box.
        Assert.Equal(ViewCommands.MessagePreview.Id, arrangement.Items[0].Command);
        Assert.False(arrangement.Items[0].InGallery);
        Assert.Equal(
            ["view.reversesort", "view.addcolumns", "view.expandcollapse"],
            arrangement.Items.SkipWhile(i => !i.InGallery).SkipWhile(i => i.InGallery)
                .Select(i => i.Command.Value).ToArray());
    }

    [Fact]
    public void TheHelpTabIsTheCaptureS()
    {
        Assert.Equal(["Help", "Tools"], Groups("help"));

        Assert.Equal(
            ["help.manual", "help.support", "help.feedback", "help.suggest",
             "help.training", "help.whatsnew", "help.supporttool"],
            Commands("help", "Help"));
        Assert.Equal(["help.diagnostics"], Commands("help", "Tools"));

        // Seven large buttons in the first group, one in the second: the capture has no small
        // stack anywhere on this tab.
        Assert.All(
            Tab("help").Groups.SelectMany(g => g.Items),
            item => Assert.Equal(RibbonItemSize.Large, item.Size));

        // F1 is the reference's own, and nothing here had it.
        Assert.Equal("F1", ViewCommands.Help.DefaultGesture);
    }

    /// <summary>
    /// The Simplified Help row: seven of the eight, and the row's own "…" holds the one it
    /// leaves out. No rule closes the row — the bar draws one in front of its overflow.
    /// </summary>
    [Fact]
    public void TheSimplifiedHelpRowLeavesGetDiagnosticsToTheOverflow()
    {
        var bar = DefaultRibbonLayouts.Mail.Simplified["help"];
        var row = bar.Flatten().Select(i => i.Command.Value).ToArray();

        Assert.False(bar.TrailingRule);
        Assert.Equal(
            ["help.manual", "help.support", "help.feedback", "help.suggest",
             "help.training", "help.whatsnew", "help.supporttool"],
            row);
        Assert.DoesNotContain("help.diagnostics", row);
    }

    /// <summary>
    /// The Folder tab is classic-only: both captures were taken minutes apart, and the
    /// Simplified one's tab strip does not carry it.
    /// </summary>
    [Fact]
    public void TheFolderTabIsNotInTheSimplifiedStrip()
    {
        Assert.True(Tab("folder").ClassicOnly);
        Assert.All(
            DefaultRibbonLayouts.Mail.Tabs.Where(t => t.Id != "folder"),
            tab => Assert.False(tab.ClassicOnly));
    }

    /// <summary>
    /// The four Help buttons with nothing behind them say so in the application's own voice.
    /// </summary>
    /// <remarks>
    /// A screentip is interface, and §5's rule that the reference is named nowhere in what a
    /// user sees applies to it as much as to a label.
    /// </remarks>
    [Fact]
    public void TheHelpScreentipsNameNobody()
    {
        foreach (var command in (MailboxCommand[])
                 [ViewCommands.ContactSupport, ViewCommands.ShowTraining,
                  ViewCommands.SupportTool, ViewCommands.GetDiagnostics])
        {
            Assert.DoesNotContain("reference", command.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("publisher", command.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Categories and the three flag entries are drawings rather than glyphs: their meaning is
    /// their colours, and a monochrome font cannot carry four swatches or a red flag.
    /// </summary>
    [Fact]
    public void TheGalleryEntriesThatAreColoursAreDrawn()
    {
        Assert.Equal("categorize", ViewCommands.ArrangeByCategories.IconArtwork);
        Assert.Equal("followup", ViewCommands.ArrangeByFlagStatus.IconArtwork);
        Assert.Equal("followup", ViewCommands.ArrangeByFlagStart.IconArtwork);
        Assert.Equal("followup", ViewCommands.ArrangeByFlagDue.IconArtwork);
    }
}
