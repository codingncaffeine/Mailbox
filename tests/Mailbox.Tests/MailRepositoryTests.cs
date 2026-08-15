using Mailbox.Store;

namespace Mailbox.Tests;

public class MailRepositoryTests
{
    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static MessageSummary Sample(string uid, string subject = "Hello",
        string from = "alice@example.com", bool read = false) => new(
        0, 0, uid, $"<{uid}@example.com>", "Alice", from, subject, "Preview text",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1024, read, false, false);

    [Fact]
    public void AnAccountGetsTheStandardFolders()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        Assert.Equal(FolderRole.Inbox, inbox.Role);
        Assert.NotNull(repo.FolderWithRole(inbox.AccountId, FolderRole.Sent));
        Assert.NotNull(repo.FolderWithRole(inbox.AccountId, FolderRole.Outbox));
        Assert.Equal(7, repo.Folders(inbox.AccountId).Count);
    }

    /// <summary>
    /// The guard that stops a re-poll re-delivering an inbox: the second attempt is refused
    /// quietly, and reports that it filed nothing.
    /// </summary>
    [Fact]
    public void FilingTheSameServerIdTwiceIsIgnoredNotDuplicated()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        Assert.NotNull(repo.AddMessage(inbox.Id, Sample("uid-1")));
        Assert.Null(repo.AddMessage(inbox.Id, Sample("uid-1")));
        Assert.Single(repo.Messages(inbox.Id));
    }

    /// <summary>
    /// A duplicate must not leave its raw copy behind. Nothing points at that blob, so it would
    /// grow the store by the size of every message ever re-polled.
    /// </summary>
    [Fact]
    public void ADuplicateLeavesNoOrphanedBlob()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var raw = System.Text.Encoding.UTF8.GetBytes("From: a@example.com\r\n\r\nBody");

        repo.AddMessage(inbox.Id, Sample("uid-1"), raw);
        repo.AddMessage(inbox.Id, Sample("uid-1"), raw);

        Assert.Equal(1, store.ScalarLong("SELECT count(*) FROM blobs"));
    }

    [Fact]
    public void TheRawMessageComesBackByteForByte()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var raw = System.Text.Encoding.UTF8.GetBytes(
            "From: alice@example.com\r\nSubject: Hello\r\n\r\n" + new string('x', 5000));

        var id = repo.AddMessage(inbox.Id, Sample("uid-1"), raw)!.Value;

        Assert.Equal(raw, repo.LoadRaw(id));
    }

    [Fact]
    public void ALargeMessageIsStoredCompressed()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var raw = System.Text.Encoding.UTF8.GetBytes(new string('a', 20_000));

        repo.AddMessage(inbox.Id, Sample("uid-1"), raw);

        Assert.Equal("deflate", store.Query(
            "SELECT compression FROM blobs", r => r.GetString(0)).Single());
        Assert.True(store.ScalarLong("SELECT length(bytes) FROM blobs") < raw.Length);
    }

    /// <summary>
    /// A deleted message's raw bytes outlive its row — in the Recover Deleted Items holding area
    /// (§11) — and go for good when the holding area is purged. The blob is never orphaned:
    /// exactly one thing points at it at any moment.
    /// </summary>
    [Fact]
    public void DeletingAMessageKeepsItsBlobUntilPurged()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var id = repo.AddMessage(inbox.Id, Sample("uid-1"), [1, 2, 3])!.Value;

        repo.DeleteMessage(id);

        Assert.Equal(1, store.ScalarLong("SELECT count(*) FROM blobs"));
        Assert.Equal(1, repo.RecoverableCount());

        repo.Purge([.. repo.Recoverable().Select(r => r.Id)]);
        Assert.Equal(0, store.ScalarLong("SELECT count(*) FROM blobs"));
        Assert.Empty(store.CheckIntegrity());
    }

    [Fact]
    public void FoldersReportWhatTheyHold()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        repo.AddMessage(inbox.Id, Sample("uid-1", read: false));
        repo.AddMessage(inbox.Id, Sample("uid-2", read: true));

        var refreshed = repo.GetFolder(inbox.Id)!;

        Assert.Equal(2, refreshed.Total);
        Assert.Equal(1, refreshed.Unread);
    }

    [Fact]
    public void KnownServerIdsComeBackForWorkingOutWhatIsNew()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        repo.AddMessage(inbox.Id, Sample("uid-1"));
        repo.AddMessage(inbox.Id, Sample("uid-2"));

        Assert.Equal(["uid-1", "uid-2"], repo.ServerUids(inbox.Id).OrderBy(x => x));
        Assert.True(repo.HasServerUid(inbox.Id, "uid-1"));
        Assert.False(repo.HasServerUid(inbox.Id, "uid-9"));
    }

    [Fact]
    public void SearchRanksAndScopes()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var sent = repo.FolderWithRole(inbox.AccountId, FolderRole.Sent)!;
        repo.AddMessage(inbox.Id, Sample("uid-1", "Quarterly numbers"));
        repo.AddMessage(sent.Id, Sample("uid-2", "Quarterly review"));

        Assert.Equal(2, repo.Search("quarterly").Count);
        Assert.Single(repo.Search("quarterly", inbox.Id));
        Assert.Empty(repo.Search("   "));
    }

    /// <summary>
    /// A search box takes whatever is typed. Unbalanced quotes and stray operators are FTS5
    /// syntax and would throw at the user rather than finding nothing.
    /// </summary>
    [Theory]
    [InlineData("\"unbalanced")]
    [InlineData("NEAR(")]
    [InlineData("a OR")]
    [InlineData("*")]
    [InlineData("^")]
    public void SearchSurvivesWhateverIsTyped(string term)
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        repo.AddMessage(inbox.Id, Sample("uid-1", "Quarterly numbers"));

        var found = repo.Search(term);        // must not throw

        Assert.NotNull(found);
    }

    [Theory]
    [InlineData("Re: Quarterly numbers", "quarterly numbers")]
    [InlineData("RE: FW: Quarterly numbers", "quarterly numbers")]
    [InlineData("Fwd: Re: Quarterly numbers", "quarterly numbers")]
    [InlineData("Quarterly numbers", "quarterly numbers")]
    public void RepliesThreadWithWhatTheyReplyTo(string subject, string expected)
        => Assert.Equal(expected, MailRepository.ThreadKey(subject));

    [Fact]
    public void MovingAMessageChangesTheFolderItCounts()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var archive = repo.FolderWithRole(inbox.AccountId, FolderRole.Archive)!;
        var id = repo.AddMessage(inbox.Id, Sample("uid-1"))!.Value;

        repo.MoveMessage(id, archive.Id);

        Assert.Empty(repo.Messages(inbox.Id));
        Assert.Single(repo.Messages(archive.Id));
    }

    /// <summary>
    /// Selecting a folder's worth of mail and acting on it is ordinary. The bulk paths exist so
    /// that is one statement rather than a thousand, and they have to agree with the single-row
    /// ones about what they did.
    /// </summary>
    [Fact]
    public void MarkingManyAsReadTakesEffectOnAllOfThem()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var ids = Enumerable.Range(0, 50)
            .Select(i => repo.AddMessage(inbox.Id, Sample($"uid-{i}"))!.Value)
            .ToList();

        Assert.Equal(50, repo.SetRead(ids, read: true));
        Assert.Equal(0, repo.GetFolder(inbox.Id)!.Unread);

        Assert.Equal(50, repo.SetRead(ids, read: false));
        Assert.Equal(50, repo.GetFolder(inbox.Id)!.Unread);
    }

    [Fact]
    public void FlaggingManyWorksTheSameWay()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var ids = Enumerable.Range(0, 5)
            .Select(i => repo.AddMessage(inbox.Id, Sample($"uid-{i}"))!.Value)
            .ToList();

        repo.SetFlagged(ids, flagged: true);

        Assert.All(repo.Messages(inbox.Id), m => Assert.True(m.IsFlagged));
    }

    [Fact]
    public void MovingManyLeavesTheSourceEmpty()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var archive = repo.FolderWithRole(inbox.AccountId, FolderRole.Archive)!;
        var ids = Enumerable.Range(0, 8)
            .Select(i => repo.AddMessage(inbox.Id, Sample($"uid-{i}"))!.Value)
            .ToList();

        Assert.Equal(8, repo.MoveMessages(ids, archive.Id));
        Assert.Empty(repo.Messages(inbox.Id));
        Assert.Equal(8, repo.Messages(archive.Id).Count);
    }

    /// <summary>
    /// Bulk delete must take the raw copies with it once the retention window closes, or the
    /// store grows without bound.
    /// </summary>
    [Fact]
    public void DeletingManyKeepsTheirBlobsOnlyForTheRetentionWindow()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var ids = Enumerable.Range(0, 6)
            .Select(i => repo.AddMessage(inbox.Id, Sample($"uid-{i}"), [1, 2, 3])!.Value)
            .ToList();

        Assert.Equal(6, store.ScalarLong("SELECT count(*) FROM blobs"));

        repo.DeleteMessages(ids);

        Assert.Empty(repo.Messages(inbox.Id));
        Assert.Equal(6, repo.RecoverableCount());

        // Not yet old enough: nothing purged. Past the window: everything, blobs included.
        Assert.Equal(0, repo.PurgeRecoverableOlderThan(DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(6, repo.PurgeRecoverableOlderThan(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(0, store.ScalarLong("SELECT count(*) FROM blobs"));
        Assert.Empty(store.CheckIntegrity());
    }

    [Fact]
    public void DeletingSomeLeavesTheRestAndTheirBlobs()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var keep = repo.AddMessage(inbox.Id, Sample("keep"), [1, 2, 3])!.Value;
        var drop = repo.AddMessage(inbox.Id, Sample("drop"), [4, 5, 6])!.Value;

        repo.DeleteMessages([drop]);
        repo.Purge([.. repo.Recoverable().Select(r => r.Id)]);

        Assert.Single(repo.Messages(inbox.Id));
        Assert.Equal(1, store.ScalarLong("SELECT count(*) FROM blobs"));
        Assert.NotNull(repo.LoadRaw(keep));
    }

    /// <summary>
    /// Recover Deleted Items: a message deleted for good comes back where it was, with the state
    /// it had, and to Deleted Items when its folder has gone.
    /// </summary>
    [Fact]
    public void ADeletedMessageCanBeRestoredToWhereItWas()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var projects = repo.AddFolder(inbox.AccountId, "Projects");
        var deleted = repo.FolderWithRole(inbox.AccountId, FolderRole.Deleted)!;

        var inProjects = repo.AddMessage(projects.Id, Sample("uid-1", "Plan", read: true), [1, 2, 3])!.Value;
        repo.SetFlagged([inProjects], true);
        var inInbox = repo.AddMessage(inbox.Id, Sample("uid-2", "Hello"), [4, 5, 6])!.Value;

        repo.DeleteMessages([inProjects, inInbox]);
        Assert.Empty(repo.Messages(projects.Id));

        var held = repo.Recoverable();
        Assert.Equal(2, held.Count);
        Assert.Contains(held, h => h.Subject == "Plan" && h.OriginalFolderName == "Projects");

        // Restore the Projects one: back where it was, read and flagged as it was, bytes intact.
        var plan = held.Single(h => h.Subject == "Plan");
        Assert.Equal(1, repo.Restore([plan.Id], deleted.Id));
        var back = Assert.Single(repo.Messages(projects.Id));
        Assert.Equal("Plan", back.Subject);
        Assert.True(back.IsRead);
        Assert.True(back.IsFlagged);
        Assert.Equal([1, 2, 3], repo.LoadRaw(back.Id));
        Assert.Single(repo.Recoverable());

        // Its folder gone: the other comes back to the fallback.
        repo.RemoveFolder(inbox.Id);
        var hello = repo.Recoverable().Single();
        Assert.Equal(1, repo.Restore([hello.Id], deleted.Id));
        Assert.Equal("Hello", Assert.Single(repo.Messages(deleted.Id)).Subject);
        Assert.Empty(repo.Recoverable());
        Assert.Empty(store.CheckIntegrity());
    }

    [Fact]
    public void TheBulkPathsDoNothingWhenGivenNothing()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        repo.AddMessage(inbox.Id, Sample("uid-1"));

        Assert.Equal(0, repo.SetRead([], true));
        Assert.Equal(0, repo.SetFlagged([], true));
        Assert.Equal(0, repo.MoveMessages([], inbox.Id));
        Assert.Equal(0, repo.DeleteMessages([]));
        Assert.Single(repo.Messages(inbox.Id));
    }

    /// <summary>
    /// Instant Search's two scopes at the store layer: a folder id narrows to one folder, and
    /// null searches every folder in the account — which is what "Current Mailbox" runs.
    /// </summary>
    [Fact]
    public void SearchScopesToOneFolderOrTheWholeAccount()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var archive = repo.FolderWithRole(inbox.AccountId, FolderRole.Archive)!;

        repo.AddMessage(inbox.Id, Sample("uid-1", subject: "Quarterly agenda"));
        repo.AddMessage(archive.Id, Sample("uid-2", subject: "Old agenda notes"));

        // Whole account: both folders' matches come back.
        Assert.Equal(2, repo.Search("agenda").Count);

        // Narrowed to the inbox: only its match.
        var inboxOnly = repo.Search("agenda", inbox.Id);
        Assert.Equal("Quarterly agenda", Assert.Single(inboxOnly).Subject);

        // A term in neither finds nothing, and a folder with no match is empty.
        Assert.Empty(repo.Search("biscuits"));
        Assert.Empty(repo.Search("quarterly", archive.Id));
    }

    /// <summary>
    /// A follow-up is a flag with a due date and a done state: flagging sets all three, completing
    /// clears the flag and leaves the check, and clearing wipes the lot.
    /// </summary>
    [Fact]
    public void FollowUpFlagsCarryADueDateAndAComplete()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var id = repo.AddMessage(inbox.Id, Sample("uid-1"))!.Value;
        var due = new DateTimeOffset(2026, 8, 20, 17, 0, 0, TimeSpan.Zero);

        repo.SetFollowUp([id], due);
        var flagged = repo.GetMessage(id)!;
        Assert.True(flagged.IsFlagged);
        Assert.False(flagged.FollowUpComplete);
        Assert.Equal(due, flagged.FollowUpDue);

        repo.CompleteFollowUp([id]);
        var done = repo.GetMessage(id)!;
        Assert.False(done.IsFlagged);
        Assert.True(done.FollowUpComplete);

        repo.ClearFollowUp([id]);
        var cleared = repo.GetMessage(id)!;
        Assert.False(cleared.IsFlagged);
        Assert.False(cleared.FollowUpComplete);
        Assert.Null(cleared.FollowUpDue);
    }

    /// <summary>Flagging with no date is a flag without a due date, not no flag.</summary>
    [Fact]
    public void AFollowUpCanHaveNoDate()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var id = repo.AddMessage(inbox.Id, Sample("uid-1"))!.Value;

        repo.SetFollowUp([id], due: null);
        var flagged = repo.GetMessage(id)!;
        Assert.True(flagged.IsFlagged);
        Assert.Null(flagged.FollowUpDue);
    }

    /// <summary>A word only in the body, in no subject or sender, is found — schema 11 indexes it.</summary>
    [Fact]
    public void SearchReachesTheBody()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        repo.AddMessage(inbox.Id, Sample("uid-1", subject: "Lunch") with
        {
            BodyText = "The reconciliation variance on line 14 is the one to talk through.",
        });

        // Not in the subject, not the sender — only the body.
        Assert.Single(repo.Search("variance"));
        Assert.Single(repo.Search("reconciliation"));
        Assert.Empty(repo.Search("aardvark"));
    }

    // ---- Snooze (§12) -----------------------------------------------------------------------

    /// <summary>
    /// A snoozed message leaves the list, is counted out of the folder, is listed among the
    /// snoozed, and comes back when its time comes — unread and at the top.
    /// </summary>
    [Fact]
    public void ASnoozedMessageLeavesTheListAndComesBackUnreadAtTheTop()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var older = repo.AddMessage(inbox.Id, Sample("uid-1", "Older", read: true))!.Value;
        var newer = repo.AddMessage(inbox.Id, Sample("uid-2", "Newer") with { Received = DateTimeOffset.UnixEpoch.AddHours(1) })!.Value;
        var later = DateTimeOffset.UtcNow.AddHours(4);

        repo.Snooze([older], later);

        Assert.Equal([newer], repo.Messages(inbox.Id).Select(m => m.Id));
        Assert.Equal(1, repo.GetFolder(inbox.Id)!.Total);
        Assert.Equal(1, repo.GetFolder(inbox.Id)!.Unread);
        Assert.Equal(later.ToUnixTimeSeconds(), repo.Snoozed(inbox.Id).Single().SnoozedUntil!.Value.ToUnixTimeSeconds());

        // Not yet due: nothing wakes.
        Assert.Empty(repo.WakeSnoozed(DateTimeOffset.UtcNow));
        Assert.Single(repo.Messages(inbox.Id));

        // Due: it comes back unread and newest, at the moment it woke.
        var wake = later.AddMinutes(1);
        Assert.Equal([(inbox.Id, older)], repo.WakeSnoozed(wake));

        var back = repo.GetMessage(older)!;
        Assert.Null(back.SnoozedUntil);
        Assert.False(back.IsRead);
        Assert.Equal(wake.ToUnixTimeSeconds(), back.Received.ToUnixTimeSeconds());
        Assert.Equal([older, newer], repo.Messages(inbox.Id).Select(m => m.Id));
        Assert.Empty(repo.Snoozed(inbox.Id));
    }

    [Fact]
    public void UnsnoozeBringsAMessageBackNowAndLeavesOthersAlone()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var one = repo.AddMessage(inbox.Id, Sample("uid-1", "One", read: true))!.Value;
        var two = repo.AddMessage(inbox.Id, Sample("uid-2", "Two", read: true))!.Value;
        repo.Snooze([one, two], DateTimeOffset.UtcNow.AddDays(1));
        Assert.Empty(repo.Messages(inbox.Id));

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, repo.Unsnooze([one], now));

        Assert.Equal([one], repo.Messages(inbox.Id).Select(m => m.Id));
        Assert.False(repo.GetMessage(one)!.IsRead);
        Assert.Equal([two], repo.Snoozed(inbox.Id).Select(m => m.Id));

        // Unsnoozing something that is not snoozed changes nothing about it.
        Assert.Equal(0, repo.Unsnooze([one], now));
        Assert.False(repo.GetMessage(one)!.IsRead);
    }

    // ---- Reminders --------------------------------------------------------------------------

    /// <summary>
    /// The Custom flag carries what it says, its dates and a reminder; the reminder comes due,
    /// is dismissed or snoozed, and goes with the flag when the flag is cleared or completed.
    /// </summary>
    [Fact]
    public void ACustomFlagCarriesAReminderThatComesDueAndCanBeSnoozed()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var id = repo.AddMessage(inbox.Id, Sample("uid-1"))!.Value;
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        repo.SetCustomFollowUp([id], "Call", now.AddDays(-1), now.AddDays(1), now.AddHours(1));
        var flagged = repo.GetMessage(id)!;
        Assert.True(flagged.IsFlagged);
        Assert.Equal("Call", flagged.FollowUpType);
        Assert.Equal(now.AddDays(-1), flagged.FollowUpStart);
        Assert.Equal(now.AddDays(1), flagged.FollowUpDue);
        Assert.Equal(now.AddHours(1), flagged.Reminder);

        Assert.Empty(repo.DueReminders(now));
        Assert.Equal([id], repo.DueReminders(now.AddHours(1)).Select(m => m.Id));

        // Snoozed: not due until later. Dismissed: never again.
        repo.SetReminder([id], now.AddHours(2));
        Assert.Empty(repo.DueReminders(now.AddHours(1)));
        Assert.Single(repo.DueReminders(now.AddHours(2)));

        repo.SetReminder([id], null);
        Assert.Empty(repo.DueReminders(now.AddDays(5)));

        // Completing or clearing the flag takes the reminder with it.
        repo.SetReminder([id], now);
        repo.CompleteFollowUp([id]);
        Assert.Empty(repo.DueReminders(now.AddDays(5)));

        repo.SetCustomFollowUp([id], "Review", null, null, now);
        repo.ClearFollowUp([id]);
        Assert.Empty(repo.DueReminders(now.AddDays(5)));
        Assert.Null(repo.GetMessage(id)!.FollowUpType);
    }
}
