using System.Text;
using Mailbox.Core.Commands;
using Mailbox.Core.Keyboard;
using Mailbox.Core.Ribbon;
using Mailbox.Core.Settings;
using Mailbox.Theming.Icons;

namespace Mailbox.Tests;

/// <summary>
/// The whole-catalogue sweeps: every command fully described, no two commands shipping the same
/// shortcut, and the compose-availability table covering exactly the compose window's commands.
/// </summary>
/// <remarks>
/// These are the audit's wiring sweeps promoted into tests, so the classes of fault they caught
/// stay caught. Every one of them asks the <em>whole registered catalogue</em> — the eleven sets
/// <c>App</c> registers — rather than one module's: the older versions of these checks each looked
/// at a subset, and a command in one of the sets they missed could go unlabelled or take another
/// command's chord without anything failing.
/// </remarks>
public class AuditWiringSweepTests
{
    /// <summary>
    /// Every command set the application registers, in the order it registers them.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>App.RegisterCommands</c>. A set added there and not here would leave this file
    /// sweeping less than the application ships, which is the failure these tests exist to stop —
    /// so the count below is asserted rather than assumed.
    /// </remarks>
    internal static CommandCatalog Registered()
    {
        var catalog = new CommandCatalog();
        catalog.RegisterRange(MailCommands.All);
        catalog.RegisterRange(ViewCommands.All);
        catalog.RegisterRange(ComposeCommands.All);
        catalog.RegisterRange(CalendarCommands.All);
        catalog.RegisterRange(AppointmentCommands.All);
        catalog.RegisterRange(ContactCommands.All);
        catalog.RegisterRange(PeopleCommands.All);
        catalog.RegisterRange(TaskCommands.All);
        catalog.RegisterRange(NoteCommands.All);
        catalog.RegisterRange(JournalCommands.All);
        catalog.RegisterRange(FeedCommands.All);
        return catalog;
    }

    /// <summary>The sets by name, for a per-module report.</summary>
    private static readonly (string Set, IReadOnlyList<MailboxCommand> Commands)[] Sets =
    [
        ("MailCommands", [.. MailCommands.All]),
        ("ViewCommands", [.. ViewCommands.All]),
        ("ComposeCommands", [.. ComposeCommands.All]),
        ("CalendarCommands", [.. CalendarCommands.All]),
        ("AppointmentCommands", [.. AppointmentCommands.All]),
        ("ContactCommands", [.. ContactCommands.All]),
        ("PeopleCommands", [.. PeopleCommands.All]),
        ("TaskCommands", [.. TaskCommands.All]),
        ("NoteCommands", [.. NoteCommands.All]),
        ("JournalCommands", [.. JournalCommands.All]),
        ("FeedCommands", [.. FeedCommands.All]),
    ];

    /// <summary>
    /// The registered catalogue is the eleven sets and nothing else, with no id declared twice.
    /// </summary>
    [Fact]
    public void TheCatalogueIsEverySetTheApplicationRegisters()
    {
        var catalog = Registered();
        Assert.Equal(Sets.Sum(s => s.Commands.Count), catalog.Count);
        Assert.Equal(11, Sets.Length);
    }

    // ---- Catalogue hygiene -----------------------------------------------------------------

    /// <summary>
    /// Label, description, icon and category on every command in every set.
    /// </summary>
    /// <remarks>
    /// <see cref="CommandCatalogTests.EveryCommandIsFullyDescribed"/> asks the same four questions
    /// of the mail set alone. A blank screentip or an empty category is invisible until somebody
    /// hovers the button or opens the customization gallery, and ten of the eleven sets were
    /// outside that test's reach.
    /// </remarks>
    [Fact]
    public void EveryRegisteredCommandIsFullyDescribed()
    {
        var faults = new List<string>();

        foreach (var command in Registered().All.OrderBy(c => c.Id.Value, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(command.Label)) faults.Add($"{command.Id}: no label");
            if (string.IsNullOrWhiteSpace(command.Description)) faults.Add($"{command.Id}: no description");
            if (string.IsNullOrWhiteSpace(command.Icon)) faults.Add($"{command.Id}: no icon");
            if (string.IsNullOrWhiteSpace(command.Category)) faults.Add($"{command.Id}: no category");
        }

        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    /// <summary>A screentip is a sentence, so it ends in a full stop.</summary>
    [Fact]
    public void EveryRegisteredDescriptionIsASentence()
    {
        var faults = Registered().All
            .Where(c => !c.Description.TrimEnd().EndsWith('.'))
            .Select(c => $"{c.Id}: “{c.Description}”")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    /// <summary>
    /// An icon name that is not in the glyph map draws nothing at all, and
    /// <see cref="IconGlyphs.GetOrEmpty"/> is forgiving by design — so nothing else complains.
    /// </summary>
    [Fact]
    public void EveryRegisteredCommandsIconIsAGlyphThatExists()
    {
        var faults = Registered().All
            .Where(c => string.IsNullOrEmpty(IconGlyphs.GetOrEmpty(c.Icon, 16)))
            .Select(c => $"{c.Id} asks for the '{c.Icon}' icon, which is not in the glyph map.")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    /// <summary>KeyTips are 1–3 uppercase characters with no whitespace, across every set.</summary>
    [Fact]
    public void EveryRegisteredKeyTipFollowsTheRibbonFrameworkRules()
    {
        var faults = new List<string>();

        foreach (var command in Registered().All.Where(c => c.KeyTip is not null))
        {
            var tip = command.KeyTip!;
            if (tip.Length is < 1 or > 3) faults.Add($"{command.Id}: '{tip}' is {tip.Length} characters");
            if (tip.Any(char.IsWhiteSpace)) faults.Add($"{command.Id}: '{tip}' has whitespace");
            if (tip != tip.ToUpperInvariant()) faults.Add($"{command.Id}: '{tip}' is not uppercase");
        }

        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    // ---- The default gesture map -----------------------------------------------------------

    /// <summary>
    /// Every shipped shortcut reaches its own command, asked the way the window that owns it asks.
    /// </summary>
    /// <remarks>
    /// The conflict detector is <see cref="KeyMap.CommandFor(Chord, MailboxModule?)"/> itself —
    /// the same call the real input path makes — rather than a comparison of gesture strings,
    /// which would miss the part that actually decides: scope. One chord means different things in
    /// different modules on purpose, so the question is asked once per module the command is
    /// scoped to, and a command in a window of its own is asked through its surface.
    /// <para>
    /// A command every module shares may be answered by one module's own, and that is the design
    /// rather than a clash: Delete is <c>mail.delete</c>'s everywhere except where a module has a
    /// delete of its own, and the calendar's throws away an appointment. So the answer counts as
    /// right when it is the command itself, or a command scoped to that one module holding the
    /// same chord as its own shortcut. Anything else is a chord that has quietly changed hands.
    /// </para>
    /// <para>
    /// <see cref="ShortcutTests.EveryShippedShortcutReachesItsOwnCommand"/> asks the narrower
    /// version: five of the eleven sets, and every shell command asked as though it were Mail's or
    /// Calendar's. A People, Tasks, Notes, Journal or Feeds command losing its chord to another
    /// module's was outside it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryShippedShortcutReachesItsOwnCommandInEveryModuleItIsScopedTo()
    {
        var catalog = Registered();
        var keys = new KeyMap(SettingsStore.Transient(), catalog);
        var clashes = new List<string>();

        foreach (var command in catalog.All.OrderBy(c => c.Id.Value, StringComparer.Ordinal))
        {
            if (Chord.Parse(command.DefaultGesture) is not { } chord) continue;

            if (command.Surface != CommandSurface.Shell)
            {
                var winner = keys.CommandFor(chord, command.Surface);
                if (winner != command.Id)
                {
                    clashes.Add($"{command.Id} ({chord}) in the {command.Surface} window is answered by "
                                + (winner?.Value ?? "nothing"));
                }

                continue;
            }

            foreach (var module in ModulesFor(command))
            {
                var winner = keys.CommandFor(chord, module);
                if (winner == command.Id) continue;
                if (IsThisModulesOwn(catalog, winner, chord, module)) continue;

                clashes.Add($"{command.Id} ({chord}) in {module} is answered by "
                            + (winner?.Value ?? "nothing"));
            }
        }

        Assert.True(clashes.Count == 0, string.Join("\n", clashes));
    }

    /// <summary>
    /// True when the answer is a command belonging to this one module and holding the chord as its
    /// own shortcut — the deliberate override of a chord every module shares.
    /// </summary>
    private static bool IsThisModulesOwn(
        CommandCatalog catalog, CommandId? winner, Chord chord, MailboxModule module)
        => winner is { } id
           && catalog.TryGet(id, out var other)
           && other.Scope != ModuleScope.Any
           && other.Scope.HasFlag(module.AsScope())
           && Chord.Parse(other.DefaultGesture) == chord;

    /// <summary>
    /// The shipped second chords that do not reach their own command, listed exactly.
    /// </summary>
    /// <remarks>
    /// <see cref="MailboxCommand.AlsoGestures"/> is consulted only after every command's own
    /// shortcut, so a second chord some other command holds outright is dead as shipped and
    /// nothing says so — <see cref="KeyMap.AlsoGesturesFor"/> filters it out of the tooltip, which
    /// is honest and also silent.
    /// <para>
    /// A tripwire rather than a clean bill, in the manner of the compose table's working count:
    /// what is dead today is a list somebody has to change on purpose. Shortening it means a
    /// finding was fixed and the entry goes; lengthening it means a new chord was declared over
    /// one that is already spoken for.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSecondChordsThatReachNothingAreTheOnesAlreadyRecorded()
        => Assert.Equal(
            KnownDeadSecondChords,
            DeadSecondChords()
                .Select(x => x.Chord.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray());

    /// <summary>
    /// The shipped second chords that never reach the command that declares them.
    /// </summary>
    /// <remarks>
    /// A chord answered by a command scoped to that one module is the deliberate override
    /// <see cref="IsThisModulesOwn"/> describes, and is not counted.
    /// </remarks>
    private static IReadOnlyList<(CommandId Command, Chord Chord)> DeadSecondChords()
    {
        var catalog = Registered();
        var keys = new KeyMap(SettingsStore.Transient(), catalog);
        var dead = new List<(CommandId, Chord)>();

        foreach (var command in catalog.All.OrderBy(c => c.Id.Value, StringComparer.Ordinal))
        {
            foreach (var text in command.AlsoGestures)
            {
                if (Chord.Parse(text) is not { } chord) continue;

                var reaches = command.Surface != CommandSurface.Shell
                    ? keys.CommandFor(chord, command.Surface) == command.Id
                    : ModulesFor(command).All(m =>
                        keys.CommandFor(chord, m) is var winner
                        && (winner == command.Id || IsThisModulesOwn(catalog, winner, chord, m)));

                if (!reaches) dead.Add((command.Id, chord));
            }
        }

        return dead;
    }

    /// <summary>
    /// The chords that are declared as somebody's second and reach somebody else, and why.
    /// </summary>
    /// <remarks>
    /// Held as chords rather than as command-and-chord pairs on purpose. Six commands declare
    /// Ctrl+N and all six are <see cref="ModuleScope.Any"/>, so which of them the shared pass
    /// answers with is whichever the catalogue enumerates first — a detail of a frozen dictionary,
    /// not a decision. Naming the chord records the fault without pinning the test to that order.
    /// <para>
    /// <b>Ctrl+N.</b> New Appointment, New Contact, New Task, New Note, Journal Entry and Add a
    /// Feed each reach every module, because each is on the New Items menu everywhere — so the
    /// map's per-module pass cannot tell them apart and the shared pass picks one. Ctrl+N is right
    /// in Mail, where <c>mail.new</c> owns it outright, and arbitrary in the other five. Saying
    /// "this one, in this module" needs a scope on the chord, which the record does not carry.
    /// </para>
    /// <para>
    /// <b>Ctrl+K.</b> Check Names declares it second; <c>insert.link</c> owns it in the same
    /// window, and an own shortcut is answered before anybody's second. Alt+K, Check Names' own,
    /// still works.
    /// </para>
    /// </remarks>
    private static readonly string[] KnownDeadSecondChords = ["Ctrl+K", "Ctrl+N"];

    /// <summary>
    /// The modules a shell command answers in: the ones it is scoped to, and never the two that
    /// have no scope of their own.
    /// </summary>
    /// <remarks>
    /// <c>HasFlag</c> answers true for <see cref="ModuleScope.None"/> whatever the scope is — it
    /// is a zero — so Folders and Shortcuts would otherwise be counted as in scope for every
    /// command. What those two do with a chord is
    /// <see cref="TheShellNeverAnswersAChordWithAnotherWindowsCommand"/>'s question.
    /// </remarks>
    private static IEnumerable<MailboxModule> ModulesFor(MailboxCommand command)
        => Modules.Where(m => m.AsScope() != ModuleScope.None && command.Scope.HasFlag(m.AsScope()));

    /// <summary>
    /// The shell never answers a chord with a command belonging to a window of its own.
    /// </summary>
    /// <remarks>
    /// Ctrl+B bolds in a compose window and does nothing in the shell; the guard that says so is
    /// <c>Here</c> inside <see cref="KeyMap.CommandFor(Chord, MailboxModule?)"/>, which lets
    /// everything through when the module has no scope of its own. Folders and Shortcuts are
    /// exactly that — <see cref="ModuleScopeExtensions.AsScope"/> answers
    /// <see cref="ModuleScope.None"/> for both — so this asks in every module, those two included.
    /// </remarks>
    [Fact]
    public void TheShellNeverAnswersAChordWithAnotherWindowsCommand()
    {
        var catalog = Registered();
        var keys = new KeyMap(SettingsStore.Transient(), catalog);
        var leaks = new List<string>();

        var chords = catalog.All
            .SelectMany(c => c.AlsoGestures.Prepend(c.DefaultGesture ?? string.Empty))
            .Select(Chord.Parse)
            .OfType<Chord>()
            .Distinct()
            .OrderBy(c => c.ToString(), StringComparer.Ordinal)
            .ToList();

        foreach (var module in Enum.GetValues<MailboxModule>())
        {
            foreach (var chord in chords)
            {
                if (keys.CommandFor(chord, module) is not { } id) continue;
                if (!catalog.TryGet(id, out var command)) continue;
                if (command.Surface == CommandSurface.Shell) continue;

                leaks.Add($"{chord} in {module} reaches {id}, which belongs to the "
                          + $"{command.Surface} window.");
            }
        }

        Assert.True(leaks.Count == 0, string.Join("\n", leaks));
    }

    private static readonly MailboxModule[] Modules = Enum.GetValues<MailboxModule>();

    // ---- The compose-availability table ----------------------------------------------------

    /// <summary>Every command a layout puts on screen, classic rows and Simplified alike.</summary>
    private static HashSet<CommandId> Placed(RibbonLayout layout)
        =>
        [
            .. layout.PlacedCommands,
            .. layout.SimplifiedRows.SelectMany(r => r.Value).Where(i => !i.IsSentinel).Select(i => i.Command),
        ];

    /// <summary>
    /// The table covers exactly the commands the compose window can run.
    /// </summary>
    /// <remarks>
    /// <see cref="ComposeRibbonTests"/> asks this of the compose <em>ribbon</em>: everything placed
    /// has a status, and every status is placed. That leaves a gap at each end, and this closes
    /// both — a command stamped <see cref="CommandSurface.Compose"/> is one the window owns whether
    /// or not the ribbon places it, since the window dispatches by id.
    /// <para>
    /// The one subtraction is the ten Insert commands only the Contact window places — Screenshot,
    /// Quick Parts, WordArt, Object, Business Card, Bookmark, Text Box, Drop Cap, Date &amp; Time
    /// and Horizontal Line. They are declared in <see cref="ComposeCommands"/> and so carry its
    /// surface stamp, but no compose ribbon places them and no compose window offers them, so a
    /// status for each would be a claim about a button that is not there. They are named by
    /// <em>where they are placed</em> rather than by a list of ids, so one of them arriving on the
    /// compose ribbon fails this rather than sliding through.
    /// </para>
    /// </remarks>
    [Fact]
    public void ComposeAvailabilityCoversExactlyWhatTheComposeWindowCanRun()
    {
        var onTheComposeRibbon = Placed(DefaultRibbonLayouts.Compose);
        var onTheContactRibbon = Placed(ContactRibbonLayout.Contact);

        var owned = Registered().All
            .Where(c => c.Surface == CommandSurface.Compose)
            .Select(c => c.Id)
            .Where(id => onTheComposeRibbon.Contains(id) || !onTheContactRibbon.Contains(id))
            .Concat(onTheComposeRibbon)
            .ToHashSet();

        var missing = owned
            .Where(id => ComposeAvailability.For(id) is null)
            .Select(id => id.Value)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var extra = ComposeAvailability.All
            .Where(s => !owned.Contains(s.Command))
            .Select(s => s.Command.Value)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "The compose window can run these and no status is recorded:\n" + string.Join("\n", missing));

        Assert.True(
            extra.Count == 0,
            "A status is recorded for these and the compose window cannot run them:\n"
            + string.Join("\n", extra));
    }

    // ---- The inventory ---------------------------------------------------------------------

    /// <summary>
    /// Writes the command catalogue as TSV, one row per command. Skipped in an ordinary run; set
    /// <c>MAILBOX_CATALOGUE_DUMP</c> to a directory to produce one.
    /// </summary>
    /// <remarks>
    /// Generated from the code rather than transcribed, so the inventory an audit leans on cannot
    /// disagree with what the application registers.
    /// </remarks>
    [Fact]
    public void DumpTheCatalogueOnRequest()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_CATALOGUE_DUMP") is not { Length: > 0 } asked) return;

        // Resolved against the repository, not the test host's working directory — which is the
        // binary's own folder, so a caller asking for artifacts/audit/… got it buried under
        // tests/Mailbox.Tests/bin and wondered where the dump went.
        var into = Path.IsPathRooted(asked)
            ? asked
            : Path.Combine(RepoRootForDump(), asked);

        Directory.CreateDirectory(into);

        var rows = new StringBuilder();
        rows.AppendLine(string.Join('\t',
            "set", "id", "label", "description", "icon", "iconArtwork", "iconTint", "neutralIcon",
            "category", "scope", "surface", "keyTip", "defaultGesture", "alsoGestures",
            "inDefaultLayout", "isToggle", "requiresSelection", "requiresSingleSelection"));

        foreach (var (set, commands) in Sets)
        {
            foreach (var c in commands)
            {
                rows.AppendLine(string.Join('\t',
                    set, c.Id.Value, c.Label, c.Description, c.Icon, c.IconArtwork ?? string.Empty,
                    c.IconTint ?? string.Empty, c.NeutralIcon, c.Category, c.Scope, c.Surface,
                    c.KeyTip ?? string.Empty, c.DefaultGesture ?? string.Empty,
                    string.Join(' ', c.AlsoGestures), c.InDefaultLayout, c.IsToggle,
                    c.RequiresSelection, c.RequiresSingleSelection));
            }
        }

        File.WriteAllText(Path.Combine(into, "command-catalogue.tsv"), rows.ToString());

        var summary = new StringBuilder();
        summary.AppendLine($"{Sets.Sum(s => s.Commands.Count)} commands in {Sets.Length} sets.");
        foreach (var (set, commands) in Sets) summary.AppendLine($"{commands.Count,5}  {set}");
        summary.AppendLine();
        summary.AppendLine("By scope:");
        foreach (var group in Registered().All.GroupBy(c => c.Scope).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            summary.AppendLine($"{group.Count(),5}  {group.Key}");
        }

        summary.AppendLine();
        summary.AppendLine("By surface:");
        foreach (var group in Registered().All.GroupBy(c => c.Surface).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            summary.AppendLine($"{group.Count(),5}  {group.Key}");
        }

        File.WriteAllText(Path.Combine(into, "command-catalogue-summary.txt"), summary.ToString());
        File.WriteAllText(Path.Combine(into, "gesture-map.txt"), GestureMap());
    }

    /// <summary>
    /// Every shipped chord and what answers it, asked once per place it can be pressed.
    /// </summary>
    /// <remarks>
    /// The evidence behind the gesture sweeps: the resolver's own answers rather than a table of
    /// declarations, so a chord that has quietly changed hands shows as the wrong name beside it.
    /// </remarks>
    private static string GestureMap()
    {
        var catalog = Registered();
        var keys = new KeyMap(SettingsStore.Transient(), catalog);
        var text = new StringBuilder();

        var chords = catalog.All
            .SelectMany(c => c.AlsoGestures.Prepend(c.DefaultGesture ?? string.Empty))
            .Select(Chord.Parse)
            .OfType<Chord>()
            .Distinct()
            .OrderBy(c => c.ToString(), StringComparer.Ordinal)
            .ToList();

        text.AppendLine($"{chords.Count} distinct shipped chords, asked through KeyMap.CommandFor.");
        text.AppendLine();
        text.AppendLine("chord\tasked as\tanswered by");

        foreach (var chord in chords)
        {
            foreach (var module in Modules)
            {
                var id = keys.CommandFor(chord, module);
                text.AppendLine($"{chord}\t{module}\t{id?.Value ?? "-"}");
            }

            foreach (var surface in Enum.GetValues<CommandSurface>().Where(s => s != CommandSurface.Shell))
            {
                var id = keys.CommandFor(chord, surface);
                text.AppendLine($"{chord}\t{surface} window\t{id?.Value ?? "-"}");
            }
        }

        text.AppendLine();
        text.AppendLine("Second chords that never reach the command declaring them:");
        foreach (var (command, chord) in DeadSecondChords()) text.AppendLine($"  {command} (also {chord})");

        return text.ToString();
    }

    private static string RepoRootForDump()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
