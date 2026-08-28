using Mailbox.Core.Commands;
using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

file static class Catalogue
{
    /// <summary>
    /// Every command the application registers, which is what a ribbon's items resolve against.
    /// </summary>
    /// <remarks>
    /// Built from the same list <c>App</c> registers rather than a hand-picked few, because a
    /// layout that places a Contact-window command resolves it against the whole catalogue and
    /// not against the module's own commands.
    /// </remarks>
    internal static CommandCatalog All()
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
}

/// <summary>
/// The audit's ribbon lane: what the bar must hold, and what it must do as it narrows.
/// </summary>
/// <remarks>
/// <see cref="AuditInventoryTests"/> dumps four layouts — mail, calendar, people and compose —
/// because those are the four the Phase 0 inventory quoted. The application builds eleven: the
/// seven modules that have a rail icon plus the three item windows and the meeting variant of the
/// appointment one, and a tab that only exists in the four is a tab nobody photographs. So this
/// file's dump covers every layout the tree can build, and the checks below hold for all of them.
/// <code>
/// MAILBOX_RIBBON_DUMP=artifacts/audit/phase2/ribbon dotnet test tests/Mailbox.Tests \
///     --filter AuditRibbonTests
/// </code>
/// </remarks>
public class AuditRibbonTests
{
    /// <summary>Every layout the application can put on a bar, named as the pose list names it.</summary>
    /// <remarks>
    /// Built from the layout classes rather than from <see cref="DefaultRibbonLayouts.For"/>,
    /// which answers an empty layout for four of the seven modules and is used by nothing but a
    /// test — the shell reaches Tasks, Notes, Journal and Feeds through their own builders.
    /// </remarks>
    public static readonly (string Name, RibbonLayout Layout)[] AllLayouts =
    [
        ("mail", DefaultRibbonLayouts.Mail),
        ("calendar", DefaultRibbonLayouts.Calendar),
        ("people", DefaultRibbonLayouts.People),
        ("tasks", TasksRibbonLayout.Build()),
        ("notes", NotesRibbonLayout.Build()),
        ("journal", JournalRibbonLayout.Build()),
        ("feeds", FeedsRibbonLayout.Build()),
        ("compose", DefaultRibbonLayouts.Compose),
        ("message", MessageRibbonLayout.Layout),
        ("contact", ContactRibbonLayout.Contact),
        ("appointment", AppointmentRibbonLayout.Appointment),
        ("meeting", AppointmentRibbonLayout.Meeting),
    ];

    // ------------------------------------------------------------------------------------
    // The inventory
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Writes tabs, groups, placed commands and both renderings, for every layout, when asked.
    /// </summary>
    [Fact]
    public void DumpEveryRibbonLayoutOnRequest()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_RIBBON_DUMP") is not { Length: > 0 } asked)
        {
            return;
        }

        var dir = Path.IsPathRooted(asked) ? asked : Path.Combine(RepoRoot(), asked);
        Directory.CreateDirectory(dir);

        var rows = new List<string>
        {
            "layout\tmode\ttab\ttab_label\ttab_keytip\tclassic_only\tbackstage\tgroup\tgroup_id\tgroup_keytip\tcollapse\titems\tcommands",
        };
        var tabRows = new List<string> { "layout\ttab\tlabel\tkeytip\tclassic_only\tbackstage\tcontextual\tgroups\tsimplified_items" };
        var summary = new List<string>();

        foreach (var (name, layout) in AllLayouts)
        {
            var groups = 0;
            var items = 0;

            foreach (var tab in layout.Tabs)
            {
                var rowItems = layout.SimplifiedRows.TryGetValue(tab.Id, out var row)
                    ? row.Count(i => !i.IsSentinel)
                    : 0;

                tabRows.Add($"{name}\t{tab.Id}\t{tab.Label}\t{tab.KeyTip}\t{tab.ClassicOnly}\t{tab.IsBackstage}"
                            + $"\t{tab.ContextualGroup ?? "-"}\t{tab.Groups.Count}\t{rowItems}");

                foreach (var group in tab.Groups)
                {
                    groups++;
                    var placed = group.Items.Where(i => !i.IsSentinel).Select(i => i.Command.Value).ToArray();
                    items += placed.Length;
                    rows.Add($"{name}\tclassic\t{tab.Id}\t{tab.Label}\t{tab.KeyTip}\t{tab.ClassicOnly}\t{tab.IsBackstage}"
                             + $"\t{group.Label}\t{group.Id}\t{group.KeyTip}\t{group.CollapsePriority}\t{placed.Length}"
                             + $"\t{string.Join(" ", placed)}");
                }
            }

            foreach (var (tabId, row) in layout.SimplifiedRows.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var placed = row.Where(i => !i.IsSentinel).Select(i => i.Command.Value).ToArray();
                rows.Add($"{name}\tsimplified\t{tabId}\t{layout.FindTab(tabId)?.Label ?? "?"}\t\t\t"
                         + $"\t(row)\t(row)\t\t\t{placed.Length}\t{string.Join(" ", placed)}");
            }

            summary.Add($"{name,-12} {layout.Tabs.Count,3} tabs  {groups,4} groups  {items,5} placed  "
                        + $"{layout.SimplifiedRows.Count,3} simplified rows");
        }

        File.WriteAllLines(Path.Combine(dir, "all-layouts.tsv"), rows);
        File.WriteAllLines(Path.Combine(dir, "all-tabs.tsv"), tabRows);
        File.WriteAllLines(Path.Combine(dir, "all-summary.txt"), summary);

        Assert.True(rows.Count > 100);
    }

    // ------------------------------------------------------------------------------------
    // What every layout has to hold
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Every tab of every layout is reachable by Alt.
    /// </summary>
    /// <remarks>
    /// A tab with no KeyTip draws no badge, so the whole tab — and every command on it — drops
    /// out of the keyboard entirely, which is invisible until somebody presses Alt.
    /// </remarks>
    [Fact]
    public void EveryTabOfEveryLayoutCarriesAKeyTip()
    {
        var missing = AllLayouts
            .SelectMany(l => l.Layout.Tabs.Select(t => (l.Name, Tab: t)))
            .Where(x => string.IsNullOrEmpty(x.Tab.KeyTip))
            .Select(x => $"{x.Name}/{x.Tab.Id}")
            .ToList();

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// No layout's tab strip claims a letter twice, and no tab's commands do either.
    /// </summary>
    /// <remarks>
    /// <see cref="RibbonLayout.FindKeyTipConflicts"/> is the rule; this runs it over every layout
    /// rather than the three a keyboard test happened to name, and it catches the prefix case as
    /// well — a tip that is a strict prefix of another fires first, every time.
    /// </remarks>
    [Fact]
    public void NoLayoutHasAKeyTipConflict()
    {
        var catalog = Catalogue.All();

        var conflicts = AllLayouts
            .SelectMany(l => l.Layout.FindKeyTipConflicts(catalog))
            .ToList();

        Assert.True(conflicts.Count == 0, string.Join("\n", conflicts));
    }

    /// <summary>
    /// The layouts the shell puts on a bar that Alt can traverse.
    /// </summary>
    /// <remarks>
    /// The compose, message, contact and appointment windows carry ribbons of their own and no
    /// KeyTip session at all — nothing in any of them calls <c>KeyTipSession.Begin</c>, so Alt
    /// does nothing there. Rules about what Alt can reach are therefore asserted over the seven
    /// the shell draws.
    /// </remarks>
    private static IEnumerable<(string Name, RibbonLayout Layout)> ShellLayouts
        => AllLayouts.Where(l => l.Name is "mail" or "calendar" or "people" or "tasks"
            or "notes" or "journal" or "feeds");

    /// <summary>
    /// A group that can collapse to a popup button carries the KeyTip that button needs.
    /// </summary>
    /// <remarks>
    /// A collapsed group draws none of its commands, so they contribute no control for a badge —
    /// the group's own badge is the only way in, and a group without one takes every command it
    /// holds off the keyboard at narrow widths.
    /// <para>
    /// Over every layout, not only the seven the shell draws. Forty-eight groups across the
    /// compose, message and contact-window bars used to declare none, on the reasoning that those
    /// windows run no Alt traversal to be locked out of — but the compose Message tab is appended
    /// to the shell's own strip while a reply grows inline, where Alt does work, so the gap was
    /// live rather than dormant. Thirty-seven declarations closed all forty-eight (Format Text and
    /// Review are one pair of tabs shared by the compose and contact windows), and this now holds
    /// the whole tree so a forty-ninth cannot arrive silently.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCollapsibleGroupCarriesAKeyTip()
    {
        var missing = AllLayouts
            .SelectMany(l => l.Layout.Tabs.SelectMany(t => t.Groups.Select(g => (l.Name, t.Id, Group: g))))
            .Where(x => x.Group.Items.Count > 0 && string.IsNullOrEmpty(x.Group.KeyTip))
            .Select(x => $"{x.Name}/{x.Id}/{x.Group.Id}")
            .ToList();

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// Every command a tab draws can be reached by Alt.
    /// </summary>
    /// <remarks>
    /// A command placed on the bar with no KeyTip draws a button and no badge: it is on the
    /// ribbon and off the keyboard, which is invisible until somebody presses Alt and looks for
    /// it. Proven against the running bar too — the badge count each pose reported matched this
    /// number on every tab of every module, in both layouts.
    /// </remarks>
    [Fact]
    public void EveryCommandPlacedOnAShellTabCarriesAKeyTip()
    {
        var catalog = Catalogue.All();
        var missing = new List<string>();

        foreach (var (name, layout) in ShellLayouts)
        {
            foreach (var tab in layout.Tabs)
            {
                var placed = tab.Groups
                    .SelectMany(g => g.Items)
                    .Concat(layout.SimplifiedRows.TryGetValue(tab.Id, out var row) ? row : [])
                    .Where(i => !i.IsSentinel)
                    .Select(i => i.Command)
                    .Distinct();

                missing.AddRange(placed
                    .Where(id => catalog.TryGet(id, out var c) && string.IsNullOrEmpty(c.KeyTip))
                    .Select(id => $"{name}/{tab.Id}: {id.Value}"));
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// Every command a layout places exists in the catalogue.
    /// </summary>
    /// <remarks>
    /// A ribbon item whose id nothing registers draws as a blank button with no label, no icon
    /// and no tooltip — which reads as a rendering fault rather than as a typo in a layout.
    /// </remarks>
    [Fact]
    public void EveryPlacedCommandIsInTheCatalogue()
    {
        var catalog = Catalogue.All();

        var unknown = AllLayouts
            .SelectMany(l => l.Layout.PlacedCommands.Select(id => (l.Name, Id: id)))
            .Where(x => x.Id != RibbonItem.SeparatorId && !catalog.TryGet(x.Id, out _))
            .Select(x => $"{x.Name}: {x.Id.Value}")
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0, string.Join("\n", unknown));
    }

    /// <summary>
    /// A tab the Simplified bar carries has a row to carry, and one it does not carry has none.
    /// </summary>
    /// <remarks>
    /// The two halves are separate documents, so they can disagree: a tab with groups and no row
    /// draws an empty bar in the mode a first run actually shows, and a row for a
    /// <see cref="RibbonTab.ClassicOnly"/> tab describes a bar nobody can reach.
    /// </remarks>
    [Fact]
    public void EveryTabWithGroupsHasASimplifiedRowUnlessItIsClassicOnly()
    {
        var wrong = new List<string>();

        foreach (var (name, layout) in AllLayouts)
        {
            foreach (var tab in layout.Tabs)
            {
                if (tab.IsBackstage) continue;

                var hasRow = layout.SimplifiedRows.TryGetValue(tab.Id, out var row)
                             && row.Any(i => !i.IsSentinel);

                if (tab.ClassicOnly)
                {
                    if (hasRow) wrong.Add($"{name}/{tab.Id}: classic-only, yet the Simplified bar has a row for it");
                    continue;
                }

                if (tab.Groups.Count > 0 && !hasRow)
                {
                    wrong.Add($"{name}/{tab.Id}: {tab.Groups.Count} classic groups and no Simplified row");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ------------------------------------------------------------------------------------
    // Collapse
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Every tab of every layout sheds its groups in the order its own document declares.
    /// </summary>
    /// <remarks>
    /// <c>RibbonCollapseTests</c> pins the ladder against invented groups. What this adds is the
    /// shipped priorities, on every tab the application can draw: at any width, a group of
    /// higher <see cref="RibbonGroup.CollapsePriority"/> is at least as collapsed as one below
    /// it — which is the whole of what "gives way first, and completely" means once you stop
    /// counting steps — and narrowing never un-collapses anything.
    /// </remarks>
    [Fact]
    public void EveryTabShedsItsGroupsInTheOrderItDeclares()
    {
        var wrong = new List<string>();

        foreach (var (name, layout) in AllLayouts)
        {
            foreach (var tab in layout.Tabs)
            {
                if (tab.Groups.Count < 2) continue;

                var groups = tab.Groups
                    .Select(g => new RibbonGroupWidth(g.Id, g.CollapsePriority, 120, 60, 40))
                    .ToList();

                var full = groups.Count * 120d;
                RibbonGroupVariant[]? previous = null;

                // Every width from "everything fits" down to "nothing can give way any further",
                // a pixel at a time through the region where the answer changes.
                for (var available = full + 10; available > 0; available -= 5)
                {
                    var chosen = RibbonCollapsePolicy.Choose(groups, available);

                    for (var i = 0; i < groups.Count; i++)
                    {
                        for (var j = 0; j < groups.Count; j++)
                        {
                            if (groups[i].CollapsePriority <= groups[j].CollapsePriority) continue;
                            if (chosen[i] >= chosen[j]) continue;

                            wrong.Add($"{name}/{tab.Id} at {available:0}: {groups[i].GroupId} "
                                      + $"(priority {groups[i].CollapsePriority}) is {chosen[i]} while "
                                      + $"{groups[j].GroupId} (priority {groups[j].CollapsePriority}) "
                                      + $"is already {chosen[j]}");
                        }

                        if (previous is not null && chosen[i] < previous[i])
                        {
                            wrong.Add($"{name}/{tab.Id} at {available:0}: {groups[i].GroupId} came "
                                      + $"back from {previous[i]} to {chosen[i]} as the bar narrowed");
                        }
                    }

                    previous = chosen;
                }

                // And the end of the ladder is reached rather than clipped part-way.
                Assert.All(RibbonCollapsePolicy.Choose(groups, 1),
                    v => Assert.Equal(RibbonGroupVariant.Popup, v));
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(20)));
    }

    /// <summary>
    /// Every tab of every layout has a collapse order a reader can predict.
    /// </summary>
    /// <remarks>
    /// Two groups on one tab sharing a priority makes which of them gives way first an accident
    /// of declaration order — the policy breaks the tie by position, so it is deterministic, but
    /// it is not what the layout document says. <c>RibbonLayoutTests</c> asserts this for the
    /// mail tabs; every other layout was unchecked.
    /// </remarks>
    [Fact]
    public void NoTabDeclaresTwoGroupsAtTheSameCollapsePriority()
    {
        var clashes = new List<string>();

        foreach (var (name, layout) in AllLayouts)
        {
            foreach (var tab in layout.Tabs)
            {
                var duplicates = tab.Groups
                    .GroupBy(g => g.CollapsePriority)
                    .Where(g => g.Count() > 1);

                clashes.AddRange(duplicates.Select(g =>
                    $"{name}/{tab.Id}: priority {g.Key} claimed by {string.Join(", ", g.Select(x => x.Id))}"));
            }
        }

        Assert.True(clashes.Count == 0, string.Join("\n", clashes));
    }

    // ------------------------------------------------------------------------------------
    // Help
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Help is the application's tab, not a module's: wherever it appears it holds the same
    /// thing, in both layouts.
    /// </summary>
    /// <remarks>
    /// Five modules and three item windows declared a Help tab with <c>Groups = []</c> and no
    /// Simplified row, so the tab sat on the strip and drew nothing at all in either layout —
    /// measured on the running bar as <c>body=(none)</c> and <c>body=(no row)</c>, and as a
    /// level-2 Alt traversal with zero badges. Mail's copy was the only filled one.
    /// </remarks>
    [Fact]
    public void EveryHelpTabHoldsTheSameThingInBothLayouts()
    {
        var wrong = new List<string>();

        // Mail's is the canonical copy — the one that has always been filled and the one the
        // captures were transcribed from — so the others are held to it rather than to a
        // number written down here.
        var canonicalGroups = DefaultRibbonLayouts.Mail.FindTab("help")!.Groups.Select(g => g.Id).ToList();
        var canonicalRow = DefaultRibbonLayouts.Mail.SimplifiedRows["help"].Count(i => !i.IsSentinel);

        foreach (var (name, layout) in AllLayouts)
        {
            if (layout.FindTab("help") is not { } help)
            {
                // Feeds has no Help tab at all, which is its own question — recorded, not
                // asserted here, so this test stays about the tabs that do exist.
                continue;
            }

            var groups = help.Groups.Select(g => g.Id).ToList();
            if (!groups.SequenceEqual(canonicalGroups))
            {
                wrong.Add($"{name}: the Help tab holds [{string.Join(", ", groups)}]");
            }

            var row = layout.SimplifiedRows.TryGetValue("help", out var r)
                ? r.Count(i => !i.IsSentinel)
                : 0;
            if (row != canonicalRow) wrong.Add($"{name}: the Help row has {row} entries, not {canonicalRow}");
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ------------------------------------------------------------------------------------
    // The Simplified row's shipped label order
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The mail Home row sheds its labels in the order the reference sheds them.
    /// </summary>
    /// <remarks>
    /// <c>SimplifiedRowFitTests</c> pins the rule against invented entries; this pins the ranks
    /// the shipped row actually declares, which is the half that decides what a reader sees. The
    /// reference at 1447 has taken the words off Reply, Reply All, Forward, Categorize and
    /// Follow Up while Unread/Read and Send/Receive All Folders — both further right — still
    /// read as words, and New Email keeps its label longest of all. The running bar was measured
    /// doing exactly that at 1600.
    /// </remarks>
    [Fact]
    public void TheMailHomeRowShedsItsLabelsInTheReferenceOrder()
    {
        var row = DefaultRibbonLayouts.Mail.SimplifiedRows["home"];

        int RankOf(CommandId id)
            => row.Where(i => i.Command == id).Select(i => i.LabelRank).First();

        foreach (var first in new[]
                 {
                     MailCommands.Reply.Id, MailCommands.ReplyAll.Id, MailCommands.Forward.Id,
                     MailCommands.Categorize.Id, MailCommands.FollowUp.Id,
                 })
        {
            Assert.Equal(RibbonItem.SheddableLabelRank, RankOf(first));
        }

        foreach (var later in new[] { MailCommands.Unread.Id, MailCommands.SendReceiveAll.Id })
        {
            Assert.True(RankOf(later) > RibbonItem.SheddableLabelRank,
                $"{later.Value} sheds its label with the Respond and Tags words");
        }

        // New Email is the row's first labelled entry, which is what the renderer promotes to
        // the primary rank — the label that goes last of all.
        var firstLabelled = row.First(i => !i.IsSentinel && i.ShowLabel
            && i.Kind is not (RibbonItemKind.TextBox or RibbonItemKind.ComboBox));
        Assert.Equal(MailCommands.NewEmail.Id, firstLabelled.Command);
    }

    // ------------------------------------------------------------------------------------
    // Contextual tabs
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// A contextual set names tabs that exist, and no layout leaves a contextual tab on screen
    /// with no set to switch it.
    /// </summary>
    /// <remarks>
    /// The mechanism is proven in <see cref="ContextualTabTests"/> against invented tabs. What
    /// this adds is the shipped layouts: a tab that declares a set nothing ever activates would
    /// be a tab no reader could reach.
    /// </remarks>
    [Fact]
    public void NoShippedLayoutDeclaresAContextualTabNothingCanActivate()
    {
        var orphans = AllLayouts
            .SelectMany(l => l.Layout.Tabs.Where(t => t.IsContextual).Select(t => $"{l.Name}/{t.Id}: {t.ContextualGroup}"))
            .ToList();

        // Every contextual tab shipped must have a host that switches its set on. None ships
        // today; the day one does, this test names it and the host has to be pointed at.
        Assert.True(orphans.Count == 0, string.Join("\n", orphans));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
