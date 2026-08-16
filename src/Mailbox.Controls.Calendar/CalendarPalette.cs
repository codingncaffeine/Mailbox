using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Mailbox.Scheduling;
using Mailbox.Theming.Tokens;

namespace Mailbox.Controls.Calendar;

/// <summary>
/// Every <c>calendar.*</c> token a view draws with, resolved once per render.
/// </summary>
/// <remarks>
/// The views draw rather than compose, so they need colours and not brushes bound to properties;
/// resolving the whole family in one go — through the same resource dictionary
/// <c>{DynamicResource}</c> reads — keeps the rule that nothing outside a theme names a colour,
/// without giving every control thirty styled properties. <see cref="CalendarSurface"/> throws
/// the cached copy away when the resources change, which is what makes a theme switch repaint.
/// </remarks>
public sealed class CalendarPalette
{
    private readonly Dictionary<string, Color> _colours = [];
    private readonly Dictionary<string, double> _numbers = [];
    private readonly Dictionary<Color, ImmutableSolidColorBrush> _brushes = [];

    private CalendarPalette()
    {
    }

    /// <summary>Reads the family out of the control's resource scope.</summary>
    public static CalendarPalette From(Control host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var palette = new CalendarPalette();

        foreach (var key in Keys)
        {
            if (host.TryFindResource(key + ".color", out var value) && value is Color colour)
            {
                palette._colours[key] = colour;
            }
            else if (host.TryFindResource(key + ".value", out var number) && number is double d)
            {
                palette._numbers[key] = d;
            }
        }

        return palette;
    }

    private static readonly string[] Keys =
    [
        TokenKeys.Calendar.Background, TokenKeys.Calendar.PastFill, TokenKeys.Calendar.TodayFill,
        TokenKeys.Calendar.TodayText, TokenKeys.Calendar.SelectedFill,
        TokenKeys.Calendar.WorkingHoursFill, TokenKeys.Calendar.NonWorkingFill,
        TokenKeys.Calendar.GridLine, TokenKeys.Calendar.CurrentTimeIndicator,
        TokenKeys.Calendar.AllDayBandBackground,
        TokenKeys.Calendar.HeaderBackground, TokenKeys.Calendar.HeaderText, TokenKeys.Calendar.HeaderLine,
        TokenKeys.Calendar.DayText, TokenKeys.Calendar.PastText, TokenKeys.Calendar.HourText,
        TokenKeys.Calendar.ToolbarText, TokenKeys.Calendar.ToolbarButton,
        TokenKeys.Calendar.ToolbarButtonBorder, TokenKeys.Calendar.ToolbarButtonText,
        TokenKeys.Calendar.ChipDefault, TokenKeys.Calendar.ChipGround, TokenKeys.Calendar.ChipTint,
        TokenKeys.Calendar.ChipText, TokenKeys.Calendar.ChipFreeFill, TokenKeys.Calendar.ChipFreeStripe,
        TokenKeys.Calendar.ChipHatch, TokenKeys.Calendar.ChipEdgeGround,
        TokenKeys.Calendar.ChipEdgeSoft, TokenKeys.Calendar.ChipEdgeStrong,
        TokenKeys.Calendar.OutOfOffice,
        TokenKeys.Calendar.NavigatorBackground, TokenKeys.Calendar.NavigatorText,
        TokenKeys.Calendar.NavigatorRange, TokenKeys.Calendar.NavigatorRangeText,
        TokenKeys.Calendar.NavigatorToday,
        TokenKeys.Nav.Background, TokenKeys.Nav.ItemSelected, TokenKeys.Nav.ItemText,
        TokenKeys.List.Background, TokenKeys.Border.Subtle, TokenKeys.Border.Strong,
        TokenKeys.Surface.Sunken, TokenKeys.State.Hover, TokenKeys.Accent.Rest,
        TokenKeys.Text.Primary, TokenKeys.Text.Secondary,
    ];

    /// <summary>A token's colour, or a visible fallback when a theme has not defined it.</summary>
    public Color Colour(string key)
        => _colours.TryGetValue(key, out var c) ? c : Colors.Magenta;

    public double Number(string key, double fallback)
        => _numbers.TryGetValue(key, out var d) ? d : fallback;

    /// <summary>A cached brush for a token, so a render pass allocates none.</summary>
    public IBrush Brush(string key) => Brush(Colour(key));

    public IBrush Brush(Color colour)
    {
        if (_brushes.TryGetValue(colour, out var brush)) return brush;
        brush = new ImmutableSolidColorBrush(colour);
        _brushes[colour] = brush;
        return brush;
    }

    // ---- Chips ---------------------------------------------------------------------------

    /// <summary>The colour a chip is drawn in: the collection's own, or the theme's default.</summary>
    public Color ChipColour(Color? collectionColour)
        => collectionColour ?? Colour(TokenKeys.Calendar.ChipDefault);

    /// <summary>
    /// How the reference draws an appointment of each Show As: the bar down its left, the body
    /// behind its text, and the line round it.
    /// </summary>
    /// <remarks>
    /// Free is the odd one and deliberately so — it is drawn <em>hollow</em>, a neutral body with
    /// a pale bar, so an hour marked free reads as unclaimed rather than as another appointment.
    /// The two edge weights come from tokens because Black swaps them: there the saturated line
    /// belongs to Free and the pale one to Busy, which is the same inversion the ribbon's
    /// swatches make.
    /// </remarks>
    public ChipPaint Chip(Color? collectionColour, BusyStatus busy)
    {
        var colour = ChipColour(collectionColour);
        var ground = Colour(TokenKeys.Calendar.ChipGround);
        var edgeGround = Colour(TokenKeys.Calendar.ChipEdgeGround);
        var soft = Mix(colour, edgeGround, Number(TokenKeys.Calendar.ChipEdgeSoft, 0.36));
        var strong = Mix(colour, edgeGround, Number(TokenKeys.Calendar.ChipEdgeStrong, 0));
        var body = Mix(colour, ground, Number(TokenKeys.Calendar.ChipTint, 0.8));

        return busy switch
        {
            BusyStatus.Free => new ChipPaint(
                Body: Colour(TokenKeys.Calendar.ChipFreeFill),
                Bar: Colour(TokenKeys.Calendar.ChipFreeStripe),
                Edge: soft,
                Hatched: false,
                Dashed: false),
            BusyStatus.Tentative => new ChipPaint(body, colour, strong, Hatched: true, Dashed: true),
            BusyStatus.OutOfOffice => new ChipPaint(body, Colour(TokenKeys.Calendar.OutOfOffice), strong, Hatched: false, Dashed: false),
            _ => new ChipPaint(body, colour, strong, Hatched: false, Dashed: false),
        };
    }

    /// <summary>Mixes toward a ground: 0 is the colour itself, 1 the ground.</summary>
    public static Color Mix(Color colour, Color ground, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(colour.R + ((ground.R - colour.R) * t)),
            (byte)Math.Round(colour.G + ((ground.G - colour.G) * t)),
            (byte)Math.Round(colour.B + ((ground.B - colour.B) * t)));
    }
}

/// <summary>What a chip is painted with, once its Show As has been read.</summary>
/// <param name="Body">The fill behind the text.</param>
/// <param name="Bar">The stripe down the left edge, or the diagonals when hatched.</param>
/// <param name="Edge">The line round the chip.</param>
/// <param name="Hatched">Tentative: the bar is drawn as diagonals over <c>calendar.chip.hatch</c>.</param>
/// <param name="Dashed">Tentative: so is the line round it.</param>
public sealed record ChipPaint(Color Body, Color Bar, Color Edge, bool Hatched, bool Dashed);
