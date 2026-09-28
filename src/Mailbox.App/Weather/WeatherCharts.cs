using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mailbox.Controls.Common;
using Mailbox.Core.Localization;
using Mailbox.Core.Weather;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Weather;

/// <summary>
/// The temperature scale: a colour for a temperature, along the theme's diverging scale.
/// </summary>
/// <remarks>
/// Five stops, each a token, pinned to temperatures a reader would name: freezing is cold, the
/// low twenties are mild, the mid-thirties hot. Between stops the colour is mixed, so a range bar
/// from 12° to 24° is a gradient through the scale rather than two flat colours.
/// </remarks>
internal static class TemperatureScale
{
    private static readonly (double Celsius, string Token)[] Stops =
    [
        (-5, TokenKeys.Weather.ScaleCold),
        (8, TokenKeys.Weather.ScaleCool),
        (18, TokenKeys.Weather.ScaleMild),
        (27, TokenKeys.Weather.ScaleWarm),
        (36, TokenKeys.Weather.ScaleHot),
    ];

    public static Color At(double celsius, Func<string, Color> colour)
    {
        if (celsius <= Stops[0].Celsius) return colour(Stops[0].Token);
        for (var i = 1; i < Stops.Length; i++)
        {
            if (celsius > Stops[i].Celsius) continue;
            var (lo, loToken) = Stops[i - 1];
            var (hi, hiToken) = Stops[i];
            return Blend.Toward(colour(loToken), colour(hiToken), (celsius - lo) / (hi - lo));
        }

        return colour(Stops[^1].Token);
    }
}

/// <summary>
/// The next hours, as a strip: a row each for the time, the picture and the temperature — the
/// numbers themselves, one per hour, as a table gives them — and under it two drawn bands that
/// share the hours' columns: the temperature's line, and the chance of precipitation as columns.
/// </summary>
/// <remarks>
/// Two bands rather than one plot with two scales: a line in degrees over bars in percent, drawn
/// against each other, invents a relation between two unrelated scales. Each band has its own,
/// and the columns line them up. Hovering an hour marks its column and says everything about it.
/// </remarks>
internal sealed class HourlyStrip : DrawnSurface
{
    public const double Column = 60;
    private const double TimeRow = 14;
    private const double PictureTop = 24;
    private const double Picture = 34;
    private const double TemperatureRow = 76;
    private const double LineTop = 92;
    private const double LineHeight = 56;
    private const double BarsTop = 168;
    private const double BarsHeight = 40;
    private const double LabelBand = 14;
    private const double Bottom = BarsTop + BarsHeight + 4;

    /// <summary>Below this chance a column is noise rather than something a reader plans around.</summary>
    private const double Worth = 10;

    private IReadOnlyList<HourlyWeather> _hours = [];
    private WeatherUnits _units = WeatherUnits.Metric;
    private int _hovered = -1;
    private bool _anyRain;

    public HourlyStrip()
    {
        Height = Bottom;
        Cursor = Cursor.Default;
    }

    public void Show(IReadOnlyList<HourlyWeather> hours, WeatherUnits units)
    {
        _hours = hours;
        _units = units;
        _anyRain = hours.Any(h => h.PrecipitationChance >= Worth);
        Width = Math.Max(Column, hours.Count * Column);
        Height = _anyRain ? Bottom : BarsTop - 8;
        _hovered = -1;
        foreach (var hour in hours) _ = WaitFor(hour.Condition.Icon);
        InvalidateVisual();
    }

    private async Task WaitFor(string icon)
    {
        if (WeatherArt.Ready(icon) is not null) return;
        await WeatherArt.LoadAsync(icon);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = (int)(e.GetPosition(this).X / Column);
        if (at < 0 || at >= _hours.Count || at == _hovered) return;
        _hovered = at;
        ToolTip.SetTip(this, Describe(_hours[at]));
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = -1;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    private string Describe(HourlyWeather hour)
    {
        var lines = new List<string>
        {
            $"{hour.Time.ToString("dddd", CultureInfo.CurrentCulture)} {hour.Time.ToString("t", CultureInfo.CurrentCulture)} — {hour.Condition.Description}",
            string.Format(CultureInfo.CurrentCulture, Strings.T("Temperature {0}, feels like {1}"),
                _units.FormatTemperature(hour.Temperature), _units.FormatTemperature(hour.FeelsLike ?? hour.Temperature)),
        };
        if (hour.PrecipitationChance is { } chance)
        {
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.T("Chance of precipitation {0}%"), Math.Round(chance)));
        }

        if (hour.Precipitation is > 0 and var amount) lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.T("Precipitation {0}"), _units.FormatPrecipitation(amount)));
        if (hour.WindSpeed is { } wind)
        {
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.T("Wind {0} {1}, gusts {2}"),
                WeatherReadings.Compass(hour.WindDirection ?? 0), _units.FormatSpeed(wind), _units.FormatSpeed(hour.WindGusts ?? wind)));
        }

        if (hour.Humidity is { } humidity) lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.T("Humidity {0}%"), Math.Round(humidity)));
        if (hour.UvIndex is { } uv && hour.IsDay) lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.T("UV index {0}"), Math.Round(uv)));
        return string.Join('\n', lines);
    }

    public override void Render(DrawingContext context)
    {
        if (_hours.Count == 0) return;

        var card = Colour(TokenKeys.Weather.Card);
        var ink = Colour(TokenKeys.Weather.CardText);
        var dim = Colour(TokenKeys.Weather.CardTextDim);
        var grid = Colour(TokenKeys.Weather.ChartGrid);
        var line = Colour(TokenKeys.Weather.ChartTemperature);
        var wash = Colour(TokenKeys.Weather.ChartTemperatureFill);
        var rain = Colour(TokenKeys.Weather.ChartRain);

        if (_hovered >= 0)
        {
            context.DrawRectangle(Brush(Colour(TokenKeys.State.Hover)), null,
                new RoundedRect(new Rect(_hovered * Column + 2, 0, Column - 4, Height), 6));
        }

        // The table rows: time, picture, temperature.
        for (var i = 0; i < _hours.Count; i++)
        {
            var hour = _hours[i];
            var centre = i * Column + Column / 2;
            var label = i == 0 ? Strings.T("Now") : HourLabel(hour.Time);
            var time = Ink(label, 11, i == 0 ? ink : dim, i == 0 ? SemiBoldFace : null);
            DrawAt(context, time, centre - time.Width / 2, TimeRow);

            if (WeatherArt.Ready(hour.Condition.Icon) is Bitmap picture)
            {
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                {
                    context.DrawImage(picture, new Rect(centre - Picture / 2, PictureTop, Picture, Picture));
                }
            }

            var degrees = Ink(_units.FormatTemperature(hour.Temperature), 13, ink, SemiBoldFace);
            DrawAt(context, degrees, centre - degrees.Width / 2, TemperatureRow);
        }

        // The temperature band: the line through the hours' centres on its own scale, a wash
        // under it, and the hour now marked by a dot ringed in the card's colour.
        var low = _hours.Min(h => h.Temperature);
        var high = _hours.Max(h => h.Temperature);
        var span = Math.Max(high - low, 1);
        Point PointAt(int i) => new(i * Column + Column / 2, LineTop + 4 + (high - _hours[i].Temperature) / span * (LineHeight - 8));

        var path = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var p = path.Open())
        using (var f = fill.Open())
        {
            p.BeginFigure(PointAt(0), false);
            f.BeginFigure(new Point(PointAt(0).X, LineTop + LineHeight), true);
            f.LineTo(PointAt(0));
            for (var i = 1; i < _hours.Count; i++)
            {
                var a = PointAt(i - 1);
                var b = PointAt(i);
                var mid = (a.X + b.X) / 2;
                p.CubicBezierTo(new Point(mid, a.Y), new Point(mid, b.Y), b);
                f.CubicBezierTo(new Point(mid, a.Y), new Point(mid, b.Y), b);
            }

            f.LineTo(new Point(PointAt(_hours.Count - 1).X, LineTop + LineHeight));
            f.EndFigure(true);
            p.EndFigure(false);
        }

        context.DrawGeometry(Brush(wash), null, fill);
        context.DrawGeometry(null, new Pen(Brush(line), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), path);
        var now = PointAt(0);
        context.DrawEllipse(Brush(card), null, now, 6, 6);
        context.DrawEllipse(Brush(line), null, now, 4, 4);

        // The precipitation band: a column per hour for its chance, grown from one baseline, with
        // the chance written above it. Only chances worth reading are drawn, and with none in the
        // hours shown the band is not drawn at all — an empty baseline is a row of nothing.
        if (!_anyRain) return;
        var baseline = BarsTop + BarsHeight;
        context.DrawLine(new Pen(Brush(grid), 1), new Point(0, baseline + 0.5), new Point(Width, baseline + 0.5));
        for (var i = 0; i < _hours.Count; i++)
        {
            if (_hours[i].PrecipitationChance is not { } chance || chance < Worth) continue;
            var height = Math.Max(2, (BarsHeight - LabelBand) * chance / 100);
            var left = i * Column + (Column - 20) / 2;
            using (context.PushClip(new Rect(left, baseline - height, 20, height)))
            {
                context.DrawRectangle(Brush(rain), null, new RoundedRect(new Rect(left, baseline - height, 20, height + 4), 4, 4, 0, 0));
            }

            var label = Ink($"{Math.Round(chance)}%", 10, dim);
            DrawAt(context, label, i * Column + (Column - label.Width) / 2, baseline - height - 3);
        }
    }

    /// <summary>"8 PM" where the reader's clock is twelve-hour, "20:00" where it is not.</summary>
    public static string HourLabel(DateTime time)
    {
        var culture = CultureInfo.CurrentCulture;
        return culture.DateTimeFormat.ShortTimePattern.Contains('h', StringComparison.Ordinal)
            ? time.ToString("h tt", culture)
            : time.ToString("HH:mm", culture);
    }
}

/// <summary>
/// A day's range: the track every day shares, the day's low-to-high span along it coloured by
/// the temperature scale, and — for today — the temperature now as a ringed dot.
/// </summary>
internal sealed class DayRangeBar : DrawnSurface
{
    private double _floor;
    private double _ceiling = 1;
    private double _low;
    private double _high;
    private double? _now;

    public DayRangeBar()
    {
        Height = 20;
        MinWidth = 60;
    }

    /// <param name="floor">The coldest low across the days shown, which is the track's left end.</param>
    /// <param name="ceiling">The warmest high, the track's right end.</param>
    public void Show(double floor, double ceiling, double low, double high, double? now)
    {
        (_floor, _ceiling, _low, _high, _now) = (floor, Math.Max(ceiling, floor + 1), low, high, now);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= 8) return;

        var middle = Bounds.Height / 2;
        const double Thickness = 6;
        double X(double celsius) => 3 + (celsius - _floor) / (_ceiling - _floor) * (width - 6);

        context.DrawRectangle(Brush(Colour(TokenKeys.Weather.RangeTrack)), null,
            new RoundedRect(new Rect(0, middle - Thickness / 2, width, Thickness), Thickness / 2));

        var from = X(_low) - 3;
        var to = X(_high) + 3;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(from, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(Math.Max(to, from + 1), 0, RelativeUnit.Absolute),
        };
        const int Steps = 6;
        for (var s = 0; s <= Steps; s++)
        {
            var celsius = _low + (_high - _low) * s / Steps;
            gradient.GradientStops.Add(new GradientStop(TemperatureScale.At(celsius, Colour), (double)s / Steps));
        }

        context.DrawRectangle(gradient, null, new RoundedRect(new Rect(from, middle - Thickness / 2, Math.Max(to - from, Thickness), Thickness), Thickness / 2));

        if (_now is { } now)
        {
            var at = new Point(Math.Clamp(X(now), 4, width - 4), middle);
            context.DrawEllipse(Brush(Colour(TokenKeys.Weather.Card)), null, at, 6, 6);
            context.DrawEllipse(Brush(Colour(TokenKeys.Weather.CardText)), null, at, 4, 4);
        }
    }
}

/// <summary>
/// The UV index on the WHO's scale: four steps in the status colours with a gap between them,
/// and the reading as a ringed dot — its name is written beside it, never left to the colour.
/// </summary>
internal sealed class UvMeter : DrawnSurface
{
    private double _index;

    public UvMeter()
    {
        Height = 16;
    }

    public void Show(double index)
    {
        _index = index;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width < 40) return;

        const double Top = 5;
        const double Thickness = 6;
        const double Gap = 2;
        const double Scale = 12;
        (double From, double To, string Token)[] steps =
        [
            (0, 3, TokenKeys.Status.Success),
            (3, 6, TokenKeys.Status.Warning),
            (6, 8, TokenKeys.Weather.AlertModerate),
            (8, Scale, TokenKeys.Status.Danger),
        ];

        foreach (var (from, to, token) in steps)
        {
            var left = from / Scale * width + (from > 0 ? Gap / 2 : 0);
            var right = to / Scale * width - (to < Scale ? Gap / 2 : 0);
            context.DrawRectangle(Brush(Colour(token)), null, new RoundedRect(new Rect(left, Top, right - left, Thickness), Thickness / 2));
        }

        var at = new Point(Math.Clamp(_index / Scale * width, 5, width - 5), Top + Thickness / 2);
        context.DrawEllipse(Brush(Colour(TokenKeys.Weather.Card)), null, at, 7, 7);
        context.DrawEllipse(Brush(Colour(TokenKeys.Weather.CardText)), null, at, 5, 5);
    }
}

/// <summary>
/// The sun's day: a hairline arc from sunrise to sunset over a horizon, the part already
/// travelled drawn in the warm colour, and the sun where it is now.
/// </summary>
internal sealed class SunArc : DrawnSurface
{
    private double _progress = -1;

    public SunArc()
    {
        Height = 64;
    }

    /// <param name="progress">How far through the day the sun is, 0 at sunrise and 1 at sunset; outside that, it is night.</param>
    public void Show(double progress)
    {
        _progress = progress;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 40) return;

        var grid = Colour(TokenKeys.Weather.ChartGrid);
        var warm = Colour(TokenKeys.Weather.ScaleWarm);
        var horizon = height - 6;
        var radiusX = width / 2 - 8;
        var radiusY = horizon - 8;
        var centre = new Point(width / 2, horizon);
        Point On(double t) => new(centre.X - Math.Cos(t * Math.PI) * radiusX, centre.Y - Math.Sin(t * Math.PI) * radiusY);

        context.DrawLine(new Pen(Brush(grid), 1), new Point(0, horizon + 0.5), new Point(width, horizon + 0.5));

        var arc = new StreamGeometry();
        using (var a = arc.Open())
        {
            a.BeginFigure(On(0), false);
            for (var s = 1; s <= 48; s++) a.LineTo(On(s / 48.0));
            a.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Brush(grid), 1.5, lineCap: PenLineCap.Round), arc);

        if (_progress is < 0 or > 1) return;

        var travelled = new StreamGeometry();
        using (var t = travelled.Open())
        {
            t.BeginFigure(On(0), false);
            var steps = Math.Max(1, (int)(_progress * 48));
            for (var s = 1; s <= steps; s++) t.LineTo(On(_progress * s / steps));
            t.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Brush(warm), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), travelled);
        var sun = On(_progress);
        context.DrawEllipse(Brush(Colour(TokenKeys.Weather.Card)), null, sun, 7, 7);
        context.DrawEllipse(Brush(warm), null, sun, 5, 5);
    }
}

/// <summary>
/// Where the wind is going: a hairline dial with its four points, and an arrow drawn the way the
/// wind blows — downwind of the direction it is named for.
/// </summary>
internal sealed class WindCompass : DrawnSurface
{
    private double? _from;

    public WindCompass()
    {
        Width = 64;
        Height = 64;
    }

    public void Show(double? fromDegrees)
    {
        _from = fromDegrees;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 10;
        var grid = Colour(TokenKeys.Weather.ChartGrid);
        var dim = Colour(TokenKeys.Weather.CardTextDim);
        var ink = Colour(TokenKeys.Weather.CardText);

        context.DrawEllipse(null, new Pen(Brush(grid), 1), centre, radius, radius);
        foreach (var (label, angle) in new[] { ("N", 0.0), ("E", 90.0), ("S", 180.0), ("W", 270.0) })
        {
            var r = angle * Math.PI / 180;
            var text = Ink(label, 9, dim);
            var at = new Point(centre.X + Math.Sin(r) * (radius + 7), centre.Y - Math.Cos(r) * (radius + 7));
            DrawAt(context, text, at.X - text.Width / 2, at.Y + text.Baseline / 2 - 1);
        }

        if (_from is not { } from) return;

        var towards = (from + 180) * Math.PI / 180;
        var tip = new Point(centre.X + Math.Sin(towards) * (radius - 3), centre.Y - Math.Cos(towards) * (radius - 3));
        var tail = new Point(centre.X - Math.Sin(towards) * (radius - 3), centre.Y + Math.Cos(towards) * (radius - 3));
        var pen = new Pen(Brush(ink), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawLine(pen, tail, tip);
        var side = 6.0;
        var left = new Point(tip.X - Math.Sin(towards - 0.5) * side, tip.Y + Math.Cos(towards - 0.5) * side);
        var right = new Point(tip.X - Math.Sin(towards + 0.5) * side, tip.Y + Math.Cos(towards + 0.5) * side);
        context.DrawLine(pen, tip, left);
        context.DrawLine(pen, tip, right);
    }
}
