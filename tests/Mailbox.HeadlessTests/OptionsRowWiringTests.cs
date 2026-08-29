using Mailbox.App.Options;

namespace Mailbox.HeadlessTests;

/// <summary>
/// A row on the Options pages either drives something or is a known promise.
/// </summary>
/// <remarks>
/// <see cref="OptionsPageRenderer"/> falls back to a row's own label when it declares no
/// <c>Key</c>, so every control persists <em>something</em> and none of them looks broken: the
/// tick comes back where the reader left it. Sixty-nine of them are read by no feature at all.
/// That is worse than a control that is plainly absent, because it reads as a promise — and the
/// Trust Center's lookalike-domain switch was in this class and actively misleading, drawing a
/// second silent copy of a switch that lived elsewhere, so turning it off left the warnings on.
/// <para>
/// Wiring sixty-nine features is a roadmap rather than a repair, so they are named here instead.
/// What this test buys is that the seventieth cannot join them quietly: a row added without a
/// key fails until somebody either gives it one or writes it down.
/// </para>
/// </remarks>
public class OptionsRowWiringTests
{
    /// <summary>
    /// How many rows persist under their own label today, per page. Recorded as counts rather
    /// than labels because the wording is the reference's and changes with it, while the count
    /// is what says whether the backlog is growing.
    /// </summary>
    private static readonly Dictionary<string, int> KnownLabelKeyedRows = new(StringComparer.Ordinal)
    {
        // Down one: the multiple-displays combo became the reference's pair of radios in the
        // Phase 12 capture comparison, and radios are counted by their group, not here.
        ["general"] = 4,
        // Up one, deliberately: "Show suggested replies" joined greyed — the AI exclusion's
        // greyed-rather-than-removed convention, drawn because the capture draws it.
        ["mail"] = 14,
        ["calendar"] = 8,
        ["tasks"] = 7,
        ["search"] = 4,
        ["language"] = 2,
        ["accessibility"] = 7,

        // Down one: "Prompt for confirmation before permanently deleting items" now gates the
        // four places that ask before a permanent delete.
        ["advanced"] = 5,

        // Down four: the automatic-download pair and the two message-authentication rows now
        // reach the reading pane. Each of the four was the behaviour unconditionally, which is
        // the actively deceptive shape the lookalike-domain row was fixed out of.
        ["trust"] = 2,
    };

    [Fact]
    public void NoPageGrowsANewRowThatDrivesNothing()
    {
        var grown = new List<string>();
        var total = 0;

        foreach (var page in OptionsPages.All)
        {
            var unkeyed = UnwiredRows(page);
            total += unkeyed;

            var known = KnownLabelKeyedRows.GetValueOrDefault(page.Id, 0);
            if (unkeyed > known)
            {
                grown.Add($"{page.Id} now has {unkeyed} rows that persist under their own label "
                          + $"and drive nothing, up from {known}. Give the new one a Key that a "
                          + "feature reads, or raise the count here and say why.");
            }
        }

        Assert.Empty(grown);

        // A floor as well, so a broken sweep fails loudly rather than passing vacuously.
        Assert.True(total > 45, $"only {total} label-keyed rows found — the sweep is not seeing the pages");
    }

    /// <summary>
    /// The counts above shrink as rows are wired, and shrinking one is the point — so a page that
    /// has been repaired below its recorded number should have the number brought down with it,
    /// rather than leaving headroom for a new promise to fill.
    /// </summary>
    [Fact]
    public void TheBacklogDoesNotLeaveHeadroomForNewPromises()
    {
        var slack = new List<string>();

        foreach (var page in OptionsPages.All)
        {
            var known = KnownLabelKeyedRows.GetValueOrDefault(page.Id, 0);
            if (known == 0) continue;

            var unkeyed = UnwiredRows(page);
            if (unkeyed < known)
            {
                slack.Add($"{page.Id} is down to {unkeyed} unwired rows from {known} — "
                          + "lower the recorded count so the room is not left open.");
            }
        }

        Assert.Empty(slack);
    }

    /// <summary>
    /// The rows on a page that carry a value and have no key of their own — a tick or a choice
    /// that persists under its own label and is read by no feature. A row that carries no value
    /// (a button, a heading) has nothing to wire and is not the concern here.
    /// </summary>
    private static int UnwiredRows(OptionsPage page)
        => page.Sections
            .SelectMany(s => s.Rows)
            .Count(r => r.Key is null && r is CheckRow or ComboRow);
}
