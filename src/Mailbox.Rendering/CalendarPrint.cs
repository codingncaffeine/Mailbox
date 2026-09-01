using System.Globalization;
using System.Text;

namespace Mailbox.Rendering;

/// <summary>The reference's calendar print styles, as far as they mean anything on paper here.</summary>
public enum CalendarPrintStyle
{
    /// <summary>One day a page: the hours down the side and what is in them.</summary>
    Daily,

    /// <summary>A week across the page, a column a day.</summary>
    Weekly,

    /// <summary>A month as its grid, one cell a day.</summary>
    Monthly,

    /// <summary>Every appointment in the range, in time order, with everything each one says.</summary>
    Details,
}

/// <summary>One appointment as the printer needs it: when, what, and where.</summary>
/// <param name="Day">The day it belongs to on paper. A run of days is one of these per day.</param>
/// <param name="Time">"09:00–10:00", or empty for an all-day item.</param>
/// <param name="Minutes">Minutes past midnight it starts, for laying out the day and week styles.</param>
public sealed record PrintedAppointment(
    DateOnly Day,
    string Time,
    string Subject,
    string Location = "",
    bool AllDay = false,
    int Minutes = 0,
    string Detail = "");

/// <summary>
/// The calendar on paper.
/// </summary>
/// <remarks>
/// The one thing a calendar is asked to do that this application could not: <c>Ctrl+P</c> in the
/// Calendar module answered nothing, and File · Print offered the three <em>mail</em> styles
/// whatever module was open — so printing a week produced a page of somebody's Inbox.
/// <para>
/// It renders to the same document the reading pane and the printed list use, through the same
/// scrubber, so the paper comes off one stylesheet and one engine. There is no second renderer to
/// disagree with the first.
/// </para>
/// <para>
/// <b>What is here and what is not.</b> Daily, Weekly, Monthly and Calendar Details are the four
/// the reference's own list starts with and the four that mean something on a printer. Its
/// Tri-fold and Memo styles are not: Tri-fold is a Windows-era folding-paper layout and Memo
/// prints one item, which is what the appointment window's own print does.
/// </para>
/// </remarks>
public static class CalendarPrint
{
    public static string Render(
        CalendarPrintStyle kind,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<PrintedAppointment> items,
        RenderStyle style,
        DateTimeOffset printedAt,
        IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(style);

        var format = culture ?? CultureInfo.CurrentCulture;
        if (to < from) (from, to) = (to, from);

        var body = new StringBuilder(Stylesheet);
        body.Append(CultureInfo.InvariantCulture, $"<h1 class=\"range\">{Escape(Title(kind, from, to, format))}</h1>");
        body.Append(CultureInfo.InvariantCulture,
            $"<p class=\"printed\">{Escape(printedAt.ToLocalTime().ToString("dddd, d MMMM yyyy HH:mm", format))}</p>");

        switch (kind)
        {
            case CalendarPrintStyle.Monthly: Month(body, from, to, items, format); break;
            case CalendarPrintStyle.Weekly: Week(body, from, to, items, format); break;
            case CalendarPrintStyle.Details: Details(body, from, to, items, format); break;
            default: Days(body, from, to, items, format); break;
        }

        return MessageRenderer.RenderHtml(body.ToString(), options: new RenderOptions { Style = style }).Html;
    }

    /// <summary>What the page is called, which is the range in the words that range deserves.</summary>
    public static string Title(CalendarPrintStyle kind, DateOnly from, DateOnly to, IFormatProvider? culture = null)
    {
        var format = culture ?? CultureInfo.CurrentCulture;
        if (kind == CalendarPrintStyle.Monthly) return from.ToString("MMMM yyyy", format);
        if (from == to) return from.ToString("dddd, d MMMM yyyy", format);
        // "%d" and not "d": a lone d is the culture's whole short-date pattern, so a run inside
        // one month read "8/9/2026 – 15 August 2026" instead of "9 – 15 August 2026".
        return from.Year == to.Year && from.Month == to.Month
            ? $"{from.ToString("%d", format)} – {to.ToString("d MMMM yyyy", format)}"
            : $"{from.ToString("d MMM yyyy", format)} – {to.ToString("d MMM yyyy", format)}";
    }

    // ---- The styles ---------------------------------------------------------------------------

    /// <summary>
    /// A day a block: its all-day band, then its appointments in time order. Used for the Daily
    /// style over however many days were asked for, which is what makes "print this day" and
    /// "print these three days" the same thing.
    /// </summary>
    private static void Days(
        StringBuilder body, DateOnly from, DateOnly to, IReadOnlyList<PrintedAppointment> items, IFormatProvider format)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            body.Append(CultureInfo.InvariantCulture,
                $"<h2 class=\"day\">{Escape(day.ToString("dddd, d MMMM yyyy", format))}</h2>");

            var mine = OnDay(items, day);
            if (mine.Count == 0)
            {
                body.Append("<p class=\"empty\">Nothing.</p>");
                continue;
            }

            body.Append("<table class=\"agenda\"><tbody>");
            foreach (var item in mine)
            {
                body.Append(CultureInfo.InvariantCulture,
                    $"<tr><td class=\"when\">{Escape(item.AllDay ? "All day" : item.Time)}</td>"
                    + $"<td>{Escape(item.Subject)}{Where(item)}</td></tr>");
            }

            body.Append("</tbody></table>");
        }
    }

    /// <summary>
    /// A week across the page: a column a day, its appointments stacked in it.
    /// </summary>
    /// <remarks>
    /// A run longer than a week is several of these rather than a table with thirty columns in
    /// it — and rather than the first seven days with the rest quietly dropped, which is what a
    /// cap would be. Seven columns is as many as a page holds and as many as a week has.
    /// </remarks>
    private static void Week(
        StringBuilder body, DateOnly from, DateOnly to, IReadOnlyList<PrintedAppointment> items, IFormatProvider format)
    {
        for (var start = from; start <= to; start = start.AddDays(7))
        {
            var days = new List<DateOnly>();
            for (var day = start; day <= to && days.Count < 7; day = day.AddDays(1)) days.Add(day);
            if (days.Count == 0) return;

            body.Append("<table class=\"week\"><thead><tr>");
            foreach (var day in days)
            {
                body.Append(CultureInfo.InvariantCulture, $"<th>{Escape(day.ToString("ddd d MMM", format))}</th>");
            }

            body.Append("</tr></thead><tbody><tr>");
            foreach (var day in days)
            {
                body.Append("<td>");
                foreach (var item in OnDay(items, day))
                {
                    body.Append(CultureInfo.InvariantCulture,
                        $"<div class=\"item\"><span class=\"when\">{Escape(item.AllDay ? "All day" : item.Time)}</span>"
                        + $" {Escape(item.Subject)}</div>");
                }

                body.Append("</td>");
            }

            body.Append("</tr></tbody></table>");
        }
    }

    /// <summary>
    /// A month as its grid. The run starts on the week the first day falls in and runs whole
    /// weeks, because a month grid with a ragged first row is not the grid anybody recognises.
    /// </summary>
    private static void Month(
        StringBuilder body, DateOnly from, DateOnly to, IReadOnlyList<PrintedAppointment> items, IFormatProvider format)
    {
        var first = from.AddDays(-(int)from.DayOfWeek);
        var last = to.AddDays(6 - (int)to.DayOfWeek);

        body.Append("<table class=\"month\"><thead><tr>");
        for (var i = 0; i < 7; i++)
        {
            body.Append(CultureInfo.InvariantCulture,
                $"<th>{Escape(first.AddDays(i).ToString("dddd", format))}</th>");
        }

        body.Append("</tr></thead><tbody>");

        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (day.DayOfWeek == DayOfWeek.Sunday) body.Append("<tr>");

            var outside = day < from || day > to;
            body.Append(CultureInfo.InvariantCulture,
                $"<td{(outside ? " class=\"outside\"" : string.Empty)}>"
                + $"<div class=\"number\">{Escape(day.Day.ToString(format))}</div>");

            foreach (var item in OnDay(items, day))
            {
                body.Append(CultureInfo.InvariantCulture,
                    $"<div class=\"item\">{Escape(item.AllDay ? item.Subject : $"{item.Time} {item.Subject}")}</div>");
            }

            body.Append("</td>");
            if (day.DayOfWeek == DayOfWeek.Saturday) body.Append("</tr>");
        }

        body.Append("</tbody></table>");
    }

    /// <summary>
    /// Everything, in time order, with everything each appointment says — the style somebody
    /// prints to take a week's detail into a room rather than to look at its shape.
    /// </summary>
    private static void Details(
        StringBuilder body, DateOnly from, DateOnly to, IReadOnlyList<PrintedAppointment> items, IFormatProvider format)
    {
        var any = false;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var mine = OnDay(items, day);
            if (mine.Count == 0) continue;

            any = true;
            body.Append(CultureInfo.InvariantCulture,
                $"<h2 class=\"day\">{Escape(day.ToString("dddd, d MMMM yyyy", format))}</h2>");

            foreach (var item in mine)
            {
                body.Append("<div class=\"detail\">");
                body.Append(CultureInfo.InvariantCulture, $"<div class=\"subject\">{Escape(item.Subject)}</div>");
                body.Append(CultureInfo.InvariantCulture,
                    $"<div class=\"when\">{Escape(item.AllDay ? "All day" : item.Time)}</div>");
                if (item.Location.Length > 0)
                {
                    body.Append(CultureInfo.InvariantCulture, $"<div class=\"where\">{Escape(item.Location)}</div>");
                }

                if (item.Detail.Length > 0)
                {
                    body.Append(CultureInfo.InvariantCulture, $"<p class=\"notes\">{Escape(item.Detail)}</p>");
                }

                body.Append("</div>");
            }
        }

        if (!any) body.Append("<p class=\"empty\">Nothing in these days.</p>");
    }

    // ---- Small pieces -------------------------------------------------------------------------

    /// <summary>
    /// A day's appointments in the order paper wants them: what has no time first, as the views
    /// put their bands above their grids, then the rest on the clock.
    /// </summary>
    private static List<PrintedAppointment> OnDay(IReadOnlyList<PrintedAppointment> items, DateOnly day)
        => [.. items
            .Where(i => i.Day == day)
            .OrderByDescending(i => i.AllDay)
            .ThenBy(i => i.Minutes)
            .ThenBy(i => i.Subject, StringComparer.CurrentCulture)];

    private static string Where(PrintedAppointment item)
        => item.Location.Length > 0 ? $"<span class=\"where\"> — {Escape(item.Location)}</span>" : string.Empty;

    /// <summary>
    /// The rules these styles need on top of the document's own, in the fragment rather than the
    /// wrapper so they go through the same scrubber every message's CSS does.
    /// </summary>
    private static string Stylesheet =>
        """
        <style>
        .range{font-size:16px;margin:0 0 2px;}
        .printed{margin:0 0 14px;font-size:11px;}
        h2.day{font-size:13px;margin:14px 0 4px;border-bottom:1px solid #808080;padding-bottom:2px;}
        p.empty{margin:2px 0 0;font-size:12px;font-style:italic;}
        table.agenda{width:100%;border-collapse:collapse;font-size:12px;}
        table.agenda td{padding:2px 6px;vertical-align:top;}
        td.when,span.when{white-space:nowrap;}
        table.week,table.month{width:100%;border-collapse:collapse;font-size:11px;table-layout:fixed;}
        table.week th,table.month th{text-align:left;border-bottom:1px solid #808080;padding:3px 4px;font-size:11px;}
        table.week td,table.month td{border:1px solid #c0c0c0;padding:3px 4px;vertical-align:top;height:96px;}
        table.month td.outside .number{opacity:0.45;}
        .number{font-weight:bold;margin-bottom:2px;}
        .item{margin:0 0 2px;word-wrap:break-word;}
        .detail{margin:0 0 10px;font-size:12px;}
        .detail .subject{font-weight:bold;}
        .detail .notes{margin:4px 0 0;white-space:pre-wrap;}
        .where{font-style:italic;}
        @media print{h2.day{page-break-after:avoid;}.detail{page-break-inside:avoid;}}
        </style>
        """;

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            switch (c)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                default: escaped.Append(c); break;
            }
        }

        return escaped.ToString();
    }
}
