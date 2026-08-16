namespace Mailbox.Scheduling;

/// <summary>Where an item goes in a day column: which of the cluster's columns, out of how many, and how many it may spread across.</summary>
/// <param name="Column">The item's column, 0 at the left.</param>
/// <param name="Columns">How many columns its cluster needs — every item in the cluster shares this width.</param>
/// <param name="Span">How many columns the item may take, starting at <see cref="Column"/>: 1, or more where the columns to its right are free for its whole time.</param>
public sealed record LayoutBox<T>(T Item, DateTimeOffset Start, DateTimeOffset End, int Column, int Columns, int Span);

/// <summary>
/// The overlapping-appointment layout §7.4 describes: items in a day are grouped into clusters
/// of mutual overlap, each cluster is packed into as few columns as its busiest moment needs,
/// and an item widens to the right over columns nothing else uses during its time.
/// </summary>
public static class EventLayout
{
    /// <summary>
    /// The default floor on an item's length for layout: a five-minute item is drawn tall
    /// enough to read, so it overlaps what a thirty-minute one would.
    /// </summary>
    public static readonly TimeSpan DefaultMinimumDuration = TimeSpan.FromMinutes(30);

    /// <summary>The boxes for the items given, in start order.</summary>
    /// <param name="start">The item's start instant.</param>
    /// <param name="end">The item's end instant; an end at or before the start is treated as the start.</param>
    /// <param name="minimumDuration">The floor on an item's length for overlap purposes; null for the default.</param>
    public static IReadOnlyList<LayoutBox<T>> Solve<T>(IEnumerable<T> items, Func<T, DateTimeOffset> start, Func<T, DateTimeOffset> end, TimeSpan? minimumDuration = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        var floor = minimumDuration ?? DefaultMinimumDuration;

        var ordered = items
            .Select(i =>
            {
                var s = start(i);
                var e = end(i);
                if (e < s) e = s;
                if (e - s < floor) e = s + floor;
                return (Item: i, Start: s, End: e);
            })
            .OrderBy(x => x.Start)
            .ThenByDescending(x => x.End - x.Start)
            .ToList();

        var result = new List<LayoutBox<T>>(ordered.Count);
        var cluster = new List<(T Item, DateTimeOffset Start, DateTimeOffset End, int Column)>();
        var columnEnds = new List<DateTimeOffset>();
        var clusterEnd = DateTimeOffset.MinValue;

        void FlushCluster()
        {
            if (cluster.Count == 0) return;
            var columns = columnEnds.Count;
            foreach (var (item, s, e, column) in cluster)
            {
                // Widen over the columns to the right that no other member of the cluster
                // touches during this item's time.
                var span = 1;
                for (var c = column + 1; c < columns; c++)
                {
                    var blocked = cluster.Any(o => o.Column == c && o.Start < e && o.End > s);
                    if (blocked) break;
                    span++;
                }
                result.Add(new LayoutBox<T>(item, s, e, column, columns, span));
            }
            cluster.Clear();
            columnEnds.Clear();
        }

        foreach (var (item, s, e) in ordered)
        {
            // A new cluster begins when this item starts after everything so far has ended.
            if (cluster.Count > 0 && s >= clusterEnd) FlushCluster();

            var column = -1;
            for (var c = 0; c < columnEnds.Count; c++)
            {
                if (columnEnds[c] <= s)
                {
                    column = c;
                    break;
                }
            }
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(e);
            }
            else
            {
                columnEnds[column] = e;
            }

            cluster.Add((item, s, e, column));
            if (e > clusterEnd || cluster.Count == 1) clusterEnd = e;
        }
        FlushCluster();

        return result;
    }
}
