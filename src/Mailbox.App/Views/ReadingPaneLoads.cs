namespace Mailbox.App.Views;

/// <summary>
/// The reading pane's rule about loads: one at a time, and only ever the newest one waiting.
/// </summary>
/// <remarks>
/// Navigating an offscreen WebKit view that is still loading tears the old document down
/// underneath the new one. That race is invisible in ordinary reading and lethal under a fast
/// hand: a forced double-load ended about one harness run in twelve with a signal 11 on a thread
/// with no managed frames, and short of that it simply lost navigations — ten asked for, two that
/// ever reported finishing, eight documents abandoned mid-parse with nothing to say so.
/// <para>
/// So a load waits for the load in flight. What waits is one document rather than a queue,
/// because a reader arrowing down a list asks for a load per row and owes nothing to the rows
/// they passed through: the pane owes them the message they stopped on. The wait is one
/// document's parse — tens of milliseconds — and it is never longer than that, since the newest
/// document always replaces the one waiting.
/// </para>
/// <para>
/// Kept apart from the pane because it is the part that can be checked without a web engine:
/// the pane's own proof needs WebKit, a running window and a race that shows up one run in
/// twelve, and this is the rule that race was about.
/// </para>
/// </remarks>
internal sealed class ReadingPaneLoads
{
    private string? _waiting;

    /// <summary>The load in flight, as a number that only goes up, or 0 while the engine is idle.</summary>
    public long InFlight { get; private set; }

    /// <summary>The last load started, whether it is still running or not.</summary>
    public long Started { get; private set; }

    /// <summary>How many loads the pane asked for, and how many were actually handed to the engine.</summary>
    public int Asked { get; private set; }

    public int Ran { get; private set; }

    /// <summary>
    /// Asks for a document to be loaded. Returns it when the engine is free to take it now, and
    /// null when it has been left waiting for the load in flight.
    /// </summary>
    public string? Ask(string html)
    {
        Asked++;

        if (InFlight != 0)
        {
            _waiting = html;
            return null;
        }

        return Take(html);
    }

    /// <summary>The load in flight has finished, however it finished.</summary>
    public void Finished() => InFlight = 0;

    /// <summary>
    /// Takes a document now, dropping whatever was in flight.
    /// </summary>
    /// <remarks>
    /// For the case where there is nothing to wait for: a pane that is attached and off screen
    /// has no engine running behind it, so its loads are never answered for and a new one cannot
    /// be racing anything. Waiting there would be worse than pointless — a load left queued
    /// behind one that will never finish is a message that arrives a watchdog late, and the
    /// reading pane is switched off in an ordinary setup for as long as the reader likes.
    /// </remarks>
    public string Now(string html)
    {
        Asked++;
        InFlight = 0;
        return Take(html);
    }

    /// <summary>
    /// The document that was waiting, once there is nothing in flight — or null, when nothing was
    /// waiting or the engine is busy again.
    /// </summary>
    public string? Next()
        => InFlight == 0 && _waiting is { } html ? Take(html) : null;

    /// <summary>
    /// Whether the load that was in flight when something was queued behind it is still in
    /// flight, with that document still waiting: an engine that never answered.
    /// </summary>
    /// <remarks>
    /// The wait has to be a wait rather than a hang. Every navigation is answered for by the
    /// engine's completion event, and the one time it is not, the pane would hold a document
    /// forever — so the caller arms a watchdog when it queues, and this is what the watchdog
    /// asks before letting the next load past.
    /// </remarks>
    public bool StillWaitingOn(long generation)
        => InFlight == generation && generation != 0 && _waiting is not null;

    /// <summary>Nothing is in flight and nothing is waiting — the engine has gone.</summary>
    /// <remarks>
    /// <see cref="Started"/> moves on as well, so anything still running for the last load can
    /// tell that it is no longer the one on show and stop rather than scripting a view whose
    /// engine is being torn down.
    /// </remarks>
    public void Forget()
    {
        InFlight = 0;
        _waiting = null;
        Started++;
    }

    private string Take(string html)
    {
        _waiting = null;
        InFlight = ++Started;
        Ran++;
        return html;
    }
}
