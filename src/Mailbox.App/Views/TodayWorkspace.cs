using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Scheduling;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The summary page: the day in three columns — what is on the calendar, what is outstanding,
/// and what is waiting in the folders.
/// </summary>
/// <remarks>
/// The reference's own summary page, which it names after itself; ours is named after this
/// application, per the name rule. It is what an account's heading in the folder pane opens, which
/// is where the reference puts it too.
/// <para>
/// <b>No capture of it exists</b>, so the arrangement is the reference's — three columns under a
/// dated heading, each a short list with a heading of its own — and the type sizes are the card's.
/// Everything on it is a link: a folder opens that folder, an appointment or a task opens its own
/// window.
/// </para>
/// <para>
/// It reads and never writes. The counts come from the same stores the panes read, so a page that
/// disagrees with the folder pane is not possible.
/// </para>
/// </remarks>
public sealed class TodayWorkspace : Border
{
    private readonly StackPanel _columns = new() { Orientation = Orientation.Horizontal, Spacing = 40 };
    private readonly TextBlock _heading = new() { FontSize = 22 };

    /// <summary>
    /// Every line the page drew, in the order it drew them, with the button behind it.
    /// </summary>
    /// <remarks>
    /// Kept because a picture of this page cannot be pressed and cannot be queried. Every line on
    /// it is a link and none of them had ever been followed by anything: a run could photograph
    /// the three columns and could not say whether a folder line opens that folder, whether an
    /// appointment line opens that appointment, or whether a line does nothing at all — and one
    /// kind of line does exactly that.
    /// </remarks>
    private readonly List<(string Column, string Text, Button? Link)> _lines = [];

    private readonly PimRepository _pim;
    private readonly Func<IReadOnlyList<OpenAccount>> _accounts;
    private readonly DateOnly _today;

    public TodayWorkspace(PimRepository pim, Func<IReadOnlyList<OpenAccount>> accounts, DateOnly today)
    {
        _pim = pim ?? throw new ArgumentNullException(nameof(pim));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _today = today;

        this[!MarginProperty] = new DynamicResourceExtension("workspace.inset.rightmargin");
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        ClipToBounds = true;
        this[!BackgroundProperty] = new DynamicResourceExtension("people.card.background.brush");

        _heading[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.text.brush");

        Child = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(32, 24, 32, 24),
                Spacing = 20,
                Children = { _heading, _columns },
            },
        };

        Reload();
    }

    /// <summary>A folder was asked for: its account's address and the folder's name.</summary>
    public event EventHandler<(string Address, string Folder)>? FolderRequested;

    /// <summary>An appointment or a task was asked for, by the row it is on.</summary>
    public event EventHandler<long>? AppointmentRequested;

    public event EventHandler<long>? TaskRequested;

    /// <summary>A borrowed row — flagged mail, or a flagged contact — whose own window should open.</summary>
    public event EventHandler<TaskRow>? BorrowedRequested;

    /// <summary>What the status bar says while this page is up.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>The dated heading, as drawn.</summary>
    public string Heading => _heading.Text ?? string.Empty;

    /// <summary>
    /// What the page is showing: the column each line is in, its words, and whether pressing it
    /// is wired to anything.
    /// </summary>
    public IReadOnlyList<(string Column, string Text, bool Acts)> Lines
        => [.. _lines.Select(l => (l.Column, l.Text, l.Link is not null))];

    /// <summary>
    /// Presses one line of one column, through the button a reader would click.
    /// </summary>
    /// <returns>False when that column has no such line, or the line is not a link at all.</returns>
    public bool Press(string column, int index)
    {
        var rows = _lines
            .Where(l => string.Equals(l.Column, column, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (index < 0 || index >= rows.Count || rows[index].Link is not { } button) return false;

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>Reads the three stores again — after a write, or when the page is opened.</summary>
    public void Reload()
    {
        _heading.Text = _today.ToDateTime(TimeOnly.MinValue).ToString("D", CultureInfo.CurrentCulture);

        _lines.Clear();
        _columns.Children.Clear();
        var appointments = Appointments();
        var tasks = Tasks();
        var unread = Messages();

        _columns.Children.Add(Column("Calendar", appointments.Controls));
        _columns.Children.Add(Column("Tasks", tasks.Controls));
        _columns.Children.Add(Column("Messages", unread.Controls));

        Status = $"Items: {appointments.Count + tasks.Count}";
    }

    // ---- The three columns ---------------------------------------------------------------------

    private (IReadOnlyList<Control> Controls, int Count) Appointments()
    {
        var zone = TimeZoneInfo.Local;
        var start = new DateTimeOffset(_today.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(_today.ToDateTime(TimeOnly.MinValue)));
        var source = new Mailbox.Controls.Calendar.CalendarSource(_pim);
        var entries = source.Between(start.ToUniversalTime(), start.AddDays(1).ToUniversalTime());

        var rows = new List<Control>();
        foreach (var entry in entries.OrderBy(e => e.Occurrence.StartUtc).Take(8))
        {
            var when = entry.AllDay
                ? "All day"
                : entry.Occurrence.StartUtc.ToOffset(zone.GetUtcOffset(entry.Occurrence.StartUtc)).ToString("h:mm tt", CultureInfo.CurrentCulture);

            var id = entry.ItemId;
            rows.Add(Line("Calendar", $"{when}   {entry.Summary}", () => AppointmentRequested?.Invoke(this, id)));
        }

        if (rows.Count == 0) rows.Add(Quiet("Calendar", "Nothing today."));
        return (rows, entries.Count);
    }

    private (IReadOnlyList<Control> Controls, int Count) Tasks()
    {
        var book = new TaskBook(_pim, () => [.. _accounts().Select(a => (a.Account.Address, a.Mail))]);
        var outstanding = book.Rows(_today).Where(r => !r.IsComplete).ToList();

        var rows = new List<Control>();
        foreach (var row in outstanding.Take(8))
        {
            var id = row.ItemId;

            // A borrowed row is a message or a contact, and its id belongs to another store's
            // numbering: asking the task module to open one would open whatever task happened to
            // share the number. It is still a link — the page's rule is that everything on it
            // opens its own window — so it goes out under its own name.
            var taken = row;
            rows.Add(Line(
                "Tasks",
                row.Task.Due is { } due ? $"{row.Summary}   ({due.Wall:d})" : row.Summary,
                () =>
                {
                    if (taken.IsBorrowed) BorrowedRequested?.Invoke(this, taken);
                    else TaskRequested?.Invoke(this, id);
                }));
        }

        if (rows.Count == 0) rows.Add(Quiet("Tasks", "Nothing outstanding."));
        return (rows, outstanding.Count);
    }

    private (IReadOnlyList<Control> Controls, int Count) Messages()
    {
        var rows = new List<Control>();
        var total = 0;

        foreach (var account in _accounts())
        {
            var address = account.Account.Address;
            var mail = account.Mail;

            // The three the reference lists, and the counts it lists them with: what is unread in
            // the Inbox, and what is simply there in Drafts and the Outbox — all three at zero
            // too, because one rule for three rows is the whole of what the column says.
            foreach (var folder in mail.Folders(account.Account.Id)
                         .Where(f => f.Role is FolderRole.Inbox or FolderRole.Drafts or FolderRole.Outbox)
                         .OrderBy(f => f.Role))
            {
                var count = folder.Role == FolderRole.Inbox ? folder.Unread : folder.Total;
                total += count;
                var name = folder.Name;
                var whose = address;
                rows.Add(Line("Messages", $"{name}   {count}", () => FolderRequested?.Invoke(this, (whose, name))));
            }
        }

        if (rows.Count == 0) rows.Add(Quiet("Messages", "No accounts yet."));
        return (rows, total);
    }

    // ---- The pieces ------------------------------------------------------------------------------

    private Control Column(string heading, IReadOnlyList<Control> rows)
    {
        var stack = new StackPanel { Width = 280, Spacing = 4 };

        var label = new TextBlock { Text = heading, FontSize = 14, FontWeight = FontWeight.SemiBold };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("accent.rest.brush");
        stack.Children.Add(label);

        var rule = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 6) };
        rule[!BackgroundProperty] = new DynamicResourceExtension("people.card.rule.brush");
        stack.Children.Add(rule);

        foreach (var row in rows) stack.Children.Add(row);
        return stack;
    }

    /// <summary>One line of a column, which is a link: everything on this page opens something.</summary>
    private Control Line(string column, string text, Action run)
    {
        var button = new Button
        {
            Classes = { "flat" },
            Padding = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
        };

        button[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("people.card.text.brush");
        button.Click += (_, _) => run();
        _lines.Add((column, text, button));
        return button;
    }

    /// <summary>The line a column with nothing in it draws instead, which is not a link.</summary>
    private Control Quiet(string column, string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 2, 0, 2) };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.subtle.brush");
        _lines.Add((column, text, null));
        return block;
    }
}
