using Avalonia;
using Avalonia.Media;

namespace Mailbox.App.Views;

/// <summary>
/// The shipped caption backdrop patterns — six invented textures, each drawn in the caption's
/// own ink so no theme ever meets a colour this file chose. Geometry only: the paint sweep
/// holds every file outside the theming project to "names no colour", and this one obeys by
/// construction. Names are provisional until the owner names them.
/// </summary>
internal static class CaptionPatterns
{
    /// <summary>The pattern names, in the order the Options row offers them.</summary>
    internal static readonly IReadOnlyList<string> Names =
        ["stitches", "weave", "hatch", "dots", "rings", "waves"];

    internal static bool IsKnown(string name)
        => Names.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>What the Options row shows for a pattern id.</summary>
    internal static string DisplayName(string name)
        => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();

    /// <summary>Draws one pattern across the bounds in the given ink. Unknown names draw nothing.</summary>
    internal static void Draw(string name, DrawingContext context, Rect bounds, IBrush ink)
    {
        switch (name.ToLowerInvariant())
        {
            case "stitches": Stitches(context, bounds, ink); break;
            case "weave": Weave(context, bounds, ink); break;
            case "hatch": Hatch(context, bounds, ink); break;
            case "dots": Dots(context, bounds, ink); break;
            case "rings": Rings(context, bounds, ink); break;
            case "waves": Waves(context, bounds, ink); break;
        }
    }

    /// <summary>Short slanted dashes in offset rows, like running stitches across cloth.</summary>
    private static void Stitches(DrawingContext context, Rect bounds, IBrush ink)
    {
        var pen = new Pen(ink, 1.4);
        for (var row = 0; row * 14.0 < bounds.Height + 14; row++)
        {
            var y = bounds.Top + 7 + (row * 14.0);
            var offset = row % 2 == 0 ? 0.0 : 11.0;
            for (var x = bounds.Left - 22 + offset; x < bounds.Right; x += 22)
            {
                context.DrawLine(pen, new Point(x, y + 2), new Point(x + 8, y - 2));
            }
        }
    }

    /// <summary>Interleaved horizontal and vertical bars, a plain-weave basket.</summary>
    private static void Weave(DrawingContext context, Rect bounds, IBrush ink)
    {
        const double cell = 12;
        for (var row = 0; row * cell < bounds.Height + cell; row++)
        {
            for (var col = 0; col * cell < bounds.Width + cell; col++)
            {
                var x = bounds.Left + (col * cell);
                var y = bounds.Top + (row * cell);
                var rect = (row + col) % 2 == 0
                    ? new Rect(x + 1, y + 4, cell - 2, 4)
                    : new Rect(x + 4, y + 1, 4, cell - 2);
                context.FillRectangle(ink, rect, 1.5f);
            }
        }
    }

    /// <summary>Parallel diagonals, the classic drafting hatch.</summary>
    private static void Hatch(DrawingContext context, Rect bounds, IBrush ink)
    {
        var pen = new Pen(ink, 1.2);
        for (var x = bounds.Left - bounds.Height; x < bounds.Right; x += 9)
        {
            context.DrawLine(pen,
                new Point(x, bounds.Bottom),
                new Point(x + bounds.Height, bounds.Top));
        }
    }

    /// <summary>A staggered polka grid.</summary>
    private static void Dots(DrawingContext context, Rect bounds, IBrush ink)
    {
        for (var row = 0; row * 11.0 < bounds.Height + 11; row++)
        {
            var y = bounds.Top + 5 + (row * 11.0);
            var offset = row % 2 == 0 ? 0.0 : 5.5;
            for (var x = bounds.Left + 5 + offset; x < bounds.Right + 5; x += 11)
            {
                context.DrawEllipse(ink, null, new Point(x, y), 1.6, 1.6);
            }
        }
    }

    /// <summary>Overlapping outline circles, scattered on a fixed grid.</summary>
    private static void Rings(DrawingContext context, Rect bounds, IBrush ink)
    {
        var pen = new Pen(ink, 1.2);
        for (var row = 0; row * 16.0 < bounds.Height + 32; row++)
        {
            var y = bounds.Top + (row * 16.0);
            var offset = row % 2 == 0 ? 0.0 : 8.0;
            for (var x = bounds.Left + offset; x < bounds.Right + 16; x += 16)
            {
                context.DrawEllipse(null, pen, new Point(x, y), 9, 9);
            }
        }
    }

    /// <summary>Rows of gentle scallops, read as water.</summary>
    private static void Waves(DrawingContext context, Rect bounds, IBrush ink)
    {
        var pen = new Pen(ink, 1.3);
        for (var row = 0; row * 12.0 < bounds.Height + 12; row++)
        {
            var y = bounds.Top + 6 + (row * 12.0);
            var geometry = new StreamGeometry();
            using (var stream = geometry.Open())
            {
                stream.BeginFigure(new Point(bounds.Left - 16, y), isFilled: false);
                for (var x = bounds.Left - 16; x < bounds.Right + 16; x += 16)
                {
                    stream.QuadraticBezierTo(new Point(x + 4, y - 5), new Point(x + 8, y));
                    stream.QuadraticBezierTo(new Point(x + 12, y + 5), new Point(x + 16, y));
                }
                stream.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }
    }
}
