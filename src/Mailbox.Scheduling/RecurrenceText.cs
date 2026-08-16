using System.Globalization;
using System.Text;

namespace Mailbox.Scheduling;

/// <summary>
/// The sentence an appointment's header says about its series — "Occurs every Monday effective
/// 16/03/2026 from 09:00 to 09:30." — from its RRULE, in the reference's wording. Dates and times
/// follow the culture given, so the sentence reads as the rest of the screen does.
/// </summary>
public static class RecurrenceText
{
    /// <summary>The sentence for a series, or null for an event that does not repeat.</summary>
    public static string? Describe(CalendarEvent calendarEvent, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (string.IsNullOrWhiteSpace(calendarEvent.Rrule)) return null;
        return Describe(calendarEvent.Rrule, calendarEvent.Start, calendarEvent.End, culture);
    }

    /// <summary>The sentence for a rule that starts a series at <paramref name="start"/>.</summary>
    public static string Describe(string rrule, EventTime start, EventTime end, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(rrule);
        culture ??= CultureInfo.CurrentCulture;
        var parts = ParseRule(rrule);
        var interval = parts.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i > 1 ? i : 1;
        var freq = parts.TryGetValue("FREQ", out var f) ? f.ToUpperInvariant() : "WEEKLY";
        var byDay = parts.TryGetValue("BYDAY", out var bd) ? bd.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        var byMonthDay = parts.TryGetValue("BYMONTHDAY", out var bmd) ? bmd.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        var bySetPos = parts.TryGetValue("BYSETPOS", out var bsp) ? bsp : null;
        var byMonth = parts.TryGetValue("BYMONTH", out var bm) ? bm : null;

        var text = new StringBuilder("Occurs ");
        switch (freq)
        {
            case "DAILY":
                if (byDay.Length == 5 && byDay.All(IsWeekday)) text.Append("every weekday");
                else text.Append(interval == 1 ? "every day" : $"every {interval} days");
                break;

            case "WEEKLY":
                var days = byDay.Length > 0
                    ? byDay.Select(d => DayName(d[^2..], culture)).Where(n => n is not null).Cast<string>().ToList()
                    : [culture.DateTimeFormat.GetDayName(start.Wall.DayOfWeek)];
                if (byDay.Length == 5 && byDay.All(IsWeekday) && interval == 1) text.Append("every weekday");
                else if (interval == 1) text.Append("every ").Append(JoinNames(days));
                else text.Append($"every {interval} weeks on ").Append(JoinNames(days));
                break;

            case "MONTHLY":
                var monthEvery = interval == 1 ? "every month" : $"every {interval} months";
                if (byDay.Length == 1 && OrdinalOf(byDay[0], bySetPos) is (string ord, string dayCode) && DayName(dayCode, culture) is { } dayName)
                    text.Append($"the {ord} {dayName} of {monthEvery}");
                else
                    text.Append($"day {(byMonthDay.Length > 0 ? string.Join(", ", byMonthDay) : start.Wall.Day.ToString(culture))} of {monthEvery}");
                break;

            case "YEARLY":
                var monthNumber = byMonth is not null && int.TryParse(byMonth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mn) ? mn : start.Wall.Month;
                var monthName = culture.DateTimeFormat.GetMonthName(monthNumber);
                var yearsOn = interval == 1 ? string.Empty : $"every {interval} years on ";
                if (byDay.Length == 1 && OrdinalOf(byDay[0], bySetPos) is (string yOrd, string yDay) && DayName(yDay, culture) is { } yDayName)
                    text.Append($"{yearsOn}the {yOrd} {yDayName} of {monthName}");
                else
                    text.Append($"{(interval == 1 ? "every " : yearsOn)}{monthName} {(byMonthDay.Length > 0 ? byMonthDay[0] : start.Wall.Day.ToString(culture))}");
                break;

            default:
                text.Append("on a schedule");
                break;
        }

        text.Append(" effective ").Append(start.Wall.ToString("d", culture));
        if (parts.TryGetValue("UNTIL", out var until) && ParseUntil(until) is { } untilDate)
            text.Append(" until ").Append(untilDate.ToString("d", culture));
        else if (parts.TryGetValue("COUNT", out var count) && int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            text.Append(n == 1 ? " for 1 occurrence" : $" for {n} occurrences");

        if (!start.AllDay)
            text.Append(" from ").Append(start.Wall.ToString("t", culture)).Append(" to ").Append(end.Wall.ToString("t", culture));
        text.Append('.');
        // ICU's patterns put a narrow no-break space before AM/PM; the sentence uses the plain one.
        return text.ToString().Replace('\u202F', ' ').Replace('\u00A0', ' ');
    }

    private static Dictionary<string, string> ParseRule(string rrule)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = rrule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? rrule["RRULE:".Length..] : rrule;
        foreach (var piece in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = piece.IndexOf('=');
            if (eq > 0) parts[piece[..eq]] = piece[(eq + 1)..];
        }
        return parts;
    }

    private static bool IsWeekday(string code) => code[^2..].ToUpperInvariant() is "MO" or "TU" or "WE" or "TH" or "FR";

    private static string? DayName(string code, CultureInfo culture) => code.ToUpperInvariant() switch
    {
        "MO" => culture.DateTimeFormat.GetDayName(DayOfWeek.Monday),
        "TU" => culture.DateTimeFormat.GetDayName(DayOfWeek.Tuesday),
        "WE" => culture.DateTimeFormat.GetDayName(DayOfWeek.Wednesday),
        "TH" => culture.DateTimeFormat.GetDayName(DayOfWeek.Thursday),
        "FR" => culture.DateTimeFormat.GetDayName(DayOfWeek.Friday),
        "SA" => culture.DateTimeFormat.GetDayName(DayOfWeek.Saturday),
        "SU" => culture.DateTimeFormat.GetDayName(DayOfWeek.Sunday),
        _ => null,
    };

    /// <summary>"1MO" or "MO" with BYSETPOS=1 → ("first", "MO"); "-1FR" → ("last", "FR").</summary>
    private static (string Ordinal, string Day)? OrdinalOf(string byDay, string? bySetPos)
    {
        var code = byDay.Length >= 2 ? byDay[^2..] : byDay;
        var prefix = byDay.Length > 2 ? byDay[..^2] : bySetPos;
        if (!int.TryParse(prefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return null;
        var ordinal = n switch
        {
            1 => "first",
            2 => "second",
            3 => "third",
            4 => "fourth",
            -1 => "last",
            _ => null,
        };
        return ordinal is null ? null : (ordinal, code);
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        2 => names[0] + " and " + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static DateTime? ParseUntil(string value)
    {
        var text = value.TrimEnd('Z');
        return DateTime.TryParseExact(text, ["yyyyMMdd'T'HHmmss", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
