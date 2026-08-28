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

    /// <summary>
    /// The box a button on the Simplified bar occupies, which is what the pointer lights.
    /// </summary>
    /// <remarks>
    /// Measured off a capture with a button hovered: 41 in a 49-tall bar, so four clear rows
    /// above and below. It read as 30 while nothing drew it, and a 30-tall fill inside a 49-tall
    /// row is visibly a smaller button than the reference's.
    /// </remarks>
    public const double SimplifiedButtonHeight = 41;

    /// <summary>
    /// The corner of the box a button wears under the pointer. Measured: the fill rounds over
    /// about four rows at its corner.
    /// </summary>
    public const double ButtonCornerRadius = 4;

    /// <summary>
    /// The padding either side of a split button's chevron. Measured: its half is 20 wide,
    /// which a 9px glyph reaches with five each side.
    /// </summary>
    public const double SplitChevronPadding = 5;
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

    /// <summary>
    /// Height of the expanded ribbon body, excluding the tab strip. Measured off the classic
    /// capture: the panel runs 100 rows, from the row under the tab strip to the row above its
    /// shadow. It was 92 for a long time, and everything inside it sat 8px too high.
    /// </summary>
    public const double BodyHeight = 100;

    /// <summary>
    /// Height of the group label strip at the bottom of the body. The label is top-aligned in
    /// it, which puts its baseline on the body's 93rd row as measured — 7 rows above the bottom
    /// — in Selawik, whose 12px line box sits its baseline 10 rows down.
    /// </summary>
    public const double GroupLabelHeight = 17;

    /// <summary>Vertical space available to items, above the group label.</summary>
    public const double ItemAreaHeight = BodyHeight - GroupLabelHeight;

    /// <summary>
    /// A large button's content is top-aligned, not centred — a one-line label's icon sits where
    /// a two-line label's does — with the icon box starting on the body's 8th row (measured
    /// 8..39) and the label's baselines then landing on the 53rd and 69th, as measured.
    /// </summary>
    public const double LargeButtonPaddingTop = 8;

    /// <summary>The pitch of a large button's two label lines: measured 16, was 13.</summary>
    public const double LargeLabelLineHeight = 16;

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
    /// <summary>
    /// Measured from the reference rather than chosen: scanning the classic ribbon's icon row
    /// puts Reply at 340.5, Reply All at 384.5 and Forward at 431.5, so the pitch is 44 to 47.
    /// A large button is no wider than its label needs — and the label breaks into two lines
    /// where the reference's does (<c>LargeButtonLabel</c> in Core), which is what keeps "New
    /// Email" and "Reply All" at this pitch. There is no maximum: a wrap inside a fixed width
    /// used to break "Signature" in the middle and put "Send/Receive All Folders" on three lines.
    /// </summary>
    public const double LargeButtonMinWidth = 46;

    public const double SmallIconSize = 16;

    /// <summary>
    /// A small button's height, which is the pitch of a stack of them: measured 26 off the Move
    /// group's three rows and the Delete group's three icons (was 22). The stack starts
    /// <see cref="SmallStackTop"/> down the item area so its icons land on rows 9, 35 and 61 —
    /// measured 9, 35 and 60.
    /// </summary>
    public const double SmallButtonHeight = 26;
    public const double SmallStackTop = 4;
    public const double SmallButtonMinWidth = 60;

    /// <summary>Small buttons stack three to a column, filling the item area exactly.</summary>
    public const int SmallButtonsPerColumn = 3;

    /// <summary>
    /// A gallery's entries are shorter than a stack's buttons: three of them fill the 72px
    /// interior of the Quick Steps box, measured, so each is 24. The box itself starts on the
    /// body's 6th row and its border is a pixel.
    /// </summary>
    public const double GallerySlotHeight = 24;
    public const double GalleryTop = 6;
    public const double GalleryInteriorHeight = 3 * GallerySlotHeight;

    /// <summary>
    /// The rule between groups runs almost the body's whole height, through the label row:
    /// measured from the 5th row to the 93rd of 100, so 5 clear above and 6 below.
    /// </summary>
    public const double SeparatorTop = 5;
    public const double SeparatorBottom = 6;

    /// <summary>
    /// The Ribbon Display Options chevron sits in the bottom-right corner of the ribbon panel in
    /// both layouts, not on the bar's centre line. Measured off three captures — the shell's
    /// Simplified and Classic ribbons and the compose window's — the glyph's centre is 14px in
    /// from the panel's right edge and 13px up from its bottom in every one, so a box of these
    /// dimensions placed flush in the corner puts it there.
    /// </summary>
    public const double DisplayOptionsWidth = 28;
    public const double DisplayOptionsHeight = 26;

    /// <summary>
    /// The clearance the Simplified row keeps before the chevron's column, so that when the bar
    /// is full its "…" ends where the reference's does: measured 45px short of the panel's right
    /// edge, which is this plus the chevron's box.
    /// </summary>
    public const double DisplayOptionsGap = 17;
}
