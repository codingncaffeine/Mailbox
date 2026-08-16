using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>Appointments: iCalendar round trips, the store row, and what a series puts on the calendar.</summary>
public class SchedulingTests
{
    private const string London = "Europe/London";
    private static readonly TimeZoneInfo LondonZone = TimeZoneInfo.FindSystemTimeZoneById(London);
    private static readonly DateTimeOffset Modified = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Weekly(string uid = "weekly@test", string rrule = "FREQ=WEEKLY;BYDAY=MO") => new()
    {
        Uid = uid,
        Summary = "Stand-up",
        Location = "Room 4",
        Description = "Ten minutes, standing.",
        Start = EventTime.At(new DateTime(2026, 3, 16, 9, 0, 0), London),
        End = EventTime.At(new DateTime(2026, 3, 16, 9, 30, 0), London),
        Rrule = rrule,
        Busy = BusyStatus.Tentative,
        ReminderMinutes = 15,
        Categories = ["Blue", "Team"],
        Attendees = [new EventAttendee("dana@example.com", "Dana Whitfield", "REQ-PARTICIPANT", "ACCEPTED", true)],
        Organizer = "you@example.com",
        Sequence = 3,
        Status = "CONFIRMED",
        LastModified = Modified,
    };

    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void AnEventRoundTripsThroughICalendarText()
    {
        var original = Weekly() with { ExceptionDates = [EventTime.At(new DateTime(2026, 3, 23, 9, 0, 0), London)] };
        var text = ICalendarCodec.Serialize(original);

        Assert.StartsWith("BEGIN:VEVENT", text);
        Assert.Contains("DTSTART;TZID=Europe/London:20260316T090000", text);
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=MO", text);
        Assert.Contains("EXDATE;TZID=Europe/London:20260323T090000", text);
        Assert.Contains("TRIGGER:-PT15M", text);
        Assert.Contains("X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE", text);
        Assert.Contains("TRANSP:OPAQUE", text);
        Assert.Contains("ORGANIZER:mailto:you@example.com", text);
        Assert.Contains("PARTSTAT=ACCEPTED", text);
        Assert.Contains("CATEGORIES:Blue,Team", text);
        Assert.DoesNotContain("BEGIN:VCALENDAR", text);

        var back = Assert.Single(ICalendarCodec.Parse(text));
        Assert.Equal(original, back);
    }

    [Fact]
    public void AnAllDayEventKeepsItsDatesAndAFloatingTimeStaysFloating()
    {
        var day = new CalendarEvent
        {
            Uid = "day@test",
            Summary = "Bank holiday",
            Start = EventTime.Date(new DateOnly(2026, 8, 31)),
            End = EventTime.Date(new DateOnly(2026, 9, 1)),
            Busy = BusyStatus.Free,
            LastModified = Modified,
        };
        var text = ICalendarCodec.Serialize(day);
        Assert.Contains("DTSTART;VALUE=DATE:20260831", text);
        Assert.Contains("DTEND;VALUE=DATE:20260901", text);
        Assert.Contains("TRANSP:TRANSPARENT", text);
        var back = Assert.Single(ICalendarCodec.Parse(text));
        Assert.True(back.AllDay);
        Assert.Equal(day, back);

        var floating = ICalendarCodec.Parse("BEGIN:VEVENT\r\nUID:f\r\nDTSTART:20260101T100000\r\nDURATION:PT1H\r\nSUMMARY:Anywhere\r\nEND:VEVENT\r\n").Single();
        Assert.Null(floating.Start.TzId);
        Assert.False(floating.AllDay);
        Assert.Equal(new DateTime(2026, 1, 1, 11, 0, 0), floating.End.Wall);
        Assert.Equal(TimeSpan.FromHours(1), floating.Duration);

        var utc = ICalendarCodec.Parse("BEGIN:VEVENT\r\nUID:u\r\nDTSTART:20260101T100000Z\r\nDTEND:20260101T103000Z\r\nEND:VEVENT\r\n").Single();
        Assert.Equal("UTC", utc.Start.TzId);
        Assert.Equal(Utc(2026, 1, 1, 10, 0), utc.Start.ToUtc());
    }

    [Fact]
    public void AnotherClientsBusyMarkingsAreRead()
    {
        static CalendarEvent Parse(string props) => ICalendarCodec.Parse($"BEGIN:VEVENT\r\nUID:b\r\nDTSTART:20260101T100000Z\r\nDTEND:20260101T110000Z\r\n{props}END:VEVENT\r\n").Single();
        Assert.Equal(BusyStatus.Free, Parse("TRANSP:TRANSPARENT\r\n").Busy);
        Assert.Equal(BusyStatus.Busy, Parse("").Busy);
        Assert.Equal(BusyStatus.Tentative, Parse("STATUS:TENTATIVE\r\n").Busy);
        Assert.Equal(BusyStatus.OutOfOffice, Parse("X-MICROSOFT-CDO-BUSYSTATUS:OOF\r\n").Busy);
        Assert.Equal(BusyStatus.Free, Parse("X-MICROSOFT-CDO-BUSYSTATUS:FREE\r\nTRANSP:OPAQUE\r\n").Busy);
    }

    [Fact]
    public void ACalendarCarriesTheZonesItsEventsName()
    {
        var text = ICalendarCodec.SerializeCalendar([Weekly()]);
        Assert.StartsWith("BEGIN:VCALENDAR", text);
        Assert.Contains("PRODID:" + ICalendarCodec.ProductId, text);
        Assert.Contains("BEGIN:VTIMEZONE", text);
        Assert.Contains("TZID:Europe/London", text);
        Assert.Contains("BEGIN:VEVENT", text);
        var back = ICalendarCodec.Parse(text);
        Assert.Equal(Weekly(), Assert.Single(back));
    }

    [Fact]
    public void GarbageIsAFormatError()
    {
        Assert.Throws<FormatException>(() => ICalendarCodec.Parse("not a calendar"));
    }

    [Fact]
    public void TheStoreRowIsDerivedFromTheEventAndReadsBackAsIt()
    {
        var e = Weekly();
        var item = PimEventCodec.ToItem(e, collectionId: 7);

        Assert.Equal(0, item.Id);
        Assert.Equal(7, item.CollectionId);
        Assert.Equal(CollectionKind.Events, item.Kind);
        Assert.Equal("weekly@test", item.Uid);
        Assert.StartsWith("BEGIN:VEVENT", item.RawPayload);
        Assert.Equal(("Stand-up", "Room 4", "Ten minutes, standing."), (item.Summary, item.Location, item.Description));
        Assert.Equal(Utc(2026, 3, 16, 9, 0), item.StartsUtc);
        Assert.Equal(Utc(2026, 3, 16, 9, 30), item.EndsUtc);
        Assert.Equal(("2026-03-16T09:00:00", "2026-03-16T09:30:00", London, false), (item.StartsLocal, item.EndsLocal, item.TzId, item.AllDay));
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", item.Rrule);
        Assert.False(item.IsOverride);
        Assert.Null(item.RecurrenceId);
        Assert.Equal(("tentative", 15, "Blue,Team", "you@example.com", 3, "CONFIRMED"), (item.Busy, item.ReminderMinutes, item.Categories, item.Organizer, item.Sequence, item.Status));
        Assert.Equal(PimSyncState.New, item.SyncState);
        Assert.Equal(Modified, item.LastModified);

        Assert.Equal(e, PimEventCodec.FromItem(item));

        // Editing a stored row keeps its identity and marks it modified.
        var stored = item with { Id = 42, SyncState = PimSyncState.Synced, DavHref = "/cal/w.ics", Etag = "\"1\"" };
        var edited = PimEventCodec.ToItem(e with { Summary = "Daily stand-up" }, 7, stored);
        Assert.Equal((42L, PimSyncState.Modified, "/cal/w.ics", "\"1\""), (edited.Id, edited.SyncState, edited.DavHref, edited.Etag));
        Assert.Equal("Daily stand-up", edited.Summary);

        // An override's RECURRENCE-ID goes into its column.
        var moved = e with { Rrule = null, RecurrenceId = EventTime.At(new DateTime(2026, 4, 6, 9, 0, 0), London), Start = EventTime.At(new DateTime(2026, 4, 7, 14, 0, 0), London), End = EventTime.At(new DateTime(2026, 4, 7, 15, 0, 0), London) };
        var overrideRow = PimEventCodec.ToItem(moved, 7);
        Assert.True(overrideRow.IsOverride);
        Assert.Equal("20260406T090000", overrideRow.RecurrenceId);
        Assert.Equal(moved, PimEventCodec.FromItem(overrideRow));
    }

    [Fact]
    public void ADamagedRowStillReadsFromItsColumns()
    {
        var item = PimEventCodec.ToItem(Weekly(), 1) with { RawPayload = "BEGIN:VEVENT\r\nthis is not right" };
        var back = PimEventCodec.FromItem(item);
        Assert.Equal("Stand-up", back.Summary);
        Assert.Equal(EventTime.At(new DateTime(2026, 3, 16, 9, 0, 0), London), back.Start);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", back.Rrule);
        Assert.Equal(BusyStatus.Tentative, back.Busy);
        Assert.Equal(["Blue", "Team"], back.Categories);

        var overrideRow = PimEventCodec.ToItem(Weekly() with { Rrule = null, RecurrenceId = EventTime.At(new DateTime(2026, 4, 6, 9, 0, 0), London) }, 1) with { RawPayload = "" };
        Assert.Equal(EventTime.At(new DateTime(2026, 4, 6, 9, 0, 0), London), PimEventCodec.FromItem(overrideRow).RecurrenceId);
    }

    [Fact]
    public void AWeeklySeriesKeepsItsWallTimeAcrossTheClocksGoingForward()
    {
        // 09:00 London: GMT until 29 March 2026, BST after.
        var occurrences = Recurrence.Expand([Weekly()], Utc(2026, 3, 1), Utc(2026, 4, 30), LondonZone);

        Assert.Equal(7, occurrences.Count);
        Assert.All(occurrences, o => Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(o.Start.Wall)));
        Assert.All(occurrences, o => Assert.Equal(new TimeOnly(9, 30), TimeOnly.FromDateTime(o.End.Wall)));
        Assert.All(occurrences, o => Assert.Equal(London, o.Start.TzId));
        Assert.All(occurrences, o => Assert.True(o.IsPartOfSeries));

        Assert.Equal(Utc(2026, 3, 16, 9, 0), occurrences[0].StartUtc);   // GMT: 09:00 is 09:00Z
        Assert.Equal(Utc(2026, 3, 23, 9, 0), occurrences[1].StartUtc);
        Assert.Equal(Utc(2026, 3, 30, 8, 0), occurrences[2].StartUtc);   // BST: 09:00 is 08:00Z
        Assert.Equal(Utc(2026, 4, 27, 8, 0), occurrences[6].StartUtc);
        Assert.All(occurrences, o => Assert.Equal(TimeSpan.FromMinutes(30), o.EndUtc - o.StartUtc));
        Assert.Equal(occurrences[2].Start, occurrences[2].RecurrenceId);
    }

    [Fact]
    public void ExceptionDatesAndOverridesShapeTheSeries()
    {
        var master = Weekly() with { ExceptionDates = [EventTime.At(new DateTime(2026, 3, 23, 9, 0, 0), London)] };
        var moved = new CalendarEvent
        {
            Uid = master.Uid,
            Summary = "Stand-up (moved)",
            Start = EventTime.At(new DateTime(2026, 4, 7, 14, 0, 0), London),
            End = EventTime.At(new DateTime(2026, 4, 7, 15, 0, 0), London),
            RecurrenceId = EventTime.At(new DateTime(2026, 4, 6, 9, 0, 0), London),
            LastModified = Modified,
        };

        var occurrences = Recurrence.Expand([master, moved], Utc(2026, 3, 1), Utc(2026, 4, 30), LondonZone);
        var starts = occurrences.Select(o => o.Start.Wall).ToList();

        Assert.DoesNotContain(new DateTime(2026, 3, 23, 9, 0, 0), starts);           // EXDATE
        Assert.DoesNotContain(new DateTime(2026, 4, 6, 9, 0, 0), starts);            // replaced by the override
        Assert.Contains(new DateTime(2026, 4, 7, 14, 0, 0), starts);                 // the override, at its own time
        Assert.Equal(6, occurrences.Count);

        var replacement = occurrences.Single(o => o.Start.Wall == new DateTime(2026, 4, 7, 14, 0, 0));
        Assert.Same(moved, replacement.Event);
        Assert.Equal("Stand-up (moved)", replacement.Summary);
        Assert.Equal(moved.RecurrenceId, replacement.RecurrenceId);
        Assert.Equal(TimeSpan.FromHours(1), replacement.EndUtc - replacement.StartUtc);
        Assert.Equal(starts.OrderBy(s => s), starts);

        // An override moved out of the window takes its occurrence with it, and one moved in shows.
        var farAway = moved with { Start = EventTime.At(new DateTime(2026, 6, 1, 9, 0, 0), London), End = EventTime.At(new DateTime(2026, 6, 1, 9, 30, 0), London) };
        Assert.Equal(5, Recurrence.Expand([master, farAway], Utc(2026, 3, 1), Utc(2026, 4, 30), LondonZone).Count);
        Assert.Single(Recurrence.Expand([master, farAway], Utc(2026, 5, 20), Utc(2026, 6, 2), LondonZone), o => o.Summary == "Stand-up (moved)");
    }

    [Fact]
    public void SingleEventsAndAllDayEventsAreShownWhenTheyTouchTheWindow()
    {
        var overnight = new CalendarEvent
        {
            Uid = "night@test",
            Summary = "Night shift",
            Start = EventTime.At(new DateTime(2026, 8, 19, 22, 0, 0), London),
            End = EventTime.At(new DateTime(2026, 8, 20, 6, 0, 0), London),
        };
        var holiday = new CalendarEvent
        {
            Uid = "hol@test",
            Summary = "Away",
            Start = EventTime.Date(new DateOnly(2026, 8, 18)),
            End = EventTime.Date(new DateOnly(2026, 8, 21)),
        };
        var elsewhere = new CalendarEvent
        {
            Uid = "else@test",
            Summary = "Not today",
            Start = EventTime.At(new DateTime(2026, 8, 21, 9, 0, 0), London),
            End = EventTime.At(new DateTime(2026, 8, 21, 10, 0, 0), London),
        };
        var dayStart = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.FromHours(1)); // London midnight
        var found = Recurrence.Expand([overnight, holiday, elsewhere], dayStart, dayStart.AddDays(1), LondonZone);

        Assert.Equal(["Away", "Night shift"], found.Select(o => o.Summary));
        Assert.All(found, o => Assert.False(o.IsPartOfSeries));
        Assert.All(found, o => Assert.Null(o.RecurrenceId));
        var away = found.Single(o => o.Summary == "Away");
        Assert.True(away.AllDay);
        Assert.Equal(TimeSpan.FromDays(3), away.EndUtc - away.StartUtc);
        Assert.Equal(dayStart.AddDays(-2), away.StartUtc);
    }

    [Fact]
    public void ACountedDailySeriesEndsAndTheNextOccurrenceIsFound()
    {
        var daily = Weekly("daily@test", "FREQ=DAILY;COUNT=3");
        var all = Recurrence.Expand([daily], Utc(2026, 1, 1), Utc(2027, 1, 1), LondonZone);
        Assert.Equal(3, all.Count);
        Assert.Equal([16, 17, 18], all.Select(o => o.Start.Wall.Day));

        var next = Recurrence.Next([daily], Utc(2026, 3, 17, 9, 15), LondonZone);
        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 18, 9, 0, 0), next.Start.Wall);
        Assert.Null(Recurrence.Next([daily], Utc(2026, 3, 19), LondonZone));

        Assert.Empty(Recurrence.Expand([daily], Utc(2026, 3, 19), Utc(2026, 3, 19), LondonZone));
    }

    [Fact]
    public void ASeriesIsDescribedInTheReferencesWords()
    {
        var gb = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        var us = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var e = Weekly();
        Assert.Equal("Occurs every Monday effective 16/03/2026 from 09:00 to 09:30.", RecurrenceText.Describe(e, gb));
        Assert.Equal("Occurs every Monday effective 3/16/2026 from 9:00 AM to 9:30 AM.", RecurrenceText.Describe(e, us));
        Assert.Null(RecurrenceText.Describe(e with { Rrule = null }, gb));

        string For(string rrule) => RecurrenceText.Describe(rrule, e.Start, e.End, gb);
        Assert.Equal("Occurs every Monday, Wednesday and Friday effective 16/03/2026 from 09:00 to 09:30.", For("FREQ=WEEKLY;BYDAY=MO,WE,FR"));
        Assert.Equal("Occurs every 2 weeks on Monday effective 16/03/2026 from 09:00 to 09:30.", For("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO"));
        Assert.Equal("Occurs every weekday effective 16/03/2026 from 09:00 to 09:30.", For("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"));
        Assert.Equal("Occurs every day effective 16/03/2026 for 3 occurrences from 09:00 to 09:30.", For("FREQ=DAILY;COUNT=3"));
        Assert.Equal("Occurs every 3 days effective 16/03/2026 until 27/04/2026 from 09:00 to 09:30.", For("FREQ=DAILY;INTERVAL=3;UNTIL=20260427T090000Z"));
        Assert.Equal("Occurs day 16 of every month effective 16/03/2026 from 09:00 to 09:30.", For("FREQ=MONTHLY"));
        Assert.Equal("Occurs the third Monday of every month effective 16/03/2026 from 09:00 to 09:30.", For("FREQ=MONTHLY;BYDAY=3MO"));
        Assert.Equal("Occurs the last Friday of every 2 months effective 16/03/2026 from 09:00 to 09:30.", For("FREQ=MONTHLY;INTERVAL=2;BYDAY=FR;BYSETPOS=-1"));
        Assert.Equal("Occurs every March 16 effective 16/03/2026 from 09:00 to 09:30.", For("FREQ=YEARLY"));
        Assert.Equal("Occurs the first Monday of September effective 16/03/2026 from 09:00 to 09:30.", For("FREQ=YEARLY;BYMONTH=9;BYDAY=1MO"));
        Assert.Equal("Occurs every 2 years on March 16 effective 16/03/2026 from 09:00 to 09:30.", For("FREQ=YEARLY;INTERVAL=2"));

        var allDay = e with { Start = EventTime.Date(new DateOnly(2026, 8, 31)), End = EventTime.Date(new DateOnly(2026, 9, 1)), Rrule = "FREQ=YEARLY" };
        Assert.Equal("Occurs every August 31 effective 31/08/2026.", RecurrenceText.Describe(allDay, gb));
    }

    [Fact]
    public void AnEventTimeOnTheMissingHourIsMovedWithTheClocks()
    {
        // 01:30 on 29 March 2026 does not exist in London; it becomes 02:30 BST.
        var missing = EventTime.At(new DateTime(2026, 3, 29, 1, 30, 0), London);
        Assert.Equal(Utc(2026, 3, 29, 1, 30), missing.ToUtc());
        Assert.Equal(TimeZoneInfo.Utc, new EventTime(DateTime.MinValue, "UTC").Zone());
        Assert.Equal(LondonZone, new EventTime(DateTime.MinValue, "Nowhere/Unknown").Zone(LondonZone));
        Assert.Equal("2026-03-29T01:30:00", missing.ToLocalText());
        Assert.Equal(missing, EventTime.FromLocalText("2026-03-29T01:30:00", London, allDay: false));
    }
}
