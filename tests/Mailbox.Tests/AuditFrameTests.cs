using System.Text.RegularExpressions;
using Mailbox.Core.Commands;
using Mailbox.Core.Keyboard;
using Mailbox.Core.Settings;
using Mailbox.Theming.Themes;

namespace Mailbox.Tests;

/// <summary>
/// The audit's frame, rail and theme sweeps, promoted so the classes of fault they caught stay
/// caught: a module in the enum that never reaches the rail, an accelerator that answers to the
/// wrong command, a caption pose that photographs a button nobody asked for, a caption state
/// token that no theme differs on, and a pose list that has gone stale against the code it was
/// generated from.
/// </summary>
/// <remarks>
/// Source-reading rather than reflection, for the reason <see cref="AuditChromeSweepTests"/>
/// gives: the test project does not reference <c>Mailbox.App</c>, and the rail, the caption
/// buttons and the pose lists all live outside the assemblies it does reference. What the code
/// says is the question these rules are about anyway.
/// </remarks>
public class AuditFrameTests
{
    /// <summary>
    /// The two modules the enum names and the rail deliberately does not carry. They are the
    /// rest of the navigation pane rather than modules of their own, are recorded as an absence
    /// in the user-requests queue, and <c>SwitchModule</c> has a guard that says so. If either
    /// ever grows a workspace this list is what has to shrink.
    /// </summary>
    private static readonly MailboxModule[] NotModulesHere =
    [
        MailboxModule.Folders,
        MailboxModule.Shortcuts,
    ];

    // ---- The rail ----------------------------------------------------------------------------

    /// <summary>
    /// The rail carries exactly the modules the enum declares, less the two that are not modules
    /// here — in the enum's own order.
    /// </summary>
    /// <remarks>
    /// A module added to <c>MailboxModule</c> and not to <c>ShellViewModel.Modules</c> is a
    /// module with an accelerator, a command and no way to reach it with the pointer; one added
    /// to the rail without a workspace lands on <c>SwitchModule</c>'s guard and writes a status
    /// line instead of switching. Neither is visible from either file alone.
    /// </remarks>
    [Fact]
    public void TheRailCarriesEveryModuleTheEnumDeclaresExceptTheTwoThatAreNotModulesHere()
    {
        var expected = Enum.GetValues<MailboxModule>().Except(NotModulesHere).ToArray();
        var rail = RailModules();

        Assert.Equal(expected, rail);
    }

    /// <summary>
    /// Every module on the rail is reachable by the accelerator its enum value spells, through
    /// the real key map, from every module — Ctrl+5 has to leave Feeds for Notes as readily as
    /// it leaves Mail.
    /// </summary>
    [Fact]
    public void EveryRailModuleAnswersToTheAcceleratorItsEnumValueSpells()
    {
        var catalog = AuditWiringSweepTests.Registered();
        var keys = new KeyMap(SettingsStore.Transient(), catalog);
        var wrong = new List<string>();

        foreach (var module in RailModules())
        {
            var chord = Chord.Parse($"Ctrl+{(int)module}");
            Assert.NotNull(chord);

            foreach (var from in RailModules())
            {
                var answered = keys.CommandFor(chord!, from);
                if (answered is not { } id || ViewCommands.ModuleOf(id) != module)
                {
                    wrong.Add($"Ctrl+{(int)module} in {from} answers {answered?.Value ?? "nothing"}, "
                              + $"which is not {module}.");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// The two accelerators the reference gives to the Folder List and to Shortcuts are bound to
    /// nothing, in every module. Recorded rather than asserted away: the day either module is
    /// built this test is what says the key is free to take.
    /// </summary>
    [Fact]
    public void TheTwoAcceleratorsWithNoModuleBehindThemAnswerNothing()
    {
        var catalog = AuditWiringSweepTests.Registered();
        var keys = new KeyMap(SettingsStore.Transient(), catalog);

        foreach (var module in NotModulesHere)
        {
            var chord = Chord.Parse($"Ctrl+{(int)module}");
            Assert.NotNull(chord);

            foreach (var from in RailModules())
            {
                Assert.Null(keys.CommandFor(chord!, from));
            }
        }
    }

    /// <summary>
    /// Every rail icon is a glyph the icon font actually has.
    /// </summary>
    /// <remarks>
    /// The rail asks for its glyph through <c>IconGlyphs.GetOrEmpty</c>, which answers an empty
    /// string for a name the set does not carry — so a misspelt icon name is not an error, it is
    /// a blank 40×40 button that still switches module. Nothing else in the tree would say so.
    /// </remarks>
    [Fact]
    public void EveryRailIconIsAGlyphTheSetActuallyHas()
    {
        var source = Read("src/Mailbox.App/ViewModels/ShellViewModel.cs");
        var block = Regex.Match(
            source,
            @"public ModuleTab\[\] Modules \{ get; \} =\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);

        var named = Regex.Matches(block.Groups["body"].Value, @"new\(MailboxModule\.(\w+),\s*""(\w+)""")
            .Select(m => (Module: m.Groups[1].Value, Icon: m.Groups[2].Value))
            .ToList();

        Assert.Equal(RailModules().Length, named.Count);

        foreach (var (module, icon) in named)
        {
            Assert.False(
                string.IsNullOrEmpty(Mailbox.Theming.Icons.IconGlyphs.GetOrEmpty(icon, 24)),
                $"the rail's {module} icon asks for “{icon}”, which the set does not have — it draws nothing.");
        }
    }

    // ---- The caption buttons -----------------------------------------------------------------

    /// <summary>
    /// The caption's pose resolves each button by its own field, not by a class two of them
    /// share.
    /// </summary>
    /// <remarks>
    /// Minimize and maximize both wear <c>caption</c>, so a lookup that asks for the class and
    /// takes the first match answers minimize to both — which is what <c>MAILBOX_HOVER=maximize</c>
    /// did, at a size and shape close enough that two captures of the same button read as two
    /// states of two buttons. Three distinct fields is the shape that cannot do that.
    /// </remarks>
    [Fact]
    public void TheCaptionPoseResolvesEachButtonSeparately()
    {
        var source = Read("src/Mailbox.App/Views/CaptionButtons.cs");
        var arms = Regex.Matches(source, @"""(minimize|maximize|restore|close)""[^=]*=>\s*(_\w+)");

        var targets = arms
            .Select(m => (Name: m.Groups[1].Value, Field: m.Groups[2].Value))
            .ToList();

        Assert.Equal(3, targets.Select(t => t.Field).Distinct().Count());
        Assert.Equal("_minimize", targets.Single(t => t.Name == "minimize").Field);
        Assert.Equal("_maximize", targets.Single(t => t.Name == "maximize").Field);
        Assert.Equal("_close", targets.Single(t => t.Name == "close").Field);
    }

    /// <summary>
    /// The close button's red, its held red and its glyph are the same in all four built-ins.
    /// </summary>
    /// <remarks>
    /// The one caption colour the reference does not vary by theme, measured off the owner's own
    /// capture at <c>#E81123</c>. Every caption in the application — title bar, dialog and
    /// system dialog — takes these three rather than carrying its own, so a theme that redefined
    /// them would break three surfaces at once.
    /// </remarks>
    [Fact]
    public void TheCloseButtonsRedIsOneColourInEveryBuiltIn()
    {
        foreach (var token in new[]
                 {
                     "titlebar.caption.close",
                     "titlebar.caption.close.pressed",
                     "titlebar.caption.close.text",
                 })
        {
            var values = OfficeThemes.All
                .Select(id => OfficeThemes.Build(id).Resolve().GetString(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(values.Count == 1, $"{token} is {string.Join(", ", values)} across the built-ins.");
        }
    }

    /// <summary>
    /// Every caption's hover and held washes are defined, differ from each other, and differ from
    /// the general button states — three surfaces, four themes, no exceptions.
    /// </summary>
    /// <remarks>
    /// A held caption button that matched <c>state.pressed</c> would be the generic button wash
    /// showing through, which is what the pressed state turned out to be: the tokens were right
    /// and nothing painted with them. Equality here is the signature of that fault at the data
    /// end, and it holds whichever end the repair happens at.
    /// </remarks>
    [Fact]
    public void EveryCaptionHasItsOwnHoverAndHeldWashInEveryBuiltIn()
    {
        var faults = new List<string>();

        foreach (var id in OfficeThemes.All)
        {
            var t = OfficeThemes.Build(id).Resolve();

            foreach (var family in new[] { "titlebar.caption", "dialog.caption", "systemdialog.caption" })
            {
                var hover = t.GetString($"{family}.hover");
                var pressed = t.GetString($"{family}.pressed");

                if (string.IsNullOrWhiteSpace(hover)) faults.Add($"{id}: {family}.hover is unset.");
                if (string.IsNullOrWhiteSpace(pressed)) faults.Add($"{id}: {family}.pressed is unset.");
                if (string.Equals(hover, pressed, StringComparison.OrdinalIgnoreCase))
                {
                    faults.Add($"{id}: {family} wears {hover} both hovered and held.");
                }

                if (string.Equals(pressed, t.GetString("state.pressed"), StringComparison.OrdinalIgnoreCase))
                {
                    faults.Add($"{id}: {family}.pressed is the general button wash, not its own.");
                }
            }
        }

        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    /// <summary>
    /// The rail is a shade apart from the navigation pane beside it in every built-in, as the
    /// reference draws it — and neither is the title bar's colour.
    /// </summary>
    [Fact]
    public void TheRailIsItsOwnShadeInEveryBuiltIn()
    {
        foreach (var id in OfficeThemes.All)
        {
            var t = OfficeThemes.Build(id).Resolve();
            var rail = t.GetString("rail.background");

            Assert.False(string.IsNullOrWhiteSpace(rail), $"{id}: rail.background is unset.");
            Assert.NotEqual(t.GetString("nav.background"), rail);
        }
    }

    // ---- The pose lists ----------------------------------------------------------------------

    /// <summary>
    /// The generated pose lists still cover the code they were generated from.
    /// </summary>
    /// <remarks>
    /// A hand-kept list cannot notice what was added to the tree and not to it, and a generated
    /// one is only generated the last time somebody ran the generator. This is the difference
    /// between the two: a caption button, a module or a built-in theme added without a pose is a
    /// surface the capture batch will not photograph, and nothing else says so.
    /// </remarks>
    [Fact]
    public void TheFramePoseListsStillCoverWhatTheyWereGeneratedFrom()
    {
        var missing = new List<string>();

        var caption = Read("tools/poses/frame-caption.tsv");
        foreach (Match m in Regex.Matches(
                     Read("src/Mailbox.App/Views/CaptionButtons.cs"),
                     @"Build\(\s*\w+Glyph\(\),\s*""([A-Za-z]+)"""))
        {
            var button = m.Groups[1].Value.ToLowerInvariant();
            if (!caption.Contains($"MAILBOX_HOVER={button}", StringComparison.Ordinal))
            {
                missing.Add($"no hover pose for the {button} caption button.");
            }

            if (!caption.Contains($"MAILBOX_CAPTION=hold:{button}", StringComparison.Ordinal))
            {
                missing.Add($"no held pose for the {button} caption button.");
            }
        }

        var rail = Read("tools/poses/frame-rail.tsv");
        foreach (var module in Enum.GetValues<MailboxModule>())
        {
            if (!rail.Contains($"MAILBOX_KEY=Ctrl+{(int)module}", StringComparison.Ordinal))
            {
                missing.Add($"no accelerator pose for {module} (Ctrl+{(int)module}).");
            }
        }

        var themes = Read("tools/poses/frame-themes.tsv");
        foreach (var id in OfficeThemes.All)
        {
            if (!themes.Contains($"MAILBOX_THEME={id} ", StringComparison.Ordinal)
                && !themes.TrimEnd().EndsWith($"MAILBOX_THEME={id}", StringComparison.Ordinal)
                && !themes.Contains($"MAILBOX_THEME={id}\n", StringComparison.Ordinal))
            {
                missing.Add($"no startup pose for the {id} theme.");
            }

            if (!themes.Contains($"MAILBOX_THEME_SWITCH={id}", StringComparison.Ordinal))
            {
                missing.Add($"no live-switch pose into the {id} theme.");
            }
        }

        // Rule 4: the two state-dependent paints are repeated in every built-in but the daily
        // one, so a fault in Dark Gray can be told from a fault in the style.
        var cross = Read("tools/poses/frame-crosstheme.tsv");
        foreach (var id in OfficeThemes.All.Where(t => t != OfficeThemes.DarkGray))
        {
            if (!cross.Contains($"MAILBOX_THEME={id} MAILBOX_CAPTION=hold:", StringComparison.Ordinal))
            {
                missing.Add($"no held-caption pose in the {id} theme.");
            }

            if (!cross.Contains($"MAILBOX_THEME={id} MAILBOX_HOVER=rail:", StringComparison.Ordinal))
            {
                missing.Add($"no rail-hover pose in the {id} theme.");
            }
        }

        Assert.True(missing.Count == 0,
            "tools/poses/generate-frame.py has not been re-run:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// Every popup the frame owns has a door, and the door inventory knows about it.
    /// </summary>
    /// <remarks>
    /// A popup is not in the application's window list, so the in-process capture photographs
    /// the shell behind it and a run that reached nothing looks exactly like one that worked.
    /// The answer is a pose that measures the presenter instead, and this is what says one
    /// exists for both of the frame's own menus.
    /// </remarks>
    [Fact]
    public void TheFramesOwnPopupsBothHaveADoor()
    {
        var window = Read("src/Mailbox.App/Views/MainWindow.axaml.cs");
        var doors = Read("tools/poses/doors.tsv");

        foreach (var key in new[] { "allapps", "windowmenu" })
        {
            Assert.Contains($"case \"{key}\":", window, StringComparison.Ordinal);
            Assert.Contains($"MAILBOX_PEEK={key}", doors, StringComparison.Ordinal);
        }

        // And both are measured rather than photographed: a log line naming a popup's size is
        // the only evidence a capture cannot give.
        Assert.Contains("FlyoutProbe.Describe", window, StringComparison.Ordinal);
    }

    // ---- The reader --------------------------------------------------------------------------

    /// <summary>The modules <c>ShellViewModel</c> hands the rail, in the order it hands them.</summary>
    private static MailboxModule[] RailModules()
    {
        var source = Read("src/Mailbox.App/ViewModels/ShellViewModel.cs");
        var block = Regex.Match(
            source,
            @"public ModuleTab\[\] Modules \{ get; \} =\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);

        Assert.True(block.Success, "ShellViewModel's Modules array did not parse.");

        return [.. Regex.Matches(block.Groups["body"].Value, @"new\(MailboxModule\.(\w+),")
            .Select(m => Enum.Parse<MailboxModule>(m.Groups[1].Value))];
    }

    private static string Read(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
