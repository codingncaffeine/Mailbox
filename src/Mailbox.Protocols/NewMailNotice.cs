namespace Mailbox.Protocols;

/// <summary>
/// What a "you have new mail" notification says, from what a send/receive brought in.
/// </summary>
/// <remarks>
/// Pure so the wording is tested without a desktop: how many, and — when more than one account
/// received — which. Nothing to say when nothing arrived, which is the common case and must not
/// pop a toast every few minutes for no new mail. It lives beside the send/receive result it
/// reads rather than in the application, so it can be checked without one.
/// </remarks>
public static class NewMailNotice
{
    /// <summary>The summary and body for a run's result, or null when nothing new arrived.</summary>
    public static (string Summary, string Body)? For(SendReceiveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var arrived = result.Accounts.Where(a => a.Received > 0).ToList();
        if (arrived.Count == 0) return null;

        var total = arrived.Sum(a => a.Received);
        var summary = total == 1 ? "1 new message" : $"{total} new messages";

        // One account: name it in the body. Several: list how many for each, so the reader knows
        // where to look rather than only that something came.
        var body = arrived.Count == 1
            ? arrived[0].Address
            : string.Join("\n", arrived.Select(a => $"{a.Address}: {a.Received}"));

        return (summary, body);
    }
}
