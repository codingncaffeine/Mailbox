using System.Diagnostics;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The store at the scale the audit set as its endurance case — 50,000 messages — measuring the
/// operations a reader waits on, so the async-store decision is made on numbers rather than on
/// worry.
/// </summary>
/// <remarks>
/// <b>Opt-in.</b> Seeding fifty thousand rows is seconds of work nobody should pay on every
/// suite run, so this only runs when <c>MAILBOX_PERF</c> is set. The point is not a pass/fail
/// gate — a machine's speed is not the code's — but a record: which calls are microseconds and
/// which are not, which is exactly what says whether the answer is <c>async</c>, or a narrower
/// query, or a cache. The recommendation on record is that a fast call made a thousand times is
/// the cost, not a slow call made once; these numbers are how that is checked.
/// <para>
/// The times are asserted only against ceilings loose enough that any development machine clears
/// them — a regression that made a folder open take a whole second would trip them, a normal
/// range would not. The absolute numbers go to the test output, which is what the ledger reads.
/// </para>
/// </remarks>
public sealed class StoreScaleTests
{
    private const int N = 50_000;

    private static bool Requested => Environment.GetEnvironmentVariable("MAILBOX_PERF") is { Length: > 0 };

    [Fact]
    public void FiftyThousandMessages()
    {
        Assert.SkipUnless(Requested, "Set MAILBOX_PERF=1 to run the 50,000-message scale measurement.");

        using var store = MailStore.Transient();
        var mail = new MailRepository(store);
        var account = mail.AddAccount("you@example.com", "A. Person", MailProtocol.Imap).Id;
        var inbox = mail.AddFolder(account, "Inbox", FolderRole.Inbox).Id;
        var archive = mail.AddFolder(account, "Archive").Id;

        // ---- Seed --------------------------------------------------------------------------
        var seed = Stopwatch.StartNew();
        store.InTransaction(() =>
        {
            for (var i = 0; i < N; i++)
            {
                var read = i % 3 != 0;   // a third unread, so the unread count is real work
                mail.AddMessage(inbox, new MessageSummary(
                    0, 0, $"uid-{i}", $"<{i}@example.com>",
                    $"Sender {i % 500}", $"sender{i % 500}@example.com",
                    $"Message {i} about quarterly figures and variance {i % 97}",
                    "A body line.", null,
                    DateTimeOffset.UnixEpoch.AddMinutes(i), 512,
                    read, IsFlagged: i % 50 == 0, HasAttachment: false)
                    { BodyText = $"The body of message {i} mentions kestrel and variance {i % 97}." });
            }

            return 0;
        });
        seed.Stop();

        Assert.Equal(N, store.ScalarLong("SELECT count(*) FROM messages"));

        // ---- The operations a reader waits on ----------------------------------------------
        // Folder pane refresh: the correlated total/unread subqueries, two full-folder scans.
        var folders = Measure("Folders() — the folder pane's total+unread counts", () => mail.Folders(account));

        // The list as it draws: the default 500-row page.
        var page = Measure("Messages(inbox, 500) — the list's default page", () => mail.Messages(inbox, 500));

        // The whole folder materialised — the worst case the list can be asked for.
        var whole = Measure("Messages(inbox, all) — the whole folder materialised", () => mail.Messages(inbox, int.MaxValue));

        // A search, which the FTS index answers rather than a scan.
        var search = Measure("Search(\"kestrel\") — the FTS query across 50k", () => mail.Search("kestrel", limit: 200));
        var searchNarrow = Measure("Search(\"variance 42\") — a narrower FTS query", () => mail.Search("variance", limit: 200));

        // The per-keystroke cost the decision names: the folder counts, recomputed ten times as
        // though a reader were typing into search, which re-runs the pane.
        var perKeystroke = Measure("Folders() × 10 — the per-keystroke count recompute", () =>
        {
            for (var i = 0; i < 10; i++) _ = mail.Folders(account);
            return 0;
        });

        // A move of a thousand messages — the arrangement/bulk-action cost.
        var someIds = mail.Messages(inbox, 1000).Select(m => m.Id).ToList();
        var move = Measure($"MoveMessages(1000 → Archive)", () => mail.MoveMessages(someIds, archive));

        // ---- The record --------------------------------------------------------------------
        var lines = new[]
        {
            $"seed {N:N0} messages: {seed.ElapsedMilliseconds:N0}ms ({seed.Elapsed.TotalMilliseconds / N:0.000}ms each)",
            $"Folders() [{folders.Result.Count} folders]: {folders.Ms:0.0}ms",
            $"Messages(inbox, 500) [{page.Result.Count}]: {page.Ms:0.0}ms",
            $"Messages(inbox, all) [{whole.Result.Count}]: {whole.Ms:0.0}ms",
            $"Search(kestrel) [{search.Result.Count}]: {search.Ms:0.0}ms",
            $"Search(variance) [{searchNarrow.Result.Count}]: {searchNarrow.Ms:0.0}ms",
            $"Folders() × 10: {perKeystroke.Ms:0.0}ms ({perKeystroke.Ms / 10:0.0}ms each)",
            $"MoveMessages(1000): {move.Ms:0.0}ms",
        };

        // To the test output — the ledger reads these.
        foreach (var line in lines) TestContext.Current.TestOutputHelper?.WriteLine(line);
        Console.WriteLine("STORE-SCALE " + string.Join(" | ", lines));

        // Loose ceilings: a normal machine clears these by a wide margin; a real regression trips.
        Assert.True(folders.Ms < 1000, $"folder counts took {folders.Ms:0}ms");
        Assert.True(page.Ms < 500, $"the default page took {page.Ms:0}ms");
        Assert.True(search.Ms < 1000, $"search took {search.Ms:0}ms");
    }

    private static (T Result, double Ms) Measure<T>(string label, Func<T> work)
    {
        _ = label;

        // A warm pass first, so the number is the steady-state cost rather than first-touch.
        _ = work();
        var sw = Stopwatch.StartNew();
        var result = work();
        sw.Stop();
        return (result, sw.Elapsed.TotalMilliseconds);
    }
}
