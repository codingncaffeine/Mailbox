using System.Globalization;
using System.Text;

namespace Mailbox.Scheduling;

/// <summary>How often a series repeats — the four the reference's pattern editor offers.</summary>
public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>Which day of a month or year a monthly or yearly pattern falls on.</summary>
public enum MonthlyMode
{
    /// <summary>"Day 15 of every 1 month(s)".</summary>
    DayOfMonth,

    /// <summary>"The second Tuesday of every 1 month(s)".</summary>
    Weekday,
}

/// <summary>
/// A recurrence as the reference's dialog states it, which is not how RFC 5545 states it.
/// </summary>
/// <remarks>
/// The dialog is a pattern editor rather than an RRULE builder: it asks for "the second
/// Tuesday of every month" and for "end after 10 occurrences", and the grammar is this record's
/// business. Keeping the two apart is what lets the dialog be the reference's without the rule
/// text being anything other than RFC 5545 — and what lets a rule another client wrote be read
/// back into the dialog when it fits one of these shapes, and left as text when it does not.
/// </remarks>
public sealed record RecurrencePattern
{
    public RecurrenceFrequency Frequency { get; init; } = RecurrenceFrequency.Weekly;

    /// <summary>Every <c>n</c> days, weeks, months or years.</summary>
    public int Interval { get; init; } = 1;

    /// <summary>For a weekly pattern: the days it falls on.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    /// <summary>
    /// Daily only: "every weekday", which is a weekly rule on the five days rather than a daily
    /// one — the reference offers it under Daily and RFC 5545 has no other way to say it.
    /// </summary>
    public bool EveryWeekday { get; init; }

    public MonthlyMode Monthly { get; init; } = MonthlyMode.DayOfMonth;

    /// <summary>Monthly and yearly by date: which day of the month.</summary>
    public int DayOfMonth { get; init; } = 1;

    /// <summary>Monthly and yearly by weekday: first, second, third, fourth, or last (-1).</summary>
    public int WeekOrdinal { get; init; } = 1;

    /// <summary>Monthly and yearly by weekday: which day.</summary>
    public DayOfWeek WeekDay { get; init; } = DayOfWeek.Monday;

    /// <summary>Yearly: which month, 1–12.</summary>
    public int Month { get; init; } = 1;

    /// <summary>Ends after this many occurrences, or null.</summary>
    public int? Count { get; init; }

    /// <summary>Ends on or before this date, or null for a series with no end.</summary>
    public DateOnly? Until { get; init; }

    /// <summary>The RRULE value, without the property name.</summary>
    /// <param name="zone">
    /// The zone "end by" is meant in — the appointment's own. UNTIL is a UTC instant, so the
    /// end of that day has to be the end of the day <em>there</em>: written as 23:59:59Z
    /// regardless, an evening series west of Greenwich lost its last occurrence while the
    /// dialog said the day was included. Null keeps the plain-UTC reading for a caller with no
    /// zone to say.
    /// </param>
    public string ToRrule(TimeZoneInfo? zone = null)
    {
        var text = new StringBuilder();

        if (EveryWeekday && Frequency == RecurrenceFrequency.Daily)
        {
            text.Append("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");
        }
        else
        {
            text.Append("FREQ=").Append(Frequency switch
            {
                RecurrenceFrequency.Daily => "DAILY",
                RecurrenceFrequency.Monthly => "MONTHLY",
                RecurrenceFrequency.Yearly => "YEARLY",
                _ => "WEEKLY",
            });

            if (Interval > 1) text.Append(";INTERVAL=").Append(Interval.ToString(CultureInfo.InvariantCulture));

            switch (Frequency)
            {
                case RecurrenceFrequency.Weekly when Days.Count > 0:
                    text.Append(";BYDAY=").Append(string.Join(",", Days.Select(Code)));
                    break;
                case RecurrenceFrequency.Monthly when Monthly == MonthlyMode.Weekday:
                    text.Append(";BYDAY=").Append(WeekOrdinal.ToString(CultureInfo.InvariantCulture)).Append(Code(WeekDay));
                    break;
                case RecurrenceFrequency.Monthly:
                    text.Append(";BYMONTHDAY=").Append(DayOfMonth.ToString(CultureInfo.InvariantCulture));
                    break;
                case RecurrenceFrequency.Yearly when Monthly == MonthlyMode.Weekday:
                    text.Append(";BYMONTH=").Append(Month.ToString(CultureInfo.InvariantCulture))
                        .Append(";BYDAY=").Append(WeekOrdinal.ToString(CultureInfo.InvariantCulture)).Append(Code(WeekDay));
                    break;
                case RecurrenceFrequency.Yearly:
                    text.Append(";BYMONTH=").Append(Month.ToString(CultureInfo.InvariantCulture))
                        .Append(";BYMONTHDAY=").Append(DayOfMonth.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    break;
            }
        }

        // COUNT and UNTIL are mutually exclusive in RFC 5545, and the dialog's own radios say so.
        if (Count is { } count && count > 0)
        {
            text.Append(";COUNT=").Append(count.ToString(CultureInfo.InvariantCulture));
        }
        else if (Until is { } until)
        {
            // Through the end of that day, so "end by the 5th" includes the 5th.
            if (zone is null)
            {
                text.Append(";UNTIL=").Append(until.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("T235959Z");
            }
            else
            {
                var endOfDay = until.ToDateTime(new TimeOnly(23, 59, 59));
                var instant = new DateTimeOffset(endOfDay, zone.GetUtcOffset(endOfDay)).ToUniversalTime();
                text.Append(";UNTIL=").Append(instant.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The pattern an RRULE states, or null when it says something this editor cannot show —
    /// in which case the rule is kept as it is rather than rewritten into something simpler.
    /// </summary>
    public static RecurrencePattern? Parse(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return null;

        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);

        if (!parts.TryGetValue("FREQ", out var freq)) return null;

        var frequency = freq.ToUpperInvariant() switch
        {
            "DAILY" => RecurrenceFrequency.Daily,
            "WEEKLY" => RecurrenceFrequency.Weekly,
            "MONTHLY" => RecurrenceFrequency.Monthly,
            "YEARLY" => RecurrenceFrequency.Yearly,
            _ => (RecurrenceFrequency?)null,
        };
        if (frequency is not { } chosen) return null;

        // Anything the editor has no control for means the rule is beyond it.
        foreach (var key in parts.Keys)
        {
            if (key is not ("FREQ" or "INTERVAL" or "BYDAY" or "BYMONTHDAY" or "BYMONTH" or "COUNT" or "UNTIL" or "WKST")) return null;
        }

        var interval = parts.TryGetValue("INTERVAL", out var everyText) && int.TryParse(everyText, CultureInfo.InvariantCulture, out var every)
            ? Math.Max(1, every)
            : 1;

        var pattern = new RecurrencePattern { Frequency = chosen, Interval = interval };

        if (parts.TryGetValue("COUNT", out var countText) && int.TryParse(countText, CultureInfo.InvariantCulture, out var count))
        {
            pattern = pattern with { Count = count };
        }
        else if (parts.TryGetValue("UNTIL", out var untilText) && untilText.Length >= 8
                 && DateOnly.TryParseExact(untilText[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
        {
            pattern = pattern with { Until = until };
        }

        var byDay = parts.TryGetValue("BYDAY", out var days)
            ? days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        switch (chosen)
        {
            case RecurrenceFrequency.Weekly when byDay.Length > 0:
            {
                var parsed = new List<DayOfWeek>();
                foreach (var day in byDay)
                {
                    if (day.Length != 2 || Day(day) is not { } value) return null;
                    parsed.Add(value);
                }

                // Monday to Friday every week is what the reference calls "every weekday".
                var weekdays = parsed.Count == 5 && interval == 1
                    && parsed.Order().SequenceEqual([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);

                return weekdays
                    ? pattern with { Frequency = RecurrenceFrequency.Daily, EveryWeekday = true, Days = parsed }
                    : pattern with { Days = parsed };
            }

            case RecurrenceFrequency.Weekly:
                return pattern;

            case RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly:
            {
                if (chosen == RecurrenceFrequency.Yearly)
                {
                    if (!parts.TryGetValue("BYMONTH", out var monthText) || !int.TryParse(monthText, CultureInfo.InvariantCulture, out var month)) return null;
                    pattern = pattern with { Month = Math.Clamp(month, 1, 12) };
                }

                if (byDay.Length == 1)
                {
                    var entry = byDay[0];
                    var code = entry[^2..];
                    if (Day(code) is not { } value) return null;
                    var ordinalText = entry[..^2];
                    if (!int.TryParse(ordinalText, CultureInfo.InvariantCulture, out var ordinal)) return null;
                    return pattern with { Monthly = MonthlyMode.Weekday, WeekOrdinal = ordinal, WeekDay = value };
                }

                if (byDay.Length > 1) return null;

                if (parts.TryGetValue("BYMONTHDAY", out var dayText) && int.TryParse(dayText, CultureInfo.InvariantCulture, out var dayOfMonth))
                {
                    return pattern with { Monthly = MonthlyMode.DayOfMonth, DayOfMonth = Math.Clamp(dayOfMonth, 1, 31) };
                }

                return pattern;
            }

            default:
                return byDay.Length == 0 ? pattern : null;
        }
    }

    /// <summary>A pattern that repeats an event the way it already falls, for a fresh series.</summary>
    public static RecurrencePattern Weekly(DateOnly on)
        => new() { Frequency = RecurrenceFrequency.Weekly, Days = [on.DayOfWeek] };

    private static string Code(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "SU",
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        _ => "SA",
    };

    private static DayOfWeek? Day(string code) => code.ToUpperInvariant() switch
    {
        "SU" => DayOfWeek.Sunday,
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        _ => null,
    };

    public bool Equals(RecurrencePattern? other)
        => other is not null
           && Frequency == other.Frequency && Interval == other.Interval
           && Days.SequenceEqual(other.Days) && EveryWeekday == other.EveryWeekday
           && Monthly == other.Monthly && DayOfMonth == other.DayOfMonth
           && WeekOrdinal == other.WeekOrdinal && WeekDay == other.WeekDay
           && Month == other.Month && Count == other.Count && Until == other.Until;

    public override int GetHashCode()
        => HashCode.Combine(Frequency, Interval, EveryWeekday, Monthly, DayOfMonth, WeekOrdinal, (int)WeekDay, Month);
}
