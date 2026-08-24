namespace Mailbox.Plugins.Api;

/// <summary>
/// Mail on its way in and on its way out. Arrival wants <c>arrival</c>; sending wants
/// <c>sending</c>.
/// </summary>
public interface IPluginPipeline
{
    /// <summary>
    /// Runs the hook on every message as it arrives, after the application's own handlers —
    /// the junk filter, ignored conversations, the Focused Inbox and the rules — so the hook
    /// sees where they left it. Called on a background thread while a send/receive is running;
    /// the answer is applied by the host, and on an IMAP account the move it makes is journalled
    /// to the server like any other.
    /// </summary>
    void OnArrival(Func<ArrivingMessage, ArrivalAction> hook);

    /// <summary>
    /// Runs the hook on every message the writer sends, before it is queued and before any
    /// cryptography — a veto must land while the writer is still there to read it, and a message
    /// stopped after signing would have been signed for nothing. Called on the UI thread;
    /// <see cref="SendDecision.Stop"/> keeps the message where it is and tells the writer which
    /// plugin stopped it and why.
    /// </summary>
    void OnSending(Func<OutgoingMessage, SendDecision> hook);
}

/// <summary>A message that has just arrived and been stored.</summary>
public sealed record ArrivingMessage(
    string Account,
    long MessageId,
    string Folder,
    string Subject,
    string From,
    IReadOnlyList<string> To);

/// <summary>
/// What an arrival hook wants done. <see cref="None"/> leaves the message where it is;
/// <see cref="MoveTo"/> names a folder by name and moves it there, creating nothing — an unknown
/// name is recorded and the message stays; <see cref="Delete"/> deletes it.
/// </summary>
public sealed record ArrivalAction
{
    public static ArrivalAction None { get; } = new(ArrivalActionKind.None, null);

    public static ArrivalAction Delete { get; } = new(ArrivalActionKind.Delete, null);

    public static ArrivalAction MoveTo(string folderName) =>
        new(ArrivalActionKind.Move, folderName);

    private ArrivalAction(ArrivalActionKind kind, string? folder)
    {
        Kind = kind;
        Folder = folder;
    }

    public ArrivalActionKind Kind { get; }

    public string? Folder { get; }
}

public enum ArrivalActionKind
{
    None,
    Move,
    Delete,
}

/// <summary>An outgoing message as the writer is sending it.</summary>
public sealed record OutgoingMessage(
    string Account,
    string Subject,
    string From,
    IReadOnlyList<string> Recipients);

/// <summary>
/// Whether an outgoing message goes. <see cref="Stop"/> names the reason in the writer's own
/// window, beside the plugin's name.
/// </summary>
public sealed record SendDecision
{
    public static SendDecision Allow { get; } = new(allowed: true, reason: null);

    public static SendDecision Stop(string reason) => new(allowed: false, reason);

    private SendDecision(bool allowed, string? reason)
    {
        Allowed = allowed;
        Reason = reason;
    }

    public bool Allowed { get; }

    public string? Reason { get; }
}
