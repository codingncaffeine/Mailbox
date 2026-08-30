using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Dav;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>Which copy of a conflicted appointment was kept.</summary>
public enum ConflictChoice
{
    /// <summary>Neither: the change stays queued and the question comes round again.</summary>
    Later,

    /// <summary>This machine's copy, pushed again over the server's.</summary>
    Local,

    /// <summary>The server's copy, written over this machine's.</summary>
    Server,
}

/// <summary>
/// The two copies of an appointment the server would not let one replace, side by side, and the
/// choice between them.
/// </summary>
/// <remarks>
/// A refused write leaves two truths and no way to tell which was meant — the sync engine
/// reports it rather than picking, and this is where the picking happens. Both copies are
/// shown in full because "your change was rejected" is not enough to choose on: the difference is
/// usually one field, and which field it is decides the answer.
/// <para>
/// Nothing here talks to the network. Keeping the local copy re-queues it carrying the server's
/// tag, keeping the server's writes it into the store; either way the next Send/Receive settles
/// it, which is the same path an offline edit takes.
/// </para>
/// </remarks>
public sealed class CalendarConflictDialog : Window
{
    private readonly IReadOnlyList<DavConflict> _conflicts;
    private readonly PimRepository _repository;
    private readonly ListBox _list = new();
    private readonly StackPanel _mine = new() { Spacing = 2 };
    private readonly StackPanel _theirs = new() { Spacing = 2 };
    private readonly Button _keepMine;
    private readonly Button _keepTheirs;

    /// <summary>What was chosen for each conflict, in the order they were given.</summary>
    public IReadOnlyDictionary<long, ConflictChoice> Choices => _choices;

    private readonly Dictionary<long, ConflictChoice> _choices = [];

    public CalendarConflictDialog(PimRepository repository, IReadOnlyList<DavConflict> conflicts)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _conflicts = conflicts ?? throw new ArgumentNullException(nameof(conflicts));

        Title = "Conflicting Changes";
        Width = 620;
        Height = conflicts.Count > 1 ? 434 : 326;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var lead = new TextBlock
        {
            Text = conflicts.Count == 1
                ? "This appointment was changed here and on the server. Nothing has been overwritten — choose which copy to keep."
                : $"{conflicts.Count} appointments were changed here and on the server. Nothing has been overwritten — choose which copy to keep.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Bind(lead, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        _keepMine = Push("Keep Mine", () => Settle(ConflictChoice.Local));
        _keepTheirs = Push("Keep Theirs", () => Settle(ConflictChoice.Server));
        var later = new Button { Content = "Decide Later", Width = 104, IsCancel = true };
        later.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { _keepMine, _keepTheirs, later },
        };

        var body = new DockPanel { Margin = new Thickness(18), Children = { buttons } };
        var stack = new StackPanel();
        stack.Children.Add(lead);

        // One conflict needs no list to choose from; several do, and the reference's own
        // multi-item prompts put the list above the detail rather than beside it.
        if (conflicts.Count > 1)
        {
            _list.Height = 96;
            _list.Margin = new Thickness(0, 0, 0, 12);
            _list.ItemsSource = conflicts.Select(Describe).ToList();
            _list.SelectionChanged += (_, _) => ShowSelected();
            // Its box and its ink come from the dialog stylesheet, which is where every dialog's
            // list gets them; naming them here would be a local value beating that.
            stack.Children.Add(_list);
        }

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        columns.Children.Add(Column("Your copy", _mine));
        var right = Column("The copy on the server", _theirs);
        Grid.SetColumn(right, 2);
        columns.Children.Add(right);
        stack.Children.Add(columns);

        body.Children.Add(stack);
        DialogChrome.Apply(this, body);

        _list.SelectedIndex = 0;
        ShowSelected();
    }

    private int Index => _conflicts.Count > 1 ? Math.Max(0, _list.SelectedIndex) : 0;

    /// <summary>
    /// Records the answer and moves on. The last one closes the window — the reader is not asked
    /// to press Decide Later after settling everything.
    /// </summary>
    private void Settle(ConflictChoice choice)
    {
        var conflict = _conflicts[Index];
        var done = choice == ConflictChoice.Local
            ? DavSync.KeepLocal(_repository, conflict)
            : DavSync.KeepServer(_repository, conflict);

        _choices[conflict.ItemId] = done ? choice : ConflictChoice.Later;

        if (_conflicts.Count == 1 || _choices.Count >= _conflicts.Count)
        {
            Close();
            return;
        }

        // The next one that has not been answered yet.
        for (var step = 1; step <= _conflicts.Count; step++)
        {
            var next = (Index + step) % _conflicts.Count;
            if (_choices.ContainsKey(_conflicts[next].ItemId)) continue;
            _list.SelectedIndex = next;
            break;
        }

        ShowSelected();
    }

    private void ShowSelected()
    {
        var conflict = _conflicts[Index];
        var local = conflict.ItemId > 0 ? _repository.Item(conflict.ItemId) : null;

        _mine.Children.Clear();
        _theirs.Children.Clear();

        if (conflict.LocalDelete)
        {
            _mine.Children.Add(Line("Deleted here.", subtle: false));
            _mine.Children.Add(Line(local is null ? "The appointment is already gone." : Named(local.Summary), subtle: true));
        }
        else if (local is null)
        {
            _mine.Children.Add(Line("No longer in this calendar.", subtle: true));
        }
        else
        {
            Describe(_mine, PimEventCodec.FromItem(local));
        }

        if (Parse(conflict.ServerPayload) is { } theirs) Describe(_theirs, theirs);
        else _theirs.Children.Add(Line("The server's copy could not be read.", subtle: true));

        // A copy that cannot be shown is a copy that cannot be chosen.
        _keepMine.IsEnabled = local is not null;
        _keepTheirs.IsEnabled = conflict.ServerPayload is { Length: > 0 };
    }

    /// <summary>The fields the two copies are compared on: what, when, where, and how it repeats.</summary>
    private void Describe(StackPanel into, CalendarEvent appointment)
    {
        into.Children.Add(Line(Named(appointment.Summary), subtle: false, bold: true));
        into.Children.Add(Line(When(appointment), subtle: false));
        if (appointment.Location.Length > 0) into.Children.Add(Line(appointment.Location, subtle: false));
        if (appointment.Rrule is { Length: > 0 } rule)
        {
            into.Children.Add(Line(RecurrenceText.Describe(rule, appointment.Start, appointment.End), subtle: true));
        }

        if (appointment.Attendees.Count > 0)
        {
            into.Children.Add(Line($"{appointment.Attendees.Count} attendee(s)", subtle: true));
        }

        into.Children.Add(Line(
            "Last changed " + appointment.LastModified.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture),
            subtle: true));
    }

    private static string When(CalendarEvent appointment)
    {
        // The culture's own patterns throughout, because the recurrence sentence under this
        // line uses them — one convention per surface, not a day-first 24-hour line over a
        // locale-shaped one.
        var culture = CultureInfo.CurrentCulture;

        if (appointment.AllDay)
        {
            var last = appointment.End.Wall.AddDays(-1);
            var first = appointment.Start.Wall;
            return last.Date <= first.Date
                ? first.ToString("D", culture)
                : $"{first.ToString("d", culture)} – {last.ToString("d", culture)}";
        }

        return appointment.Start.Wall.ToString("f", culture)
               + " – " + appointment.End.Wall.ToString(
                   appointment.End.Wall.Date == appointment.Start.Wall.Date ? "t" : "f",
                   culture);
    }

    /// <summary>The master out of a server payload — the copy the two-column view compares.</summary>
    private static CalendarEvent? Parse(string? payload)
    {
        if (payload is not { Length: > 0 }) return null;
        try
        {
            var events = ICalendarCodec.Parse(payload);
            return events.FirstOrDefault(e => !e.IsOverride) ?? events.FirstOrDefault();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string Describe(DavConflict conflict)
    {
        var name = Named(conflict.Summary);
        return conflict.LocalDelete ? name + " (deleted here)" : name;
    }

    private static string Named(string summary) => summary.Length > 0 ? summary : "(no subject)";

    // ---- The dialog's furniture ----------------------------------------------------------------

    private Control Column(string heading, StackPanel content)
    {
        var panel = new StackPanel { Spacing = 6 };
        var title = new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold };
        Bind(title, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        panel.Children.Add(title);

        var box = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            MinHeight = 132,
            Child = content,
        };
        Bind(box, BackgroundProperty, "dialog.surface.brush");
        Bind(box, BorderBrushProperty, "dialog.border.brush");
        panel.Children.Add(box);
        return panel;
    }

    private TextBlock Line(string text, bool subtle, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            // Subtle text inside a box is the box's own ink at reduced opacity: a dialog has six
            // colours and a seventh grey is not one of them.
            Opacity = subtle ? 0.72 : 1,
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        return block;
    }

    private Button Push(string content, Action click)
    {
        var button = new Button { Content = content, Width = 104 };
        button.Click += (_, _) => click();
        return button;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>Asks about a run of conflicts, and hands back what was settled.</summary>
    public static async Task<IReadOnlyDictionary<long, ConflictChoice>> AskAsync(
        Window owner,
        PimRepository repository,
        IReadOnlyList<DavConflict> conflicts)
    {
        if (conflicts.Count == 0) return new Dictionary<long, ConflictChoice>();
        var dialog = new CalendarConflictDialog(repository, conflicts);
        await dialog.ShowDialog(owner);
        return dialog.Choices;
    }
}
