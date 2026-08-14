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
    public const double TabStripHeight = 26;

    /// <summary>Height of the Simplified single-row bar. Roughly a third of the classic body.</summary>
    public const double SimplifiedHeight = 40;

    public const double SimplifiedButtonHeight = 30;
    public const double SimplifiedIconSize = 18;

    /// <summary>Height of the expanded ribbon body, excluding the tab strip.</summary>
    public const double BodyHeight = 92;

    /// <summary>Height of the group label strip at the bottom of the body.</summary>
    public const double GroupLabelHeight = 16;

    /// <summary>Vertical space available to items, above the group label.</summary>
    public const double ItemAreaHeight = BodyHeight - GroupLabelHeight;

    public const double TabPaddingH = 11;
    public const double GroupPaddingH = 6;
    public const double GroupMinWidth = 32;

    public const double LargeIconSize = 32;
    public const double LargeButtonMinWidth = 46;
    public const double LargeButtonMaxWidth = 84;

    public const double SmallIconSize = 16;
    public const double SmallButtonHeight = 22;
    public const double SmallButtonMinWidth = 60;

    /// <summary>Small buttons stack three to a column, filling the item area exactly.</summary>
    public const int SmallButtonsPerColumn = 3;

    public const double SeparatorMargin = 3;

    /// <summary>Width below which the ribbon starts collapsing groups to popup buttons.</summary>
    public const double CollapseThreshold = 40;
}
