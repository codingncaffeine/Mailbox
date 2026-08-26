using Mailbox.Store.Lists;

namespace Mailbox.Tests;

/// <summary>
/// Arrangements decide both the order and the group headers, and the date buckets are the part
/// with rules worth checking: they are relative near the present and absolute further back, and
/// getting a boundary wrong is the kind of thing nobody notices until they are looking for
/// something and it is filed under the wrong week.
/// </summary>
public class ArrangementTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 14, 0, 0, TimeSpan.Zero);

    private sealed record Row(
        string DisplayFrom,
        string Subject,
        DateTimeOffset Received,
        long SizeBytes = 1024,
        bool IsFlagged = false,
        bool HasAttachment = false,
        DateTimeOffset? FollowUpStart = null,
        DateTimeOffset? FollowUpDue = null) : IArrangeable;

    private static Row At(int daysAgo, string from = "Alice", string subject = "Subject")
        => new(from, subject, Now.AddDays(-daysAgo));

    /// <summary>
    /// Flag: Start Date and Flag: Due Date, which the reference's Arrangement gallery lists
    /// beside Flag Status. A row with no flag has no date, and sorts at the far end either way
    /// rather than reading as the oldest.
    /// </summary>
    [Fact]
    public void TheFlagDatesGroupByTheirOwnDatesAndPutTheUnflaggedLast()
    {
        var due = new Row("Alice", "Due today", Now, FollowUpDue: Now);
        var later = new Row("Bob", "Due tomorrow", Now, FollowUpDue: Now.AddDays(1));
        var none = new Row("Carol", "No flag", Now);

        var groups = Arrangements.Group([none, later, due], Arrangement.FlagDue, descending: false, today: Now);

        Assert.Equal(["Today", "Later", "None"], groups.Select(g => g.Header));
        Assert.Equal(["Due today"], groups[0].Items.Select(i => i.Subject));
        Assert.Equal(["No flag"], groups[^1].Items.Select(i => i.Subject));

        // The other way round, the dated rows reverse and the undated one stays at the end.
        var down = Arrangements.Group([none, due, later], Arrangement.FlagDue, descending: true, today: Now);
        Assert.Equal(["Later", "Today", "None"], down.Select(g => g.Header));

        // Start dates read the other column: the same rows arrange differently under it.
        var starts = Arrangements.Group(
            [new Row("Alice", "Starts today", Now, FollowUpStart: Now), none],
            Arrangement.FlagStart, descending: false, today: Now);
        Assert.Equal(["Today", "None"], starts.Select(g => g.Header));
    }

    [Fact]
    public void TheFlagArrangementsAreNamedAsTheGalleryNamesThem()
    {
        Assert.Equal("Flag Status", Arrangements.Label(Arrangement.Flag));
        Assert.Equal("Flag: Start Date", Arrangements.Label(Arrangement.FlagStart));
        Assert.Equal("Flag: Due Date", Arrangements.Label(Arrangement.FlagDue));
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(3, "Tuesday")]
    [InlineData(6, "Saturday")]
    [InlineData(8, "Last Week")]
    [InlineData(15, "Two Weeks Ago")]
    [InlineData(22, "Three Weeks Ago")]
    public void DatesBucketRelativeToToday(int daysAgo, string expected)
        => Assert.Equal(expected, Arrangements.DateBand(Now.AddDays(-daysAgo), Now));

    /// <summary>
    /// Past four weeks the counted weeks run out. July is the month before August, so it is
    /// "Last Month"; anything older is named, with the year once it is not this one.
    /// </summary>
    [Fact]
    public void OlderDatesBucketByMonth()
    {
        Assert.Equal("Last Month",
            Arrangements.DateBand(new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero), Now));
        Assert.Equal("March",
            Arrangements.DateBand(new DateTimeOffset(2026, 3, 9, 9, 0, 0, TimeSpan.Zero), Now));
        Assert.Equal("November 2025",
            Arrangements.DateBand(new DateTimeOffset(2025, 11, 9, 9, 0, 0, TimeSpan.Zero), Now));
    }

    /// <summary>A clock ahead of ours puts mail in the future. It gets its own bucket.</summary>
    [Fact]
    public void FutureDatesAreNotFiledAsToday()
        => Assert.Equal("Later", Arrangements.DateBand(Now.AddDays(2), Now));

    [Fact]
    public void ArrangingByDateGroupsNewestFirst()
    {
        var groups = Arrangements.Group(
            [At(0, subject: "New"), At(1, subject: "Old"), At(0, subject: "Also new")],
            Arrangement.Date, descending: true, Now);

        Assert.Equal(["Today", "Yesterday"], groups.Select(g => g.Header));
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void ArrangingAscendingPutsOldestFirst()
    {
        var groups = Arrangements.Group(
            [At(0), At(5)], Arrangement.Date, descending: false, Now);

        Assert.Equal("Sunday", groups[0].Header);
        Assert.Equal("Today", groups[1].Header);
    }

    [Fact]
    public void ArrangingByFromGroupsBySender()
    {
        var groups = Arrangements.Group(
            [At(0, "Bob"), At(1, "Alice"), At(2, "Bob")],
            Arrangement.From, descending: false, Now);

        Assert.Equal(["Alice", "Bob"], groups.Select(g => g.Header));
        Assert.Equal(2, groups[1].Count);
    }

    /// <summary>
    /// Within a group the newest is still first. Two messages from the same sender should read
    /// in the order they arrived rather than in whatever order the store returned them.
    /// </summary>
    [Fact]
    public void RowsInsideAGroupStayNewestFirst()
    {
        var groups = Arrangements.Group(
            [At(3, "Bob", "older"), At(1, "Bob", "newer")],
            Arrangement.From, descending: false, Now);

        Assert.Equal(["newer", "older"], groups[0].Items.Select(r => r.Subject));
    }

    /// <summary>"Re: Budget" belongs with Budget, not under R with every other reply.</summary>
    [Theory]
    [InlineData("Re: Budget", "Budget")]
    [InlineData("RE: FW: Budget", "Budget")]
    [InlineData("Fwd: Budget", "Budget")]
    [InlineData("Budget", "Budget")]
    public void SubjectPrefixesAreIgnoredWhenFiling(string subject, string expected)
        => Assert.Equal(expected, Arrangements.NormalisedSubject(subject));

    [Fact]
    public void ArrangingBySubjectGroupsByFirstLetter()
    {
        var groups = Arrangements.Group(
            [At(0, subject: "Re: Budget"), At(1, subject: "Agenda"), At(2, subject: "42 things")],
            Arrangement.Subject, descending: false, Now);

        Assert.Equal(["0–9", "A", "B"], groups.Select(g => g.Header));
    }

    [Theory]
    [InlineData(5_000, "Tiny (under 10 KB)")]
    [InlineData(20_000, "Small (10 to 25 KB)")]
    [InlineData(60_000, "Medium (25 to 100 KB)")]
    [InlineData(300_000, "Large (100 to 500 KB)")]
    [InlineData(2_000_000, "Very Large (500 KB to 5 MB)")]
    [InlineData(20_000_000, "Enormous (over 5 MB)")]
    public void SizesBucketIntoBands(long bytes, string expected)
        => Assert.Equal(expected, Arrangements.SizeBand(bytes));

    [Fact]
    public void ArrangingByFlagSeparatesFlaggedFromTheRest()
    {
        var groups = Arrangements.Group(
            [At(0) with { IsFlagged = true }, At(1), At(2) with { IsFlagged = true }],
            Arrangement.Flag, descending: true, Now);

        Assert.Equal("Flagged", groups[0].Header);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal("Unflagged", groups[1].Header);
    }

    [Fact]
    public void ArrangingByAttachmentsSeparatesThemToo()
    {
        var groups = Arrangements.Group(
            [At(0) with { HasAttachment = true }, At(1)],
            Arrangement.Attachments, descending: true, Now);

        Assert.Equal(["With attachments", "No attachments"], groups.Select(g => g.Header));
    }

    [Fact]
    public void AnEmptyListProducesNoGroupsRatherThanOneEmptyOne()
        => Assert.Empty(Arrangements.Group<Row>([], Arrangement.Date, true, Now));

    [Fact]
    public void EveryArrangementHasALabelAndGroupsWithoutThrowing()
    {
        foreach (var arrangement in Arrangements.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(Arrangements.Label(arrangement)));

            var groups = Arrangements.Group([At(0), At(1)], arrangement, true, Now);

            Assert.NotEmpty(groups);
            Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Header)));
            Assert.Equal(2, groups.Sum(g => g.Count));
        }
    }
}
