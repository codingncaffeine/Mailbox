using System.Text;
using Mailbox.Core.Commands;
using Mailbox.Core.Keyboard;

namespace Mailbox.App;

/// <summary>
/// Writes the shipped keyboard shortcuts out as Markdown, for the wiki's Keyboard shortcuts page.
/// </summary>
/// <remarks>
/// Generated rather than written, because a transcribed shortcut list is wrong within a month and
/// nothing tells the reader which half of it to trust. The source is the command catalogue itself
/// — <see cref="MailboxCommand.DefaultGesture"/> and <see cref="MailboxCommand.AlsoGestures"/>,
/// through the same <see cref="App.BuiltInCommands"/> the running application resolves keys with
/// — so a command that gains, loses or changes a chord changes this page on the next run of
/// <c>tools/export-shortcuts.sh</c>.
/// <para>
/// What cannot come from the catalogue is the keys a <em>view</em> answers: the arrows in the
/// calendar grids, Tab through a day's appointments. Those are not commands and are not
/// rebindable, so they are written down here, in one place, marked as what they are.
/// </para>
/// </remarks>
internal static class ShortcutsExport
{
    public static int Write(string? path)
    {
        var file = path is { Length: > 0 } ? path : "Keyboard-shortcuts.md";
        File.WriteAllText(file, Markdown());
        Console.WriteLine($"Wrote {file}.");
        return 0;
    }

    public static string Markdown()
    {
        var catalog = App.BuiltInCommands();
        var page = new StringBuilder();

        page.AppendLine("# Keyboard shortcuts");
        page.AppendLine();
        page.AppendLine("Every shortcut Mailbox ships with. **This page is generated** from the command");
        page.AppendLine("catalogue by `tools/export-shortcuts.sh`, so it says what the application does rather");
        page.AppendLine("than what somebody remembered it doing — edit the commands, not the page.");
        page.AppendLine();
        page.AppendLine("All of these can be changed: **Options › Customize Ribbon › Keyboard shortcuts:");
        page.AppendLine("Customize…** rebinds any command, and a chord given to one command is taken away from");
        page.AppendLine("whichever had it. Commands with no shortcut are left out; there are far more commands");
        page.AppendLine("than chords, and every one of them can be given one.");
        page.AppendLine();

        Section(page, catalog, CommandSurface.Shell, "The main window");
        Section(page, catalog, CommandSurface.Compose, "The compose window");
        Section(page, catalog, CommandSurface.Appointment, "The appointment window");
        Section(page, catalog, CommandSurface.Contact, "The contact window");

        page.Append(InsideAView);
        return page.ToString();
    }

    /// <summary>
    /// One window's shortcuts. The shell's are grouped by the module they belong to, because a
    /// flat table of two hundred rows is a list nobody reads; the item windows are one table each.
    /// </summary>
    private static void Section(StringBuilder page, CommandCatalog catalog, CommandSurface surface, string title)
    {
        var commands = catalog.All
            .Where(c => c.Surface == surface)
            .Where(c => Gestures(c).Count > 0)
            .ToList();
        if (commands.Count == 0) return;

        page.AppendLine($"## {title}");
        page.AppendLine();

        if (surface != CommandSurface.Shell)
        {
            Table(page, commands);
            return;
        }

        // Everywhere first, then a heading per module. A command scoped to several modules but
        // not to all of them appears under each of them, because that is where a reader looks.
        var everywhere = commands.Where(c => c.Scope == ModuleScope.Any).ToList();
        if (everywhere.Count > 0)
        {
            page.AppendLine("### Anywhere");
            page.AppendLine();
            Table(page, everywhere);
        }

        foreach (var module in (ModuleScope[])
                 [
                     ModuleScope.Mail, ModuleScope.Calendar, ModuleScope.People,
                     ModuleScope.Tasks, ModuleScope.Notes, ModuleScope.Journal, ModuleScope.Feeds,
                 ])
        {
            var mine = commands
                .Where(c => c.Scope != ModuleScope.Any && (c.Scope & module) != 0)
                .ToList();
            if (mine.Count == 0) continue;

            page.AppendLine($"### {module}");
            page.AppendLine();
            Table(page, mine);
        }
    }

    private static void Table(StringBuilder page, IEnumerable<MailboxCommand> commands)
    {
        page.AppendLine("| Shortcut | Command | What it does |");
        page.AppendLine("| --- | --- | --- |");

        foreach (var command in commands
                     .OrderBy(c => c.Category, StringComparer.Ordinal)
                     .ThenBy(c => c.Label, StringComparer.Ordinal))
        {
            var chords = string.Join(", ", Gestures(command).Select(g => $"`{g}`"));
            page.AppendLine($"| {chords} | {Cell(command.Label)} | {Cell(command.Description)} |");
        }

        page.AppendLine();
    }

    /// <summary>
    /// A command's chords as a reader sees them, its own first. Read back through
    /// <see cref="Chord"/> so the page spells a key the way the application does — the catalogue
    /// accepts "Del" and "PgDn", and a page that printed one of each would look like two keys.
    /// </summary>
    private static List<string> Gestures(MailboxCommand command)
        => [.. new[] { command.DefaultGesture }
            .Concat(command.AlsoGestures)
            .Select(Chord.Parse)
            .Where(c => c is not null)
            .Select(c => c!.Display)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>A cell that cannot break the table: the pipe is the only character that can.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>
    /// The keys a surface answers itself. Not commands, so not in the catalogue and not
    /// rebindable — and the only part of this page written by hand.
    /// </summary>
    private const string InsideAView = """
        ## Inside a view

        These belong to the surface that has the focus rather than to a command, so they are the
        same in every module that draws one and they cannot be rebound.

        Switching module puts the keyboard on that module's own surface — the grid in Calendar,
        the list in Mail, People, Tasks, Notes and Journal, the reader in Feeds — so the keys
        below work straight after the module switch (`Ctrl+1` for Mail, `Ctrl+2` for Calendar and
        so on), with nothing to click first.

        ### Any list

        | Shortcut | What it does |
        | --- | --- |
        | `Up` / `Down` | Move the selection by a row |
        | `Home` / `End` | The first and last row |
        | `Enter` | Open what is selected |

        `Page Up` and `Page Down` move a screenful in the mail list and the contact list. The
        notes wall walks with all four arrows, since its rows have columns. In the task list
        `Space` ticks the selected task off.

        ### The calendar, day and week

        | Shortcut | What it does |
        | --- | --- |
        | `Up` / `Down` | Move by one slot, at whatever time scale the grid is set to |
        | `Left` / `Right` | Move by a day |
        | `Home` / `End` | Midnight and the last slot of the day |
        | `Page Up` / `Page Down` | The whole run — a day in Day view, a week in the others |
        | `Tab` / `Shift+Tab` | Through the day's appointments; past the last one the focus leaves |
        | `Enter` | Open the appointment the caret is inside, or make one there |
        | `Escape` | Call off a drag |

        Arrowing off either end of the run pages the view, so the arrows are a way to travel and
        not only a way to look around one week.

        ### The calendar, month

        | Shortcut | What it does |
        | --- | --- |
        | `Left` / `Right` | Move by a day |
        | `Up` / `Down` | Move by a week |
        | `Home` / `End` | The first and last day of the caret's week |
        | `Page Up` / `Page Down` | The same day of the previous or next month |
        | `Tab` / `Shift+Tab` | Through the day's appointments, including any the cell was too small to draw |
        | `Enter` | Open the appointment Tab took hold of, or make one on the caret's day |

        ### The calendar, Schedule View

        Time runs sideways here, so the two axes are the day view's turned a quarter turn.

        | Shortcut | What it does |
        | --- | --- |
        | `Left` / `Right` | Move through the day by half an hour |
        | `Up` / `Down` | Move between calendars |
        | `Home` / `End` | The ends of the day |
        | `Page Up` / `Page Down` | The day either side |
        | `Tab` / `Shift+Tab` | Through that calendar's appointments |
        | `Enter` | Open the appointment under the caret, or make one there |

        The hours on show follow the caret, which is the only thing that scrolls this view
        sideways.
        """;
}
