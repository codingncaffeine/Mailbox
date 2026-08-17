using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    /// <summary>What the status bar says while this page is up.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>Reads the three stores again — after a write, or when the page is opened.</summary>
    public void Reload()
    {
        _heading.Text = _today.ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);

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
            rows.Add(Line($"{when}   {entry.Summary}", () => AppointmentRequested?.Invoke(this, id)));
        }

        if (rows.Count == 0) rows.Add(Quiet("Nothing today."));
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
            var message = row.IsMessage;
            rows.Add(Line(
                row.Task.Due is { } due ? $"{row.Summary}   ({due.Wall:d})" : row.Summary,
                () =>
                {
                    if (!message) TaskRequested?.Invoke(this, id);
                }));
        }

        if (rows.Count == 0) rows.Add(Quiet("Nothing outstanding."));
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
            // the Inbox, and what is simply there in Drafts and the Outbox.
            foreach (var folder in mail.Folders(account.Account.Id)
                         .Where(f => f.Role is FolderRole.Inbox or FolderRole.Drafts or FolderRole.Outbox)
                         .OrderBy(f => f.Role))
            {
                var count = folder.Role == FolderRole.Inbox ? folder.Unread : folder.Total;
                if (count == 0 && folder.Role != FolderRole.Inbox) continue;

                total += count;
                var name = folder.Name;
                var whose = address;
                rows.Add(Line($"{name}   {count}", () => FolderRequested?.Invoke(this, (whose, name))));
            }
        }

        if (rows.Count == 0) rows.Add(Quiet("No accounts yet."));
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
    private static Control Line(string text, Action run)
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
        return button;
    }

    private static Control Quiet(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 2, 0, 2) };
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("people.card.subtle.brush");
        return block;
    }
}
