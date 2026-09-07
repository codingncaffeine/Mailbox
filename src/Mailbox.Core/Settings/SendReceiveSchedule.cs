namespace Mailbox.Core.Settings;

/// <summary>
/// Which scheduled group is due, and when each was last started.
/// </summary>
/// <remarks>
/// The decision the shell's minute timer makes, kept apart from the timer so it can be proven
/// without one. Every scheduled group is due the moment the application opens — the reference
/// checks mail when it starts, and a client that opens onto stale mail and says nothing for
/// half an hour reads as one that is not checking at all — and then not again until its own
/// interval has passed since the run started for it.
/// <para>
/// One group per ask: two send/receives at once would open two sessions to the same server,
/// and the second would find nothing the first had not already taken. The caller runs the
/// first group due and asks again on the next tick.
/// </para>
/// <para>
/// Groups are known by name. The dialog edits a group by replacing it with a copy, and a copy
/// with a shorter interval counts from the run that already happened rather than from the
/// moment it was edited.
/// </para>
/// </remarks>
public sealed class SendReceiveSchedule
{
    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first group whose turn has come, or null when none has.</summary>
    public SendReceiveGroup? Due(IEnumerable<SendReceiveGroup> groups, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(groups);

        foreach (var group in groups)
        {
            if (!group.ScheduleEnabled) continue;
            if (!_started.TryGetValue(group.Name, out var last)) return group;
            if (now - last >= TimeSpan.FromMinutes(group.ScheduleMinutes)) return group;
        }

        return null;
    }

    /// <summary>Notes that a run was started for the group, so its next turn is an interval away.</summary>
    public void Started(SendReceiveGroup group, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(group);
        _started[group.Name] = now;
    }
}
