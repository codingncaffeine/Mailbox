using Avalonia.Media;
using Mailbox.Controls.Calendar;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The calendar's model layer: the pattern editor's grammar, the three edit-scope operations, the
/// month view's lane packing, iMIP's state machine, and which reminder is due.
/// </summary>
public class CalendarPhaseTests
{
    private static readonly string Zone = TimeZoneInfo.Local.Id;

    private static CalendarEvent At(string uid, DateTime start, int minutes = 60, string? rrule = null) => new()
    {
        Uid = uid,
        Summary = "Weekly sync",
        Start = EventTime.At(start, Zone),
        End = EventTime.At(start.AddMinutes(minutes), Zone),
        Rrule = rrule,
    };

    // ---- Dropping the pattern ------------------------------------------------------------------

    /// <summary>
    /// Taking the RRULE off a series orphans its overrides, and something has to discard them.
    /// </summary>
    /// <remarks>
    /// The audit found a series made single leaving its exception behind: a row with a
    /// RECURRENCE-ID naming an occurrence nothing generates any more, still drawn on its own day,
    /// so the appointment somebody had just told to stop repeating turned up once more. The store
    /// could not catch it either — its delete cascade is guarded on the master still having a
    /// pattern, which is exactly what the edit takes away.
    /// </remarks>
    [Fact]
    public void RemovingAPatternIsRecognisedAsOrphaningItsOverrides()
    {
        var master = At("series", new DateTime(2026, 8, 2, 10, 0, 0), rrule: "FREQ=WEEKLY;BYDAY=SU");

        Assert.True(SeriesEditor.PatternDropped(master, master with { Rrule = null }));
        Assert.True(SeriesEditor.PatternDropped(master, master with { Rrule = "" }));
    }

    /// <summary>
    /// And every other edit leaves them alone — changing the pattern, or editing a series that
    /// never had one, must not take an exception with it.
    /// </summary>
    [Fact]
    public void ChangingOrKeepingAPatternDoesNotOrphanAnything()
    {
        var master = At("series", new DateTime(2026, 8, 2, 10, 0, 0), rrule: "FREQ=WEEKLY;BYDAY=SU");
        var single = At("single", new DateTime(2026, 8, 2, 10, 0, 0));

        Assert.False(SeriesEditor.PatternDropped(master, master with { Rrule = "FREQ=DAILY" }));
        Assert.False(SeriesEditor.PatternDropped(master, master));
        Assert.False(SeriesEditor.PatternDropped(single, single));
        Assert.False(SeriesEditor.PatternDropped(single, single with { Rrule = "FREQ=DAILY" }));
    }

    // ---- The recurrence pattern ---------------------------------------------------------------

    [Theory]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=DAILY;INTERVAL=3")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15")]
    [InlineData("FREQ=MONTHLY;INTERVAL=2;BYDAY=2TU")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR")]
    [InlineData("FREQ=YEARLY;BYMONTH=3;BYMONTHDAY=17")]
    [InlineData("FREQ=YEARLY;BYMONTH=11;BYDAY=4TH")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO;COUNT=10")]
    public void APatternSurvivesARoundTripThroughItsRule(string rule)
    {
        var pattern = RecurrencePattern.Parse(rule);
        Assert.NotNull(pattern);
        Assert.Equal(rule, pattern.ToRrule());
    }

    /// <summary>
    /// "Every weekday" is the reference's own wording under Daily and RFC 5545 has no other way
    /// to say it, so it has to survive being read back as that rather than as a weekly rule.
    /// </summary>
    [Fact]
    public void EveryWeekdayIsReadBackAsItselfRatherThanAsAWeeklyPattern()
    {
        var pattern = RecurrencePattern.Parse("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");

        Assert.NotNull(pattern);
        Assert.Equal(RecurrenceFrequency.Daily, pattern.Frequency);
        Assert.True(pattern.EveryWeekday);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", pattern.ToRrule());
    }

    [Fact]
    public void AnEndDateBecomesAnUntilThroughTheEndOfThatDay()
    {
        var pattern = new RecurrencePattern
        {
            Frequency = RecurrenceFrequency.Weekly,
            Days = [DayOfWeek.Monday],
            Until = new DateOnly(2026, 9, 5),
        };

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO;UNTIL=20260905T235959Z", pattern.ToRrule());
        Assert.Equal(new DateOnly(2026, 9, 5), RecurrencePattern.Parse(pattern.ToRrule())!.Until);
    }

    /// <summary>
    /// A rule the editor cannot state must be left exactly as it was found, or opening a series
    /// another client wrote would quietly simplify it.
    /// </summary>
    [Theory]
    [InlineData("FREQ=WEEKLY;BYSETPOS=2;BYDAY=MO,TU")]
    [InlineData("FREQ=DAILY;BYHOUR=9,17")]
    [InlineData("FREQ=SECONDLY")]
    public void ARuleTheEditorCannotStateIsNotParsed(string rule)
        => Assert.Null(RecurrencePattern.Parse(rule));

    // ---- The three edit-scope operations -------------------------------------------------------

    [Fact]
    public void EditingOneOccurrenceMakesAnOverrideAtItsOwnTime()
    {
        var master = At("series", new DateTime(2026, 8, 3, 10, 0, 0), rrule: "FREQ=WEEKLY;BYDAY=MO");
        var occurrences = Recurrence.Expand([master], Instant(new DateTime(2026, 8, 17)), Instant(new DateTime(2026, 8, 18)));
        var third = Assert.Single(occurrences);

        var edited = SeriesEditor.OverrideFor(master, third);

        Assert.True(edited.IsOverride);
        Assert.Null(edited.Rrule);
        Assert.Equal(new DateTime(2026, 8, 17, 10, 0, 0), edited.RecurrenceId!.Wall);
        Assert.Equal(master.Uid, edited.Uid);
    }

    [Fact]
    public void DeletingOneOccurrenceTakesItOutOfTheSeriesAndLeavesTheRest()
    {
        var master = At("series", new DateTime(2026, 8, 3, 10, 0, 0), rrule: "FREQ=WEEKLY;BYDAY=MO");
        var excluded = SeriesEditor.Exclude(master, EventTime.At(new DateTime(2026, 8, 17, 10, 0, 0), Zone));

        var occurrences = Recurrence.Expand([excluded], Instant(new DateTime(2026, 8, 1)), Instant(new DateTime(2026, 9, 1)));

        Assert.DoesNotContain(occurrences, o => o.Start.Wall.Date == new DateTime(2026, 8, 17));
        Assert.Contains(occurrences, o => o.Start.Wall.Date == new DateTime(2026, 8, 10));
        Assert.Contains(occurrences, o => o.Start.Wall.Date == new DateTime(2026, 8, 24));
    }

    [Fact]
    public void ThisAndAllFollowingEndsTheSeriesTheEveningBefore()
    {
        var master = At("series", new DateTime(2026, 8, 3, 10, 0, 0), rrule: "FREQ=WEEKLY;BYDAY=MO;COUNT=20");
        var ended = SeriesEditor.EndBefore(master, EventTime.At(new DateTime(2026, 8, 17, 10, 0, 0), Zone));

        Assert.DoesNotContain("COUNT", ended.Rrule);
        Assert.Contains("UNTIL=", ended.Rrule, StringComparison.Ordinal);

        var occurrences = Recurrence.Expand([ended], Instant(new DateTime(2026, 8, 1)), Instant(new DateTime(2026, 9, 1)));
        Assert.Equal(2, occurrences.Count);
        Assert.Equal(new DateTime(2026, 8, 10), occurrences[^1].Start.Wall.Date);
    }

    // ---- The month view's lanes -----------------------------------------------------------------

    /// <summary>
    /// The arrangement that makes a bar spanning three days one bar while the cells under it hold
    /// different numbers of appointments.
    /// </summary>
    [Fact]
    public void OverlappingBarsTakeSeparateLanesAndDisjointOnesShareOne()
    {
        (int First, int Last)[] spans = [(0, 3), (2, 5), (4, 6), (0, 1)];

        var bars = MonthLayout.Solve(spans, s => s, columns: 7);

        Assert.Equal(0, bars[0].Lane);
        Assert.Equal(1, bars[1].Lane);
        Assert.Equal(0, bars[2].Lane);   // 4..6 clears 0..3
        Assert.Equal(1, bars[3].Lane);   // 0..1 is free in lane 1, whose bar starts at column 2
    }

    [Fact]
    public void ABarRunningOffTheRowIsClippedAndSaysSo()
    {
        var bars = MonthLayout.Solve([(-2, 9)], s => s, columns: 7);

        var bar = Assert.Single(bars);
        Assert.Equal(0, bar.StartColumn);
        Assert.Equal(6, bar.EndColumn);
        Assert.True(bar.ContinuesBefore);
        Assert.True(bar.ContinuesAfter);
    }

    [Fact]
    public void AnItemWhollyOutsideTheRowIsDropped()
        => Assert.Empty(MonthLayout.Solve([(8, 10)], s => s, columns: 7));

    // ---- What a chip is painted with -------------------------------------------------------------

    /// <summary>
    /// A chip's body is its calendar's colour mixed toward the theme's chip ground, which is the
    /// one part of the drawing that can be checked without a running application — the rest is
    /// asserted by measuring a capture against the reference.
    /// </summary>
    [Fact]
    public void AChipsFillIsItsColourMixedTowardTheGround()
    {
        var colour = Color.FromRgb(0, 120, 212);

        Assert.Equal(Color.FromRgb(128, 188, 234), CalendarPalette.Mix(colour, Colors.White, 0.5));
        Assert.Equal(Colors.White, CalendarPalette.Mix(colour, Colors.White, 1));
        Assert.Equal(colour, CalendarPalette.Mix(colour, Colors.White, 0));

        // The measured Dark Gray pair: #0078D4 at 0.835 toward #F4F4F4 is the #CCE0EF the
        // reference draws a Tentative chip in.
        Assert.Equal(
            Color.FromRgb(0xCC, 0xE0, 0xEF),
            CalendarPalette.Mix(colour, Color.FromRgb(0xF4, 0xF4, 0xF4), 0.835));
    }

    // ---- The item type the views take --------------------------------------------------------------

    [Fact]
    public void AnAllDayEntrySpansTheDaysItCoversWithoutTheExclusiveEnd()
    {
        var entry = Entry(new CalendarEvent
        {
            Uid = "holiday",
            Summary = "Offsite",
            Start = EventTime.Date(new DateOnly(2026, 8, 27)),
            End = EventTime.Date(new DateOnly(2026, 8, 30)),
        });

        var (first, last) = entry.Days();
        Assert.Equal(new DateOnly(2026, 8, 27), first);
        Assert.Equal(new DateOnly(2026, 8, 29), last);
        Assert.True(entry.IsMultiDay);
    }

    [Fact]
    public void ATimedEntryEndingAtMidnightBelongsToTheDayItStartedOn()
    {
        var entry = Entry(At("late", new DateTime(2026, 8, 20, 22, 0, 0), minutes: 120));

        var (first, last) = entry.Days();
        Assert.Equal(new DateOnly(2026, 8, 20), first);
        Assert.Equal(new DateOnly(2026, 8, 20), last);
        Assert.False(entry.IsMultiDay);
    }

    [Fact]
    public void AMonthChipReadsTimeSubjectThenPlace()
    {
        var entry = Entry(new CalendarEvent
        {
            Uid = "dentist",
            Summary = "Dentist",
            Location = "Fern Street Practice",
            Start = EventTime.At(new DateTime(2026, 8, 20, 17, 0, 0), Zone),
            End = EventTime.At(new DateTime(2026, 8, 20, 17, 45, 0), Zone),
        });

        Assert.Equal(
            "5:00pm Dentist; Fern Street Practice",
            entry.MonthLabel(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- Reading the store ---------------------------------------------------------------------

    [Fact]
    public void TheSourceExpandsASeriesAndTagsEachOccurrenceWithItsCalendar()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(CollectionKind.Events, "Work", "#107C10");
        repository.AddItem(PimEventCodec.ToItem(
            At("series", new DateTime(2026, 8, 3, 10, 0, 0), rrule: "FREQ=WEEKLY;BYDAY=MO"),
            calendar.Id));

        var entries = new CalendarSource(repository).Between(
            Instant(new DateTime(2026, 8, 1)), Instant(new DateTime(2026, 9, 1)));

        Assert.Equal(5, entries.Count);
        Assert.All(entries, e => Assert.Equal(calendar.Id, e.CollectionId));
        Assert.All(entries, e => Assert.Equal(Color.FromRgb(0x10, 0x7C, 0x10), e.Colour));
        Assert.All(entries, e => Assert.NotEqual(0, e.ItemId));
    }

    /// <summary>
    /// A declined meeting is written CANCELLED and kept — a re-invitation has to find the row —
    /// but the reference takes it off the calendar, and a chip would count the reader busy for
    /// an hour they said no to. One filter, in the source, so the grids and the Scheduling
    /// Assistant's free/busy all read the same answer.
    /// </summary>
    [Fact]
    public void ADeclinedMeetingIsKeptInTheStoreAndDrawnNowhere()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(CollectionKind.Events, "Work", "#107C10");
        repository.AddItem(PimEventCodec.ToItem(
            At("declined", new DateTime(2026, 8, 3, 10, 0, 0)) with { Status = "CANCELLED" },
            calendar.Id));

        var entries = new CalendarSource(repository).Between(
            Instant(new DateTime(2026, 8, 1)), Instant(new DateTime(2026, 9, 1)));

        Assert.Empty(entries);
        Assert.Single(repository.ItemsBetween(
            Instant(new DateTime(2026, 8, 1)), Instant(new DateTime(2026, 9, 1)), [calendar.Id]));
    }

    [Fact]
    public void AHiddenCalendarIsNotDrawn()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(CollectionKind.Events, "Team", "#0078D4");
        repository.AddItem(PimEventCodec.ToItem(At("one", new DateTime(2026, 8, 3, 10, 0, 0)), calendar.Id));

        var source = new CalendarSource(repository);
        Assert.Single(source.Between(Instant(new DateTime(2026, 8, 1)), Instant(new DateTime(2026, 9, 1))));

        repository.SetCollectionVisible(calendar.Id, false);
        Assert.Empty(source.Between(Instant(new DateTime(2026, 8, 1)), Instant(new DateTime(2026, 9, 1))));
    }

    // ---- iMIP ------------------------------------------------------------------------------------

    private const string Invitation = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        METHOD:REQUEST
        BEGIN:VEVENT
        UID:invite@example.net
        DTSTAMP:20260801T000000Z
        DTSTART:20260820T090000Z
        DTEND:20260820T100000Z
        SUMMARY:Design review
        LOCATION:Room 2
        SEQUENCE:1
        ORGANIZER:mailto:priya@example.net
        ATTENDEE;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:you@example.com
        ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:sam@example.net
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void AnInvitationIsReadWithItsMethodAndItsAttendees()
    {
        var message = Imip.Read(Invitation);

        Assert.NotNull(message);
        Assert.Equal(ItipMethod.Request, message.Method);
        Assert.True(message.WantsReply);
        Assert.Equal("Design review", message.Event.Summary);
        Assert.Equal(2, message.Event.Attendees.Count);
        Assert.NotNull(message.AttendeeFor("you@example.com"));
        Assert.NotNull(message.AttendeeFor("mailto:you@example.com"));
        Assert.Null(message.AttendeeFor("nobody@example.net"));
    }

    /// <summary>
    /// RFC 5546 wants a reply to carry only the answering attendee. Sending the whole list tells
    /// every other invitee's client that this machine speaks for them, and some of them believe it.
    /// </summary>
    [Fact]
    public void AReplyCarriesOnlyTheAnsweringAttendee()
    {
        var invitation = Imip.Read(Invitation)!;

        var reply = Imip.Reply(invitation, "you@example.com", ItipResponse.Accepted);

        Assert.Contains("METHOD:REPLY", reply, StringComparison.Ordinal);

        // Read back rather than searched: RFC 5545 folds a long line, so the address itself is
        // split across two of them and a substring test would pass or fail on its length.
        var parsed = Imip.Read(reply)!;
        Assert.Equal(ItipMethod.Reply, parsed.Method);
        Assert.Equal("invite@example.net", parsed.Event.Uid);
        var answering = Assert.Single(parsed.Event.Attendees);
        Assert.Equal("you@example.com", answering.Address);
        Assert.Equal("ACCEPTED", answering.PartStat);
    }

    [Fact]
    public void AcceptingWritesABusyAppointmentAndBeingTentativeWritesATentativeOne()
    {
        var invitation = Imip.Read(Invitation)!;

        Assert.Equal(BusyStatus.Busy, Imip.Apply(invitation, null, ItipResponse.Accepted)!.Busy);
        Assert.Equal(BusyStatus.Tentative, Imip.Apply(invitation, null, ItipResponse.Tentative)!.Busy);
        Assert.Equal("CANCELLED", Imip.Apply(invitation, null, ItipResponse.Declined)!.Status);
    }

    [Fact]
    public void ACancellationMeansTakeItOffTheCalendar()
    {
        var cancellation = Imip.Read(Invitation.Replace("METHOD:REQUEST", "METHOD:CANCEL", StringComparison.Ordinal))!;
        Assert.Equal(ItipMethod.Cancel, cancellation.Method);
        Assert.Null(Imip.Apply(cancellation, cancellation.Event));
    }

    /// <summary>
    /// A re-delivered invitation older than what is held must not undo a later change — the
    /// sequence is what iTIP gives a client to tell the two apart.
    /// </summary>
    [Fact]
    public void AnInvitationOlderThanWhatIsHeldIsIgnored()
    {
        var invitation = Imip.Read(Invitation)!;
        var newer = invitation.Event with { Sequence = 5, Summary = "Design review (moved)" };

        var applied = Imip.Apply(invitation, newer);

        Assert.Equal("Design review (moved)", applied!.Summary);
        Assert.Equal(5, applied.Sequence);
    }

    [Fact]
    public void AReplyUpdatesOnlyTheAttendeeItNames()
    {
        var invitation = Imip.Read(Invitation)!;
        var reply = Imip.Read(Imip.Reply(invitation, "you@example.com", ItipResponse.Declined))!;

        var applied = Imip.Apply(reply, invitation.Event)!;

        Assert.Equal("DECLINED", applied.Attendees.First(a => a.Address.Contains("you@", StringComparison.Ordinal)).PartStat);
        Assert.Equal("NEEDS-ACTION", applied.Attendees.First(a => a.Address.Contains("sam@", StringComparison.Ordinal)).PartStat);
    }

    /// <summary>
    /// The bar says when the meeting is on the reader's clock, because the block it draws when
    /// they accept is on the reader's clock. Stating the organizer's instead is an invitation
    /// that disagrees with the appointment it makes.
    /// </summary>
    [Fact]
    public void AnInvitationIsDescribedOnTheReadersOwnClock()
    {
        var invitation = Imip.Read(Invitation)!;
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        var arizona = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        // 09:00Z on 20 August: two in the morning in Arizona, six in the evening in Tokyo.
        Assert.Contains("Thursday, 20 August 2026 02:00–03:00", Imip.Describe(invitation, culture, arizona), StringComparison.Ordinal);
        Assert.Contains("Thursday, 20 August 2026 18:00–19:00", Imip.Describe(invitation, culture, tokyo), StringComparison.Ordinal);

        // And with no clock named, the time exactly as it was written.
        Assert.Contains("Thursday, 20 August 2026 09:00–10:00", Imip.Describe(invitation, culture), StringComparison.Ordinal);
    }

    /// <summary>
    /// The reply that goes out and the appointment that stays behind have to say the same thing:
    /// the appointment is the copy the server gets, and it said NEEDS-ACTION for ever.
    /// </summary>
    [Fact]
    public void AnsweringAnInvitationRecordsTheAnswerOnTheAppointmentToo()
    {
        var invitation = Imip.Read(Invitation)!;

        var accepted = Imip.Apply(invitation, null, ItipResponse.Accepted, "you@example.com")!;
        Assert.Equal("ACCEPTED", accepted.Attendees.First(a => a.Address.Contains("you@", StringComparison.Ordinal)).PartStat);
        Assert.Equal("NEEDS-ACTION", accepted.Attendees.First(a => a.Address.Contains("sam@", StringComparison.Ordinal)).PartStat);

        var declined = Imip.Apply(invitation, null, ItipResponse.Declined, "mailto:you@example.com")!;
        Assert.Equal("DECLINED", declined.Attendees.First(a => a.Address.Contains("you@", StringComparison.Ordinal)).PartStat);

        // Nobody named, nothing rewritten — an arrival nobody answered must not claim one.
        var untouched = Imip.Apply(invitation, null, ItipResponse.Accepted)!;
        Assert.All(untouched.Attendees, a => Assert.Equal("NEEDS-ACTION", a.PartStat));
    }

    // ---- Reminders ----------------------------------------------------------------------------

    [Fact]
    public void AReminderIsDueOnceItsLeadTimeHasPassedAndNotBefore()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(CollectionKind.Events, "Work");

        var start = DateTime.Now.AddMinutes(10);
        repository.AddItem(PimEventCodec.ToItem(
            At("soon", start) with { ReminderMinutes = 15 },
            calendar.Id));
        repository.AddItem(PimEventCodec.ToItem(
            At("later", DateTime.Now.AddHours(6)) with { ReminderMinutes = 15 },
            calendar.Id));

        var due = AppointmentReminders.Due(repository, DateTimeOffset.UtcNow);

        var one = Assert.Single(due);
        Assert.Equal("Weekly sync", one.Summary);
    }

    /// <summary>
    /// Dismissing this week's reminder must not silence next week's, which is why what is stored
    /// is the occurrence's start rather than a flag.
    /// </summary>
    [Fact]
    public void DismissingOneOccurrencesReminderLeavesTheNextOneToCome()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(CollectionKind.Events, "Work");

        var today = DateTime.Today.AddHours(DateTime.Now.Hour).AddMinutes(10);
        var item = repository.AddItem(PimEventCodec.ToItem(
            At("series", today.AddDays(-7), rrule: "FREQ=DAILY") with { ReminderMinutes = 15 },
            calendar.Id));

        // dismissPast, so the backlog a week-old series has behind it is cleared and what is left
        // is the one occurrence this test is about.
        var first = Assert.Single(AppointmentReminders.Due(repository, DateTimeOffset.UtcNow, dismissPast: true));
        AppointmentReminders.Dismiss(repository, first);

        Assert.Empty(AppointmentReminders.Due(repository, DateTimeOffset.UtcNow, dismissPast: true));

        // Tomorrow's occurrence is a different reminder and still comes round.
        var tomorrow = AppointmentReminders.Due(repository, DateTimeOffset.UtcNow.AddDays(1), dismissPast: true);
        Assert.Single(tomorrow);
        Assert.Equal(item.Id, tomorrow[0].ItemId);
    }

    /// <summary>
    /// A row whose text will not parse still shows, from its columns. Ical.Net accepts a VEVENT
    /// with no DTSTART and then throws from the property getter, so the failure lands two lines
    /// past the load — and one damaged row used to take the whole calendar down with it.
    /// </summary>
    [Fact]
    public void ARowWhoseTextWillNotParseIsRebuiltFromItsColumns()
    {
        Assert.Throws<FormatException>(() => ICalendarCodec.Parse("BEGIN:VEVENT\r\nUID:broken\r\nEND:VEVENT"));

        var item = new PimItem
        {
            CollectionId = 1,
            Uid = "broken",
            Kind = CollectionKind.Events,
            RawPayload = "BEGIN:VEVENT\r\nUID:broken\r\nEND:VEVENT",
            Summary = "Board meeting",
            StartsLocal = "2026-08-20T09:00:00",
            EndsLocal = "2026-08-20T10:00:00",
            TzId = Zone,
        };

        var rebuilt = PimEventCodec.FromItem(item);

        Assert.Equal("Board meeting", rebuilt.Summary);
        Assert.Equal(new DateTime(2026, 8, 20, 9, 0, 0), rebuilt.Start.Wall);
    }

    [Fact]
    public void AnItemWithNoReminderIsNeverDue()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(CollectionKind.Events, "Work");
        repository.AddItem(PimEventCodec.ToItem(
            At("quiet", DateTime.Now.AddMinutes(5)) with { ReminderMinutes = null },
            calendar.Id));

        Assert.Empty(AppointmentReminders.Due(repository, DateTimeOffset.UtcNow));
    }


    /// <summary>
    /// A nine o'clock meeting in New York is not at nine o'clock on a calendar in London: the
    /// grid is one clock, and an appointment is placed by what that clock reads at its instant.
    /// </summary>
    [Fact]
    public void AnAppointmentWrittenInAnotherZoneIsDrawnAtTheViewsOwnHour()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(CollectionKind.Events, "Work");
        repository.AddItem(PimEventCodec.ToItem(
            new CalendarEvent
            {
                Uid = "ny@test",
                Summary = "Call New York",
                Start = EventTime.At(new DateTime(2026, 8, 18, 9, 0, 0), "America/New_York"),
                End = EventTime.At(new DateTime(2026, 8, 18, 10, 0, 0), "America/New_York"),
            },
            calendar.Id));

        var entry = Assert.Single(Shown(repository, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")));

        Assert.Equal(new DateTime(2026, 8, 18, 14, 0, 0), entry.StartWall);
        Assert.Equal(new DateTime(2026, 8, 18, 15, 0, 0), entry.EndWall);
        Assert.Equal(new DateOnly(2026, 8, 18), entry.Days().First);
    }

    /// <summary>
    /// An all-day item is a date rather than an instant, so it keeps the day it was written
    /// with — converting it would put a public holiday on the evening before.
    /// </summary>
    [Fact]
    public void AnAllDayItemKeepsItsDayInEveryZone()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(CollectionKind.Events, "Work");
        repository.AddItem(PimEventCodec.ToItem(
            new CalendarEvent
            {
                Uid = "holiday@test",
                Summary = "Public holiday",
                Start = EventTime.Date(new DateOnly(2026, 8, 20)),
                End = EventTime.Date(new DateOnly(2026, 8, 21)),
            },
            calendar.Id));

        foreach (var zone in new[] { "Europe/London", "America/New_York", "Asia/Tokyo" })
        {
            var entry = Assert.Single(Shown(repository, TimeZoneInfo.FindSystemTimeZoneById(zone)));
            Assert.Equal(new DateOnly(2026, 8, 20), entry.Days().First);
            Assert.Equal(new DateOnly(2026, 8, 20), entry.Days().Last);
        }
    }

    private static IReadOnlyList<Mailbox.Controls.Calendar.CalendarEntry> Shown(PimRepository repository, TimeZoneInfo zone)
        => new Mailbox.Controls.Calendar.CalendarSource(repository).Between(
            new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero),
            zone: zone);

    // ---- Helpers -----------------------------------------------------------------------------

    private static CalendarEntry Entry(CalendarEvent calendarEvent)
    {
        var occurrence = Recurrence.Expand(
            [calendarEvent],
            calendarEvent.Start.ToUtc().AddDays(-2),
            calendarEvent.End.ToUtc().AddDays(2)).First();
        return new CalendarEntry { Occurrence = occurrence, CollectionId = 1, ItemId = 1 };
    }

    private static DateTimeOffset Instant(DateTime wall)
        => new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(wall)).ToUniversalTime();

    /// <summary>
    /// The reference's "Automatically dismiss reminders for past calendar events", which is off
    /// out of the box: coming back from a week away to a list of what was missed is the default,
    /// and clearing it automatically is the choice.
    /// </summary>
    [Fact]
    public void AReminderForAMeetingThatHasFinishedShowsUnlessItIsSetToDismiss()
    {
        using var store = PimStore.Transient();
        var repository = new PimRepository(store);
        var calendar = repository.AddCollection(CollectionKind.Events, "Work");

        // Yesterday, and long over.
        var over = DateTime.Today.AddDays(-1).AddHours(9);
        repository.AddItem(PimEventCodec.ToItem(
            At("missed", over) with { ReminderMinutes = 15 }, calendar.Id));

        var now = DateTimeOffset.UtcNow;
        Assert.Single(AppointmentReminders.Due(repository, now));

        // Switched on it is answered rather than listed — and stays answered, which is the whole
        // point: a version that merely hid them would show the lot again the moment it went off.
        Assert.Empty(AppointmentReminders.Due(repository, now, dismissPast: true));
        Assert.Empty(AppointmentReminders.Due(repository, now));
    }
}
