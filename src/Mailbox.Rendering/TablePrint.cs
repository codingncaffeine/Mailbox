using System.Globalization;
using System.Text;

namespace Mailbox.Rendering;

/// <summary>One line of a printed list: a message, or the header of a group of them.</summary>
public sealed record TableRow(string From, string Subject, string Received, string Size)
{
    /// <summary>A group header — the arrangement's own heading, printed as one.</summary>
    public static TableRow Group(string label) => new(label, string.Empty, string.Empty, string.Empty)
    {
        IsGroup = true,
    };

    public bool IsGroup { get; init; }

    public bool IsUnread { get; init; }
}

/// <summary>
/// The reference's Table print style: the message list as it stands, not a message.
/// </summary>
/// <remarks>
/// The counterpart to Memo, and the one people actually use — a folder printed as a list of
/// what is in it, with the arrangement's own groups kept, because a printed list in a different
/// order from the one on screen is a different list.
/// <para>
/// It renders to the same document the reading pane uses, so the paper it produces comes off
/// the same stylesheet and the same engine. There is no second renderer to disagree.
/// </para>
/// </remarks>
public static class TablePrint
{
    public static string Render(
        string title, IReadOnlyList<TableRow> rows, RenderStyle style, DateTimeOffset printedAt)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(style);

        var body = new StringBuilder(Stylesheet);

        body.Append(CultureInfo.InvariantCulture,
            $"<h1 class=\"folder\">{Escape(title)}</h1>");
        body.Append(CultureInfo.InvariantCulture,
            $"<p class=\"printed\">{Escape(printedAt.ToLocalTime().ToString("dddd, d MMMM yyyy HH:mm"))}</p>");

        body.Append("<table class=\"list\"><thead><tr>")
            .Append("<th>From</th><th>Subject</th><th>Received</th><th>Size</th>")
            .Append("</tr></thead><tbody>");

        foreach (var row in rows)
        {
            if (row.IsGroup)
            {
                body.Append(CultureInfo.InvariantCulture,
                    $"<tr class=\"group\"><td colspan=\"4\">{Escape(row.From)}</td></tr>");
                continue;
            }

            var unread = row.IsUnread ? " class=\"unread\"" : string.Empty;

            body.Append(CultureInfo.InvariantCulture,
                $"<tr{unread}><td>{Escape(row.From)}</td><td>{Escape(row.Subject)}</td>"
                + $"<td class=\"when\">{Escape(row.Received)}</td>"
                + $"<td class=\"size\">{Escape(row.Size)}</td></tr>");
        }

        body.Append("</tbody></table>");

        return MessageRenderer.RenderHtml(
            body.ToString(),
            options: new RenderOptions { Style = style }).Html;
    }

    /// <summary>
    /// The rules the list needs on top of the document's own.
    /// </summary>
    /// <remarks>
    /// Put in the fragment rather than in the wrapper, so they go through the same scrubber
    /// every message's CSS does. There is one path into the engine and a printed list does not
    /// get a private one.
    /// </remarks>
    private static string Stylesheet =>
        """
        <style>
        .folder{font-size:16px;margin:0 0 2px;}
        .printed{margin:0 0 14px;font-size:11px;}
        table.list{width:100%;border-collapse:collapse;font-size:12px;}
        table.list th{text-align:left;border-bottom:1px solid #808080;padding:3px 6px;}
        table.list td{padding:3px 6px;vertical-align:top;}
        tr.group td{font-weight:bold;padding-top:10px;}
        tr.unread td{font-weight:bold;}
        td.when,td.size{white-space:nowrap;}
        @media print{tr.group td{border-bottom:1px solid #000000;}}
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
