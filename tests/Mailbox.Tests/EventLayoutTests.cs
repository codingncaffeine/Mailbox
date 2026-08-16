using Mailbox.Scheduling;

namespace Mailbox.Tests;

/// <summary>Packing a day's overlapping appointments into columns.</summary>
public class EventLayoutTests
{
    private static readonly DateTimeOffset Day = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    private static (string Name, DateTimeOffset Start, DateTimeOffset End) At(string name, double startHour, double endHour)
        => (name, Day.AddHours(startHour), Day.AddHours(endHour));

    private static IReadOnlyList<LayoutBox<(string Name, DateTimeOffset Start, DateTimeOffset End)>> Solve(params (string Name, DateTimeOffset Start, DateTimeOffset End)[] items)
        => EventLayout.Solve(items, i => i.Start, i => i.End);

    [Fact]
    public void ItemsThatDoNotOverlapEachTakeTheWholeWidth()
    {
        var boxes = Solve(At("a", 9, 10), At("b", 10, 11), At("c", 14, 15));
        Assert.All(boxes, b => Assert.Equal((0, 1, 1), (b.Column, b.Columns, b.Span)));
        Assert.Equal(["a", "b", "c"], boxes.Select(b => b.Item.Name));
    }

    [Fact]
    public void TwoOverlappingItemsShareTheWidthSideBySide()
    {
        var boxes = Solve(At("a", 9, 10), At("b", 9.5, 11));
        Assert.Equal((0, 2, 1), (boxes[0].Column, boxes[0].Columns, boxes[0].Span));
        Assert.Equal((1, 2, 1), (boxes[1].Column, boxes[1].Columns, boxes[1].Span));
    }

    [Fact]
    public void AClusterIsAsWideAsItsBusiestMomentAndAnItemWidensOverFreeColumns()
    {
        // a 9–12, b 9–10, c 9–10 → three columns; d 10:30–11:30 overlaps only a, so it sits in
        // column 1 and widens over column 2, which nothing uses at that hour.
        var boxes = Solve(At("a", 9, 12), At("b", 9, 10), At("c", 9, 10), At("d", 10.5, 11.5));
        var byName = boxes.ToDictionary(b => b.Item.Name);
        Assert.All(boxes, b => Assert.Equal(3, b.Columns));
        Assert.Equal((0, 1), (byName["a"].Column, byName["a"].Span));
        Assert.Equal((1, 1), (byName["b"].Column, byName["b"].Span));
        Assert.Equal((2, 1), (byName["c"].Column, byName["c"].Span));
        Assert.Equal((1, 2), (byName["d"].Column, byName["d"].Span));
    }

    [Fact]
    public void ClustersAreIndependent()
    {
        var boxes = Solve(At("a", 9, 10), At("b", 9, 10), At("c", 13, 14));
        Assert.Equal(2, boxes.Single(b => b.Item.Name == "a").Columns);
        Assert.Equal(1, boxes.Single(b => b.Item.Name == "c").Columns);
    }

    [Fact]
    public void AShortItemCountsAsThirtyMinutesSoItCanBeRead()
    {
        var boxes = Solve(At("a", 9, 9.1), At("b", 9.25, 10));
        Assert.Equal(2, boxes[0].Columns);
        Assert.Equal(Day.AddHours(9.5), boxes[0].End);

        var exact = EventLayout.Solve(new[] { At("a", 9, 9.1), At("b", 9.25, 10) }, i => i.Start, i => i.End, TimeSpan.Zero);
        Assert.All(exact, b => Assert.Equal(1, b.Columns));
    }

    [Fact]
    public void TheLongerOfTwoItemsStartingTogetherTakesTheLeftColumn()
    {
        var boxes = Solve(At("short", 9, 9.5), At("long", 9, 12));
        Assert.Equal("long", boxes[0].Item.Name);
        Assert.Equal(0, boxes[0].Column);
        Assert.Equal(1, boxes[1].Column);
    }
}
