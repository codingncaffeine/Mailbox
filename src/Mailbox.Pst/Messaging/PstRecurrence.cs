using System.Buffers.Binary;

namespace Mailbox.Pst.Messaging;

/// <summary>A modified occurrence inside a recurrence blob: where it moved to, and which occurrence it was.</summary>
public sealed record PstRecurrenceException(DateTime Start, DateTime End, DateTime OriginalStart);

/// <summary>
/// A recurring series as [MS-OXOCAL]'s AppointmentRecurrencePattern states one, translated as
/// far as an RRULE can carry it.
/// </summary>
/// <param name="Rrule">The pattern as an RRULE value — <c>FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,FR</c>.</param>
/// <param name="RemovedDates">Occurrences deleted outright — the deleted list less the modified one.</param>
/// <param name="Overrides">Occurrences that moved, with their new wall times.</param>
/// <param name="StartMinutes">Minutes after midnight each occurrence starts.</param>
/// <param name="EndMinutes">Minutes after midnight each occurrence ends — smaller than the start for one that crosses it.</param>
public sealed record PstRecurrence(
    string Rrule,
    IReadOnlyList<DateOnly> RemovedDates,
    IReadOnlyList<PstRecurrenceException> Overrides,
    int StartMinutes,
    int EndMinutes)
{
    private static readonly DateTime Epoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static DateTime FromMinutes(uint minutes) => Epoch.AddMinutes(minutes);

    /// <summary>
    /// Reads an appointment's blob — the pattern inside its wrapper of daily times and moved
    /// occurrences — or answers null for a shape an RRULE cannot say (the Hijri and lunar
    /// calendar patterns), so the caller can keep the master as a single appointment and say
    /// why, rather than inventing a different series.
    /// </summary>
    /// <exception cref="PstException">The blob is structurally broken, as against merely foreign.</exception>
    public static PstRecurrence? Parse(byte[] blob) => ParseCore(blob, withWrapper: true);

    /// <summary>
    /// Reads a task's blob, which is the bare pattern with no wrapper — a task recurs by date,
    /// so there are no daily time offsets and no moved occurrences to read.
    /// </summary>
    public static PstRecurrence? ParseBare(byte[] blob) => ParseCore(blob, withWrapper: false);

    private static PstRecurrence? ParseCore(byte[] blob, bool withWrapper)
    {
        if (blob.Length < 40)
            throw new PstException("A recurrence pattern must be at least 40 bytes and this one is not.");

        var span = blob.AsSpan();
        if (BinaryPrimitives.ReadUInt16LittleEndian(span) != 0x3004)
            throw new PstException("The recurrence pattern does not begin with its own version number: the value is damaged.");

        var frequency = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        var patternType = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        var calendarType = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        var period = BinaryPrimitives.ReadUInt32LittleEndian(span[14..]);

        // The Hijri and lunar shapes have no RRULE equivalent; a Gregorian file says 0, 1 or 2.
        if (patternType >= 0x000A || calendarType > 2) return null;

        var at = 22;
        uint monthDay = 0, weekdayBits = 0, instance = 0;
        switch (patternType)
        {
            case 0x0000:
                break;
            case 0x0001:
                weekdayBits = Read32(span, ref at);
                break;
            case 0x0002 or 0x0004:
                monthDay = Read32(span, ref at);
                break;
            case 0x0003:
                weekdayBits = Read32(span, ref at);
                instance = Read32(span, ref at);
                break;
            default:
                return null;
        }

        var endType = Read32(span, ref at);
        var occurrenceCount = Read32(span, ref at);
        var firstDow = Read32(span, ref at);

        var deletedCount = Read32(span, ref at);
        var deleted = ReadDates(span, ref at, deletedCount);
        var modifiedCount = Read32(span, ref at);
        var modified = ReadDates(span, ref at, modifiedCount);

        var startDate = Read32(span, ref at);
        var endDate = Read32(span, ref at);

        var startMinutes = 0;
        var endMinutes = 0;
        var exceptionCount = 0;
        if (withWrapper)
        {
            // The wrapper around the pattern: versions, the daily time offsets, and the moved
            // occurrences' own times.
            if (at + 18 > span.Length)
                throw new PstException("The recurrence pattern ends before its appointment wrapper.");

            at += 8; // ReaderVersion2, WriterVersion2
            startMinutes = (int)Read32(span, ref at);
            endMinutes = (int)Read32(span, ref at);
            exceptionCount = BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
            at += 2;
        }

        var overrides = new List<PstRecurrenceException>(exceptionCount);
        for (var i = 0; i < exceptionCount; i++)
        {
            if (at + 14 > span.Length)
                throw new PstException("The recurrence pattern claims more exceptions than it holds.");

            var start = FromMinutes(Read32(span, ref at));
            var end = FromMinutes(Read32(span, ref at));
            var original = FromMinutes(Read32(span, ref at));
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
            at += 2;

            // The optional fields are skipped by the flags that announce them; only the times
            // matter here, but a mis-skip would shear every exception after the first.
            if ((flags & 0x0001) != 0) SkipString(span, ref at); // subject
            if ((flags & 0x0002) != 0) at += 4;                  // meeting type
            if ((flags & 0x0004) != 0) at += 4;                  // reminder delta
            if ((flags & 0x0008) != 0) at += 4;                  // reminder set
            if ((flags & 0x0010) != 0) SkipString(span, ref at); // location
            if ((flags & 0x0020) != 0) at += 4;                  // busy status
            if ((flags & 0x0040) != 0) at += 4;                  // attachment
            if ((flags & 0x0080) != 0) at += 4;                  // subtype
            if ((flags & 0x0100) != 0) at += 4;                  // colour

            overrides.Add(new PstRecurrenceException(start, end, original));
        }

        // Deleted dates cover moved occurrences too; only the difference is really gone.
        var movedOriginals = overrides.Select(o => DateOnly.FromDateTime(o.OriginalStart)).ToHashSet();
        var removed = deleted
            .Select(d => DateOnly.FromDateTime(FromMinutes(d)))
            .Where(d => !movedOriginals.Contains(d))
            .ToList();

        var rrule = BuildRrule(frequency, patternType, period, weekdayBits, monthDay, instance,
            endType, occurrenceCount, firstDow, FromMinutes(startDate), FromMinutes(endDate), startMinutes);
        if (rrule is null) return null;

        return new PstRecurrence(rrule, removed, overrides, startMinutes, endMinutes);
    }

    private static uint Read32(ReadOnlySpan<byte> span, ref int at)
    {
        if (at + 4 > span.Length)
            throw new PstException("The recurrence pattern ends in the middle of a field.");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
        at += 4;
        return value;
    }

    private static uint[] ReadDates(ReadOnlySpan<byte> span, ref int at, uint count)
    {
        if (count > 4096 || at + count * 4 > span.Length)
            throw new PstException("The recurrence pattern claims more instance dates than it holds.");

        var dates = new uint[count];
        for (var i = 0; i < count; i++) dates[i] = Read32(span, ref at);
        return dates;
    }

    private static void SkipString(ReadOnlySpan<byte> span, ref int at)
    {
        if (at + 4 > span.Length)
            throw new PstException("The recurrence pattern ends in the middle of an exception's text.");
        int length = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 2)..]);
        at += 4 + length;
        if (at > span.Length)
            throw new PstException("The recurrence pattern ends in the middle of an exception's text.");
    }

    private static readonly string[] DayNames = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    private static string? ByDay(uint bits)
    {
        var days = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            if ((bits & (1u << i)) != 0) days.Add(DayNames[i]);
        }

        return days.Count == 0 ? null : string.Join(",", days);
    }

    private static string? BuildRrule(uint frequency, uint patternType, uint period,
        uint weekdayBits, uint monthDay, uint instance, uint endType, uint count, uint firstDow,
        DateTime firstDay, DateTime lastDay, int startMinutes)
    {
        var yearly = frequency == 0x200D;
        var parts = new List<string>();

        switch (patternType)
        {
            case 0x0000:
                parts.Add("FREQ=DAILY");
                if (period / 1440 > 1) parts.Add($"INTERVAL={period / 1440}");
                break;

            case 0x0001:
                // "Every weekday" travels as a daily frequency wearing the weekly shape with
                // an interval of one; a real weekly pattern stores its period in weeks.
                parts.Add("FREQ=WEEKLY");
                if (frequency != 0x200A && period > 1) parts.Add($"INTERVAL={period}");
                if (ByDay(weekdayBits) is { } weekly) parts.Add($"BYDAY={weekly}");
                if (firstDow < 7 && (frequency == 0x200B && period > 1)) parts.Add($"WKST={DayNames[firstDow]}");
                break;

            case 0x0002 or 0x0004:
                parts.Add(yearly ? "FREQ=YEARLY" : "FREQ=MONTHLY");
                if (!yearly && period > 1) parts.Add($"INTERVAL={period}");
                if (yearly && period > 12) parts.Add($"INTERVAL={period / 12}");
                if (yearly) parts.Add($"BYMONTH={firstDay.Month}");

                // Day 29 and up means "or the last day of a shorter month" in the source; the
                // rule's closest truth is the last day, which is exact for 31 and one to two
                // days early only in the months the source clamps as well.
                parts.Add(patternType == 0x0004 || monthDay > 28 ? "BYMONTHDAY=-1" : $"BYMONTHDAY={monthDay}");
                break;

            case 0x0003:
                parts.Add(yearly ? "FREQ=YEARLY" : "FREQ=MONTHLY");
                if (!yearly && period > 1) parts.Add($"INTERVAL={period}");
                if (yearly && period > 12) parts.Add($"INTERVAL={period / 12}");
                if (yearly) parts.Add($"BYMONTH={firstDay.Month}");
                if (ByDay(weekdayBits) is not { } nth) return null;
                var position = instance == 5 ? -1 : (int)instance;

                // One weekday takes its ordinal directly; a set of them wants BYSETPOS.
                if (!nth.Contains(','))
                {
                    parts.Add($"BYDAY={position}{nth}");
                }
                else
                {
                    parts.Add($"BYDAY={nth}");
                    parts.Add($"BYSETPOS={position}");
                }

                break;

            default:
                return null;
        }

        if (endType == 0x2022 && count > 0)
        {
            parts.Add($"COUNT={count}");
        }
        else if (endType == 0x2021)
        {
            // UNTIL is inclusive and instant-based: the last occurrence's date at the series'
            // own start time, stated as this reader states every imported time — in UTC.
            var until = lastDay.AddMinutes(startMinutes);
            parts.Add($"UNTIL={until:yyyyMMdd'T'HHmmss}Z");
        }

        return string.Join(";", parts);
    }
}
