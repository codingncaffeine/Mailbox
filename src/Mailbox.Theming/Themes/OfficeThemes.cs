using Mailbox.Theming.Tokens;

namespace Mailbox.Theming.Themes;

/// <summary>
/// The four built-in Office themes: Colorful, White, Dark Gray and Black.
/// </summary>
/// <remarks>
/// These are authored as <em>complete, explicit token sets</em> — never as deltas from a
/// generic base, and never passed through an abstraction that flattens them toward a common
/// denominator. That is the mechanism that makes "customization never compromises the default
/// look" structurally true rather than aspirational: a user theme overrides the same surface,
/// but nothing about the built-ins is derived, so nothing about them can be disturbed.
/// <para>
/// The fidelity harness gates these against reference captures in CI.
/// </para>
/// </remarks>
public static class OfficeThemes
{
    public const string Colorful = "colorful";
    public const string White = "white";
    public const string DarkGray = "darkgray";
    public const string Black = "black";

    public static IReadOnlyList<string> All { get; } = [Colorful, White, DarkGray, Black];

    public static string DisplayName(string id) => id switch
    {
        Colorful => "Colorful",
        White => "White",
        DarkGray => "Dark Gray",
        Black => "Black",
        _ => id,
    };

    public static bool IsDark(string id) => id is Black;

    public static TokenSet Build(string id) => id switch
    {
        Colorful => BuildColorful(),
        White => BuildWhite(),
        DarkGray => BuildDarkGray(),
        Black => BuildBlack(),
        _ => throw new ArgumentException($"'{id}' is not a built-in theme.", nameof(id)),
    };

    // ------------------------------------------------------------------------------------
    // Shared primitives. Geometry and type are identical across the four themes — Office
    // themes change colour, not layout. Density is a separate orthogonal axis.
    // ------------------------------------------------------------------------------------
    private static TokenSet Geometry()
    {
        var t = new TokenSet();

        // Type. Family values are logical names; the font resolver maps them to whatever is
        // actually installed, preferring the real Microsoft face when present.
        t.Set(TokenKeys.Typography.UiFamily, "Segoe UI");
        t.Set(TokenKeys.Typography.UiSize, "12");
        t.Set(TokenKeys.Typography.UiSizeSmall, "11");
        t.Set(TokenKeys.Typography.UiSizeLarge, "14");
        t.Set(TokenKeys.Typography.ContentFamily, "Calibri");
        t.Set(TokenKeys.Typography.ContentSize, "14.6667");
        t.Set(TokenKeys.Typography.MonoFamily, "Consolas");

        // Spacing scale.
        t.Set("space.0", "0");
        t.Set("space.1", "2");
        t.Set("space.2", "4");
        t.Set("space.3", "6");
        t.Set("space.4", "8");
        t.Set("space.5", "12");
        t.Set("space.6", "16");
        t.Set("space.7", "24");

        t.Set("radius.none", "0");
        t.Set("radius.small", "2");
        t.Set("radius.medium", "4");

        t.Set("border.width.hairline", "1");
        t.Set("motion.fast", "100");
        t.Set("motion.normal", "150");

        // Chrome geometry, measured from reference captures.
        t.Set(TokenKeys.Ribbon.TabStripHeight, "28");
        t.Set(TokenKeys.Ribbon.Height, "94");
        t.Set(TokenKeys.Nav.Width, "236");

        // Title bar, all measured. The search box clears the top by 7px and its left edge
        // sits where the message list starts: rail (57) + folder pane (236) + splitter (1).
        t.Set(TokenKeys.TitleBar.Height, "49");
        t.Set(TokenKeys.TitleBar.SearchWidth, "511");
        t.Set(TokenKeys.TitleBar.SearchHeight, "34");
        t.Set(TokenKeys.TitleBar.SearchOffset, "294");
        t.Set(TokenKeys.Rail.Width, "57");
        t.Set(TokenKeys.Rail.ItemHeight, "48");
        // The ribbon and the workspace are held the same distance off the window's right edge,
        // which is what makes them read as a stack of separate panels rather than one block.
        t.Set(TokenKeys.Workspace.Inset, "9");
        t.Set(TokenKeys.Rail.IndicatorWidth, "1.5");
        t.Set(TokenKeys.Rail.IndicatorHeight, "32");
        t.Set(TokenKeys.Rail.IndicatorInset, "6");
        t.Set(TokenKeys.List.Width, "436");
        t.Set(TokenKeys.List.RowHeight, "44");
        t.Set(TokenKeys.List.RowHeightCompact, "22");
        t.Set(TokenKeys.List.UnreadBarWidth, "4");
        t.Set(TokenKeys.List.GroupHeaderHeight, "26");
        t.Set(TokenKeys.StatusBar.Height, "24");

        return t;
    }

    // ------------------------------------------------------------------------------------
    // Colorful — the reference application default. Brand-blue title bar and tab strip, white ribbon.
    // ------------------------------------------------------------------------------------
    private static TokenSet BuildColorful()
    {
        var t = Geometry();

        // Palette
        t.Set("palette.brand.primary", "#0F6CBD");
        t.Set("palette.brand.dark", "#0C5595");
        t.Set("palette.brand.darker", "#0A4A82");
        t.Set("palette.brand.light", "#EFF6FC");
        t.Set("palette.neutral.white", "#FFFFFF");
        t.Set("palette.neutral.lighter", "#F3F2F1");
        t.Set("palette.neutral.light", "#EDEBE9");
        t.Set("palette.neutral.quaternary", "#E1DFDD");
        t.Set("palette.neutral.tertiary", "#C8C6C4");
        t.Set("palette.neutral.secondary", "#605E5C");
        t.Set("palette.neutral.primary", "#323130");
        t.Set("palette.neutral.dark", "#201F1E");

        LightSemantics(t);
        LightChrome(t);

        // The title bar alone carries the hue. The tab strip below it is light, which is what
        // an earlier guess got wrong — it had the blue running down through the tab strip.
        t.Set(TokenKeys.TitleBar.Background, "#0078D4");
        t.Set(TokenKeys.TitleBar.Foreground, "{palette.neutral.white}");
        t.Set(TokenKeys.TitleBar.Search, "#CCE4F6");
        t.Set(TokenKeys.TitleBar.SearchBorder, "#CCE4F6");   // no border in the capture
        t.Set(TokenKeys.TitleBar.SearchText, "#1664A7");

        // Chrome below the title bar, measured. Colorful and White share every one of these:
        // the two themes differ only in the title bar and the search box sitting in it.
        t.Set(TokenKeys.Rail.Background, "#EFE9E6");
        t.Set(TokenKeys.Rail.ItemText, "#242424");
        t.Set(TokenKeys.Rail.Indicator, "#0072C6");
        t.Set(TokenKeys.Nav.Background, "#F5F5F5");
        t.Set(TokenKeys.StatusBar.Background, "#F5F5F5");
        t.Set(TokenKeys.StatusBar.Foreground, "#242424");
        t.Set(TokenKeys.StatusBar.Slider, "#616161");
        t.Set(TokenKeys.List.HeaderBackground, "#FFFFFF");
        t.Set(TokenKeys.Ribbon.Background, "#FFFFFF");
        t.Set(TokenKeys.Ribbon.TabStripBackground, "#E9EEF2");
        t.Set(TokenKeys.Ribbon.TabRest, "#00000000");
        t.Set(TokenKeys.Ribbon.TabHover, "#14000000");
        t.Set(TokenKeys.Ribbon.TabSelected, "#00000000");
        t.Set(TokenKeys.Ribbon.TabUnderline, "#106EBE");
        t.Set(TokenKeys.Ribbon.TabText, "#242424");
        t.Set(TokenKeys.Ribbon.TabTextSelected, "#242424");

        return t;
    }

    // ------------------------------------------------------------------------------------
    // White — flat and pale. Title bar and tab strip are white; only the accent carries hue.
    // ------------------------------------------------------------------------------------
    private static TokenSet BuildWhite()
    {
        var t = Geometry();

        t.Set("palette.brand.primary", "#0F6CBD");
        t.Set("palette.brand.dark", "#0C5595");
        t.Set("palette.brand.darker", "#0A4A82");
        t.Set("palette.brand.light", "#EFF6FC");
        t.Set("palette.neutral.white", "#FFFFFF");
        t.Set("palette.neutral.lighter", "#F8F8F8");
        t.Set("palette.neutral.light", "#EDEBE9");
        t.Set("palette.neutral.quaternary", "#E1DFDD");
        t.Set("palette.neutral.tertiary", "#C8C6C4");
        t.Set("palette.neutral.secondary", "#605E5C");
        t.Set("palette.neutral.primary", "#323130");
        t.Set("palette.neutral.dark", "#201F1E");

        LightSemantics(t);
        LightChrome(t);

        // Not actually white: the title bar is the same pale blue-grey as the tab strip, and
        // only the ribbon and the content behind it are truly white.
        t.Set(TokenKeys.TitleBar.Background, "#E9EEF2");
        t.Set(TokenKeys.TitleBar.Foreground, "#242424");
        t.Set(TokenKeys.TitleBar.Search, "#FAFAFA");
        t.Set(TokenKeys.TitleBar.SearchBorder, "#D6D6D6");
        t.Set(TokenKeys.TitleBar.SearchText, "#616161");

        // Chrome below the title bar, measured. Colorful and White share every one of these:
        // the two themes differ only in the title bar and the search box sitting in it.
        t.Set(TokenKeys.Rail.Background, "#EFE9E6");
        t.Set(TokenKeys.Rail.ItemText, "#242424");
        t.Set(TokenKeys.Rail.Indicator, "#0072C6");
        t.Set(TokenKeys.Nav.Background, "#F5F5F5");
        t.Set(TokenKeys.StatusBar.Background, "#F5F5F5");
        t.Set(TokenKeys.StatusBar.Foreground, "#242424");
        t.Set(TokenKeys.StatusBar.Slider, "#616161");
        t.Set(TokenKeys.List.HeaderBackground, "#FFFFFF");
        t.Set(TokenKeys.Ribbon.Background, "#FFFFFF");
        t.Set(TokenKeys.Ribbon.TabStripBackground, "#E9EEF2");
        t.Set(TokenKeys.Ribbon.TabRest, "#00000000");
        t.Set(TokenKeys.Ribbon.TabHover, "#14000000");
        t.Set(TokenKeys.Ribbon.TabSelected, "#00000000");
        t.Set(TokenKeys.Ribbon.TabUnderline, "#106EBE");
        t.Set(TokenKeys.Ribbon.TabText, "#242424");
        t.Set(TokenKeys.Ribbon.TabTextSelected, "#242424");

        return t;
    }

    // ------------------------------------------------------------------------------------
    // Dark Gray — dark chrome around a light content area. The high-contrast Office theme.
    // ------------------------------------------------------------------------------------
    /// <remarks>
    /// Every value here is <em>measured</em> from reference captures of a running copy rather
    /// than guessed, by taking the modal colour of each flat region. The colours matter as much
    /// as the geometry, and an earlier hand-picked palette had this theme substantially wrong —
    /// notably the message rows, which are light (#D4D4D4) sitting inside a darker pane
    /// (#666666), not dark rows as the name suggests.
    /// </remarks>
    private static TokenSet BuildDarkGray()
    {
        var t = Geometry();

        t.Set("palette.brand.primary", "#0F6CBD");
        t.Set("palette.brand.dark", "#0C5595");
        t.Set("palette.brand.darker", "#0A4A82");
        t.Set("palette.brand.light", "#B3D3EC");     // measured: selected row
        t.Set("palette.neutral.white", "#FFFFFF");

        // Measured chrome.
        t.Set("palette.chrome.titlebar", "#555155");
        t.Set("palette.chrome.tabstrip", "#535154");
        t.Set("palette.chrome.ribbon", "#BDBDBD");
        t.Set("palette.chrome.rail", "#575255");
        t.Set("palette.chrome.nav", "#3D3D3D");
        t.Set("palette.chrome.navselected", "#666666");
        t.Set("palette.chrome.statusbar", "#525252");

        // Measured content.
        t.Set("palette.content.pane", "#666666");     // list background below the rows
        t.Set("palette.content.row", "#D4D4D4");      // the rows themselves
        t.Set("palette.content.groupheader", "#444444");

        t.Set("palette.neutral.lighter", "#E6E6E6");
        t.Set("palette.neutral.light", "#C9C9C9");
        t.Set("palette.neutral.quaternary", "#B0B0B0");
        t.Set("palette.neutral.tertiary", "#8A8A8A");
        t.Set("palette.neutral.secondary", "#505050");
        t.Set("palette.neutral.primary", "#262626");
        t.Set("palette.neutral.dark", "#1A1A1A");

        LightSemantics(t);
        LightChrome(t);

        // Content sits light inside dark chrome.
        t.Set(TokenKeys.Surface.Ground, "{palette.content.row}");
        t.Set(TokenKeys.Surface.Raised, "{palette.content.row}");
        t.Set(TokenKeys.Surface.Sunken, "{palette.content.pane}");

        t.Set(TokenKeys.TitleBar.Background, "{palette.chrome.titlebar}");
        t.Set(TokenKeys.TitleBar.Foreground, "{palette.neutral.white}");
        t.Set(TokenKeys.TitleBar.Search, "#BDBDBD");
        t.Set(TokenKeys.TitleBar.SearchBorder, "#808080");
        t.Set(TokenKeys.TitleBar.SearchText, "#424242");
        t.Set(TokenKeys.Rail.Background, "{palette.chrome.rail}");
        t.Set(TokenKeys.Rail.ItemText, "#E8E8E8");
        t.Set(TokenKeys.Rail.ItemActive, "#8FC3F0");
        t.Set(TokenKeys.Rail.Indicator, "#0072C6");
        t.Set(TokenKeys.Backstage.Field, "#9C9C9C");
        t.Set(TokenKeys.Ribbon.Background, "{palette.chrome.ribbon}");
        t.Set(TokenKeys.Ribbon.TabStripBackground, "{palette.chrome.tabstrip}");
        t.Set(TokenKeys.Ribbon.TabRest, "#00000000");
        t.Set(TokenKeys.Ribbon.TabHover, "#1AFFFFFF");
        t.Set(TokenKeys.Ribbon.TabSelected, "#00000000");
        t.Set(TokenKeys.Ribbon.TabUnderline, "#B3D6F2");
        t.Set(TokenKeys.Ribbon.TabText, "{palette.neutral.white}");
        t.Set(TokenKeys.Ribbon.TabTextSelected, "{palette.neutral.white}");

        t.Set(TokenKeys.Nav.Background, "{palette.chrome.nav}");
        t.Set(TokenKeys.Nav.ItemText, "{palette.neutral.white}");
        t.Set(TokenKeys.Nav.ItemHover, "#1AFFFFFF");
        t.Set(TokenKeys.Nav.ItemSelected, "{palette.chrome.navselected}");
        t.Set(TokenKeys.Nav.UnreadCount, "#8FC3F0");

        t.Set(TokenKeys.List.Background, "{palette.content.pane}");
        t.Set(TokenKeys.List.RowBackground, "{palette.content.row}");
        t.Set(TokenKeys.List.HeaderBackground, "{palette.content.pane}");
        t.Set(TokenKeys.List.HeaderText, "{palette.neutral.white}");
        t.Set(TokenKeys.List.RowSelected, "{palette.brand.light}");
        t.Set(TokenKeys.List.RowHover, "#22000000");
        t.Set(TokenKeys.List.UnreadText, "#024A91");   // measured unread blue
        t.Set(TokenKeys.List.ReadText, "{palette.neutral.primary}");
        t.Set(TokenKeys.List.PreviewText, "{palette.neutral.secondary}");
        t.Set(TokenKeys.List.GroupHeaderBackground, "{palette.content.groupheader}");
        t.Set(TokenKeys.List.GroupHeaderText, "{palette.neutral.white}");
        t.Set(TokenKeys.List.Separator, "{palette.neutral.quaternary}");

        t.Set(TokenKeys.Reading.Background, "{palette.content.row}");
        t.Set(TokenKeys.Reading.HeaderBackground, "{palette.content.row}");

        t.Set(TokenKeys.StatusBar.Background, "{palette.chrome.statusbar}");
        t.Set(TokenKeys.StatusBar.Slider, "#C7C7C7");
        t.Set(TokenKeys.StatusBar.Foreground, "{palette.neutral.white}");

        // Rules read as shadow, not highlight. Inheriting the light palette's #B0B0B0 painted
        // a bright white hairline across every dark surface.
        t.Set(TokenKeys.Border.Subtle, "#565656");
        t.Set(TokenKeys.Border.Strong, "#7A7A7A");

        return t;
    }

    // ------------------------------------------------------------------------------------
    // Black — true dark mode. Dark chrome and dark workspace.
    // ------------------------------------------------------------------------------------
    private static TokenSet BuildBlack()
    {
        var t = Geometry();

        t.Set("palette.brand.primary", "#4DA3F0");
        t.Set("palette.brand.dark", "#2B87DC");
        t.Set("palette.brand.darker", "#1B6CB8");
        t.Set("palette.brand.light", "#12283A");
        t.Set("palette.neutral.white", "#FFFFFF");
        t.Set("palette.neutral.lighter", "#2B2B2B");
        t.Set("palette.neutral.light", "#333333");
        t.Set("palette.neutral.quaternary", "#3F3F3F");
        t.Set("palette.neutral.tertiary", "#5A5A5A");
        t.Set("palette.neutral.secondary", "#A6A6A6");
        t.Set("palette.neutral.primary", "#E6E6E6");
        t.Set("palette.neutral.dark", "#F5F5F5");
        t.Set("palette.ground", "#1F1F1F");
        t.Set("palette.raised", "#252525");
        t.Set("palette.sunken", "#171717");

        // Semantics — dark. Deliberately authored rather than inverted from the light set:
        // a naive inversion produces muddy hover states and unreadable disabled text.
        t.Set(TokenKeys.Surface.Ground, "{palette.ground}");
        t.Set(TokenKeys.Surface.Raised, "{palette.raised}");
        t.Set(TokenKeys.Surface.Sunken, "{palette.sunken}");
        t.Set(TokenKeys.Surface.Overlay, "#2D2D2D");
        t.Set(TokenKeys.Text.Primary, "{palette.neutral.primary}");
        t.Set(TokenKeys.Text.Secondary, "{palette.neutral.secondary}");
        t.Set(TokenKeys.Text.Disabled, "{palette.neutral.tertiary}");
        t.Set(TokenKeys.Text.OnAccent, "#0A0A0A");
        t.Set(TokenKeys.Text.Link, "{palette.brand.primary}");
        t.Set(TokenKeys.Accent.Rest, "{palette.brand.primary}");
        t.Set(TokenKeys.Accent.Hover, "{palette.brand.dark}");
        t.Set(TokenKeys.Accent.Pressed, "{palette.brand.darker}");
        t.Set(TokenKeys.Accent.Subtle, "{palette.brand.light}");
        t.Set(TokenKeys.Accent.Disabled, "{palette.neutral.tertiary}");
        t.Set(TokenKeys.Border.Subtle, "{palette.neutral.quaternary}");
        t.Set(TokenKeys.Border.Strong, "{palette.neutral.tertiary}");
        t.Set(TokenKeys.Border.Focus, "{palette.brand.primary}");
        t.Set(TokenKeys.State.Hover, "#33FFFFFF");
        t.Set(TokenKeys.State.Selected, "#3D4DA3F0");
        t.Set(TokenKeys.State.SelectedInactive, "#26FFFFFF");
        t.Set(TokenKeys.State.Pressed, "#4DFFFFFF");
        t.Set(TokenKeys.Status.Success, "#5CC28C");
        t.Set(TokenKeys.Status.Warning, "#D9A441");
        t.Set(TokenKeys.Status.Danger, "#E8776F");
        t.Set(TokenKeys.Status.Info, "{palette.brand.primary}");

        // Measured. The chrome is not neutral black — it carries a blue cast, while the rail
        // goes the other way and is warm.
        t.Set(TokenKeys.TitleBar.Background, "#1B2127");
        t.Set(TokenKeys.TitleBar.Foreground, "#FFFFFF");
        t.Set(TokenKeys.TitleBar.Search, "#1F1F1F");
        t.Set(TokenKeys.TitleBar.SearchBorder, "#1F1F1F");   // no border in the capture
        t.Set(TokenKeys.TitleBar.SearchText, "#ADADAD");
        t.Set(TokenKeys.Rail.Background, "#201A17");
        t.Set(TokenKeys.Rail.ItemText, "#FFFFFF");
        t.Set(TokenKeys.Rail.ItemActive, "#58B8FE");
        t.Set(TokenKeys.Rail.Indicator, "#58B8FE");
        t.Set(TokenKeys.Backstage.Rail, "#1B1B1B");
        t.Set(TokenKeys.Backstage.RailText, "{palette.neutral.primary}");
        t.Set(TokenKeys.Backstage.RailDisabled, "{palette.neutral.tertiary}");
        t.Set(TokenKeys.Backstage.RailRule, "{palette.neutral.quaternary}");
        t.Set(TokenKeys.Backstage.Field, "{palette.raised}");
        t.Set(TokenKeys.Ribbon.Background, "#292929");
        t.Set(TokenKeys.Ribbon.TabStripBackground, "#1A2126");
        t.Set(TokenKeys.Ribbon.TabRest, "#00000000");
        t.Set(TokenKeys.Ribbon.TabHover, "#1AFFFFFF");
        t.Set(TokenKeys.Ribbon.TabSelected, "#00000000");
        t.Set(TokenKeys.Ribbon.TabUnderline, "#82C7FF");
        t.Set(TokenKeys.Ribbon.TabText, "#FFFFFF");
        t.Set(TokenKeys.Ribbon.TabTextSelected, "#FFFFFF");
        t.Set(TokenKeys.Ribbon.GroupLabel, "{palette.neutral.secondary}");
        t.Set(TokenKeys.Ribbon.GroupSeparator, "{palette.neutral.quaternary}");

        t.Set(TokenKeys.Nav.Background, "#141414");
        t.Set(TokenKeys.Nav.ItemText, "{palette.neutral.primary}");
        t.Set(TokenKeys.Nav.ItemHover, "{state.hover}");
        t.Set(TokenKeys.Nav.ItemSelected, "{state.selected}");
        t.Set(TokenKeys.Nav.UnreadCount, "{palette.brand.primary}");

        t.Set(TokenKeys.List.Background, "#262626");
        t.Set(TokenKeys.List.RowBackground, "#262626");
        t.Set(TokenKeys.List.HeaderBackground, "{palette.raised}");
        t.Set(TokenKeys.List.HeaderText, "{palette.neutral.secondary}");
        t.Set(TokenKeys.List.RowHover, "{state.hover}");
        t.Set(TokenKeys.List.RowSelected, "{state.selected}");
        t.Set(TokenKeys.List.UnreadBar, "{palette.brand.primary}");
        t.Set(TokenKeys.List.UnreadText, "{palette.brand.primary}");
        t.Set(TokenKeys.List.ReadText, "{palette.neutral.primary}");
        t.Set(TokenKeys.List.PreviewText, "{palette.neutral.secondary}");
        t.Set(TokenKeys.List.GroupHeaderBackground, "{palette.raised}");
        t.Set(TokenKeys.List.GroupHeaderText, "{palette.brand.primary}");
        t.Set(TokenKeys.List.Separator, "{palette.neutral.light}");

        t.Set(TokenKeys.Reading.Background, "{palette.ground}");
        t.Set(TokenKeys.Reading.HeaderBackground, "{palette.raised}");
        t.Set(TokenKeys.Reading.InfoBarBackground, "{palette.raised}");
        t.Set(TokenKeys.Reading.InfoBarText, "{palette.neutral.primary}");
        t.Set(TokenKeys.Reading.InfoBarWarningBackground, "#3A2E14");

        t.Set(TokenKeys.StatusBar.Background, "#141414");
        t.Set(TokenKeys.StatusBar.Slider, "#B2B2B2");
        t.Set(TokenKeys.StatusBar.Foreground, "{palette.neutral.secondary}");

        t.Set(TokenKeys.Calendar.Background, "{palette.ground}");
        t.Set(TokenKeys.Calendar.WorkingHoursFill, "{palette.raised}");
        t.Set(TokenKeys.Calendar.NonWorkingFill, "{palette.sunken}");
        t.Set(TokenKeys.Calendar.GridLine, "{palette.neutral.light}");
        t.Set(TokenKeys.Calendar.CurrentTimeIndicator, "#E8776F");
        t.Set(TokenKeys.Calendar.AllDayBandBackground, "{palette.raised}");

        return t;
    }

    // ------------------------------------------------------------------------------------
    // Light semantics and chrome, shared by Colorful, White and Dark Gray. Each of those
    // then overrides the frame colours that distinguish it.
    // ------------------------------------------------------------------------------------
    private static void LightSemantics(TokenSet t)
    {
        t.Set(TokenKeys.Surface.Ground, "{palette.neutral.white}");
        t.Set(TokenKeys.Surface.Raised, "{palette.neutral.white}");
        t.Set(TokenKeys.Surface.Sunken, "{palette.neutral.lighter}");
        t.Set(TokenKeys.Surface.Overlay, "{palette.neutral.white}");
        t.Set(TokenKeys.Text.Primary, "{palette.neutral.primary}");
        t.Set(TokenKeys.Text.Secondary, "{palette.neutral.secondary}");
        t.Set(TokenKeys.Text.Disabled, "{palette.neutral.tertiary}");
        t.Set(TokenKeys.Text.OnAccent, "{palette.neutral.white}");
        t.Set(TokenKeys.Text.Link, "{palette.brand.primary}");
        t.Set(TokenKeys.Accent.Rest, "{palette.brand.primary}");
        t.Set(TokenKeys.Accent.Hover, "{palette.brand.dark}");
        t.Set(TokenKeys.Accent.Pressed, "{palette.brand.darker}");
        t.Set(TokenKeys.Accent.Subtle, "{palette.brand.light}");
        t.Set(TokenKeys.Accent.Disabled, "{palette.neutral.tertiary}");
        t.Set(TokenKeys.Border.Subtle, "{palette.neutral.quaternary}");
        t.Set(TokenKeys.Border.Strong, "{palette.neutral.tertiary}");
        t.Set(TokenKeys.Border.Focus, "{palette.brand.primary}");
        t.Set(TokenKeys.State.Hover, "{palette.neutral.light}");
        t.Set(TokenKeys.State.Selected, "{palette.brand.light}");
        t.Set(TokenKeys.State.SelectedInactive, "{palette.neutral.light}");
        t.Set(TokenKeys.State.Pressed, "{palette.neutral.quaternary}");
        t.Set(TokenKeys.Status.Success, "#107C10");
        t.Set(TokenKeys.Status.Warning, "#797673");
        t.Set(TokenKeys.Status.Danger, "#A4262C");
        t.Set(TokenKeys.Status.Info, "{palette.brand.primary}");
    }

    private static void LightChrome(TokenSet t)
    {
        t.Set(TokenKeys.Ribbon.Background, "{palette.neutral.white}");
        t.Set(TokenKeys.Ribbon.GroupLabel, "{palette.neutral.secondary}");
        t.Set(TokenKeys.Ribbon.GroupSeparator, "{palette.neutral.quaternary}");

        t.Set(TokenKeys.Rail.Background, "{palette.neutral.lighter}");
        t.Set(TokenKeys.Rail.ItemText, "{palette.neutral.secondary}");
        t.Set(TokenKeys.Rail.ItemActive, "{palette.brand.primary}");
        t.Set(TokenKeys.Rail.Indicator, "#0072C6");

        // Backstage keeps a dark rail whatever the theme, so these are literal rather than
        // derived from the light palette around them.
        t.Set(TokenKeys.Backstage.Rail, "#2B2B2B");
        t.Set(TokenKeys.Backstage.RailText, "#F2F2F2");
        t.Set(TokenKeys.Backstage.RailDisabled, "#8A8A8A");
        t.Set(TokenKeys.Backstage.RailRule, "#4A4A4A");
        t.Set(TokenKeys.Backstage.Field, "{palette.neutral.lighter}");
        t.Set(TokenKeys.Nav.Background, "{palette.neutral.lighter}");
        t.Set(TokenKeys.Nav.ItemText, "{palette.neutral.primary}");
        t.Set(TokenKeys.Nav.ItemHover, "{palette.neutral.light}");
        t.Set(TokenKeys.Nav.ItemSelected, "{palette.neutral.quaternary}");
        t.Set(TokenKeys.Nav.UnreadCount, "{palette.brand.primary}");

        t.Set(TokenKeys.List.Background, "{palette.neutral.white}");
        t.Set(TokenKeys.List.RowBackground, "{palette.neutral.white}");
        t.Set(TokenKeys.List.HeaderBackground, "{palette.neutral.lighter}");
        t.Set(TokenKeys.List.HeaderText, "{palette.neutral.secondary}");
        t.Set(TokenKeys.List.RowHover, "{palette.neutral.lighter}");
        t.Set(TokenKeys.List.RowSelected, "{palette.brand.light}");
        t.Set(TokenKeys.List.UnreadBar, "{palette.brand.primary}");
        t.Set(TokenKeys.List.UnreadText, "{palette.brand.primary}");
        t.Set(TokenKeys.List.ReadText, "{palette.neutral.primary}");
        t.Set(TokenKeys.List.PreviewText, "{palette.neutral.secondary}");
        t.Set(TokenKeys.List.GroupHeaderBackground, "{palette.neutral.lighter}");
        t.Set(TokenKeys.List.GroupHeaderText, "{palette.brand.primary}");
        t.Set(TokenKeys.List.Separator, "{palette.neutral.light}");

        t.Set(TokenKeys.Reading.Background, "{palette.neutral.white}");
        t.Set(TokenKeys.Reading.HeaderBackground, "{palette.neutral.white}");
        t.Set(TokenKeys.Reading.InfoBarBackground, "{palette.neutral.lighter}");
        t.Set(TokenKeys.Reading.InfoBarText, "{palette.neutral.primary}");
        t.Set(TokenKeys.Reading.InfoBarWarningBackground, "#FFF4CE");

        t.Set(TokenKeys.StatusBar.Background, "{palette.brand.primary}");
        t.Set(TokenKeys.StatusBar.Foreground, "{palette.neutral.white}");

        t.Set(TokenKeys.Calendar.Background, "{palette.neutral.white}");
        t.Set(TokenKeys.Calendar.WorkingHoursFill, "{palette.neutral.white}");
        t.Set(TokenKeys.Calendar.NonWorkingFill, "{palette.neutral.lighter}");
        t.Set(TokenKeys.Calendar.GridLine, "{palette.neutral.light}");
        t.Set(TokenKeys.Calendar.CurrentTimeIndicator, "#A4262C");
        t.Set(TokenKeys.Calendar.AllDayBandBackground, "{palette.neutral.lighter}");
    }
}
