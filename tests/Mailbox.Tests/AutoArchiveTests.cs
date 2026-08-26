using Mailbox.Core.Archive;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>AutoArchive's pure half, its settings, and the store queries behind a run.</summary>
public class AutoArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailbox-autoarchive-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DueIsTheIntervalSinceTheLastRunOrNever()
    {
        Assert.True(AutoArchive.IsDue(null, 14, Now));
        Assert.False(AutoArchive.IsDue(Now.AddDays(-13), 14, Now));
        Assert.True(AutoArchive.IsDue(Now.AddDays(-14), 14, Now));
        Assert.True(AutoArchive.IsDue(Now.AddDays(-2), 0, Now));
    }

    [Fact]
    public void TheCutoffCountsInTheUnit()
    {
        Assert.Equal(Now.AddDays(-10), AutoArchive.Cutoff(10, ArchiveUnit.Days, Now));
        Assert.Equal(Now.AddDays(-14), AutoArchive.Cutoff(2, ArchiveUnit.Weeks, Now));
        Assert.Equal(Now.AddMonths(-6), AutoArchive.Cutoff(6, ArchiveUnit.Months, Now));
        Assert.Equal("month", AutoArchive.UnitWord(ArchiveUnit.Months, 1));
        Assert.Equal("weeks", AutoArchive.UnitWord(ArchiveUnit.Weeks, 3));
    }

    [Fact]
    public void AFoldersChoiceWinsOverTheDefaultAndOffMeansAlone()
    {
        var defaults = new FolderArchivePolicy { OlderThan = 6, Unit = ArchiveUnit.Months };
        Assert.Same(defaults, AutoArchive.Effective(null, defaults));
        Assert.Same(defaults, AutoArchive.Effective(new FolderArchivePolicy { Mode = FolderArchiveMode.Default, OlderThan = 1 }, defaults));
        Assert.Null(AutoArchive.Effective(new FolderArchivePolicy { Mode = FolderArchiveMode.Off }, defaults));

        var own = new FolderArchivePolicy { Mode = FolderArchiveMode.Custom, OlderThan = 30, Unit = ArchiveUnit.Days, Action = ArchiveAction.Delete };
        Assert.Same(own, AutoArchive.Effective(own, defaults));

        var back = FolderArchivePolicy.FromJson(own.ToJson());
        Assert.Equal(own, back);
        Assert.Equal(FolderArchiveMode.Default, FolderArchivePolicy.FromJson(null).Mode);
        Assert.Equal(FolderArchiveMode.Default, FolderArchivePolicy.FromJson("{nope").Mode);
    }

    /// <summary>
    /// Every switch reads as the reference has it — except the one that decides whether any of
    /// it happens, which is off until a reader turns it on.
    /// </summary>
    [Fact]
    public void TheOptionsReadTheReferencesDefaultsAndRememberChanges()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsStore(Path.Combine(_root, "settings.json"));
        var options = new AutoArchiveOptions(settings);

        // Off: nothing archives, and nothing asks, until somebody has gone looking for it.
        Assert.False(options.Enabled);

        Assert.Equal(14, options.EveryDays);
        Assert.True(options.Prompt);
        Assert.True(options.DeleteExpired);
        Assert.True(options.ArchiveOld);
        Assert.Equal(6, options.OlderThan);
        Assert.Equal(ArchiveUnit.Months, options.Unit);
        Assert.Equal(ArchiveAction.Move, options.Action);
        Assert.Null(options.LastRun);

        options.EveryDays = 7;
        options.Unit = ArchiveUnit.Weeks;
        options.Action = ArchiveAction.Delete;
        options.LastRun = Now;
        var again = new AutoArchiveOptions(new SettingsStore(Path.Combine(_root, "settings.json")));
        Assert.Equal(7, again.EveryDays);
        Assert.Equal(ArchiveUnit.Weeks, again.Unit);
        Assert.Equal(ArchiveAction.Delete, again.Action);
        Assert.Equal(Now, again.LastRun);
        Assert.Equal(ArchiveUnit.Weeks, again.DefaultPolicy.Unit);
    }

    [Fact]
    public void TheStoreFindsOldAndExpiredMailAndKeepsAFoldersChoice()
    {
        using var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        var inbox = repo.FolderWithRole(account.Id, FolderRole.Inbox)!;

        long Add(string subject, DateTimeOffset received, DateTimeOffset? expires = null)
        {
            var message = new MimeMessage { Subject = subject, Date = received };
            message.From.Add(new MailboxAddress("A", "a@example.org"));
            message.To.Add(new MailboxAddress("You", "you@example.com"));
            message.Body = new TextPart("plain") { Text = "Body" };
            message.MessageId = $"<{Guid.NewGuid():n}@example.org>";
            if (expires is { } when) message.Headers.Add("Expires", MimeKit.Utils.DateUtils.FormatDate(when));
            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            var raw = buffer.ToArray();
            return repo.AddMessage(inbox.Id, MessageMapper.ToSummary(message, Guid.NewGuid().ToString("n"), raw.Length, received), raw)!.Value;
        }

        var old = Add("Old", Now.AddMonths(-8));
        var recent = Add("Recent", Now.AddDays(-2));
        var expired = Add("Expired offer", Now.AddDays(-3), expires: Now.AddDays(-1));
        var current = Add("Current offer", Now.AddDays(-3), expires: Now.AddDays(10));

        // The mapper read the header, and the store kept it.
        Assert.Equal(Now.AddDays(-1), repo.GetMessage(expired)!.Expires);
        Assert.Null(repo.GetMessage(old)!.Expires);

        Assert.Equal([old], repo.MessagesOlderThan(inbox.Id, AutoArchive.Cutoff(6, ArchiveUnit.Months, Now)).Select(m => m.Id));
        Assert.Equal([expired], repo.ExpiredMessages(Now).Select(m => m.Id));
        Assert.DoesNotContain(current, repo.ExpiredMessages(Now).Select(m => m.Id));
        _ = recent;

        Assert.Null(repo.FolderAutoArchive(inbox.Id));
        var policy = new FolderArchivePolicy { Mode = FolderArchiveMode.Custom, OlderThan = 2, Unit = ArchiveUnit.Weeks };
        repo.SetFolderAutoArchive(inbox.Id, policy.ToJson());
        Assert.Equal(policy, FolderArchivePolicy.FromJson(repo.FolderAutoArchive(inbox.Id)));
        repo.SetFolderAutoArchive(inbox.Id, null);
        Assert.Null(repo.FolderAutoArchive(inbox.Id));
    }
}
