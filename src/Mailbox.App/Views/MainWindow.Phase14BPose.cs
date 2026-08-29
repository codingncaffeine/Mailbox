using System.Globalization;
using IO = System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.Core;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The doors the store engines needed: the ground truth a reported number has to be held
/// against, a dialog's own text, the backup engine, and a corpus whose ages can be posed.
/// </summary>
/// <remarks>
/// Every engine in this slice reports a number — bytes recovered, items archived, items that can
/// still be recovered — and a number is the one thing a capture cannot check. So the read-back
/// here is deliberately two-sided: <see cref="ReportStores"/> asks SQLite what the file holds,
/// and <see cref="DumpTopWindow"/> reads the sentence the dialog drew, so the two can be put
/// beside each other. Proving a reported number <em>wrong</em> is half of what this slice is for,
/// and neither half says anything on its own.
/// <para>
/// <b>Why the ages are posed rather than seeded.</b> AutoArchive and the recover-deleted
/// retention are arithmetic on a clock, and the only corpus in the tree is dated a few days
/// either side of the seed's day. A rule with a six-month cutoff has nothing to say about it.
/// <c>MAILBOX_AGE</c> moves a message's <c>received_utc</c> to a stated number of days before the
/// posed clock, which is exactly the column the engine reads, so an age boundary can be walked —
/// one either side and one exactly on it — and walked again next year to the same answer.
/// </para>
/// <para>
/// <b>Why the backup has a door at all.</b> Nothing in the application calls
/// <see cref="StoreBackup"/>; the absence is on the standing queue and the engine still has to be
/// proven. The interesting claim is the one its own remarks make — that a copy taken while the
/// store is being written is complete rather than "opens cleanly and is missing the last
/// however-many messages" — and that cannot be shown by a test that writes nothing during it.
/// <c>during:</c> runs a writer on a thread of its own for the length of the copy.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this lane's doors. Called once, from the constructor.</summary>
    private void WirePhase14BDoors()
    {
        // The two corpus poses run on Opened itself rather than on a posted pass, and this file
        // is wired before the MAILBOX_PEEK switch registers its own handlers — so a dialog whose
        // numbers are read at construction (Mailbox Cleanup, the Data File dialog) sees the store
        // these left rather than the store as the seed shipped it. Neither needs layout.
        if (Environment.GetEnvironmentVariable("MAILBOX_AGE") is { Length: > 0 } ages)
        {
            Opened += (_, _) => PoseAges(ages);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_BACKUP") is { Length: > 0 } backup)
        {
            var hold = Theming.WindowCapture.IsRequested ? Theming.WindowCapture.Hold() : null;
            Opened += (_, _) => _ = PoseBackupAsync(backup, hold);
        }

        if (Environment.GetEnvironmentVariable("MAILBOX_DAV") is { Length: > 0 } dav)
        {
            // Before MAILBOX_CALENDAR_SYNC (Background), so the collection has an address and a
            // queued change by the time the sync runs.
            Opened += (_, _) => Dispatcher.UIThread.Post(() => PoseDav(dav), DispatcherPriority.Loaded);
        }

        // A dialog's own text, at each of the moments named. Wired here rather than as a step of
        // MAILBOX_DIALOG_PRESS so the two compose: press, dump, press again.
        if (Environment.GetEnvironmentVariable("MAILBOX_DIALOG_DUMP") is { Length: > 0 } dump)
        {
            // The hold is taken on the dispatcher in the same pass the window is built, before
            // the capture's own timer starts counting: a dump scheduled for later than the
            // capture is a dump of a process that has already gone.
            var hold = Theming.WindowCapture.IsRequested ? Theming.WindowCapture.Hold() : null;
            Opened += (_, _) => _ = DumpDialogsAsync(dump, hold);
        }

        // Last of all, twice deferred — and later still with @ms, which is what a report after a
        // dialog's own button has finished needs: the store as the run leaves it is the claim,
        // not the store half-way through it.
        if (Environment.GetEnvironmentVariable("MAILBOX_STORE_REPORT") is { Length: > 0 } report)
        {
            var hold = Theming.WindowCapture.IsRequested && report.Contains('@') ? Theming.WindowCapture.Hold() : null;
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => Dispatcher.UIThread.Post(() => _ = ReportStoresLaterAsync(report, hold), DispatcherPriority.Background),
                DispatcherPriority.Background);
        }
    }

    // ---- The store, as SQLite sees it ------------------------------------------------------

    /// <summary>Waits out <c>=all@3000</c>'s milliseconds, then reports.</summary>
    private static async Task ReportStoresLaterAsync(string spec, IDisposable? hold)
    {
        try
        {
            if (spec.Split('@', 2) is [var head, var delay] && int.TryParse(delay, out var ms))
            {
                await Task.Delay(ms).ConfigureAwait(true);
                spec = head;
            }

            ReportStores(spec);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    /// <summary>
    /// What every store actually holds, beside what the application says it holds.
    /// </summary>
    /// <remarks>
    /// <c>MAILBOX_STORE_REPORT=1</c> for the accounts, <c>=all</c> for the PIM store beside them.
    /// The three sizes are reported separately on purpose: a store in WAL mode is a file and a
    /// write-ahead log, and everything in the application that calls a store's size reads the
    /// first of them alone.
    /// </remarks>
    private static void ReportStores(string spec)
    {
        try
        {
            ReportStoresCore(spec);
        }
        catch (Exception ex)
        {
            // A report that throws takes the run with it and leaves no evidence at all, which is
            // the one outcome a read-back must not have.
            Log.Warn("Harness: the store report failed.", ex);
        }
    }

    private static void ReportStoresCore(string spec)
    {
        var all = spec.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);

        foreach (var account in App.Accounts.All)
        {
            var path = account.Path;
            Log.Info($"Harness: store {account.Account.Address} — {Sizes(path)}, "
                     + $"{Pages(account.Store)}, reported {account.Bytes:N0} bytes.");

            var folders = account.Mail.Folders(account.Account.Id);
            foreach (var folder in folders)
            {
                var rows = account.Store.ScalarLong(
                    "SELECT count(*) FROM messages WHERE folder_id = $f", ("$f", folder.Id));
                var bytes = account.Store.ScalarLong(
                    "SELECT coalesce(sum(size_bytes), 0) FROM messages WHERE folder_id = $f", ("$f", folder.Id));

                Log.Info($"Harness: store {account.Account.Address} folder “{folder.Name}” ({folder.Role}) — "
                         + $"Total {folder.Total}, Unread {folder.Unread}, rows {rows}, bytes {bytes:N0}.");
            }

            Log.Info($"Harness: store {account.Account.Address} totals — messages "
                     + $"{account.Store.ScalarLong("SELECT count(*) FROM messages")}, blobs "
                     + $"{account.Store.ScalarLong("SELECT count(*) FROM blobs")}, fts "
                     + $"{Indexed(account.Store)}, recoverable "
                     + $"{account.Mail.RecoverableCount()}, outbox "
                     + $"{account.Store.ScalarLong("SELECT count(*) FROM outbox")}.");
        }

        if (!all) return;

        Log.Info($"Harness: store pim — {Sizes(App.Pim.Store.Path)}, {Pages(App.Pim.Store)}, "
                 + $"items {App.Pim.Store.ScalarLong("SELECT count(*) FROM pim_items")}, "
                 + $"queue {App.Pim.Store.ScalarLong("SELECT count(*) FROM dav_queue")}.");
    }

    /// <summary>The file and its write-ahead log, which are two different numbers.</summary>
    private static string Sizes(string path)
    {
        long Of(string suffix) => IO.File.Exists(path + suffix) ? new FileInfo(path + suffix).Length : 0;
        return $"db {Of(string.Empty):N0} + wal {Of("-wal"):N0} + shm {Of("-shm"):N0} = {Of(string.Empty) + Of("-wal"):N0} bytes";
    }

    /// <summary>
    /// How many documents the search index holds, whether it agrees with itself, and what a real
    /// query finds.
    /// </summary>
    /// <remarks>
    /// Counted off <c>messages_fts_docsize</c> rather than the index itself: nothing can be
    /// selected <em>from</em> this index, because it is declared over a content column
    /// (<c>body</c>) that <c>messages</c> does not have — it is called <c>body_text</c> — so every
    /// read-through answers "no such column: T.body". The triggers pass their values explicitly
    /// and so are unaffected, and the application only ever matches and joins on the rowid, which
    /// is why nothing has ever noticed. It is recorded here because a search index that cannot be
    /// read through also cannot be rebuilt, and the size of a store is not evidence that its index
    /// survived a compaction.
    /// </remarks>
    private static string Indexed(SqliteStore store)
    {
        var docs = store.ScalarLong("SELECT count(*) FROM messages_fts_docsize");
        string check;
        try
        {
            store.Execute("INSERT INTO messages_fts(messages_fts) VALUES('integrity-check')");
            check = "consistent";
        }
        catch (Exception ex)
        {
            check = "INCONSISTENT: " + ex.Message;
        }

        var found = new List<string>();
        foreach (var term in (string[])["agenda", "Filed", "variance"])
        {
            found.Add($"{term}={store.ScalarLong("SELECT count(*) FROM messages m JOIN messages_fts ON messages_fts.rowid = m.id WHERE messages_fts MATCH $t", ("$t", term))}");
        }

        return $"{docs} docs ({check}), matches {string.Join(" ", found)}";
    }

    private static string Pages(SqliteStore store)
        => $"page_size {store.ScalarLong("PRAGMA page_size")}, page_count {store.ScalarLong("PRAGMA page_count")}, "
           + $"freelist {store.ScalarLong("PRAGMA freelist_count")}";

    // ---- Posing an age ----------------------------------------------------------------------

    /// <summary>
    /// Dates messages relative to the posed clock: <c>MAILBOX_AGE=Q3:200|briefing:180</c>, or
    /// <c>*:400</c> for everything in every account.
    /// </summary>
    /// <remarks>
    /// The number is days before <see cref="PosedClock.UtcNow"/>, so a run with
    /// <c>MAILBOX_TODAY</c> set writes the same corpus every time. <c>received_utc</c> is the
    /// column both AutoArchive and the Archive dialog read, and nothing else about the message
    /// changes — the point is a boundary, not a fixture.
    /// </remarks>
    private static void PoseAges(string spec)
    {
        var now = PosedClock.UtcNow;

        foreach (var part in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = part.LastIndexOf(':');
            if (split < 1 || !double.TryParse(part[(split + 1)..], CultureInfo.InvariantCulture, out var days)) continue;
            var match = part[..split];
            var stamp = now.AddDays(-days).ToUnixTimeSeconds();

            foreach (var account in App.Accounts.All)
            {
                var changed = match == "*"
                    ? account.Store.Execute("UPDATE messages SET received_utc = $when", ("$when", stamp))
                    : account.Store.Execute(
                        "UPDATE messages SET received_utc = $when WHERE subject LIKE $like",
                        ("$when", stamp), ("$like", "%" + match + "%"));

                if (changed > 0)
                {
                    Log.Info($"Harness: age — {changed} message(s) in {account.Account.Address} matching “{match}” "
                             + $"dated {days} day(s) before the posed clock "
                             + $"({DateTimeOffset.FromUnixTimeSeconds(stamp):yyyy-MM-dd HH:mm} UTC).");
                }
            }
        }
    }

    // ---- The backup engine --------------------------------------------------------------------

    /// <summary>
    /// Drives <see cref="StoreBackup"/> over the live store:
    /// <c>MAILBOX_BACKUP=write:20;to:/tmp/b.db;during:200;from:/tmp/b.db;open:/tmp/b.db</c>.
    /// </summary>
    /// <remarks>
    /// Steps, in order, over the default account:
    /// <list type="bullet">
    /// <item><description><c>write:n</c> — files n invented messages in the Inbox first, so the
    /// copy has something to be missing.</description></item>
    /// <item><description><c>to:path</c> — the consistent copy, quiet.</description></item>
    /// <item><description><c>during:n</c> — the copy again with a writer filing n messages on a
    /// thread of its own throughout, which is the claim the engine's own remarks make and the one
    /// nothing had ever exercised.</description></item>
    /// <item><description><c>open:path</c> — opens a file cold, on its own, and says what is in
    /// it: the question a backup exists to answer.</description></item>
    /// <item><description><c>from:path</c> — the restore, and where the displaced store
    /// went.</description></item>
    /// </list>
    /// </remarks>
    private async Task PoseBackupAsync(string spec, IDisposable? hold = null)
    {
        try
        {
            await RunBackupStepsAsync(spec).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the backup pose failed.", ex);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    private async Task RunBackupStepsAsync(string spec)
    {
        if (App.Accounts.Default is not { } account)
        {
            Log.Warn("Harness: backup — there is no account to copy.");
            return;
        }

        foreach (var step in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = step.IndexOf(':');
            var verb = (split < 0 ? step : step[..split]).Trim().ToLowerInvariant();
            var argument = split < 0 ? string.Empty : step[(split + 1)..].Trim();

            switch (verb)
            {
                case "write":
                    Log.Info($"Harness: backup — filed {FileMessages(account, int.TryParse(argument, out var n) ? n : 10, "before")} message(s) before the copy.");
                    break;

                case "to":
                {
                    var result = StoreBackup.To(account.Store, argument);
                    Log.Info($"Harness: backup to {argument} — {(result.Ok ? "ok" : "failed: " + result.Error)}, "
                             + $"{result.Bytes:N0} bytes; beside it {Sizes(argument)}.");
                    break;
                }

                case "during":
                {
                    // A writer for the length of the copy. The engine's whole claim is that a copy
                    // taken while something is writing is a point in time rather than a torn one.
                    var at = argument.IndexOf('@');
                    var count = int.TryParse(at < 0 ? argument : argument[..at], out var many) ? many : 200;
                    var target = at < 0 ? Path.Combine(Path.GetTempPath(), "mailbox-14b-during.db") : argument[(at + 1)..];
                    using var writing = new CancellationTokenSource();
                    var written = 0;
                    var writer = Task.Run(() =>
                    {
                        for (var i = 0; i < count && !writing.IsCancellationRequested; i++)
                        {
                            written += FileMessages(account, 1, $"during-{i}");
                        }
                    });

                    var result = StoreBackup.To(account.Store, target);
                    var duringCopy = Volatile.Read(ref written);
                    await writer.ConfigureAwait(true);

                    Log.Info($"Harness: backup during writes to {target} — {(result.Ok ? "ok" : "failed: " + result.Error)}, "
                             + $"{result.Bytes:N0} bytes; the writer had filed {duringCopy} of {written} by the time the copy returned.");
                    Report(target, "the copy taken during writes");
                    Log.Info($"Harness: backup — the live store now holds "
                             + $"{account.Store.ScalarLong("SELECT count(*) FROM messages")} message(s).");
                    break;
                }

                case "open":
                    Report(argument, "opened cold");
                    break;

                case "from":
                {
                    var (result, displaced) = StoreBackup.From(argument, account.Path);
                    Log.Info($"Harness: restore from {argument} — {(result.Ok ? "ok" : "failed: " + result.Error)}; "
                             + $"displaced {displaced ?? "nothing"}.");
                    break;
                }

                case "carry":
                {
                    // What a reader actually does with a backup: take the one file somewhere else.
                    // Anything the engine left in a companion file does not travel with it.
                    var (from, to) = argument.Split('>', 2) is [var a, var b] ? (a.Trim(), b.Trim()) : (argument, argument + ".carried");
                    IO.File.Copy(from, to, overwrite: true);
                    Report(to, $"carried from {from} as one file");
                    break;
                }
            }
        }
    }

    /// <summary>Files invented messages in an account's Inbox, and says how many landed.</summary>
    private static int FileMessages(OpenAccount account, int count, string tag)
    {
        if (account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox) is not { } inbox) return 0;

        var filed = 0;
        for (var i = 0; i < count; i++)
        {
            var uid = $"14b-{tag}-{i}";
            var raw = System.Text.Encoding.UTF8.GetBytes(
                $"From: A. Person <a.person@example.com>\r\nSubject: Filed {tag} {i}\r\n\r\nBody {tag} {i}\r\n");

            if (account.Mail.AddMessage(inbox.Id, new MessageSummary(
                    0, 0, uid, $"<{uid}@example.com>", "A. Person", "a.person@example.com",
                    $"Filed {tag} {i}", "Body", null, PosedClock.UtcNow, raw.Length,
                    false, false, false) { BodyText = $"Body {tag} {i}" }, raw) is not null)
            {
                filed++;
            }
        }

        return filed;
    }

    /// <summary>Opens a file on its own and says what is in it — the only question a backup answers.</summary>
    private static void Report(string path, string what)
    {
        if (!IO.File.Exists(path))
        {
            Log.Warn($"Harness: backup — {what}: there is no file at {path}.");
            return;
        }

        try
        {
            using var opened = new MailStore(path);
            var problems = opened.CheckIntegrity();
            Log.Info($"Harness: backup — {what} at {path}: {Sizes(path)}, schema {opened.Version}, "
                     + $"messages {opened.ScalarLong("SELECT count(*) FROM messages")}, "
                     + $"blobs {opened.ScalarLong("SELECT count(*) FROM blobs")}, "
                     + $"fts {Indexed(opened)}, "
                     + $"recoverable {opened.ScalarLong("SELECT count(*) FROM recoverable")}, "
                     + $"integrity {(problems.Count == 0 ? "ok" : string.Join("; ", problems))}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Harness: backup — {what} at {path} would not open.", ex);
        }
    }

    // ---- What a dialog drew --------------------------------------------------------------------

    /// <summary>
    /// Writes the top-most window's own text out, at each moment named:
    /// <c>MAILBOX_DIALOG_DUMP=600,2500</c> (milliseconds after the shell opens).
    /// </summary>
    /// <remarks>
    /// A dialog whose whole content is numbers — Mailbox Cleanup's sizes, the Data File dialog's
    /// Size row, Recover Deleted Items' status line, the conflict prompt's two columns — is
    /// photographed perfectly and read by nothing. This is the read half: the caption of every
    /// text the window is drawing, in visual order, which is what the reader sees and what
    /// <c>sqlite3</c> then gets held against.
    /// </remarks>
    private async Task DumpDialogsAsync(string spec, IDisposable? hold)
    {
        try
        {
            var waited = 0;
            foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out var at)) continue;
                await Task.Delay(Math.Max(0, at - waited)).ConfigureAwait(true);
                waited = at;
                DumpTopWindow(at);
            }
        }
        finally
        {
            hold?.Dispose();
        }
    }

    private void DumpTopWindow(int at)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life) return;
        if (life.Windows.LastOrDefault(w => !ReferenceEquals(w, this) && w.IsVisible) is not { } window)
        {
            Log.Info($"Harness: dialog dump at {at}ms — no dialog is open.");
            return;
        }

        window.UpdateLayout();

        var lines = new List<string>();
        foreach (var control in window.GetVisualDescendants())
        {
            switch (control)
            {
                case TextBlock { Text: { Length: > 0 } text }:
                    lines.Add(text.Replace('\n', ' ').Replace('\r', ' '));
                    break;
                case TextBox { Text: { Length: > 0 } typed }:
                    lines.Add($"[box] {typed}");
                    break;
                case NumericUpDown { Value: { } value }:
                    lines.Add($"[number] {value.ToString(CultureInfo.InvariantCulture)}");
                    break;
                case Button { Content: string caption } button:
                    lines.Add($"[button{(button.IsEffectivelyEnabled ? string.Empty : " greyed")}] {caption}");
                    break;
            }
        }

        Log.Info($"Harness: dialog dump at {at}ms — “{window.Title}” {window.Width:0}×{window.Height:0}: "
                 + string.Join(" ⏐ ", lines));
    }

    // ---- A calendar with a server behind it ------------------------------------------------------

    /// <summary>
    /// Points the default calendar at a real address and, when asked, queues a local change over
    /// it: <c>MAILBOX_DAV=http://127.0.0.1:8811/cal/|edit:Moved here</c>.
    /// </summary>
    /// <remarks>
    /// Nothing in the interface can make a DAV account (the absence is on the standing queue), so
    /// the engine can only be reached from a running application by writing the address onto a
    /// collection — which is exactly the hand-edited row the queue's own entry describes. The
    /// <c>edit:</c> half is what makes a conflict possible: a change made here, queued, and
    /// refused by a server whose copy has moved. Both copies then have to be real, which is the
    /// claim the conflict prompt makes and the reason for this door.
    /// </remarks>
    private static void PoseDav(string spec)
    {
        var parts = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;

        var url = parts[0];
        var calendar = App.Pim.DefaultCalendar();
        App.Pim.Store.Execute(
            "UPDATE collections SET dav_url = $url, account = $account, ctag = NULL, sync_token = NULL WHERE id = $id",
            ("$url", url), ("$account", "dav@example.net"), ("$id", calendar.Id));

        Log.Info($"Harness: dav — “{calendar.DisplayName}” now answers to {url}; it holds "
                 + $"{App.Pim.Items(calendar.Id).Count} item(s).");

        foreach (var step in parts.Skip(1))
        {
            if (!step.StartsWith("edit:", StringComparison.OrdinalIgnoreCase)) continue;

            var summary = step["edit:".Length..].Trim();
            if (App.Pim.Items(calendar.Id).FirstOrDefault(i => !i.IsOverride) is not { } item)
            {
                Log.Warn("Harness: dav — there is nothing in the calendar to change.");
                continue;
            }

            var changed = item with { Summary = summary };
            App.Pim.UpdateItem(changed);
            App.Pim.SetSyncState(item.Id, PimSyncState.Modified, item.Etag, item.DavHref);
            App.Pim.Queue(calendar.Id, item.Id, "put", item.DavHref);

            Log.Info($"Harness: dav — item {item.Id} uid {item.Uid} changed here from “{item.Summary}” "
                     + $"to “{summary}” and queued; href {item.DavHref ?? "none"}, etag {item.Etag ?? "none"}.");
        }
    }
}
