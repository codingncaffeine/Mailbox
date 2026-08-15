namespace Mailbox.Protocols;

/// <summary>
/// One desktop notification about new mail: what it says, and — when it is about a single
/// message — which message, so its buttons have something to act on.
/// </summary>
/// <param name="Summary">The heading: the sender for one message, a count otherwise.</param>
/// <param name="Body">The subject and first line for one message; the accounts otherwise.</param>
/// <param name="Address">The account the mail arrived in, or the first that did for a count.</param>
/// <param name="MessageId">The store id of the message, or null for a toast about several.</param>
public sealed record NewMailToast(string Summary, string Body, string Address, long? MessageId)
{
    /// <summary>True when the toast is about one message and can offer Reply, Delete and Mark Read.</summary>
    public bool IsSingle => MessageId is not null;
}

/// <summary>What a toast needs to say about one message: who it is from, its subject and its first line.</summary>
public sealed record ArrivedMessage(string From, string Subject, string Preview);

/// <summary>
/// What a "you have new mail" notification says, from what a send/receive brought in.
/// </summary>
/// <remarks>
/// Pure so the wording is tested without a desktop: how many, and — when more than one account
/// received — which. Nothing to say when nothing arrived, which is the common case and must not
/// pop a toast every few minutes for no new mail. It lives beside the send/receive result it
/// reads rather than in the application, so it can be checked without one.
/// <para>
/// It reads <see cref="AccountRunResult.Arrived"/> — what landed in an Inbox — rather than the
/// download count. Mail the junk filter filed on the way in was downloaded, and is not news:
/// a toast announcing it would undo the filter, and the count on the Junk folder is how the
/// reader learns something was caught.
/// </para>
/// </remarks>
public static class NewMailNotice
{
    /// <summary>
    /// Up to this many new messages get a toast each, naming the sender and subject with the
    /// reference's Reply / Delete / Mark Read on it. Past it, one toast carries the count: a
    /// first poll of a full mailbox is not four hundred alerts.
    /// </summary>
    public const int PerMessageLimit = 3;

    /// <summary>The count toast's summary and body, or null when nothing new arrived.</summary>
    public static (string Summary, string Body)? For(SendReceiveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var arrived = result.Accounts.Where(a => a.Arrived.Count > 0).ToList();
        if (arrived.Count == 0) return null;

        var total = arrived.Sum(a => a.Arrived.Count);
        var summary = total == 1 ? "1 new message" : $"{total} new messages";

        // One account: name it in the body. Several: list how many for each, so the reader knows
        // where to look rather than only that something came.
        var body = arrived.Count == 1
            ? arrived[0].Address
            : string.Join("\n", arrived.Select(a => $"{a.Address}: {a.Arrived.Count}"));

        return (summary, body);
    }

    /// <summary>
    /// The toasts for a run: one per message while there are few, naming the sender and the
    /// subject, or the single count toast <see cref="For"/> builds when there are many.
    /// </summary>
    /// <param name="describe">
    /// Looks a message up by account and store id, or returns null when it cannot be read — in
    /// which case the run gets the count toast rather than a toast that says nothing.
    /// </param>
    public static IReadOnlyList<NewMailToast> Toasts(
        SendReceiveResult result, Func<string, long, ArrivedMessage?> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);

        if (For(result) is not { } count) return [];

        var arrivals = result.Accounts
            .SelectMany(a => a.Arrived.Select(id => (a.Address, Id: id)))
            .ToList();

        if (arrivals.Count <= PerMessageLimit)
        {
            var toasts = new List<NewMailToast>(arrivals.Count);
            foreach (var (address, id) in arrivals)
            {
                if (describe(address, id) is not { } message) break;

                toasts.Add(new NewMailToast(
                    message.From.Length > 0 ? message.From : address,
                    Body(message),
                    address,
                    id));
            }

            if (toasts.Count == arrivals.Count) return toasts;
        }

        var first = result.Accounts.First(a => a.Arrived.Count > 0).Address;
        return [new NewMailToast(count.Summary, count.Body, first, null)];
    }

    /// <summary>The subject, then the first line under it — the reference's alert shows both.</summary>
    private static string Body(ArrivedMessage message)
    {
        var subject = message.Subject.Length > 0 ? message.Subject : "(no subject)";
        var preview = FirstLine(message.Preview);
        return preview.Length > 0 ? subject + "\n" + preview : subject;
    }

    private static string FirstLine(string text)
    {
        var line = text.AsSpan().Trim();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0) line = line[..end].TrimEnd();

        const int max = 90;
        return line.Length <= max ? line.ToString() : string.Concat(line[..(max - 1)].TrimEnd(), "…");
    }
}
