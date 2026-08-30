using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// Notes and journal entries to and from RFC 5545 text, and to and from the store's row.
/// </summary>
public class JournalCodecTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    private static JournalEntry Note() => new JournalEntry
    {
        Uid = "note-1@mailbox",
        When = EventTime.At(new DateTime(2026, 8, 16, 9, 0, 0), "Europe/London"),
        Categories = ["Yellow Category"],
        LastModified = Stamp,
    }.WithBody("Milk, bread, and a new kettle\nThe old one has gone.");

    [Fact]
    public void ANoteTakesItsTitleFromItsFirstLine()
    {
        var note = Note();
        Assert.Equal("Milk, bread, and a new kettle", note.Summary);
        Assert.StartsWith("Milk, bread", note.Description, StringComparison.Ordinal);
        Assert.True(note.IsNote);
    }

    [Fact]
    public void AnEmptyNoteStillHasSomethingToShowInAList()
        => Assert.Equal(JournalEntry.Untitled, new JournalEntry { Uid = "u" }.WithBody(string.Empty).Titled());

    [Fact]
    public void ANoteSurvivesARoundTripThroughText()
    {
        var note = Note();
        var back = JournalCodec.Parse(JournalCodec.Serialize(note)).Single();

        Assert.Equal(note.Uid, back.Uid);
        Assert.Equal(note.Summary, back.Summary);
        Assert.Equal(note.Description, back.Description);
        Assert.Equal(note.When, back.When);
        Assert.Equal(["Yellow Category"], back.Categories);
        Assert.True(back.IsNote);
    }

    [Fact]
    public void ANoteSaysNothingAboutItsTypeBecauseANoteIsTheDefault()
    {
        // A note's text is what any other client would have written; only an entry that is
        // something else carries the extra property.
        Assert.DoesNotContain("X-MAILBOX-ENTRY-TYPE", JournalCodec.Serialize(Note()), StringComparison.Ordinal);
    }

    [Fact]
    public void AJournalEntryKeepsWhatItWasAndHowLongItTook()
    {
        var entry = Note() with
        {
            EntryType = "Phone call",
            Duration = TimeSpan.FromMinutes(45),
            Contacts = ["A. Person"],
        };

        var text = JournalCodec.Serialize(entry);
        Assert.Contains("X-MAILBOX-ENTRY-TYPE:Phone call", text, StringComparison.Ordinal);

        var back = JournalCodec.Parse(text).Single();
        Assert.Equal("Phone call", back.EntryType);
        Assert.Equal(TimeSpan.FromMinutes(45), back.Duration);
        Assert.Equal(["A. Person"], back.Contacts);
        Assert.False(back.IsNote);
    }

    [Fact]
    public void ADurationAnotherClientWroteItsOwnWayIsDroppedRatherThanGuessedAt()
    {
        var text = """
            BEGIN:VJOURNAL
            UID:other@example.com
            DTSTAMP:20260816T090000Z
            SUMMARY:From somewhere else
            X-MAILBOX-ENTRY-DURATION:three quarters of an hour
            END:VJOURNAL
            """;

        Assert.Null(JournalCodec.Parse(text).Single().Duration);
    }

    [Theory]
    [InlineData(30, "30 minutes")]
    [InlineData(1, "1 minute")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    [InlineData(90, "1 hour 30 minutes")]
    [InlineData(1440, "1 day")]
    [InlineData(2880, "2 days")]
    public void HowLongItTookReadsAsWords(int minutes, string expected)
        => Assert.Equal(expected, JournalCodec.DurationText(TimeSpan.FromMinutes(minutes), System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void TheCompanyAndThePrivateMarkTravelBothWays()
    {
        var entry = Note() with
        {
            EntryType = "Phone call",
            Company = "Pipes Ltd",
            IsPrivate = true,
        };

        var text = JournalCodec.Serialize(entry);
        Assert.Contains("X-MAILBOX-COMPANY:Pipes Ltd", text, StringComparison.Ordinal);
        Assert.Contains("CLASS:PRIVATE", text, StringComparison.Ordinal);

        var back = JournalCodec.Parse(text).Single();
        Assert.Equal("Pipes Ltd", back.Company);
        Assert.True(back.IsPrivate);

        // And through the store's row: the Entry List groups off the columns, never the text.
        var item = PimJournalCodec.ToItem(entry, 7);
        Assert.Equal("Pipes Ltd", item.Company);
        Assert.True(item.IsPrivate);
        var fromColumns = PimJournalCodec.FromColumns(item);
        Assert.Equal("Pipes Ltd", fromColumns.Company);
        Assert.True(fromColumns.IsPrivate);
    }

    [Fact]
    public void ConfidentialFromAnotherClientReadsAsPrivate()
    {
        var text = """
            BEGIN:VJOURNAL
            UID:other@example.com
            DTSTAMP:20260816T090000Z
            SUMMARY:Kept back
            CLASS:CONFIDENTIAL
            END:VJOURNAL
            """;

        Assert.True(JournalCodec.Parse(text).Single().IsPrivate);
    }

    // ---- The store's row ---------------------------------------------------------------------

    [Fact]
    public void TheRowsColumnsAgreeWithTheEntryTheTextHolds()
    {
        var row = PimJournalCodec.ToItem(Note(), collectionId: 3);

        Assert.Equal(CollectionKind.Journal, row.Kind);
        Assert.Equal("Milk, bread, and a new kettle", row.Summary);
        Assert.Equal(JournalEntry.NoteType, row.Status);
        Assert.Equal("2026-08-16T09:00:00", row.StartsLocal);
        Assert.Equal("Yellow Category", row.Categories);
        Assert.Equal(Note(), PimJournalCodec.FromItem(row));
    }

    [Fact]
    public void AJournalEntrysLengthIsTheDistanceBetweenTheRowsTwoEnds()
    {
        var row = PimJournalCodec.ToItem(Note() with { EntryType = "Meeting", Duration = TimeSpan.FromHours(1) }, 3);

        Assert.Equal("2026-08-16T10:00:00", row.EndsLocal);
        Assert.Equal(TimeSpan.FromHours(1), PimJournalCodec.FromColumns(row).Duration);
    }

    [Fact]
    public void ARowWhoseTextIsDamagedStillDescribesItsNote()
    {
        var row = PimJournalCodec.ToItem(Note(), 3) with { RawPayload = "BEGIN:VJOURNAL\r\nnonsense" };
        var note = PimJournalCodec.FromItem(row);

        Assert.Equal("Milk, bread, and a new kettle", note.Summary);
        Assert.True(note.IsNote);
    }
}
