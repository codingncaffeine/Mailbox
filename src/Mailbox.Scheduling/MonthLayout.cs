namespace Mailbox.Scheduling;

/// <summary>
/// Where one item sits in a week row of the month view: the columns it covers, the lane it was
/// given, and whether it runs off either end of the row.
/// </summary>
/// <param name="Lane">Its row within the cell, 0 at the top, shared with everything else in the same lane.</param>
/// <param name="ContinuesBefore">True when the item began before this week — the reference draws that end open.</param>
/// <param name="ContinuesAfter">True when it runs on past the last column.</param>
public sealed record MonthBar<T>(T Item, int StartColumn, int EndColumn, int Lane, bool ContinuesBefore, bool ContinuesAfter)
{
    public int Columns => EndColumn - StartColumn + 1;
}

/// <summary>
/// The month view's own layout pass: a week row is a set of lanes, and every item —
/// a bar spanning several days or a single day's appointment — takes the first lane free for
/// every column it covers.
/// </summary>
/// <remarks>
/// One pass rather than two, and that is the point. A timed appointment on Wednesday has to sit
/// <em>below</em> an all-day bar crossing Wednesday, not beside it and not on top of it, so both
/// kinds are packed into the same lanes. Feeding the all-day and multi-day items in first is
/// what puts them at the top of the cell, exactly as the reference draws them; the caller
/// decides that order, and this keeps it.
/// </remarks>
public static class MonthLayout
{
    /// <summary>
    /// Lanes for the items given, in the order they were given. <paramref name="span"/> returns
    /// the first and last column an item touches; either may fall outside the row, and an item
    /// wholly outside it is dropped.
    /// </summary>
    public static IReadOnlyList<MonthBar<T>> Solve<T>(IEnumerable<T> items, Func<T, (int First, int Last)> span, int columns)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(span);
        if (columns <= 0) return [];

        var lanes = new List<bool[]>();
        var result = new List<MonthBar<T>>();

        foreach (var item in items)
        {
            var (first, last) = span(item);
            if (last < first) (first, last) = (last, first);
            if (last < 0 || first > columns - 1) continue;

            var start = Math.Max(first, 0);
            var end = Math.Min(last, columns - 1);

            var lane = -1;
            for (var l = 0; l < lanes.Count && lane < 0; l++)
            {
                var free = true;
                for (var c = start; c <= end && free; c++) free = !lanes[l][c];
                if (free) lane = l;
            }

            if (lane < 0)
            {
                lane = lanes.Count;
                lanes.Add(new bool[columns]);
            }

            for (var c = start; c <= end; c++) lanes[lane][c] = true;
            result.Add(new MonthBar<T>(item, start, end, lane, first < 0, last > columns - 1));
        }

        return result;
    }
}
