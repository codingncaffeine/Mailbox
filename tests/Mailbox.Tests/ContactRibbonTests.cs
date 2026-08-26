using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

/// <summary>
/// The contact window's ribbon, against the two classic captures it was transcribed from.
/// </summary>
/// <remarks>
/// Same reasoning as the shell's own transcription tests: a claim about somebody else's
/// application only stays true if changing it breaks something. What is held here is the group
/// order, the item order, and the two places the reference uses a stack rather than a row.
/// </remarks>
public class ContactRibbonTests
{
    private static readonly RibbonLayout Layout = ContactRibbonLayout.Contact;

    private static RibbonTab Tab(string id) => Layout.Tabs.Single(t => t.Id == id);

    private static string[] Commands(string tab, string group) =>
    [
        .. Tab(tab).Groups.Single(g => g.Label == group).Items.Select(i => i.Command.Value),
    ];

    [Fact]
    public void TheStripIsTheSixTheCaptureShows()
        => Assert.Equal(
            ["file", "contact", "insert", "formattext", "review", "help"],
            Layout.Tabs.Select(t => t.Id).ToArray());

    [Fact]
    public void TheContactTabIsTheCaptureS()
    {
        Assert.Equal(
            ["Actions", "Show", "Communicate", "Names", "Options", "Tags", "Immersive", "Zoom"],
            Tab("contact").Groups.Select(g => g.Label).ToArray());

        Assert.Equal(
            ["contact.save", "contact.delete", "contact.save.new", "contact.forward"],
            Commands("contact", "Actions"));
        Assert.Equal(
            ["contact.email", "contact.meeting", "contact.more"],
            Commands("contact", "Communicate"));
        Assert.Equal(["view.reader"], Commands("contact", "Immersive"));
        Assert.Equal(["view.zoom"], Commands("contact", "Zoom"));

        // Show is the one group on this tab with a stack: General leads it as a large button and
        // the other three pages sit beside it.
        var show = Tab("contact").Groups.Single(g => g.Label == "Show");
        Assert.Equal(RibbonItemSize.Large, show.Items[0].Size);
        Assert.All(show.Items.Skip(1), item => Assert.Equal(RibbonItemSize.Small, item.Size));
    }

    [Fact]
    public void TheInsertTabIsTheCaptureSAndNotTheComposeWindowS()
    {
        Assert.Equal(
            ["Include", "Tables", "Illustrations", "Links", "Text", "Symbols"],
            Tab("insert").Groups.Select(g => g.Label).ToArray());

        Assert.Equal(
            ["compose.attach.file", "compose.attach.item", "insert.businesscard", "compose.signature"],
            Commands("insert", "Include"));
        Assert.Equal(
            ["insert.pictures", "insert.shapes", "insert.icons", "insert.models3d",
             "insert.smartart", "insert.chart", "insert.screenshot"],
            Commands("insert", "Illustrations"));
        Assert.Equal(["insert.link", "insert.bookmark"], Commands("insert", "Links"));
        Assert.Equal(
            ["insert.textbox", "insert.quickparts", "insert.wordart",
             "insert.dropcap", "insert.datetime", "insert.object"],
            Commands("insert", "Text"));
        Assert.Equal(
            ["insert.equation", "insert.symbol", "insert.horizontalline"],
            Commands("insert", "Symbols"));

        // The compose window's Insert tab is a different tab in the reference, and the two
        // transcriptions were read off their own captures: this one has no Stock Images.
        var compose = DefaultRibbonLayouts.Compose.Tabs.Single(t => t.Id == "insert");
        Assert.Contains(
            compose.Groups.SelectMany(g => g.Items),
            item => item.Command.Value == "insert.stockimages");
        Assert.DoesNotContain(
            Tab("insert").Groups.SelectMany(g => g.Items),
            item => item.Command.Value == "insert.stockimages");
    }

    /// <summary>
    /// Format Text and Review are the compose window's own, because the reference gives both
    /// windows the same two tabs and the note is a document like a message's body.
    /// </summary>
    [Fact]
    public void TheDocumentTabsAreSharedRatherThanCopied()
    {
        var mine = Tab("formattext").Groups.SelectMany(g => g.Items).Select(i => i.Command.Value);
        var theirs = DefaultRibbonLayouts.Compose.Tabs.Single(t => t.Id == "formattext")
            .Groups.SelectMany(g => g.Items).Select(i => i.Command.Value);

        Assert.Equal(theirs, mine);

        Assert.Equal(
            DefaultRibbonLayouts.Compose.SimplifiedRows["review"].Select(i => i.Command.Value),
            Layout.SimplifiedRows["review"].Select(i => i.Command.Value));
    }

    /// <summary>
    /// The Simplified bar carries its own transcription — the reference's Simplified contact
    /// window shows a different Insert run from its classic one.
    /// </summary>
    [Fact]
    public void TheSimplifiedInsertRowIsItsOwnTranscription()
    {
        var row = Layout.Simplified["insert"].Flatten().Select(i => i.Command.Value).ToList();

        Assert.Contains("insert.screenshot", row);
        Assert.Contains("insert.quickparts", row);

        // Bookmark, Text Box, Drop Cap, Date & Time and the Symbols group are classic-only: the
        // Simplified capture's run ends at Object.
        Assert.DoesNotContain("insert.bookmark", row);
        Assert.DoesNotContain("insert.horizontalline", row);
    }
}
