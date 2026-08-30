using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Scheduling;

namespace Mailbox.App.Views;

/// <summary>
/// The Tracking tab on a meeting the reader organizes: who was asked, in what capacity, and what
/// they have said.
/// </summary>
/// <remarks>
/// The replies are already in the store — an iMIP REPLY writes the answering attendee's PARTSTAT
/// onto the appointment (§9) — and until now nothing showed them. That is the whole feature: a
/// table of what the invitations came back with, headed by the count the reference puts at the
/// top of the tab.
/// <para>
/// Composed rather than drawn: it is a dozen rows of text with no grid to keep crisp, and the
/// names have to be selectable.
/// </para>
/// </remarks>
internal sealed class TrackingView : Border
{
    public TrackingView(CalendarEvent meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        this[!BackgroundProperty] = new DynamicResourceExtension("list.background.brush");
        Padding = new Thickness(24, 18, 24, 18);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(Summary(meeting));
        stack.Children.Add(Head());

        // The organiser first, as the reference's table lists them: the summary above counts a
        // reply nobody could otherwise see the row for.
        if (meeting.Organizer is { Length: > 0 } organizer)
        {
            var row = Columns();
            Add(row, Address(organizer), 0, semiBold: false);
            Add(row, "Meeting Organizer", 1, semiBold: false);
            Add(row, "None", 2, semiBold: false);
            stack.Children.Add(new Border { Child = row, Padding = new Thickness(0, 5, 0, 5) });
        }

        foreach (var attendee in meeting.Attendees)
        {
            stack.Children.Add(Row(attendee));
        }

        if (meeting.Attendees.Count == 0)
        {
            stack.Children.Add(Line("Nobody has been asked to this meeting yet.", subtle: true));
        }

        Child = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    /// <summary>The line the reference heads the tab with: how the replies stand.</summary>
    private Control Summary(CalendarEvent meeting)
    {
        var accepted = meeting.Attendees.Count(a => Verdict(a.PartStat) == "Accepted");
        var tentative = meeting.Attendees.Count(a => Verdict(a.PartStat) == "Tentative");
        var declined = meeting.Attendees.Count(a => Verdict(a.PartStat) == "Declined");
        var waiting = meeting.Attendees.Count - accepted - tentative - declined;

        var text = meeting.Attendees.Count == 0
            ? "No responses have been received for this meeting."
            : $"{accepted} accepted, {tentative} tentatively accepted, {declined} declined, {waiting} not yet responded.";

        var block = new TextBlock { Text = text, FontSize = 14, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");
        return block;
    }

    private Control Head()
    {
        var grid = Columns();
        Add(grid, "Name", 0, semiBold: true);
        Add(grid, "Attendance", 1, semiBold: true);
        Add(grid, "Response", 2, semiBold: true);

        var head = new Border { Child = grid, Padding = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(0, 0, 0, 1) };
        head[!BorderBrushProperty] = new DynamicResourceExtension("border.subtle.brush");
        return head;
    }

    private Control Row(EventAttendee attendee)
    {
        var grid = Columns();
        Add(grid, attendee.Name.Length > 0 ? $"{attendee.Name} <{Address(attendee.Address)}>" : Address(attendee.Address), 0, semiBold: false);
        Add(grid, Capacity(attendee.Role), 1, semiBold: false);
        Add(grid, Verdict(attendee.PartStat), 2, semiBold: false);

        return new Border { Child = grid, Padding = new Thickness(0, 5, 0, 5) };
    }

    private static Grid Columns() => new() { ColumnDefinitions = new ColumnDefinitions("*,160,140") };

    private static void Add(Grid grid, string text, int column, bool semiBold)
    {
        var block = new SelectableTextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = semiBold ? FontWeight.SemiBold : FontWeight.Normal,
            TextWrapping = TextWrapping.NoWrap,
        };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(semiBold ? "text.secondary.brush" : "text.primary.brush");
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private Control Line(string text, bool subtle)
    {
        var block = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(subtle ? "text.secondary.brush" : "text.primary.brush");
        return block;
    }

    /// <summary>What an address looks like without the <c>mailto:</c> an iCalendar file puts on it.</summary>
    private static string Address(string address)
    {
        var text = address.Trim();
        return text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? text[7..] : text;
    }

    /// <summary>ROLE, in the reference's own words.</summary>
    internal static string Capacity(string role) => role.ToUpperInvariant() switch
    {
        "OPT-PARTICIPANT" => "Optional Attendee",
        "NON-PARTICIPANT" => "Resource",
        "CHAIR" => "Organizer",
        _ => "Required Attendee",
    };

    /// <summary>PARTSTAT, in the reference's own words.</summary>
    internal static string Verdict(string partStat) => partStat.ToUpperInvariant() switch
    {
        "ACCEPTED" => "Accepted",
        "DECLINED" => "Declined",
        "TENTATIVE" => "Tentative",
        "DELEGATED" => "Delegated",
        _ => "None",
    };
}
