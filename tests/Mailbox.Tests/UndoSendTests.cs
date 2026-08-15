using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Undo Send (§12): the hold, and taking a message back out of it.
/// </summary>
/// <remarks>
/// The half worth testing is the race. Everything else is a number in a settings file, but
/// whether a message comes back depends on what the sender is doing at that instant, and getting
/// it wrong in the generous direction means handing somebody a compose window for a message that
/// is already on its way to the recipient.
/// </remarks>
public class UndoSendTests : IDisposable
{
    private readonly string _settingsPath =
        Path.Combine(Path.GetTempPath(), $"mailbox-undo-{Guid.NewGuid():n}.json");

    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private UndoSend Setting() => new(new SettingsStore(_settingsPath));

    private static (MailStore Store, MailRepository Repo, long Account) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, account.Id);
    }

    private static MimeMessage Message(string subject = "Regrettable")
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress("You", "you@example.com"));
        message.To.Add(new MailboxAddress("A. Person", "a@example.com"));
        message.Body = new TextPart("plain") { Text = "Sent in haste." };
        return message;
    }

    // ---- The setting -------------------------------------------------------------------------

    /// <summary>
    /// On by default, which is the unusual part and the deliberate one: the cost is a few
    /// seconds nobody notices, and the cost of not having it is one everybody has paid.
    /// </summary>
    [Fact]
    public void ItIsOnByDefaultAtFiveSeconds()
    {
        var undo = Setting();

        Assert.True(undo.IsEnabled);
        Assert.Equal(5, undo.Seconds);
        Assert.Equal(Now.AddSeconds(5), undo.HoldUntil(Now));
    }

    [Fact]
    public void TurnedOffItHoldsNothing()
    {
        var undo = Setting();
        undo.IsEnabled = false;

        Assert.Null(undo.HoldUntil(Now));
    }

    [Fact]
    public void ASettingSurvivesARestart()
    {
        var undo = Setting();
        undo.Seconds = 12;
        undo.IsEnabled = false;

        var reopened = Setting();
        Assert.Equal(12, reopened.Seconds);
        Assert.False(reopened.IsEnabled);
    }

    /// <summary>
    /// Past about half a minute this stops being an undo and becomes delayed delivery, which
    /// already exists and is per-message. The cap keeps them from becoming the same feature.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, UndoSend.MaximumSeconds)]
    [InlineData(-3, 0)]
    public void TheHoldIsClampedToSomethingThatIsStillAnUndo(int asked, int kept)
    {
        var undo = Setting();
        undo.Seconds = asked;

        Assert.Equal(kept, undo.Seconds);
    }

    [Fact]
    public void ZeroSecondsHoldsNothingEvenWhenEnabled()
    {
        var undo = Setting();
        undo.Seconds = 0;

        Assert.True(undo.IsEnabled);
        Assert.Null(undo.HoldUntil(Now));
    }

    // ---- Taking it back ------------------------------------------------------------------------

    [Fact]
    public void AHeldMessageComesBackWithItsBytes()
    {
        var (store, repo, account) = Fresh();
        using var _ = store;

        var id = new SmtpSender(repo).Queue(account, Message(), Now);
        repo.ScheduleOutbox(id, Now.AddSeconds(5));

        var raw = repo.WithdrawOutbox(id, Now.AddSeconds(2));

        Assert.NotNull(raw);
        Assert.Empty(repo.Outbox(account));

        using var stream = new MemoryStream(raw);
        Assert.Equal("Regrettable",
            MimeMessage.Load(stream, TestContext.Current.CancellationToken).Subject);
    }

    /// <summary>
    /// The whole feature turns on this. Once the hold is up the message is the sender's, and
    /// handing somebody a compose window for it would be showing them a message that has
    /// already gone.
    /// </summary>
    [Fact]
    public void OnceTheHoldExpiresItIsTooLate()
    {
        var (store, repo, account) = Fresh();
        using var _ = store;

        var id = new SmtpSender(repo).Queue(account, Message(), Now);
        repo.ScheduleOutbox(id, Now.AddSeconds(5));

        Assert.Null(repo.WithdrawOutbox(id, Now.AddSeconds(5)));
        Assert.Null(repo.WithdrawOutbox(id, Now.AddSeconds(30)));

        // And it is still there to be sent, rather than having been quietly destroyed.
        Assert.Single(repo.Outbox(account));
    }

    /// <summary>A message the sender has already claimed is not one to take back.</summary>
    [Fact]
    public void AMessageAlreadySendingIsNotWithdrawn()
    {
        var (store, repo, account) = Fresh();
        using var _ = store;

        var id = new SmtpSender(repo).Queue(account, Message(), Now);
        repo.ScheduleOutbox(id, Now.AddSeconds(5));
        repo.SetOutboxState(id, OutboxState.Sending);

        Assert.Null(repo.WithdrawOutbox(id, Now.AddSeconds(1)));
    }

    [Theory]
    [InlineData(OutboxState.Sent)]
    [InlineData(OutboxState.Failed)]
    [InlineData(OutboxState.Held)]
    public void OnlySomethingStillQueuedComesBack(OutboxState state)
    {
        var (store, repo, account) = Fresh();
        using var _ = store;

        var id = new SmtpSender(repo).Queue(account, Message(), Now);
        repo.ScheduleOutbox(id, Now.AddSeconds(5));
        repo.SetOutboxState(id, state);

        Assert.Null(repo.WithdrawOutbox(id, Now.AddSeconds(1)));
    }

    /// <summary>A message queued with no hold at all was never withdrawable.</summary>
    [Fact]
    public void AMessageWithNoHoldIsNotWithdrawable()
    {
        var (store, repo, account) = Fresh();
        using var _ = store;

        var id = new SmtpSender(repo).Queue(account, Message(), Now);
        repo.Store.Execute("UPDATE outbox SET next_try_utc = NULL WHERE id = $id", ("$id", id));

        Assert.Null(repo.WithdrawOutbox(id, Now));
    }

    [Fact]
    public void WithdrawingSomethingThatIsNotThereSaysSoRatherThanThrowing()
    {
        var (store, repo, _) = Fresh();
        using var _s = store;

        Assert.Null(repo.WithdrawOutbox(9999, Now));
    }

    /// <summary>Pressing Undo twice must not take a second message back.</summary>
    [Fact]
    public void ItCanOnlyBeTakenBackOnce()
    {
        var (store, repo, account) = Fresh();
        using var _ = store;

        var id = new SmtpSender(repo).Queue(account, Message(), Now);
        repo.ScheduleOutbox(id, Now.AddSeconds(5));

        Assert.NotNull(repo.WithdrawOutbox(id, Now.AddSeconds(1)));
        Assert.Null(repo.WithdrawOutbox(id, Now.AddSeconds(2)));
    }

    // ---- What the hold does to sending -----------------------------------------------------------

    /// <summary>
    /// The hold has to actually hold. A send/receive during the grace period must leave the
    /// message alone, or the undo is a button over a message that has already gone.
    /// </summary>
    [Fact]
    public void AHeldMessageIsNotDueYet()
    {
        var (store, repo, account) = Fresh();
        using var _ = store;

        var id = new SmtpSender(repo).Queue(account, Message(), Now);
        repo.ScheduleOutbox(id, Now.AddSeconds(5));

        Assert.Empty(repo.DueOutbox(account, Now.AddSeconds(1)));
        Assert.Single(repo.DueOutbox(account, Now.AddSeconds(5)));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
        }
        catch (Exception)
        {
            // A scratch file that will not delete is not a test failure.
        }
    }
}
