using Mailbox.Core.Settings;

namespace Mailbox.Tests;

public class SendReceiveGroupTests
{
    private static readonly string[] Everyone = ["you@example.com", "work@example.net"];

    private static SendReceiveGroups Fresh(SettingsStore? settings = null)
        => new(settings ?? SettingsStore.Transient());

    [Fact]
    public void OneGroupShipsAndItCoversEverything()
    {
        var groups = Fresh();

        var only = Assert.Single(groups.All);
        Assert.Equal("All Accounts", only.Name);
        Assert.True(only.IncludeInSendReceiveAll);
        Assert.Equal(Everyone, groups.AccountsForSendReceiveAll(Everyone));
    }

    /// <summary>
    /// An empty account list means every account. A group that listed them would quietly stop
    /// covering the next one added.
    /// </summary>
    [Fact]
    public void AnEmptyListMeansEveryAccountIncludingLaterOnes()
    {
        var group = new SendReceiveGroup { Name = "All Accounts" };

        Assert.True(group.Includes("someone@new.example"));
    }

    [Fact]
    public void AGroupThatNamesAccountsCoversOnlyThose()
    {
        var groups = Fresh();
        groups.Replace([new SendReceiveGroup { Name = "Personal", Accounts = ["you@example.com"] }]);

        Assert.Equal(["you@example.com"], groups.AccountsForSendReceiveAll(Everyone));
    }

    [Fact]
    public void AGroupLeftOutOfSendReceiveAllIsNotChecked()
    {
        var groups = Fresh();
        groups.Replace(
        [
            new SendReceiveGroup { Name = "Personal", Accounts = ["you@example.com"] },
            new SendReceiveGroup
            {
                Name = "Work",
                Accounts = ["work@example.net"],
                IncludeInSendReceiveAll = false,
            },
        ]);

        Assert.Equal(["you@example.com"], groups.AccountsForSendReceiveAll(Everyone));
    }

    /// <summary>
    /// An account in two groups is one account. Checking it twice downloads nothing the second
    /// time and reports two tasks for one mailbox.
    /// </summary>
    [Fact]
    public void AnAccountInTwoGroupsIsCheckedOnce()
    {
        var groups = Fresh();
        groups.Replace(
        [
            new SendReceiveGroup { Name = "One", Accounts = ["you@example.com"] },
            new SendReceiveGroup { Name = "Two", Accounts = ["you@example.com"] },
        ]);

        Assert.Equal(["you@example.com"], groups.AccountsForSendReceiveAll(Everyone));
    }

    [Fact]
    public void NoGroupIncludedMeansNothingIsChecked()
    {
        var groups = Fresh();
        groups.Replace(
        [
            new SendReceiveGroup { Name = "Manual", IncludeInSendReceiveAll = false },
        ]);

        Assert.Empty(groups.AccountsForSendReceiveAll(Everyone));
    }

    [Fact]
    public void GroupsSurviveAReopen()
    {
        var settings = SettingsStore.Transient();

        Fresh(settings).Replace(
        [
            new SendReceiveGroup
            {
                Name = "Work",
                Accounts = ["work@example.net"],
                ScheduleEnabled = true,
                ScheduleMinutes = 10,
                IncludeInSendReceiveAll = false,
            },
        ]);

        var reopened = Assert.Single(Fresh(settings).All);

        Assert.Equal("Work", reopened.Name);
        Assert.Equal(["work@example.net"], reopened.Accounts);
        Assert.True(reopened.ScheduleEnabled);
        Assert.Equal(10, reopened.ScheduleMinutes);
        Assert.False(reopened.IncludeInSendReceiveAll);
    }

    /// <summary>
    /// The settings file is meant to be editable by hand, so it is allowed to be wrong — and
    /// send/receive still has to work afterwards.
    /// </summary>
    [Fact]
    public void ABadlyEditedFileFallsBackToTheShippedGroup()
    {
        var settings = SettingsStore.Transient();
        settings.Set(SendReceiveGroups.Key, "{ this is not a list");

        var only = Assert.Single(Fresh(settings).All);
        Assert.Equal("All Accounts", only.Name);
    }

    [Fact]
    public void AGroupWithNoNameIsDropped()
    {
        var settings = SettingsStore.Transient();
        settings.Set(SendReceiveGroups.Key, """[{"includeInAll":true},{"name":"Kept"}]""");

        Assert.Equal("Kept", Assert.Single(Fresh(settings).All).Name);
    }

    [Fact]
    public void ReplacingWithNothingLeavesTheShippedGroup()
    {
        var groups = Fresh();
        groups.Replace([]);

        Assert.Equal("All Accounts", Assert.Single(groups.All).Name);
    }

    [Fact]
    public void ANewGroupGetsANameNothingElseIsUsing()
    {
        var groups = Fresh();
        Assert.Equal("New Group", groups.NextName());

        groups.Replace([new SendReceiveGroup { Name = "New Group" }]);
        Assert.Equal("New Group 2", groups.NextName());
    }

    [Fact]
    public void AScheduleOutsideADayIsClamped()
    {
        var settings = SettingsStore.Transient();
        settings.Set(SendReceiveGroups.Key, """[{"name":"A","minutes":100000}]""");

        Assert.Equal(1440, Assert.Single(Fresh(settings).All).ScheduleMinutes);
    }

    [Fact]
    public void FindIsCaseInsensitive()
    {
        var groups = Fresh();
        Assert.NotNull(groups.Find("all accounts"));
        Assert.Null(groups.Find("nothing"));
    }

    /// <summary>
    /// The shipped group is checked every thirty minutes, as the reference's own is. A client
    /// that only checks mail when asked is one whose reader learns to press F9, which is not
    /// what a mail client is for.
    /// </summary>
    [Fact]
    public void TheShippedGroupIsCheckedEveryThirtyMinutes()
    {
        var only = Assert.Single(Fresh().All);

        Assert.True(only.ScheduleEnabled);
        Assert.Equal(30, only.ScheduleMinutes);
    }

    /// <summary>A hand-written group that says nothing about a schedule gets the shipped one.</summary>
    [Fact]
    public void AGroupWrittenWithoutAScheduleIsCheckedLikeTheShippedOne()
    {
        var settings = SettingsStore.Transient();
        settings.Set(SendReceiveGroups.Key, """[{"name":"A"}]""");

        var only = Assert.Single(Fresh(settings).All);
        Assert.True(only.ScheduleEnabled);
        Assert.Equal(30, only.ScheduleMinutes);
    }

    /// <summary>
    /// The Options page edits one group by projecting the live list. Replace clears that list
    /// before it reads what it was handed, so a projection evaluated late reads an empty list —
    /// and the edit became a reset to the shipped group.
    /// </summary>
    [Fact]
    public void ReplacingFromTheLiveListKeepsTheEdit()
    {
        var groups = Fresh();
        var current = groups.Find("All Accounts")!;
        var updated = current with { ScheduleMinutes = 5 };

        groups.Replace(groups.All.Select(g => ReferenceEquals(g, current) ? updated : g));

        Assert.Equal(5, Assert.Single(groups.All).ScheduleMinutes);
    }

    /// <summary>
    /// Cancel on the Options page puts the store back. The groups are read once and kept, so
    /// they have to follow the store rather than hold the cancelled edit until the next launch.
    /// </summary>
    [Fact]
    public void ARevertedStorePutsTheGroupsBack()
    {
        var settings = SettingsStore.Transient();
        var groups = Fresh(settings);
        var before = settings.Snapshot();

        groups.Replace([new SendReceiveGroup { Name = "Work", ScheduleMinutes = 5 }]);
        settings.Revert(before);

        var only = Assert.Single(groups.All);
        Assert.Equal("All Accounts", only.Name);
        Assert.Equal(30, only.ScheduleMinutes);
    }

    [Fact]
    public void ARevertedStorePutsAnEarlierEditBack()
    {
        var settings = SettingsStore.Transient();
        var groups = Fresh(settings);
        groups.Replace([new SendReceiveGroup { Name = "Work", ScheduleMinutes = 10 }]);
        var before = settings.Snapshot();

        groups.Replace([new SendReceiveGroup { Name = "Work", ScheduleMinutes = 5 }]);
        settings.Revert(before);

        var only = Assert.Single(groups.All);
        Assert.Equal("Work", only.Name);
        Assert.Equal(10, only.ScheduleMinutes);
    }
}
