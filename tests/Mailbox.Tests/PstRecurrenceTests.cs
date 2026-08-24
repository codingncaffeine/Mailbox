using Mailbox.Pst;
using Mailbox.Pst.Messaging;

namespace Mailbox.Tests;

/// <summary>
/// The recurrence blob against hand-built patterns — [MS-OXOCAL]'s AppointmentRecurrencePattern
/// written byte by byte, so every branch of the RRULE translation is pinned to an exact string.
/// The real-file case, a weekly series with a deleted and two moved occurrences, is proven by
/// the corpus import test reading the moved times back out of the store.
/// </summary>
public class PstRecurrenceTests
{
    private static uint Minutes(DateTime moment) => (uint)(moment - new DateTime(1601, 1, 1)).TotalMinutes;

    private sealed class BlobBuilder
    {
        private readonly MemoryStream _bytes = new();

        public BlobBuilder Word(ushort value)
        {
            _bytes.Write(BitConverter.GetBytes(value));
            return this;
        }

        public BlobBuilder Dword(uint value)
        {
            _bytes.Write(BitConverter.GetBytes(value));
            return this;
        }

        public BlobBuilder Bytes(byte[] raw)
        {
            _bytes.Write(raw);
            return this;
        }

        public byte[] Build() => _bytes.ToArray();
    }

    private static BlobBuilder Pattern(ushort frequency, ushort patternType) => new BlobBuilder()
        .Word(0x3004).Word(0x3004).Word(frequency).Word(patternType).Word(0x0001).Dword(0);

    [Fact]
    public void AWeeklyPatternWithAnEndDateSaysUntil()
    {
        var blob = Pattern(0x200B, 0x0001)
            .Dword(2)                                   // period: every 2 weeks
            .Dword(0)                                   // sliding
            .Dword(0x04)                                // Tuesday
            .Dword(0x2021)                              // end after date
            .Dword(0).Dword(0)                          // count, first DOW (Sunday)
            .Dword(0).Dword(0)                          // no deleted, no modified
            .Dword(Minutes(new DateTime(2026, 1, 6)))
            .Dword(Minutes(new DateTime(2026, 3, 31)))
            .Dword(0x3006).Dword(0x3009)
            .Dword(600).Dword(660)                      // 10:00 to 11:00
            .Word(0)                                    // no exceptions
            .Build();

        var recurrence = PstRecurrence.Parse(blob);

        Assert.NotNull(recurrence);
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU;WKST=SU;UNTIL=20260331T100000Z", recurrence.Rrule);
        Assert.Empty(recurrence.RemovedDates);
        Assert.Empty(recurrence.Overrides);
        Assert.Equal(600, recurrence.StartMinutes);
    }

    [Fact]
    public void ThirdFridayAndLastWeekendBothTranslate()
    {
        byte[] MonthNth(uint days, uint instance) => Pattern(0x200C, 0x0003)
            .Dword(1).Dword(0)
            .Dword(days).Dword(instance)
            .Dword(0x2022)                              // end after N occurrences
            .Dword(10).Dword(0)
            .Dword(0).Dword(0)
            .Dword(Minutes(new DateTime(2026, 1, 16)))
            .Dword(Minutes(new DateTime(2026, 10, 16)))
            .Dword(0x3006).Dword(0x3009).Dword(540).Dword(570).Word(0)
            .Build();

        var thirdFriday = PstRecurrence.Parse(MonthNth(0x20, 3));
        Assert.Equal("FREQ=MONTHLY;BYDAY=3FR;COUNT=10", thirdFriday!.Rrule);

        // A weekend "day" is two weekdays at once, which BYDAY alone cannot rank — BYSETPOS
        // carries the ordinal, and 5 means last.
        var lastWeekend = PstRecurrence.Parse(MonthNth(0x41, 5));
        Assert.Equal("FREQ=MONTHLY;BYDAY=SU,SA;BYSETPOS=-1;COUNT=10", lastWeekend!.Rrule);
    }

    [Fact]
    public void ABareTaskPatternParsesWithoutTheWrapper()
    {
        var blob = Pattern(0x200A, 0x0000)
            .Dword(3 * 1440)                            // every three days, stored in minutes
            .Dword(0)
            .Dword(0x2023)                              // never ends
            .Dword(10).Dword(0)
            .Dword(0).Dword(0)
            .Dword(Minutes(new DateTime(2026, 2, 2)))
            .Dword(0x5AE980DF)
            .Build();

        var recurrence = PstRecurrence.ParseBare(blob);

        Assert.Equal("FREQ=DAILY;INTERVAL=3", recurrence!.Rrule);
        Assert.Empty(recurrence.Overrides);
    }

    [Fact]
    public void AMovedOccurrenceWithASubjectStillLeavesTheOnesAfterItAligned()
    {
        // Two exceptions, the first carrying an inline subject — a mis-skip of its optional
        // fields would shear the second exception into garbage.
        var subject = "Moved"u8.ToArray();
        var blob = Pattern(0x200A, 0x0000)
            .Dword(1440).Dword(0)
            .Dword(0x2023).Dword(10).Dword(0)
            .Dword(3)                                    // three deleted: one outright, two moved
            .Dword(Minutes(new DateTime(2026, 5, 4)))
            .Dword(Minutes(new DateTime(2026, 5, 5)))
            .Dword(Minutes(new DateTime(2026, 5, 6)))
            .Dword(2)                                    // two modified
            .Dword(Minutes(new DateTime(2026, 5, 5)))
            .Dword(Minutes(new DateTime(2026, 5, 6)))
            .Dword(Minutes(new DateTime(2026, 5, 1)))
            .Dword(Minutes(new DateTime(2026, 12, 31)))
            .Dword(0x3006).Dword(0x3009).Dword(480).Dword(510)
            .Word(2)
            // First exception: moved an hour later, subject and busy status overridden.
            .Dword(Minutes(new DateTime(2026, 5, 5, 9, 0, 0)))
            .Dword(Minutes(new DateTime(2026, 5, 5, 9, 30, 0)))
            .Dword(Minutes(new DateTime(2026, 5, 5, 8, 0, 0)))
            .Word(0x0001 | 0x0020)
            .Word((ushort)(subject.Length + 1)).Word((ushort)subject.Length).Bytes(subject)
            .Dword(2)                                    // busy status
            // Second exception: plain move to the afternoon.
            .Dword(Minutes(new DateTime(2026, 5, 6, 14, 0, 0)))
            .Dword(Minutes(new DateTime(2026, 5, 6, 14, 30, 0)))
            .Dword(Minutes(new DateTime(2026, 5, 6, 8, 0, 0)))
            .Word(0)
            .Build();

        var recurrence = PstRecurrence.Parse(blob);

        Assert.Equal(2, recurrence!.Overrides.Count);
        Assert.Equal(new DateTime(2026, 5, 5, 9, 0, 0), recurrence.Overrides[0].Start);
        Assert.Equal(new DateTime(2026, 5, 6, 14, 0, 0), recurrence.Overrides[1].Start);
        Assert.Equal(new DateTime(2026, 5, 6, 8, 0, 0), recurrence.Overrides[1].OriginalStart);

        var removed = Assert.Single(recurrence.RemovedDates);
        Assert.Equal(new DateOnly(2026, 5, 4), removed);
    }

    [Fact]
    public void RemovedDatesAreTheDeletedLessTheMoved()
    {
        var blob = Pattern(0x200A, 0x0000)
            .Dword(1440).Dword(0)
            .Dword(0x2023).Dword(10).Dword(0)
            .Dword(2)
            .Dword(Minutes(new DateTime(2026, 5, 4)))
            .Dword(Minutes(new DateTime(2026, 5, 5)))
            .Dword(1)
            .Dword(Minutes(new DateTime(2026, 5, 5)))
            .Dword(Minutes(new DateTime(2026, 5, 1)))
            .Dword(Minutes(new DateTime(2026, 12, 31)))
            .Dword(0x3006).Dword(0x3009).Dword(480).Dword(510)
            .Word(1)
            .Dword(Minutes(new DateTime(2026, 5, 5, 9, 0, 0)))
            .Dword(Minutes(new DateTime(2026, 5, 5, 9, 30, 0)))
            .Dword(Minutes(new DateTime(2026, 5, 5, 8, 0, 0)))
            .Word(0)                                     // no overridden fields
            .Build();

        var recurrence = PstRecurrence.Parse(blob);

        var removed = Assert.Single(recurrence!.RemovedDates);
        Assert.Equal(new DateOnly(2026, 5, 4), removed);

        var moved = Assert.Single(recurrence.Overrides);
        Assert.Equal(new DateTime(2026, 5, 5, 8, 0, 0), moved.OriginalStart);
        Assert.Equal(new DateTime(2026, 5, 5, 9, 0, 0), moved.Start);
    }
}
