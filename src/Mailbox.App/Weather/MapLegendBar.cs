using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mailbox.Core.Weather;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Weather;

/// <summary>
/// A layer's legend, drawn by the map itself: a heading with the reader's unit, the colour bar in
/// the colours the map draws — faded or tinted as the layer is — and the labels under it.
/// </summary>
/// <remarks>
/// The services draw legends of their own, but as white pictures in their own units: Celsius and
/// knots on every screen, and a white card on a black theme. Drawn here, the legend follows the
/// theme and says what the rest of the page says.
/// </remarks>
internal sealed class MapLegendBar : Control
{
    private const double TitleHeight = 16;
    private const double BarHeight = 8;
    private const double Gap = 4;
    private const double LabelSize = 10;

    private MapLegend? _legend;
    private MapLayer? _layer;
    private WeatherUnits _units = WeatherUnits.Metric;

    public MapLegendBar()
    {
        Width = 220;
        Height = TitleHeight + Gap + BarHeight + 3 + 14;
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Shows a layer's legend in a reader's units.</summary>
    public void Show(MapLayer layer, WeatherUnits units)
    {
        _layer = layer;
        _legend = layer.Legend;
        _units = units;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_legend is not { } legend || _layer is not { } layer || legend.Stops.Count < 2) return;
        var width = Bounds.Width;
        var culture = CultureInfo.CurrentCulture;
        var face = this.TryFindResource("ui.fontfamily", out var family) && family is FontFamily ui ? new Typeface(ui) : Typeface.Default;
        var strong = new Typeface(face.FontFamily, FontStyle.Normal, FontWeight.SemiBold);
        var text = Brush(TokenKeys.Weather.CardText);
        var dim = Brush(TokenKeys.Weather.CardTextDim);

        // The heading, and the unit beside it in the quieter ink.
        var title = new FormattedText(legend.Title, culture, FlowDirection.LeftToRight, strong, 11, text);
        context.DrawText(title, new Point(0, (TitleHeight - title.Height) / 2));
        var unit = legend.Unit(_units);
        if (unit.Length > 0)
        {
            var unitText = new FormattedText(unit, culture, FlowDirection.LeftToRight, face, 11, dim);
            context.DrawText(unitText, new Point(title.Width + 5, (TitleHeight - unitText.Height) / 2));
        }

        // The bar.
        var bar = new Rect(0, TitleHeight + Gap, width, BarHeight);
        var shape = new RoundedRect(bar, BarHeight / 2);
        using (context.PushClip(shape))
        {
            if (legend.Stepped)
            {
                var n = legend.Stops.Count;
                for (var i = 0; i < n; i++)
                {
                    var block = new Rect(bar.X + bar.Width * i / n, bar.Y, bar.Width / n + 0.5, bar.Height);
                    context.FillRectangle(new SolidColorBrush(Colour(layer, legend, legend.Stops[i])), block);
                }
            }
            else
            {
                var gradient = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                };
                for (var i = 0; i < legend.Stops.Count; i++)
                {
                    gradient.GradientStops.Add(new GradientStop(Colour(layer, legend, legend.Stops[i]), (double)i / (legend.Stops.Count - 1)));
                }

                context.FillRectangle(gradient, bar);
            }
        }

        context.DrawRectangle(null, new Pen(Brush(TokenKeys.Weather.CardBorder), 1), shape.Deflate(0.5, 0.5));

        // The labels, each centred under its place on the bar, nudged apart where they would touch.
        var labels = legend.Labels(_units, culture)
            .Select(l => (l.At, Text: new FormattedText(l.Text, culture, FlowDirection.LeftToRight, face, LabelSize, dim)))
            .ToList();
        const double Space = 6;
        var lefts = labels.Select(l => Math.Clamp(l.At * width - l.Text.Width / 2, 0, Math.Max(0, width - l.Text.Width))).ToArray();
        for (var i = 1; i < lefts.Length; i++) lefts[i] = Math.Max(lefts[i], lefts[i - 1] + labels[i - 1].Text.Width + Space);
        for (var i = lefts.Length - 1; i >= 0; i--)
        {
            var limit = i == lefts.Length - 1 ? width : lefts[i + 1] - Space;
            lefts[i] = Math.Min(lefts[i], limit - labels[i].Text.Width);
        }

        var top = bar.Bottom + 3;
        for (var i = 0; i < labels.Count; i++)
        {
            if (lefts[i] < -0.5) continue;
            context.DrawText(labels[i].Text, new Point(lefts[i], top));
        }
    }

    /// <summary>A stop's colour as the map draws it: faded at a rate's slight end, or as cloud in the theme's colour.</summary>
    private Color Colour(MapLayer layer, MapLegend legend, MapLegendStop stop)
    {
        var colour = Color.FromUInt32(stop.Colour);
        switch (layer.Look)
        {
            case MapLook.Faded:
            {
                var strength = MapPixels.FadeStrength(stop.Value, legend.Stops[0].Value, MapPixels.FadeFull, MapPixels.FadeFloor);
                return Color.FromArgb((byte)Math.Round(colour.A * strength), colour.R, colour.G, colour.B);
            }

            case MapLook.Tinted:
            {
                var cloud = this.TryFindResource(TokenKeys.Weather.MapCloud + ".color", out var found) && found is Color c ? c : Colors.Magenta;
                var (lowest, highest) = (legend.Stops[0].Value, legend.Stops[^1].Value);
                var share = Math.Clamp((stop.Value - lowest) / (highest - lowest), 0, 1);
                return Color.FromArgb((byte)Math.Round(cloud.A * share), cloud.R, cloud.G, cloud.B);
            }

            default:
                return colour;
        }
    }

    private IBrush Brush(string token)
        => this.TryFindResource(token + ".brush", out var found) && found is IBrush brush ? brush : Brushes.Magenta;
}
