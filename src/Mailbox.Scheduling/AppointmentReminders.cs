using Mailbox.Store.Pim;

namespace Mailbox.Scheduling;

/// <summary>An appointment whose reminder time has come.</summary>
/// <param name="ItemId">The row it is on, so dismissing it can be recorded.</param>
/// <param name="Occurrence">Which occurrence — a series has one row and many reminders.</param>
public sealed record DueAppointment(long ItemId, string Summary, string Location, Occurrence Occurrence)
{
    public DateTimeOffset StartsUtc => Occurrence.StartUtc;
}

/// <summary>
/// Which appointments are due to be reminded about, now.
/// </summary>
/// <remarks>
/// A series is one row with many occurrences, so "is this reminder due" cannot be a column
/// comparison: the reminder is due <em>per occurrence</em>, and dismissing this week's must not
/// silence next week's. What is stored is the start of the occurrence last dismissed, and an
/// occurrence later than that is a reminder that has not been answered yet.
/// </remarks>
public static class AppointmentReminders
{
    /// <summary>
    /// How far ahead an occurrence is looked for. A reminder is at most a day before its
    /// appointment in the reference's own list, so a two-day window covers every one of them
    /// without expanding a decade of a series to find out.
    /// </summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(2);

    /// <summary>Everything whose reminder has come and not been dealt with, soonest first.</summary>
    public static IReadOnlyList<DueAppointment> Due(PimRepository repository, DateTimeOffset nowUtc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var due = new List<DueAppointment>();

        foreach (var item in repository.ItemsWithReminders())
        {
            if (item.ReminderMinutes is not { } minutes) continue;

            var master = PimEventCodec.FromItem(item);
            var overrides = item.Rrule is { Length: > 0 }
                ? repository.ItemsByUid(item.CollectionId, item.Uid).Where(i => i.IsOverride).Select(PimEventCodec.FromItem).ToList()
                : [];

            var (dismissed, snoozed) = repository.ReminderState(item.Id);

            // Look from a little before now — an appointment already under way still shows in the
            // reference's list — to the horizon.
            var occurrences = Recurrence.Expand(
                new[] { master }.Concat(overrides),
                nowUtc - Horizon,
                nowUtc + Horizon,
                zone);

            foreach (var occurrence in occurrences.OrderBy(o => o.StartUtc))
            {
                if (dismissed is { } last && occurrence.StartUtc <= last) continue;

                var at = snoozed ?? occurrence.StartUtc.AddMinutes(-minutes);
                if (at > nowUtc) continue;

                // The appointment is over and was never answered: the reference stops showing it.
                if (occurrence.EndUtc <= nowUtc) continue;

                due.Add(new DueAppointment(item.Id, master.Summary, master.Location, occurrence));
                break;
            }
        }

        return [.. due.OrderBy(d => d.StartsUtc)];
    }

    /// <summary>Marks this occurrence answered, so the next one in a series still comes round.</summary>
    public static void Dismiss(PimRepository repository, DueAppointment appointment)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(appointment);
        repository.SetReminderState(appointment.ItemId, appointment.StartsUtc, null);
    }

    /// <summary>Puts it off, which is a time to fire again rather than a dismissal.</summary>
    public static void Snooze(PimRepository repository, DueAppointment appointment, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(appointment);
        var (dismissed, _) = repository.ReminderState(appointment.ItemId);
        repository.SetReminderState(appointment.ItemId, dismissed, until);
    }
}
