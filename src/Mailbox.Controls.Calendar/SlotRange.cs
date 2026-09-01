namespace Mailbox.Controls.Calendar;

/// <summary>
/// A stretch of empty time somebody asked for a new appointment over.
/// </summary>
/// <remarks>
/// One shape for both ways of asking. The keyboard extends the caret with <c>Shift</c> and the
/// pointer sweeps across the grid, and they are the same two ends either way — which is the point
/// of having a range at all rather than a start and a fixed half-hour. A single slot is a range
/// like any other; <see cref="Minutes"/> is what it is, not a special case.
/// <para>
/// One day. A range that crossed midnight would be two things to draw, two rows to hit-test and
/// an appointment nobody asked for on the far day; the far commoner intent — and the one both
/// drivers naturally express — is a stretch within a day.
/// </para>
/// </remarks>
/// <param name="Day">The day both ends are in.</param>
/// <param name="From">The first slot, inclusive.</param>
/// <param name="To">The end of the last slot, exclusive — so a single half-hour slot is 30 minutes.</param>
public readonly record struct SlotRange(DateOnly Day, TimeOnly From, TimeOnly To)
{
    /// <summary>
    /// How long the appointment would be.
    /// </summary>
    /// <remarks>
    /// Counted in minutes rather than taken from the subtraction, because a range running to the
    /// bottom of the day ends at <c>00:00</c> — midnight is the next day's zero, and
    /// <c>To - From</c> reads that as a negative length.
    /// </remarks>
    public int Minutes
    {
        get
        {
            var from = (From.Hour * 60) + From.Minute;
            var to = (To.Hour * 60) + To.Minute;
            return to <= from ? (24 * 60) - from : to - from;
        }
    }

    /// <summary>True when this is one slot of the given length, which is what a plain click means.</summary>
    public bool IsSingle(int slotMinutes) => Minutes <= slotMinutes;

    public DateTime Start => Day.ToDateTime(From);

    /// <summary>
    /// The end as a moment. A range running to the last slot of the day ends at midnight, which
    /// is the next day at 00:00 rather than 23:59 — an appointment that stops a minute short of
    /// the day's end is not what anybody swept for.
    /// </summary>
    public DateTime End => Start.AddMinutes(Minutes);

    /// <summary>The two ends in order, whichever way round they were given.</summary>
    public static SlotRange Between(DateOnly day, TimeOnly anchor, TimeOnly caret, int slotMinutes)
    {
        var first = anchor <= caret ? anchor : caret;
        var last = anchor <= caret ? caret : anchor;

        // The caret names a slot and the range covers it, so the far end runs to the end of the
        // slot the caret is on rather than to its start — a sweep down three rows is 90 minutes,
        // not 60.
        return new SlotRange(day, first, last.Add(TimeSpan.FromMinutes(slotMinutes)));
    }
}
