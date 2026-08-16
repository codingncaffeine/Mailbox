using Mailbox.Core.Search;
using Mailbox.Core.Views;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>The view document, the in-memory search matcher behind filters and formatting, and the store's views.</summary>
public class MailViewTests
{
    [Fact]
    public void AViewRoundTripsThroughItsDocument()
    {
        var view = MailView.Preview with
        {
            Columns = [new ViewColumn(ViewFields.From, 120), new ViewColumn(ViewFields.Subject, 400), new ViewColumn(ViewFields.Received, 90)],
            GroupBy = "From",
            GroupAscending = true,
            GroupsExpanded = false,
            SortField = "Size",
            SortDescending = false,
            Filter = "hasattachment:yes size:>1mb",
            PreviewLines = 2,
            CompactMode = CompactMode.AlwaysSingleLine,
            CompactBelowChars = 90,
            Formats = [.. MailView.DefaultFormats(), new ConditionalFormat("From Alice") { Bold = true, ColourToken = "category.green", Condition = "from:alice" }],
            ColumnFormats = new Dictionary<string, ColumnFormat> { [ViewFields.Received] = new() { Label = "When", DateFormat = DateFormat.Short } },
        };

        var back = MailView.FromJson(view.ToJson());

        Assert.Equal(view.Name, back.Name);
        Assert.Equal(ViewLayout.Preview, back.Layout);
        Assert.Equal(view.Columns, back.Columns);
        Assert.Equal("From", back.GroupBy);
        Assert.True(back.GroupAscending);
        Assert.False(back.GroupsExpanded);
        Assert.Equal("Size", back.SortField);
        Assert.False(back.SortDescending);
        Assert.Equal("hasattachment:yes size:>1mb", back.Filter);
        Assert.Equal(2, back.PreviewLines);
        Assert.Equal(CompactMode.AlwaysSingleLine, back.CompactMode);
        Assert.Equal(90, back.CompactBelowChars);
        Assert.Equal(3, back.Formats.Count);
        Assert.Equal("From Alice", back.Formats[2].Name);
        Assert.Equal("category.green", back.Formats[2].ColourToken);
        Assert.Equal("When", back.ColumnFormats[ViewFields.Received].Label);
        Assert.Equal(DateFormat.Short, back.ColumnFormats[ViewFields.Received].DateFormat);

        // The header takes the reader's label over the field's.
        Assert.Contains(back.HeaderColumns(), c => c.Id == ViewFields.Received && c.Header == "When");
    }

    [Fact]
    public void TheThreeThatShipAreThemselvesAndAnUnreadableDocumentIsCompact()
    {
        Assert.Equal(ViewLayout.Compact, MailView.BuiltIn("Compact")!.Layout);
        Assert.Equal(0, MailView.BuiltIn("Single")!.PreviewLines);
        Assert.Equal(ViewLayout.Preview, MailView.BuiltIn("Preview")!.Layout);
        Assert.Null(MailView.BuiltIn("Mine"));
        Assert.True(MailView.Compact.IsBuiltIn);
        Assert.False((MailView.Compact with { Name = "Mine" }).IsBuiltIn);
        Assert.Equal("Compact", MailView.FromJson("{not json").Name);
    }

    // ---- The matcher ----------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static SearchFacts Facts() => new()
    {
        FromName = "Alice Chen",
        FromAddress = "alice@example.org",
        To = ["you@example.com"],
        Cc = ["bob@example.org"],
        Subject = "Re: Q3 numbers",
        Body = "Thanks for pulling those together.",
        Categories = ["Finance"],
        HasAttachment = true,
        IsRead = false,
        IsFlagged = true,
        Importance = 2,
        SizeBytes = 2_500_000,
        Received = Now.AddHours(-1),
        Sent = Now.AddHours(-2),
        Due = Now.AddDays(-2),
    };

    [Theory]
    [InlineData("q3", true)]
    [InlineData("alice numbers", true)]
    [InlineData("zebra", false)]
    [InlineData("from:alice", true)]
    [InlineData("from:carol", false)]
    [InlineData("to:you@example.com", true)]
    [InlineData("cc:bob", true)]
    [InlineData("subject:\"q3 numbers\"", true)]
    [InlineData("subject:budget", false)]
    [InlineData("body:pulling", true)]
    [InlineData("category:finance", true)]
    [InlineData("category:travel", false)]
    [InlineData("hasattachment:yes", true)]
    [InlineData("hasattachment:no", false)]
    [InlineData("read:no", true)]
    [InlineData("unread:no", false)]
    [InlineData("flagged:yes importance:high", true)]
    [InlineData("importance:low", false)]
    [InlineData("size:>1mb", true)]
    [InlineData("size:<1mb", false)]
    [InlineData("received:today", true)]
    [InlineData("received:yesterday", false)]
    [InlineData("sent:today", true)]
    [InlineData("due:<today", true)]
    [InlineData("flagged:yes due:<today", true)]
    [InlineData("due:today", false)]
    public void TheMatcherAgreesWithTheGrammar(string query, bool expected)
    {
        var parsed = SearchQuery.Parse(query, Now);
        Assert.Equal(expected, SearchMatcher.Matches(parsed, Facts()));
    }

    [Fact]
    public void AnEmptyQueryMatchesEverythingAndDueNeedsADueDate()
    {
        Assert.True(SearchMatcher.Matches(SearchQuery.Parse(""), Facts()));
        Assert.False(SearchMatcher.Matches(SearchQuery.Parse("due:<today", Now), Facts() with { Due = null }));
        Assert.True(SearchMatcher.Matches(SearchQuery.Parse("received:last7days", Now), Facts()));
    }

    // ---- The store --------------------------------------------------------------------------------

    [Fact]
    public void FoldersKeepTheirViewAndSavedViewsAreListedRenamedAndDeleted()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;

        Assert.Null(repo.FolderView(inbox.Id));
        var json = MailView.Single.ToJson();
        repo.SetFolderView(inbox.Id, json);
        Assert.Equal(json, repo.FolderView(inbox.Id));
        repo.SetFolderView(inbox.Id, null);
        Assert.Null(repo.FolderView(inbox.Id));

        var saved = repo.SaveView("Receipts", (MailView.Preview with { Name = "Receipts", Filter = "category:receipts" }).ToJson(), Now);
        Assert.Equal("Receipts", saved.Name);
        Assert.Single(repo.Views());
        Assert.Equal("category:receipts", MailView.FromJson(repo.ViewNamed("receipts")!.Definition).Filter);

        // Saving under the same name replaces; the name compares without case.
        repo.SaveView("RECEIPTS", MailView.Compact.ToJson(), Now);
        Assert.Single(repo.Views());

        repo.RenameView(saved.Id, "Shop");
        Assert.Null(repo.ViewNamed("Receipts"));
        Assert.NotNull(repo.ViewNamed("Shop"));
        repo.DeleteView(saved.Id);
        Assert.Empty(repo.Views());
    }
}
