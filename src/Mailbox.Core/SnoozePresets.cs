namespace Mailbox.Core;

/// <summary>
/// The Snooze menu's presets, from a moment: later today is four hours on; the rest are
/// eight in the morning of the day named — tomorrow, this weekend (Saturday, or Sunday when it
/// already is Saturday), next week (Monday). The reference's own times, in the shape of the
/// follow-up flag menu.
/// </summary>
public static class SnoozePresets
{
    public static IReadOnlyList<(string Header, DateTimeOffset Until)> For(DateTimeOffset now)
    {
        var later = now.AddHours(4);
        var tomorrow = MorningOf(now.AddDays(1));

        var toSaturday = ((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7;
        if (toSaturday == 0) toSaturday = 1;
        var weekend = MorningOf(now.AddDays(toSaturday));

        var toMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (toMonday == 0) toMonday = 7;
        var nextWeek = MorningOf(now.AddDays(toMonday));

        return
        [
            ($"Later Today ({later:h:mm tt})", later),
            ($"Tomorrow ({tomorrow:ddd h:mm tt})", tomorrow),
            ($"This Weekend ({weekend:ddd h:mm tt})", weekend),
            ($"Next Week ({nextWeek:ddd h:mm tt})", nextWeek),
        ];
    }

    private static DateTimeOffset MorningOf(DateTimeOffset day)
        => new(day.Year, day.Month, day.Day, 8, 0, 0, day.Offset);
}
