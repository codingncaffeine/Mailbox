using System.Text.RegularExpressions;
using Avalonia.Media;
using Mailbox.Controls.Calendar;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// What the audit's peek sweep proved by running the application, kept as tests so the class
/// stays caught.
/// </summary>
/// <remarks>
/// The layout numbers already live in <see cref="PeekTests"/> and the to-do list's own rows in
/// <c>TaskBookTests</c>; nothing here repeats either. What is here is the half those two cannot
/// reach: the colour rule that separates the floating popup from the docked pane, the docked
/// pane's ink measured against the owner's own capture, and two prose faults the sweep found by
/// photographing surfaces rather than by reading them.
/// <para>
/// <b>How each was proved.</b> The colour rule was measured, not reasoned: the same pose
/// (<c>MAILBOX_PEEK=calendar</c>, then <c>=peoplepeek</c>) was captured in all four built-in
/// themes and the peek's own box — 286×330 at (63,141), and 249×330 for People's — compared
/// pixel for pixel across them. Every comparison came back with zero differing pixels, which is
/// the popup keeping the desktop's colours whatever the shell is wearing. The docked pane in the
/// same four captures measured #666666, #FDFEFD, #272727 and #FDFEFD, which is the opposite
/// claim and the one the reference makes for a pane that is part of the window.
/// </para>
/// </remarks>
public class AuditPeekTests
{
    private static string View(string file)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Mailbox.App", "Views", file));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "The repository root was not found above the test binary.");
    }

    // ---- The colour rule: a popup is not a pane -----------------------------------------------

    /// <summary>
    /// A floating peek is a desktop popup and keeps the desktop's colours in every theme.
    /// </summary>
    /// <remarks>
    /// Proved with pixels first: the calendar peek's 286×330 box and the People peek's 249×330
    /// box are byte-identical across Colorful, White, Dark Gray and Black. That can only hold
    /// while every token behind them holds the same value in all four, which is what this asserts
    /// — one theme quietly restating <c>peek.pop.background</c> would break the photograph and
    /// nothing else would notice.
    /// </remarks>
    [Fact]
    public void EveryFloatingPeekTokenHasOneValueAcrossAllFourThemes()
    {
        var themes = OfficeThemes.All.ToList();
        Assert.Equal(4, themes.Count);

        foreach (var key in TokenKeys.Peek.Floating)
        {
            // The colour rather than the text it is written as: a theme is free to say
            // "{palette.neutral.white}" where another says "#FFFFFF", and the photograph cannot
            // tell them apart either.
            var values = themes
                .Select(id => (Theme: id, Colour: OfficeThemes.Build(id).Resolve().GetColor(key)))
                .ToList();

            var first = values[0];
            foreach (var one in values)
            {
                Assert.True(
                    first.Colour == one.Colour,
                    $"'{key}' is {first.Colour} in {first.Theme} and {one.Colour} in {one.Theme}. The floating "
                    + "peek is a desktop popup and looks the same in every theme; a theme that restates one of "
                    + "its tokens makes it look like the shell instead.");
            }
        }
    }

    /// <summary>
    /// The docked pane is part of the window, so it does the opposite: it follows the theme.
    /// </summary>
    /// <remarks>
    /// The pair matters more than either half. Without this a theme could satisfy the test above
    /// by making the whole family one colour everywhere, which would draw the To-Do Bar's calendar
    /// section in the popup's light grey down the edge of a black window.
    /// </remarks>
    [Fact]
    public void TheDockedPanesGroundFollowsTheThemeWhereThePopupsDoesNot()
    {
        var grounds = OfficeThemes.All
            .ToDictionary(id => id, id => OfficeThemes.Build(id).Resolve().GetColor(TokenKeys.Peek.Background));

        Assert.True(
            grounds.Values.Distinct().Count() > 1,
            "Every theme gives the docked peek the same ground, so it is not following the theme: "
            + string.Join(", ", grounds.Select(g => $"{g.Key}={g.Value}")));

        // And it is genuinely dark where the shell is dark: measured #666666 in Dark Gray and
        // #262626 in Black, against #F0F0F0 for the popup in both.
        var popup = OfficeThemes.Build(OfficeThemes.DarkGray).Resolve().GetColor(TokenKeys.Peek.PopBackground);
        Assert.NotEqual(popup, grounds[OfficeThemes.DarkGray]);
        Assert.NotEqual(popup, grounds[OfficeThemes.Black]);
    }

    /// <summary>
    /// The docked pane's ink, measured off the owner's own capture of it.
    /// </summary>
    /// <remarks>
    /// Read out of <c>calendar docked.png</c> at 100% with a modal-colour read of a flat region,
    /// and read back out of this application's own capture of the same pane at the same points:
    /// the ground #666666, the divider down its left edge #444444, and the rule under the grid
    /// drawn as two rows — #444444 over #000000 — which is what gives it its groove. Today's cell
    /// measured #0472C7 in the capture against #0072C6 declared here, the difference being the
    /// screenshot's own anti-aliasing.
    /// </remarks>
    [Fact]
    public void TheDockedPanesInkIsTheOneTheReferenceDraws()
    {
        var dark = OfficeThemes.Build(OfficeThemes.DarkGray).Resolve();

        Assert.Equal(Color.Parse("#666666"), dark.GetColor(TokenKeys.Peek.Background));
        Assert.Equal(Color.Parse("#444444"), dark.GetColor(TokenKeys.Peek.Divider));
        Assert.Equal(Color.Parse("#444444"), dark.GetColor(TokenKeys.Peek.RuleSoft));
        Assert.Equal(Color.Parse("#000000"), dark.GetColor(TokenKeys.Peek.Rule));
        Assert.Equal(Color.Parse("#0072C6"), dark.GetColor(TokenKeys.Peek.Today));
    }

    /// <summary>
    /// The To-Do Bar's task section stands on the list's own grounds, which the reference's Dark
    /// Gray capture measures as a light row band under a dark group header.
    /// </summary>
    /// <remarks>
    /// Measured off <c>tasks.png</c>: the row ground and the "Type a new task" box both #D4D4D4,
    /// the Today band header #444444, the workspace behind them #666666. The same three points in
    /// this application's own To-Do Bar capture read #D4D4D4, #D4D4D4 and #444444 — so the bar is
    /// not a second idea about what a task list looks like, and this holds it to that.
    /// </remarks>
    [Fact]
    public void TheToDoBarsTaskSectionKeepsTheReferencesGrounds()
    {
        var dark = OfficeThemes.Build(OfficeThemes.DarkGray).Resolve();

        Assert.Equal(Color.Parse("#D4D4D4"), dark.GetColor(TokenKeys.List.RowBackground));
        Assert.Equal(Color.Parse("#444444"), dark.GetColor(TokenKeys.List.GroupHeaderBackground));

        // A pane whose calendar half is the chrome's ground and whose task half is the content's
        // is the whole of Dark Gray's inversion; asserting they differ is asserting the pane is
        // not one flat surface by accident.
        Assert.NotEqual(dark.GetColor(TokenKeys.Peek.Background), dark.GetColor(TokenKeys.List.RowBackground));
    }

    /// <summary>
    /// Every theme states the agenda's scrollbar, and states it so the thumb can be seen on the
    /// track it runs down.
    /// </summary>
    /// <remarks>
    /// The gutter it goes in was reserved from the first drawing of the peek and nothing was ever
    /// drawn in it, so a day with more appointments than fit was clipped without a mark: five on
    /// 11 August hid 98 pixels of themselves in the floating popup and 105 in the docked pane,
    /// measured by the run that found it. The thumb is the reference's own #C7C7C7, read off the
    /// scrollbar in <c>calendar.png</c>; what is held here is the pair, because a thumb the same
    /// colour as its track is a scrollbar nobody can see and the token test above would not
    /// notice.
    /// </remarks>
    [Fact]
    public void EveryThemeDrawsTheAgendasScrollbarWithAThumbThatShowsOnItsTrack()
    {
        foreach (var id in OfficeThemes.All)
        {
            var t = OfficeThemes.Build(id).Resolve();

            foreach (var (track, thumb) in new[]
                     {
                         (TokenKeys.Peek.Scroll, TokenKeys.Peek.ScrollThumb),
                         (TokenKeys.Peek.PopScroll, TokenKeys.Peek.PopScrollThumb),
                     })
            {
                Assert.True(t.Contains(track), $"{id} does not state {track}.");
                Assert.True(t.Contains(thumb), $"{id} does not state {thumb}.");
                Assert.True(
                    t.GetColor(track) != t.GetColor(thumb),
                    $"{id} draws {thumb} in the same colour as {track}, so the scrollbar is "
                    + "invisible and a clipped day still says nothing.");
            }
        }
    }

    // ---- The pane the dock host reserves ------------------------------------------------------

    /// <summary>
    /// The docked pane is 254 wide beside a 1px divider, which is what the To-Do Bar sets itself
    /// to and what the reference measures.
    /// </summary>
    /// <remarks>
    /// Confirmed against both pictures at 100%: in <c>calendar docked.png</c> the divider is one
    /// #444444 column at x=1180 with the pane's content running 1181–1434, and in this
    /// application's own capture the divider is at x=1016 with content 1017–1270. Both 254.
    /// </remarks>
    [Fact]
    public void TheDockedPaneIsTwoHundredAndFiftyFiveWideIncludingItsDivider()
    {
        Assert.Equal(254, PeekLayout.DockedWidth);
        Assert.Equal(1, PeekLayout.DividerWidth);

        // What the pane reserves, which is what the To-Do Bar's own Width is set from.
        Assert.Equal(255, PeekLayout.DockedWidth + PeekLayout.DividerWidth);
    }

    // ---- Two prose faults a photograph found ---------------------------------------------------

    /// <summary>
    /// The People peek spells its heading the way the sentence under it spells the same word.
    /// </summary>
    /// <remarks>
    /// The sweep photographed it reading <b>Favorites</b> over "Right-click a person to add them
    /// to your <b>favourites</b>." — two spellings twenty pixels apart in one picture. The heading
    /// came from the reference and the sentence had been rewritten; the rewrite changed the
    /// spelling and the heading did not follow.
    /// <para>
    /// This held them together in British while that was the answer. The answer is now American —
    /// the interface speaks the reference's English, which is what its cloned labels were always
    /// in — so the check is the same check with the spellings the other way round. What it is for
    /// has not changed: one surface, one spelling, and nothing else in the tree would notice it
    /// drifting back.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePeoplePeekSpellsItsHeadingTheWayItsOwnSentenceDoes()
    {
        // Comments stripped first: this file's own remarks quote the word it is looking for, and
        // so does the peek's, which explains why the heading changed.
        var source = string.Join(
            '\n',
            View("PeoplePeek.cs").Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var strings = Regex.Matches(source, "\"([^\"\\\\]*)\"")
            .Select(m => m.Groups[1].Value)
            .Where(s => s.Contains("favo", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(strings);

        var british = strings.Where(s => s.Contains("favourite", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(
            british.Count == 0,
            "The People peek writes " + string.Join(" and ", british.Select(s => $"“{s}”"))
            + ", but its heading and the People menu that fills it both write "
            + "“Favorites”. One surface, one spelling.");
    }

    /// <summary>
    /// The Reminders window's "Due in …" line takes its plural from the number it prints.
    /// </summary>
    /// <remarks>
    /// It used to say the number twice: the count was floored to at least one so that a reminder
    /// half a minute away did not read "0 minutes", while the plural was decided from the
    /// unfloored value — so it read <b>"Due in 1 minutes"</b> for everything under sixty seconds.
    /// The floor and the plural now come from one variable, and this holds them together.
    /// </remarks>
    [Fact]
    public void TheRemindersWindowTakesItsPluralFromTheNumberItPrints()
    {
        var source = View("RemindersWindow.cs");

        // The fault in one line: a Math.Max floor beside a plural test that reads the unfloored
        // value. Either alone is fine; the pair is the bug.
        var floored = source.Contains("Math.Max(1, (int)span.TotalMinutes)", StringComparison.Ordinal);
        var unflooredPlural = source.Contains("(int)span.TotalMinutes == 1", StringComparison.Ordinal);

        Assert.False(
            floored && unflooredPlural,
            "DueIn floors the count to one and then decides the plural from the unfloored value, "
            + "so anything under a minute reads “1 minutes”. Take both from one variable.");
    }
}
