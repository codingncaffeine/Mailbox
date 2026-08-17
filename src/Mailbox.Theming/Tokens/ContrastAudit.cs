using System.Globalization;

namespace Mailbox.Theming.Tokens;

/// <summary>One ink-on-ground pair that fell short: which two tokens, their colours, the ratio.</summary>
public sealed record ContrastFinding(string Ink, string Ground, string InkColour, string GroundColour, double Ratio)
{
    public override string ToString()
        => $"{Ink} ({InkColour}) on {Ground} ({GroundColour}) is {Ratio:0.00}:1";
}

/// <summary>
/// The contrast checker §8 asks for: every pair of a text token and the surface behind it,
/// against the WCAG ratio, over a resolved theme. The built-ins are held to it by a test; a
/// theme file is checked when it is applied, and told in the log which of its words cannot be
/// read — because a token that is present but unreadable is exactly what the coverage gate
/// cannot see.
/// </summary>
public static class ContrastAudit
{
    /// <summary>The loosest ratio that still means "can be read": WCAG's threshold for large text.</summary>
    public const double MinimumRatio = 3.0;

    /// <summary>
    /// The ink-on-ground pairs — only pairs somebody has to read. Marks such as the tab
    /// underline and the rail indicator are shapes read by being somewhere, and keep a weaker
    /// rule in the tests.
    /// </summary>
    public static IReadOnlyList<(string Ink, string Ground)> Pairs { get; } =
    [
        (TokenKeys.Ribbon.TabText, TokenKeys.Ribbon.TabStripBackground),
        (TokenKeys.Ribbon.TabTextSelected, TokenKeys.Ribbon.TabStripBackground),
        (TokenKeys.TitleBar.SearchText, TokenKeys.TitleBar.Search),
        (TokenKeys.TitleBar.Foreground, TokenKeys.TitleBar.Background),
        (TokenKeys.Rail.ItemText, TokenKeys.Rail.Background),
        (TokenKeys.List.UnreadText, TokenKeys.List.RowBackground),
        (TokenKeys.List.ReadText, TokenKeys.List.RowBackground),
        (TokenKeys.Dialog.Foreground, TokenKeys.Dialog.Background),
        (TokenKeys.Dialog.ForegroundSubtle, TokenKeys.Dialog.Background),
        (TokenKeys.Dialog.SurfaceText, TokenKeys.Dialog.Surface),
        (TokenKeys.Compose.HeaderText, TokenKeys.Compose.HeaderBackground),
        (TokenKeys.Compose.HeaderLabel, TokenKeys.Compose.HeaderBackground),
        (TokenKeys.Compose.BodyText, TokenKeys.Compose.BodyBackground),
        (TokenKeys.SystemDialog.Foreground, TokenKeys.SystemDialog.Background),
        (TokenKeys.SystemDialog.Foreground, TokenKeys.SystemDialog.Banner),
        (TokenKeys.SystemDialog.Foreground, TokenKeys.SystemDialog.Surface),
        (TokenKeys.SystemDialog.Foreground, TokenKeys.SystemDialog.ListBackground),
        (TokenKeys.SystemDialog.Foreground, TokenKeys.SystemDialog.Selection),
        (TokenKeys.SystemDialog.Foreground, TokenKeys.SystemDialog.SelectionFocused),
        (TokenKeys.SystemDialog.Foreground, TokenKeys.SystemDialog.Button),
        (TokenKeys.SystemDialog.Foreground, TokenKeys.SystemDialog.TitleBar),
        (TokenKeys.Text.Primary, TokenKeys.Surface.Ground),
        (TokenKeys.Nav.ItemText, TokenKeys.Nav.Background),
        (TokenKeys.StatusBar.Foreground, TokenKeys.StatusBar.Background),
        (TokenKeys.Ribbon.GroupLabel, TokenKeys.Ribbon.Background),
        (TokenKeys.Calendar.DayText, TokenKeys.Calendar.Background),
        (TokenKeys.Calendar.PastText, TokenKeys.Calendar.PastFill),
        (TokenKeys.Calendar.HeaderText, TokenKeys.Calendar.HeaderBackground),
        (TokenKeys.Calendar.TodayText, TokenKeys.Calendar.TodayFill),
        (TokenKeys.Calendar.ChipText, TokenKeys.Calendar.ChipFreeFill),
        (TokenKeys.Calendar.ToolbarText, TokenKeys.List.Background),
        (TokenKeys.Calendar.ToolbarButtonText, TokenKeys.Calendar.ToolbarButton),
        (TokenKeys.Calendar.NavigatorText, TokenKeys.Calendar.NavigatorBackground),
        (TokenKeys.Calendar.NavigatorRangeText, TokenKeys.Calendar.NavigatorRange),
        (TokenKeys.Calendar.TodayText, TokenKeys.Calendar.NavigatorToday),
        (TokenKeys.Peek.Day, TokenKeys.Peek.Background),
        (TokenKeys.Peek.DayOther, TokenKeys.Peek.Background),
        (TokenKeys.Peek.Title, TokenKeys.Peek.Background),
        (TokenKeys.Peek.Text, TokenKeys.Peek.Background),
        (TokenKeys.Peek.TextDim, TokenKeys.Peek.Background),
        (TokenKeys.Peek.TodayText, TokenKeys.Peek.Today),
        (TokenKeys.Peek.PopDay, TokenKeys.Peek.PopBackground),
        (TokenKeys.Peek.PopDayOther, TokenKeys.Peek.PopBackground),
        (TokenKeys.Peek.PopTitle, TokenKeys.Peek.PopBackground),
        (TokenKeys.Peek.PopText, TokenKeys.Peek.PopBackground),
        (TokenKeys.Peek.PopTextDim, TokenKeys.Peek.PopBackground),
        (TokenKeys.Peek.PopTodayText, TokenKeys.Peek.PopToday),

        // A note's face is a colour mixed toward its ground, so the ground is the end of that
        // mix and the honest thing to hold the ink against: what passes here passes on every
        // note, whatever category it carries.
        (TokenKeys.Notes.Text, TokenKeys.Notes.Ground),
        (TokenKeys.Notes.TextDim, TokenKeys.Notes.Ground),
        (TokenKeys.Journal.HeaderText, TokenKeys.Journal.HeaderBackground),
        (TokenKeys.Journal.EntryText, TokenKeys.Journal.EntryGround),
    ];

    /// <summary>Every pair below the ratio, in the order of <see cref="Pairs"/>. Empty is a pass.</summary>
    public static IReadOnlyList<ContrastFinding> Check(ResolvedTokens tokens, double minimum = MinimumRatio)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var findings = new List<ContrastFinding>();

        foreach (var (ink, ground) in Pairs)
        {
            if (!tokens.Contains(ink) || !tokens.Contains(ground)) continue;
            var inkColour = tokens.GetString(ink);
            var groundColour = tokens.GetString(ground);
            if (Luminance(inkColour) is not { } a || Luminance(groundColour) is not { } b) continue;

            var ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
            if (ratio < minimum) findings.Add(new ContrastFinding(ink, ground, inkColour, groundColour, ratio));
        }

        return findings;
    }

    /// <summary>The WCAG contrast ratio between two colours, 1.0 (identical) to 21.0; null if either is not a colour.</summary>
    public static double? Ratio(string first, string second)
    {
        if (Luminance(first) is not { } a || Luminance(second) is not { } b) return null;
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>WCAG relative luminance of <c>#RRGGBB</c> / <c>#AARRGGBB</c>: sRGB linearised, then weighted for the eye.</summary>
    public static double? Luminance(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#') return null;
        var text = hex[1..];
        if (text.Length == 8) text = text[2..];
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;

        static double Channel(int value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
    }
}
