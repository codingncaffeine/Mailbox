using System.Globalization;
using Mailbox.Rendering;

namespace Mailbox.Tests;

/// <summary>
/// The calendar on paper: the four styles, and what each of them has to get right.
/// </summary>
/// <remarks>
/// Held at the markup, which is what reaches the engine and then the printer. There is no second
/// renderer for paper — the printed calendar goes through the same document the reading pane and
/// the printed message list do — so a page that is right here is a page that comes out right.
/// </remarks>
public class CalendarPrintTests
{
    private static readonly RenderStyle Paper =
        new("#ffffff", "#000000", "#0000ee", "#666666", "sans-serif", 13);

    private static readonly DateTimeOffset Printed = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly PrintedAppointment[] Week =
    [
        new(new DateOnly(2026, 8, 11), "09:00–09:15", "Standup", "Room 2", Minutes: 540),
        new(new DateOnly(2026, 8, 11), "11:00–12:00", "Interview", "Room 4", Minutes: 660, Detail: "Bring the sheet."),
        new(new DateOnly(2026, 8, 12), string.Empty, "Public holiday", AllDay: true),
        new(new DateOnly(2026, 8, 14), "17:00–17:30", "Retro", Minutes: 1020),
    ];

    private static string Render(CalendarPrintStyle kind, DateOnly from, DateOnly to)
        => CalendarPrint.Render(kind, from, to, Week, Paper, Printed, CultureInfo.GetCultureInfo("en-GB"));

    /// <summary>
    /// The day style is a block per day over whatever run was asked for, and a day with nothing
    /// on it says so — a page that silently omits Wednesday reads as a page that lost it.
    /// </summary>
    [Fact]
    public void TheDailyStyleGivesEveryDayItsOwnBlock()
    {
        var page = Render(CalendarPrintStyle.Daily, new DateOnly(2026, 8, 11), new DateOnly(2026, 8, 13));

        Assert.Contains("Tuesday, 11 August 2026", page, StringComparison.Ordinal);
        Assert.Contains("Wednesday, 12 August 2026", page, StringComparison.Ordinal);
        Assert.Contains("Thursday, 13 August 2026", page, StringComparison.Ordinal);
        Assert.Contains("Nothing.", page, StringComparison.Ordinal);

        Assert.Contains("09:00–09:15", page, StringComparison.Ordinal);
        Assert.Contains("Room 2", page, StringComparison.Ordinal);

        // An all-day item has no time to print, and says what it is instead.
        Assert.Contains("All day", page, StringComparison.Ordinal);

        // Not this day's business: Friday's appointment is outside the run.
        Assert.DoesNotContain("Retro", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWeeklyStyleIsAColumnADay()
    {
        var page = Render(CalendarPrintStyle.Weekly, new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 15));

        Assert.Contains("<table class=\"week\">", page, StringComparison.Ordinal);
        Assert.Equal(7, Count(page, "<th>"));
        Assert.Equal(7, Count(page, "<td>"));
        Assert.Contains("Standup", page, StringComparison.Ordinal);
        Assert.Contains("Retro", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run longer than a week is several week tables, not the first seven days with the rest
    /// dropped — a page that silently loses a fortnight is worse than one that refuses.
    /// </summary>
    [Fact]
    public void AWeeklyStyleOverALongRunIsSeveralWeeks()
    {
        var page = Render(CalendarPrintStyle.Weekly, new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 29));

        Assert.Equal(3, Count(page, "<table class=\"week\">"));
        Assert.Contains("Sat 29 Aug", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// A month is drawn as its grid, whole weeks, with the days either side of it greyed — a
    /// month grid with a ragged first row is not the grid anybody recognises.
    /// </summary>
    [Fact]
    public void TheMonthlyStyleIsWholeWeeksWithTheEdgesMarked()
    {
        var page = Render(CalendarPrintStyle.Monthly, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        Assert.Contains("<table class=\"month\">", page, StringComparison.Ordinal);

        // August 2026 starts on a Saturday and ends on a Monday, so the grid runs Sunday 26 July
        // to Saturday 5 September: six weeks, forty-two cells, eleven of them outside the month —
        // six of July and five of September.
        Assert.Equal(42, Count(page, "<td"));
        Assert.Equal(11, Count(page, "class=\"outside\""));
        Assert.Contains("11:00–12:00 Interview", page, StringComparison.Ordinal);

        // An all-day item is its own name on the day, with no time in front of it.
        Assert.Contains(">Public holiday<", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetailsStyleCarriesWhatWasWrittenInEachOne()
    {
        var page = Render(CalendarPrintStyle.Details, new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 15));

        Assert.Contains("Bring the sheet.", page, StringComparison.Ordinal);
        Assert.Contains("Room 4", page, StringComparison.Ordinal);

        // Days with nothing on them are left out of this style: it is a list of what there is.
        Assert.DoesNotContain("Sunday, 9 August 2026", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page's own name, which is also the preview window's title. A run inside one month is
    /// written as one: "9 – 15 August 2026", not the culture's short date against a long one.
    /// </summary>
    [Fact]
    public void TheTitleSaysTheRangeTheWayThatRangeDeserves()
    {
        var gb = CultureInfo.GetCultureInfo("en-GB");

        Assert.Equal("August 2026", CalendarPrint.Title(
            CalendarPrintStyle.Monthly, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), gb));

        Assert.Equal("Tuesday, 11 August 2026", CalendarPrint.Title(
            CalendarPrintStyle.Daily, new DateOnly(2026, 8, 11), new DateOnly(2026, 8, 11), gb));

        Assert.Equal("9 – 15 August 2026", CalendarPrint.Title(
            CalendarPrintStyle.Weekly, new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 15), gb));

        // "Sept", because that is what en-GB abbreviates September to; the culture writes the
        // month, this does not.
        Assert.Equal("30 Aug 2026 – 5 Sept 2026", CalendarPrint.Title(
            CalendarPrintStyle.Weekly, new DateOnly(2026, 8, 30), new DateOnly(2026, 9, 5), gb));
    }

    /// <summary>
    /// A subject with markup in it is a subject, not markup. The renderer's own scrubber is the
    /// second wall; this is the first, and it is the one that keeps the page a page.
    /// </summary>
    [Fact]
    public void ASubjectCannotBecomeMarkup()
    {
        var page = CalendarPrint.Render(
            CalendarPrintStyle.Daily,
            new DateOnly(2026, 8, 11),
            new DateOnly(2026, 8, 11),
            [new PrintedAppointment(new DateOnly(2026, 8, 11), "09:00–10:00", "<b>Not bold</b> & co")],
            Paper,
            Printed);

        Assert.Contains("&lt;b&gt;Not bold&lt;/b&gt; &amp; co", page, StringComparison.Ordinal);
    }

    /// <summary>A range given backwards is the same range: nobody gets a blank page for it.</summary>
    [Fact]
    public void ARangeGivenBackwardsPrintsTheSameDays()
    {
        var page = Render(CalendarPrintStyle.Daily, new DateOnly(2026, 8, 13), new DateOnly(2026, 8, 11));
        Assert.Contains("Tuesday, 11 August 2026", page, StringComparison.Ordinal);
        Assert.Contains("Thursday, 13 August 2026", page, StringComparison.Ordinal);
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
