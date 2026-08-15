namespace Mailbox.App.Views;

/// <summary>
/// A message that has gone to the outbox and can still be pulled back.
/// </summary>
/// <param name="Address">The sending account, which is where the outbox is.</param>
/// <param name="OutboxId">The row to withdraw, if the reader is quick.</param>
/// <param name="Expires">When the hold runs out and it goes for real.</param>
/// <param name="Subject">What to reopen it as, if it comes back.</param>
public sealed class QueuedMessageEventArgs(
    string address, long outboxId, DateTimeOffset expires, string subject) : EventArgs
{
    public string Address { get; } = address;

    public long OutboxId { get; } = outboxId;

    public DateTimeOffset Expires { get; } = expires;

    public string Subject { get; } = subject;

    /// <summary>How long is left, never negative.</summary>
    public TimeSpan Remaining(DateTimeOffset now)
    {
        var left = Expires - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}
