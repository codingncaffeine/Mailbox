using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// Quick Click is the reference's fastest way to work a folder: one click on a row's Categories
/// or Flag cell does what "Set Quick Click…" nominated. What has to hold is that the two are
/// remembered separately, that the flag presets mean the same thing here as they do in the
/// Follow Up menu, and that a nomination can be taken back.
/// </summary>
public class QuickClickTests
{
    private static readonly DateTimeOffset Monday = new(2026, 8, 10, 9, 30, 0, TimeSpan.Zero);

    private static QuickClickSettings Fresh() => new(SettingsStore.Transient());

    [Fact]
    public void NothingIsNominatedUntilItIsAndTheFlagStartsAtToday()
    {
        var quick = Fresh();

        Assert.False(quick.HasCategory);
        Assert.Equal(string.Empty, quick.Category);
        Assert.Equal(QuickFlag.Today, quick.Flag);
    }

    [Fact]
    public void TheTwoNominationsAreIndependentAndSurviveAReopen()
    {
        var settings = SettingsStore.Transient();

        var quick = new QuickClickSettings(settings);
        quick.Category = "Blue Category";
        quick.Flag = QuickFlag.NextWeek;

        var second = new QuickClickSettings(settings);
        Assert.Equal("Blue Category", second.Category);
        Assert.Equal(QuickFlag.NextWeek, second.Flag);
        Assert.True(second.HasCategory);
    }

    /// <summary>"No Category" in the dialog is how a nomination is taken back.</summary>
    [Fact]
    public void ChoosingNoCategoryForgetsTheNomination()
    {
        var settings = SettingsStore.Transient();
        var quick = new QuickClickSettings(settings) { Category = "Red Category" };

        quick.Category = string.Empty;

        Assert.False(quick.HasCategory);
        Assert.False(settings.Has(QuickClickSettings.CategoryKey));
        Assert.Equal(string.Empty, new QuickClickSettings(settings).Category);
    }

    /// <summary>
    /// The dates the menu and a single click both use. From a Monday: this week is the coming
    /// Friday, next week the one after, and both fall due at the end of the working day.
    /// </summary>
    [Theory]
    [InlineData(QuickFlag.Today, "2026-08-10 17:00")]
    [InlineData(QuickFlag.Tomorrow, "2026-08-11 17:00")]
    [InlineData(QuickFlag.ThisWeek, "2026-08-14 17:00")]
    [InlineData(QuickFlag.NextWeek, "2026-08-21 17:00")]
    public void ThePresetsFallDueWhenTheReferenceSaysTheyDo(QuickFlag flag, string expected)
    {
        var due = QuickClickSettings.DueDate(flag, Monday);

        Assert.NotNull(due);
        Assert.Equal(expected, due!.Value.ToString("yyyy-MM-dd HH:mm"));
    }

    /// <summary>A Friday's "This Week" is that Friday, not the next one.</summary>
    [Fact]
    public void ThisWeekOnAFridayIsThatDay()
    {
        var friday = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-08-14 17:00",
            QuickClickSettings.DueDate(QuickFlag.ThisWeek, friday)!.Value.ToString("yyyy-MM-dd HH:mm"));
        Assert.Equal("2026-08-21 17:00",
            QuickClickSettings.DueDate(QuickFlag.NextWeek, friday)!.Value.ToString("yyyy-MM-dd HH:mm"));
    }

    /// <summary>No Date and Complete carry no due date; the flag is the whole of what they say.</summary>
    [Theory]
    [InlineData(QuickFlag.NoDate)]
    [InlineData(QuickFlag.Complete)]
    public void SomeFlagsHaveNoDate(QuickFlag flag)
        => Assert.Null(QuickClickSettings.DueDate(flag, Monday));

    [Fact]
    public void EveryFlagHasALabelTheMenusCanShow()
    {
        foreach (var flag in Enum.GetValues<QuickFlag>())
        {
            var label = QuickClickSettings.Label(flag);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.DoesNotContain('_', label);
        }

        Assert.Equal("This Week", QuickClickSettings.Label(QuickFlag.ThisWeek));
        Assert.Equal("No Date", QuickClickSettings.Label(QuickFlag.NoDate));
    }

    /// <summary>A settings file written by hand, or by a newer build, must not throw.</summary>
    [Fact]
    public void AnUnknownFlagFallsBackToToday()
    {
        var settings = SettingsStore.Transient();
        settings.Set(QuickClickSettings.FlagKey, "SometimeNextYear");

        Assert.Equal(QuickFlag.Today, new QuickClickSettings(settings).Flag);
    }
}
