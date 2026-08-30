using System.Globalization;
using System.Xml;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;

namespace Mailbox.Scheduling;

/// <summary>
/// Notes and journal entries to and from RFC 5545 text, on the same terms as the other two
/// codecs: the text is the truth, and this is the only place it is read or written.
/// </summary>
/// <remarks>
/// A VJOURNAL is the smallest of the three components — a summary, a description, a date and
/// categories — and the two things the reference keeps that it has no property for, the entry's
/// type and how long it took, travel as X- properties. RFC 5545 gives VJOURNAL no DURATION at
/// all, so writing one would be a statement the standard does not allow rather than an extension
/// of it.
/// </remarks>
public static class JournalCodec
{
    public const string ProductId = "-//Mailbox//Mailbox Notes//EN";

    private const string TypeProperty = "X-MAILBOX-ENTRY-TYPE";
    private const string DurationProperty = "X-MAILBOX-ENTRY-DURATION";
    private const string CompanyProperty = "X-MAILBOX-COMPANY";
    private const string ContactProperty = "CONTACT";
    private const string PrivateClass = "PRIVATE";

    /// <summary>One VJOURNAL block, as the store keeps a row.</summary>
    public static string Serialize(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ComponentSerializer().SerializeToString(ToIcal(entry)) ?? string.Empty;
    }

    /// <summary>A whole VCALENDAR of them, as a server is sent one.</summary>
    public static string SerializeCalendar(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var calendar = new IcalCalendar { ProductId = ProductId };
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            calendar.Journals.Add(ToIcal(entry));
            if (entry.When?.TzId is { } tz && !string.Equals(tz, "UTC", StringComparison.OrdinalIgnoreCase)) zones.Add(tz);
        }

        foreach (var tz in zones)
        {
            try { calendar.AddTimeZone(tz); }
            catch (Exception) { /* an unknown zone is still named on the DTSTART; the reader resolves it. */ }
        }

        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    /// <summary>The entries in RFC 5545 text: a VCALENDAR, or a bare VJOURNAL block.</summary>
    /// <exception cref="FormatException">The text is not iCalendar.</exception>
    public static IReadOnlyList<JournalEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("BEGIN:VJOURNAL", StringComparison.OrdinalIgnoreCase))
            trimmed = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:" + ProductId + "\r\n" + trimmed.TrimEnd() + "\r\nEND:VCALENDAR\r\n";

        try
        {
            var calendar = IcalCalendar.Load(trimmed);
            if (calendar is null) throw new FormatException("The text is not an iCalendar object.");
            return calendar.Journals.Select(FromIcal).OrderBy(e => e.Uid, StringComparer.Ordinal).ToList();
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new FormatException("The text is not an iCalendar object.", ex);
        }
    }

    internal static Journal ToIcal(JournalEntry entry)
    {
        var ical = new Journal
        {
            Uid = entry.Uid,
            Summary = entry.Summary,
            Sequence = entry.Sequence,
            LastModified = new CalDateTime(entry.LastModified.UtcDateTime, "UTC", true),
            DtStamp = new CalDateTime(entry.LastModified.UtcDateTime, "UTC", true),
        };

        if (entry.Description.Length > 0) ical.Description = entry.Description;
        if (entry.When is { } when) ical.DtStart = ICalendarCodec.ToCal(when);
        foreach (var category in entry.Categories) ical.Categories.Add(category);
        foreach (var contact in entry.Contacts) ical.Properties.Add(new CalendarProperty(ContactProperty, contact));

        // A note is the default, so only an entry that is something else says what it is — which
        // keeps a note's text to what any other client would have written.
        if (!entry.IsNote) ical.Properties.Add(new CalendarProperty(TypeProperty, entry.EntryType));
        if (entry.Duration is { } duration)
        {
            ical.Properties.Add(new CalendarProperty(DurationProperty, XmlConvert.ToString(duration)));
        }

        if (entry.Company.Length > 0) ical.Properties.Add(new CalendarProperty(CompanyProperty, entry.Company));
        if (entry.IsPrivate) ical.Class = PrivateClass;

        return ical;
    }

    internal static JournalEntry FromIcal(Journal ical)
    {
        TimeSpan? duration = null;
        if (ical.Properties.Get<string>(DurationProperty) is { Length: > 0 } text)
        {
            try
            {
                duration = XmlConvert.ToTimeSpan(text);
            }
            catch (FormatException)
            {
                // A duration another client wrote in its own way is dropped rather than guessed at.
            }
        }

        return new JournalEntry
        {
            Uid = ical.Uid ?? JournalEntry.NewUid(),
            Summary = ical.Summary ?? string.Empty,
            Description = ical.Description ?? string.Empty,
            When = ical.DtStart is { } start ? ICalendarCodec.FromCal(start) : null,
            Duration = duration,
            EntryType = ical.Properties.Get<string>(TypeProperty) is { Length: > 0 } kind ? kind : JournalEntry.NoteType,
            Categories = ical.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).ToList(),
            Contacts = ical.Properties
                .AllOf(ContactProperty)
                .Select(p => p.Value?.ToString() ?? string.Empty)
                .Where(v => v.Length > 0)
                .ToList(),
            Company = ical.Properties.Get<string>(CompanyProperty) ?? string.Empty,
            // CONFIDENTIAL reads as private here for the reason the event codec gives: both mean
            // "not for the reader of a shared collection".
            IsPrivate = ical.Class is { Length: > 0 } klass
                && (klass.Equals(PrivateClass, StringComparison.OrdinalIgnoreCase)
                    || klass.Equals("CONFIDENTIAL", StringComparison.OrdinalIgnoreCase)),
            Sequence = ical.Sequence,
            LastModified = ical.LastModified is { } lm ? new DateTimeOffset(lm.AsUtc, TimeSpan.Zero)
                : ical.DtStamp is { } ds ? new DateTimeOffset(ds.AsUtc, TimeSpan.Zero)
                : DateTimeOffset.UtcNow,
        };
    }

    /// <summary>How long an entry took, as a list writes it: "30 minutes", "2 hours", "1 day".</summary>
    public static string DurationText(TimeSpan duration, IFormatProvider? culture = null)
    {
        var format = culture ?? CultureInfo.CurrentCulture;
        if (duration < TimeSpan.FromHours(1)) return Plural((int)Math.Round(duration.TotalMinutes), "minute", format);
        if (duration >= TimeSpan.FromDays(1) && duration.TotalDays % 1 == 0) return Plural((int)duration.TotalDays, "day", format);
        if (duration.TotalHours % 1 == 0) return Plural((int)duration.TotalHours, "hour", format);
        return Plural((int)duration.TotalHours, "hour", format) + " " + Plural(duration.Minutes, "minute", format);
    }

    private static string Plural(int count, string noun, IFormatProvider culture)
        => count.ToString(culture) + " " + noun + (count == 1 ? string.Empty : "s");
}
