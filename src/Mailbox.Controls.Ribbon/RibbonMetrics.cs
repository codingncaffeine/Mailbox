namespace Mailbox.Controls.Ribbon;

/// <summary>
/// Chrome measurements taken from reference captures of the reference application rather than guessed.
/// </summary>
/// <remarks>
/// These are the numbers the fidelity harness checks against. Kept in one place so a
/// correction lands everywhere at once, and separate from the theme tokens because Office
/// themes change colour, not layout.
/// </remarks>
public static class RibbonMetrics
{
    /// <summary>Height of the tab strip alone.</summary>
    public const double TabStripHeight = 29;

    /// <summary>Height of the Simplified single-row bar. Roughly a third of the classic body.</summary>
    public const double SimplifiedHeight = 49;

    /// <summary>
    /// The ribbon body is a rounded panel floating on the chrome rather than a full-bleed
    /// band: measured 8px corners, flush against the app rail on the left and held 9px off
    /// the window's right edge.
    /// </summary>
    public const double BodyCornerRadius = 8;

    /// <summary>Clearance between the ribbon panel and the window's right edge.</summary>
    public const double BodyRightInset = 9;

    /// <summary>
    /// Gap between the ribbon panel and the content panel below it. The two are separate
    /// rounded surfaces on the chrome, not one continuous block.
    /// </summary>
    public const double BodyBottomGap = 6;

    public const double SimplifiedButtonHeight = 30;
    public const double SimplifiedIconSize = 18;

    /// <summary>
    /// Em size for a Simplified-bar glyph. Measured: the reference's icons carry 17 rows of ink,
    /// and deriving the size from the 18px box gave 10. The box is left alone so the 36px button
    /// pitch holds; only the glyph inside it grows.
    /// </summary>
    public const double SimplifiedIconFontSize = 20;

    /// <summary>
    /// Padding either side of an icon-only button on the Simplified bar. Measured: Bold, Italic
    /// and Underline sit at x=327, 363 and 399 on the compose ribbon, a pitch of 36 around an
    /// 18px glyph.
    /// </summary>
    public const double SimplifiedGlyphPadding = 9;

    /// <summary>
    /// Where the first control starts, measured in from the panel's left edge: the panel begins
    /// at x=12 and Paste's glyph at x=31, which with the padding above puts the inset at 13.
    /// </summary>
    public const double SimplifiedRowInset = 13;

    /// <summary>
    /// Padding either side of a field on the bar. The reference's rule sits at x=115 and the
    /// Font box starts at 128.
    /// </summary>
    public const double FieldPadding = 6;

    /// <summary>
    /// A field on the Simplified bar. Measured off the compose ribbon: the Font box runs
    /// x=128–234 and the Font Size box x=251–301, both 28px tall in a 48px row.
    /// </summary>
    public const double FieldHeight = 28;
    public const double FieldWidth = 107;
    public const double FontSizeFieldWidth = 51;

    /// <summary>
    /// The vertical rule between clusters on the Simplified bar, measured off the compose
    /// ribbon: 1px wide and 32px tall, which is taller than the 30px buttons it divides.
    /// </summary>
    public const double InlineSeparatorHeight = 32;

    /// <summary>
    /// Clearance either side of that rule. The reference's Message row puts Format Painter's
    /// glyph at x=86–100, the rule at 115 and the Font box at 128.
    /// </summary>
    public const double InlineSeparatorMargin = 6;

    /// <summary>Height of the expanded ribbon body, excluding the tab strip.</summary>
    public const double BodyHeight = 92;

    /// <summary>Height of the group label strip at the bottom of the body.</summary>
    public const double GroupLabelHeight = 16;

    /// <summary>Vertical space available to items, above the group label.</summary>
    public const double ItemAreaHeight = BodyHeight - GroupLabelHeight;

    public const double TabPaddingH = 11;

    /// <summary>Thickness of the rule marking the active tab.</summary>
    public const double TabUnderlineThickness = 2;

    /// <summary>Clearance between that rule and the bottom of the tab strip.</summary>
    public const double TabUnderlineGap = 3;

    /// <summary>
    /// Where the rule starts, measured from the top of the strip. Derived from the gap rather
    /// than bottom-aligning the rule, because the control it sits in is not always exactly the
    /// strip's height and the overflow is clipped from the bottom.
    /// </summary>
    public const double TabUnderlineTop =
        TabStripHeight - TabUnderlineGap - TabUnderlineThickness;
    public const double GroupPaddingH = 6;
    public const double GroupMinWidth = 32;

    public const double LargeIconSize = 32;
    public const double LargeButtonMinWidth = 46;

    /// <summary>
    /// Measured from the reference rather than chosen: scanning the classic ribbon's icon row
    /// puts Reply at 340.5, Reply All at 384.5 and Forward at 431.5, so the pitch is 44 to 47.
    /// </summary>
    /// <remarks>
    /// This is what makes a two-word label wrap. At 84 it did not, so "New Email" and "Reply
    /// All" each rendered on one line and every large button was half again as wide as the
    /// reference's — which pushed the whole Home tab past the window and had groups collapsing
    /// at widths where the reference collapses nothing.
    /// </remarks>
    public const double LargeButtonMaxWidth = 54;

    public const double SmallIconSize = 16;
    public const double SmallButtonHeight = 22;
    public const double SmallButtonMinWidth = 60;

    /// <summary>Small buttons stack three to a column, filling the item area exactly.</summary>
    public const int SmallButtonsPerColumn = 3;

    public const double SeparatorMargin = 3;
}
