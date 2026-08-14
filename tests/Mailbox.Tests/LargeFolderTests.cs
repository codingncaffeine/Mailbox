using System.Diagnostics;
using Mailbox.Store;
using Mailbox.Store.Lists;

namespace Mailbox.Tests;

/// <summary>
/// A hundred thousand messages in one folder is the size the list has to survive, and the parts
/// that decide whether it does are the store's query and the arrangement pass — both of which
/// touch every row. These are budgets rather than benchmarks: generous enough not to fail on a
/// loaded machine, tight enough that an accidental quadratic cannot hide under them.
/// </summary>
public class LargeFolderTests
{
    private const int Count = 100_000;

    private sealed record Row(
        string DisplayFrom,
        string Subject,
        DateTimeOffset Received,
        long SizeBytes,
        string ThreadKey,
        long FolderId,
        bool IsUnread) : IThreadable
    {
        public bool IsFlagged => false;
        public bool HasAttachment => false;
    }

    private static List<Row> Rows(int count)
    {
        var start = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var rows = new List<Row>(count);

        for (var i = 0; i < count; i++)
        {
            // Threads of three, senders spread over fifty, dates over two years — enough
            // variety that grouping does real work rather than producing one bucket.
            rows.Add(new Row(
                $"Sender {i % 50}",
                $"Subject {i / 3}",
                start.AddMinutes(-i * 7),
                1024 + (i % 900_000),
                $"thread {i / 3}",
                1 + (i % 2),
                i % 4 == 0));
        }

        return rows;
    }

    [Fact]
    public void ArrangingAHundredThousandStaysUnderBudget()
    {
        var rows = Rows(Count);

        var clock = Stopwatch.StartNew();
        var groups = Arrangements.Group(rows, Arrangement.Date, descending: true);
        clock.Stop();

        Assert.Equal(Count, groups.Sum(g => g.Count));
        Assert.True(clock.ElapsedMilliseconds < 2000,
            $"Arranging {Count:N0} took {clock.ElapsedMilliseconds} ms.");
    }

    /// <summary>Grouping by sender is the same pass with a string comparison in it.</summary>
    [Fact]
    public void ArrangingBySenderStaysUnderBudgetToo()
    {
        var rows = Rows(Count);

        var clock = Stopwatch.StartNew();
        var groups = Arrangements.Group(rows, Arrangement.From, descending: false);
        clock.Stop();

        Assert.Equal(50, groups.Count);
        Assert.True(clock.ElapsedMilliseconds < 2000,
            $"Arranging by sender took {clock.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// Threading is where a naive implementation goes quadratic — scanning the list for each
    /// message's thread rather than bucketing once.
    /// </summary>
    [Fact]
    public void ThreadingAHundredThousandIsLinearEnough()
    {
        var rows = Rows(Count);

        var clock = Stopwatch.StartNew();
        var threads = Conversations.Build(rows);
        clock.Stop();

        Assert.Equal((Count + 2) / 3, threads.Count);
        Assert.True(clock.ElapsedMilliseconds < 2000,
            $"Threading {Count:N0} took {clock.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// Reading a large folder out of the store. The index on (folder, received) is what makes
    /// this a scan of the first page rather than of the table.
    /// </summary>
    [Fact]
    public void ReadingALargeFolderUsesTheIndex()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;

        const int Stored = 20_000;
        store.InTransaction(() =>
        {
            for (var i = 0; i < Stored; i++)
            {
                repo.AddMessage(inbox.Id, new MessageSummary(
                    0, 0, $"uid-{i}", null, "Alice", "alice@example.com", $"Subject {i}",
                    "Preview", null, DateTimeOffset.UnixEpoch.AddMinutes(i), 1024,
                    false, false, false));
            }

            return 0;
        });

        var clock = Stopwatch.StartNew();
        var page = repo.Messages(inbox.Id, limit: 500);
        clock.Stop();

        Assert.Equal(500, page.Count);
        Assert.True(clock.ElapsedMilliseconds < 250,
            $"Reading a page of a {Stored:N0}-message folder took {clock.ElapsedMilliseconds} ms.");

        // Newest first, which is what the index is ordered for.
        Assert.True(page[0].Received > page[^1].Received);
    }
}
