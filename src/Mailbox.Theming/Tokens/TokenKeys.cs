namespace Mailbox.Theming.Tokens;

/// <summary>
/// Well-known token names. Referencing these instead of string literals is what lets the
/// coverage audit prove no surface is unreachable — a hard-coded colour anywhere in the UI
/// is a build failure, not a style nit.
/// </summary>
public static class TokenKeys
{
    public static class Surface
    {
        public const string Ground = "surface.ground";
        public const string Raised = "surface.raised";
        public const string Sunken = "surface.sunken";
        public const string Overlay = "surface.overlay";
    }

    public static class Text
    {
        public const string Primary = "text.primary";
        public const string Secondary = "text.secondary";
        public const string Disabled = "text.disabled";
        public const string OnAccent = "text.onaccent";
        public const string Link = "text.link";
    }

    public static class Accent
    {
        public const string Rest = "accent.rest";
        public const string Hover = "accent.hover";
        public const string Pressed = "accent.pressed";
        public const string Subtle = "accent.subtle";
        public const string Disabled = "accent.disabled";
    }

    public static class Border
    {
        public const string Subtle = "border.subtle";
        public const string Strong = "border.strong";
        public const string Focus = "border.focus";
    }

    public static class State
    {
        public const string Hover = "state.hover";
        public const string Selected = "state.selected";
        public const string SelectedInactive = "state.selectedinactive";
        public const string Pressed = "state.pressed";
    }

    public static class Status
    {
        public const string Success = "status.success";
        public const string Warning = "status.warning";
        public const string Danger = "status.danger";
        public const string Info = "status.info";
    }

    public static class TitleBar
    {
        public const string Background = "titlebar.background";
        public const string Foreground = "titlebar.foreground";
        public const string Search = "titlebar.search";
        public const string SearchBorder = "titlebar.search.border";
        public const string SearchText = "titlebar.search.text";
        public const string Height = "titlebar.height";
        public const string SearchWidth = "titlebar.search.width";
        public const string SearchHeight = "titlebar.search.height";
        public const string SearchOffset = "titlebar.search.offset";
    }

    /// <summary>The account button in the title bar, and the panel it opens.</summary>
    public static class Avatar
    {
        /// <summary>Fill of the initial circle. Theme-driven, not derived from the account.</summary>
        public const string Background = "avatar.background";
        public const string Foreground = "avatar.foreground";
    }

    /// <summary>The six colour categories, by name rather than by value.</summary>
    public static class Category
    {
        public const string Red = "category.red";
        public const string Orange = "category.orange";
        public const string Yellow = "category.yellow";
        public const string Green = "category.green";
        public const string Blue = "category.blue";
        public const string Purple = "category.purple";

        public static readonly IReadOnlyList<string> All =
            [Red, Orange, Yellow, Green, Blue, Purple];
    }

    public static class Ribbon
    {
        public const string Background = "ribbon.background";
        public const string TabStripBackground = "ribbon.tabstrip.background";
        public const string TabRest = "ribbon.tab.rest";
        public const string TabHover = "ribbon.tab.hover";
        public const string TabSelected = "ribbon.tab.selected";
        public const string TabUnderline = "ribbon.tab.underline";
        public const string TabText = "ribbon.tab.text";
        public const string TabTextSelected = "ribbon.tab.text.selected";
        public const string GroupLabel = "ribbon.group.label";
        public const string GroupSeparator = "ribbon.group.separator";

        /// <summary>
        /// A gallery's box on the ribbon — Quick Steps. Its own pair rather than the content
        /// surface's, because a gallery is chrome: in Dark Gray the reference draws it in the
        /// panel's own fill with a mid-grey line, and the content pane's dark grey there painted
        /// it as a dark box in a light panel.
        /// </summary>
        public const string GalleryBackground = "ribbon.gallery.background";
        public const string GalleryBorder = "ribbon.gallery.border";

        /// <summary>
        /// The box round a dropdown's button while its own menu is open, which is how the menu
        /// says which button it belongs to.
        /// </summary>
        /// <remarks>
        /// Measured in Dark Gray, the one theme an open menu was captured in: a #CCCCCC face
        /// inside a #5C5C5C line, on a #BDBDBD ribbon — <em>lighter</em> than the ribbon, where a
        /// pressed content control is darker than its surface. The other three are authored from
        /// their own neutrals, since nothing lighter than a white ribbon exists to use.
        /// </remarks>
        public const string ButtonOpen = "ribbon.button.open";
        public const string ButtonOpenBorder = "ribbon.button.open.border";

        /// <summary>
        /// The fill under the pointer. Measured #D1D1D1 on Dark Gray's #BDBDBD ribbon — a plain
        /// button takes this and nothing else, no line round it.
        /// </summary>
        public const string ButtonHover = "ribbon.button.hover";

        /// <summary>
        /// The line round a <em>split</em> button under the pointer, and the divider between its
        /// two halves. A split button is outlined where a plain one is not, because the outline
        /// is what says the two halves can be hit separately — and only the half being pointed at
        /// takes the fill.
        /// </summary>
        public const string ButtonSplitBorder = "ribbon.button.split.border";
        public const string Height = "ribbon.height";
        public const string TabStripHeight = "ribbon.tabstrip.height";
    }

    /// <summary>
    /// The ribbon's icons are not one colour.
    /// </summary>
    /// <remarks>
    /// The reference draws them as polychrome artwork — outlined shapes with a light fill, and
    /// colour where colour means something: Reply and Reply All are magenta, Forward and the
    /// Apps grid and Change View are blue, Send/Receive is green, Follow Up is a red flag on a
    /// grey pole, Categorize is four coloured swatches. Everything else is that one dark
    /// outline, which is why an accent-blue ribbon reads as another application.
    /// <para>
    /// Each theme has its own tint of the whole set, and it is not a shade of the accent: it is
    /// picked so the artwork reads on that theme's ribbon. Every value here is measured off the
    /// four theme captures, and Black inverts each pair of the swatches and the flag, drawing
    /// the light colour as the outline and the saturated one as the fill.
    /// </para>
    /// <para>
    /// Our icons come from a monochrome font, so a glyph takes one of these and the fill and the
    /// small coloured badges inside a two-tone icon — New Email's green plus, Archive's green
    /// lid, Move To's blue arrow — are not reproduced. The two whose meaning <em>is</em> their
    /// colours are drawn instead, by <c>RibbonArtwork</c>.
    /// </para>
    /// </remarks>
    public static class RibbonIcon
    {
        /// <summary>
        /// The dark line every outlined icon is drawn with — most of the ribbon.
        /// </summary>
        /// <remarks>
        /// The reference's icons are outlined shapes with a light fill, and the outline is this
        /// one colour throughout: New Email, Delete, Archive, Move To, Unread/Read, Address
        /// Book, Filter, Quick Steps and the rest. A monochrome font can draw the outline and
        /// not the fill, so this is what the ribbon's glyphs take unless a command says
        /// otherwise. It is not <c>text.primary</c>: that is the near-black of the formatting
        /// run, which <c>NeutralIcon</c> asks for and the reference draws a shade darker again.
        /// </remarks>
        public const string Outline = "ribbon.icon.outline";

        /// <summary>Reply and Reply All — the magenta arrows.</summary>
        public const string Magenta = "ribbon.icon.magenta";

        /// <summary>Forward's arrow, the Apps grid and Change View — the icons drawn wholly in blue.</summary>
        public const string Blue = "ribbon.icon.blue";

        /// <summary>Send/Receive All Folders' circling arrows, drawn wholly in green.</summary>
        public const string Green = "ribbon.icon.green";

        /// <summary>The Follow Up flag: its cloth, its outline, and the pole under it.</summary>
        public const string Flag = "ribbon.icon.flag";
        public const string FlagOutline = "ribbon.icon.flag.outline";
        public const string FlagPole = "ribbon.icon.flag.pole";

        /// <summary>Categorize's four swatches, each a fill inside a 1px outline.</summary>
        public const string SwatchBlue = "ribbon.icon.swatch.blue";
        public const string SwatchBlueOutline = "ribbon.icon.swatch.blue.outline";
        public const string SwatchGrey = "ribbon.icon.swatch.grey";
        public const string SwatchGreyOutline = "ribbon.icon.swatch.grey.outline";
        public const string SwatchGold = "ribbon.icon.swatch.gold";
        public const string SwatchGoldOutline = "ribbon.icon.swatch.gold.outline";
        public const string SwatchGreen = "ribbon.icon.swatch.green";
        public const string SwatchGreenOutline = "ribbon.icon.swatch.green.outline";

        public static readonly IReadOnlyList<string> All =
        [
            Outline, Magenta, Blue, Green, Flag, FlagOutline, FlagPole,
            SwatchBlue, SwatchBlueOutline, SwatchGrey, SwatchGreyOutline,
            SwatchGold, SwatchGoldOutline, SwatchGreen, SwatchGreenOutline,
        ];
    }

    public static class Backstage
    {
        /// <summary>The dark page rail down the left of the File view.</summary>
        public const string Rail = "backstage.rail";
        public const string RailText = "backstage.rail.text";
        public const string RailDisabled = "backstage.rail.disabled";
        public const string RailRule = "backstage.rail.rule";

        /// <summary>Fill behind the account picker and the large section buttons.</summary>
        public const string Field = "backstage.field";
    }

    public static class Rail
    {
        /// <summary>The vertical app rail is a shade apart from the folder pane beside it.</summary>
        public const string Background = "rail.background";
        public const string ItemText = "rail.item.text";
        public const string ItemActive = "rail.item.active";
        public const string Width = "rail.width";
        public const string ItemHeight = "rail.item.height";
        public const string Indicator = "rail.indicator";
        public const string IndicatorWidth = "rail.indicator.width";
        public const string IndicatorHeight = "rail.indicator.height";
        public const string IndicatorInset = "rail.indicator.inset";
    }

    /// <summary>The workspace below the ribbon: its own rounded surface on the chrome.</summary>
    public static class Workspace
    {
        public const string Inset = "workspace.inset";
    }

    public static class Nav
    {
        public const string Background = "nav.background";
        public const string ItemText = "nav.item.text";
        public const string ItemHover = "nav.item.hover";
        public const string ItemSelected = "nav.item.selected";
        public const string UnreadCount = "nav.unreadcount";
        public const string Width = "nav.width";
    }

    public static class List
    {
        public const string Background = "list.background";
        public const string RowBackground = "list.row.background";
        public const string HeaderBackground = "list.header.background";
        public const string HeaderText = "list.header.text";
        public const string RowHeight = "list.row.height";
        public const string RowHeightCompact = "list.row.height.compact";
        public const string RowHover = "list.row.hover";
        public const string RowSelected = "list.row.selected";
        public const string UnreadBar = "list.row.unread.bar";
        public const string UnreadBarWidth = "list.row.unread.bar.width";
        public const string UnreadText = "list.row.unread.text";
        public const string ReadText = "list.row.read.text";
        public const string PreviewText = "list.row.preview.text";
        public const string GroupHeaderBackground = "list.group.header.background";
        public const string GroupHeaderText = "list.group.header.text";
        public const string GroupHeaderHeight = "list.group.header.height";
        public const string Separator = "list.separator";
        public const string Width = "list.width";

        /// <summary>
        /// A task that is due today or already late, which the reference's to-do list draws in
        /// red — the same red as the flag beside it.
        /// </summary>
        public const string OverdueText = "list.overdue.text";
    }

    public static class Reading
    {
        public const string Background = "reading.background";
        public const string HeaderBackground = "reading.header.background";
        public const string InfoBarBackground = "reading.infobar.background";
        public const string InfoBarText = "reading.infobar.text";
        public const string InfoBarWarningBackground = "reading.infobar.warning.background";
    }

    /// <summary>
    /// The compose window's own surfaces. Its body is a document rather than a pane: the
    /// reference draws it white even in Dark Gray, where the reading pane is #D4D4D4.
    /// </summary>
    public static class Compose
    {
        public const string BodyBackground = "compose.body.background";
        public const string BodyText = "compose.body.text";
        public const string HeaderBackground = "compose.header.background";

        /// <summary>
        /// The ink on that header, and on the labels beside it.
        /// </summary>
        /// <remarks>
        /// Its own pair rather than <c>text.primary</c> and <c>text.secondary</c>, for the same
        /// reason a dialog has its own six: <b>the compose header is chrome, not content</b>, and
        /// in Dark Gray those disagree — content is light and chrome is dark, so content ink on
        /// this header is near-black on near-black. It was, and only in the one theme the owner
        /// actually runs, which is how it survived. Measured off the reference.
        /// </remarks>
        public const string HeaderText = "compose.header.text";

        public const string HeaderLabel = "compose.header.label";
        public const string FieldRule = "compose.field.rule";
    }

    public static class StatusBar
    {
        public const string Background = "statusbar.background";
        public const string Foreground = "statusbar.foreground";
        public const string Height = "statusbar.height";

        /// <summary>Track and tick of the zoom control; the reference draws both as one hairline.</summary>
        public const string Slider = "statusbar.slider";
    }

    /// <summary>
    /// Dialog chrome: Options, Account Settings, and everything else that opens over the shell.
    /// </summary>
    /// <remarks>
    /// A dialog is chrome, not content, and the two do not agree in every theme. Dark Gray is
    /// the case that proves it: its content is light and its chrome is dark, so a dialog painted
    /// from the content surface comes out inverted — light where the reference is dark, with the
    /// list boxes on it losing the contrast that makes them read as boxes at all.
    /// </remarks>
    public static class Dialog
    {
        /// <summary>The dialog's own ground.</summary>
        public const string Background = "dialog.background";

        /// <summary>Labels, headings and notes standing directly on that ground.</summary>
        public const string Foreground = "dialog.foreground";

        /// <summary>Quieter ink on it: explanatory notes, a field's units, a status line.</summary>
        public const string ForegroundSubtle = "dialog.foreground.subtle";

        /// <summary>The boxes on it: the page rail, list panes, buttons and fields.</summary>
        public const string Surface = "dialog.surface";

        /// <summary>Text inside those boxes, which is not the same ink as on the ground.</summary>
        public const string SurfaceText = "dialog.surface.text";

        /// <summary>The line around a box.</summary>
        public const string Border = "dialog.border";

        /// <summary>A selected row inside one.</summary>
        public const string Selection = "dialog.selection";
    }

    /// <summary>
    /// The dialogs the reference draws with the operating system's own controls rather than
    /// its own — Account Settings and its children — which stay light in every theme, the
    /// dark ones included.
    /// </summary>
    /// <remarks>
    /// Its own family rather than the six dialog tokens because the two disagree by design:
    /// a themed dialog is chrome and follows the theme, while these follow the desktop's light
    /// dialog palette whatever the theme says. Every built-in carries the same values, all of
    /// them measured off the Account Settings captures; a theme file may still override any
    /// of them, which is why they are tokens and not literals in the views.
    /// </remarks>
    public static class SystemDialog
    {
        /// <summary>The caption band.</summary>
        public const string TitleBar = "systemdialog.titlebar";
        /// <summary>The dialog's ground.</summary>
        public const string Background = "systemdialog.background";
        /// <summary>The white band under the caption that names the page, and the rule under it.</summary>
        public const string Banner = "systemdialog.banner";
        public const string BannerRule = "systemdialog.banner.rule";
        public const string Foreground = "systemdialog.foreground";
        public const string ForegroundDisabled = "systemdialog.foreground.disabled";
        /// <summary>A tab's page.</summary>
        public const string Surface = "systemdialog.surface";
        /// <summary>A tab that is not selected, and the page's faint shadow.</summary>
        public const string Tab = "systemdialog.tab";
        /// <summary>The faint line round a tab, a page and between a list's column headers.</summary>
        public const string Border = "systemdialog.border";

        /// <summary>
        /// The line round a screentip. Its own value rather than the dialog's line, which is much
        /// lighter: measured #666666 round the reference's #F0F0F0 tip, and a #E5E5E5 edge would
        /// leave the tip with no visible boundary at all against a light window.
        /// </summary>
        public const string TooltipBorder = "systemdialog.tooltip.border";
        public const string ListBackground = "systemdialog.list.background";
        public const string ListBorder = "systemdialog.list.border";
        /// <summary>A selected row while the list does not have the focus, and while it does.</summary>
        public const string Selection = "systemdialog.selection";
        public const string SelectionFocused = "systemdialog.selection.focused";
        /// <summary>A toolbar button or row under the pointer.</summary>
        public const string Hover = "systemdialog.hover";
        public const string HoverBorder = "systemdialog.hover.border";
        public const string Pressed = "systemdialog.pressed";
        /// <summary>The line round a focused or hovered push button, and the focus rectangle.</summary>
        public const string Accent = "systemdialog.accent";
        /// <summary>The line round a text field.</summary>
        public const string FieldBorder = "systemdialog.field.border";
        /// <summary>A push button: its fill, its line, and the darker line along its bottom edge.</summary>
        public const string Button = "systemdialog.button";
        public const string ButtonBorder = "systemdialog.button.border";
        public const string ButtonBorderBottom = "systemdialog.button.border.bottom";
        public const string ButtonDisabled = "systemdialog.button.disabled";
        public const string ButtonDisabledBorder = "systemdialog.button.disabled.border";

        /// <summary>
        /// The palette of the small coloured toolbar icons these dialogs carry: an envelope, a
        /// hammer and wrench, a form and pencil, a folder, a book, and the arrows.
        /// </summary>
        public const string IconInk = "systemdialog.icon.ink";
        public const string IconPaper = "systemdialog.icon.paper";
        public const string IconGold = "systemdialog.icon.gold";
        public const string IconGoldDark = "systemdialog.icon.gold.dark";
        public const string IconSteel = "systemdialog.icon.steel";
        public const string IconSteelDark = "systemdialog.icon.steel.dark";
        public const string IconWood = "systemdialog.icon.wood";
        public const string IconGreen = "systemdialog.icon.green";
        public const string IconBlue = "systemdialog.icon.blue";
        public const string IconBlueDark = "systemdialog.icon.blue.dark";

        public static readonly IReadOnlyList<string> All =
        [
            TitleBar, Background, Banner, BannerRule, Foreground, ForegroundDisabled,
            Surface, Tab, Border, ListBackground, ListBorder, Selection, SelectionFocused,
            Hover, HoverBorder, Pressed, Accent, FieldBorder,
            Button, ButtonBorder, ButtonBorderBottom, ButtonDisabled, ButtonDisabledBorder,
            TooltipBorder,
            IconInk, IconPaper, IconGold, IconGoldDark, IconSteel, IconSteelDark,
            IconWood, IconGreen, IconBlue, IconBlueDark,
        ];
    }

    /// <summary>
    /// The calendar views (§7.4): the month grid's cells and chips, the day and week views'
    /// hours, and the date navigator. Measured off the reference's month view where a
    /// capture exists; the rest follows the theme's own semantics.
    /// </summary>
    public static class Calendar
    {
        /// <summary>A day cell that is today or later; the ground of the day and week views.</summary>
        public const string Background = "calendar.background";
        /// <summary>A day cell already gone by — the reference shades the past.</summary>
        public const string PastFill = "calendar.past.fill";
        /// <summary>Today's whole cell in the month view.</summary>
        public const string TodayFill = "calendar.today.fill";
        public const string TodayText = "calendar.today.text";
        /// <summary>The selected day, or the selected time slot.</summary>
        public const string SelectedFill = "calendar.selected.fill";
        public const string WorkingHoursFill = "calendar.workinghours.fill";
        public const string NonWorkingFill = "calendar.nonworking.fill";
        public const string GridLine = "calendar.gridline";
        public const string CurrentTimeIndicator = "calendar.currenttime";
        public const string AllDayBandBackground = "calendar.allday.background";
        /// <summary>The weekday header row across the top of a view.</summary>
        public const string HeaderBackground = "calendar.header.background";
        public const string HeaderText = "calendar.header.text";
        /// <summary>The line closing the weekday header, which the reference draws lighter than the grid's own.</summary>
        public const string HeaderLine = "calendar.header.line";
        /// <summary>The day numbers in the month view.</summary>
        public const string DayText = "calendar.day.text";
        /// <summary>A day number already gone by: the reference dims the past as well as shading it.</summary>
        public const string PastText = "calendar.past.text";
        /// <summary>The time ruler down the day and week views.</summary>
        public const string HourText = "calendar.hour.text";

        /// <summary>The row above a calendar view holding Today, the arrows and the date.</summary>
        public const string ToolbarText = "calendar.toolbar.text";
        public const string ToolbarButton = "calendar.toolbar.button";
        public const string ToolbarButtonBorder = "calendar.toolbar.button.border";
        public const string ToolbarButtonText = "calendar.toolbar.button.text";
        /// <summary>
        /// A link on the appointment form, which sits on the workspace's own ground rather than
        /// on a page: light blue over the dark themes' chrome, the brand blue over the light
        /// themes' white.
        /// </summary>
        public const string Link = "calendar.link";

        /// <summary>The colour of a calendar that has none of its own.</summary>
        public const string ChipDefault = "calendar.chip.default";
        /// <summary>What a chip's colour is tinted toward for its fill.</summary>
        public const string ChipGround = "calendar.chip.ground";
        /// <summary>How far toward the ground the fill goes: 0 is the colour itself, 1 the ground.</summary>
        public const string ChipTint = "calendar.chip.tint";
        public const string ChipText = "calendar.chip.text";
        /// <summary>A Free appointment is drawn hollow — a neutral fill rather than a tint of its colour.</summary>
        public const string ChipFreeFill = "calendar.chip.free.fill";
        /// <summary>The pale stripe down a Free appointment, where a Busy one has a solid bar.</summary>
        public const string ChipFreeStripe = "calendar.chip.free.stripe";
        /// <summary>The ground the Tentative stripe's diagonals are drawn over.</summary>
        public const string ChipHatch = "calendar.chip.hatch";
        /// <summary>What a chip's edge is mixed toward when it is softened.</summary>
        public const string ChipEdgeGround = "calendar.chip.edge.ground";
        /// <summary>
        /// How far a Free chip's edge moves toward <see cref="ChipEdgeGround"/>: the reference draws
        /// it as the lighter of the two edges. Black inverts the pair, as it inverts the ribbon's
        /// swatches, so this is a token rather than a constant.
        /// </summary>
        public const string ChipEdgeSoft = "calendar.chip.edge.soft";
        /// <summary>The same mix for a Busy, Tentative or Out of Office chip's edge.</summary>
        public const string ChipEdgeStrong = "calendar.chip.edge.strong";
        /// <summary>The Out of Office stripe.</summary>
        public const string OutOfOffice = "calendar.outofoffice";

        /// <summary>The panel the date navigator's months are drawn on, inside the navigation pane.</summary>
        public const string NavigatorBackground = "calendar.navigator.background";
        public const string NavigatorText = "calendar.navigator.text";
        /// <summary>The date navigator's block over the days a view is showing.</summary>
        public const string NavigatorRange = "calendar.navigator.range";
        /// <summary>Ink on that block — dark in the light themes, light in the dark ones.</summary>
        public const string NavigatorRangeText = "calendar.navigator.range.text";
        /// <summary>Today in the date navigator.</summary>
        public const string NavigatorToday = "calendar.navigator.today";
    }

    /// <summary>
    /// The calendar peek: the miniature month and day's agenda the rail's Calendar icon opens,
    /// and the same content pinned down the right-hand edge beside the mail.
    /// </summary>
    /// <remarks>
    /// Two sets, because the reference draws the two states in two different palettes and the
    /// capture of it says so plainly: the docked pane is the application's and follows the
    /// theme — light grid on white, #F3F2F1 on #666666 in Dark Gray — while the floating one is
    /// a desktop popup and keeps the desktop's own light colours whatever the theme is, exactly
    /// as Account Settings does. The <c>pop</c> half is therefore the same in all four built-ins,
    /// and a theme file may still override any of it.
    /// </remarks>
    public static class Peek
    {
        /// <summary>The docked pane's ground: the list's own, which it sits beside.</summary>
        public const string Background = "peek.background";
        /// <summary>The line down its left edge, which is all that separates it from the list.</summary>
        public const string Divider = "peek.divider";
        /// <summary>The month's name and the row of weekday letters.</summary>
        public const string Title = "peek.title";
        /// <summary>A day of the month on show.</summary>
        public const string Day = "peek.day";
        /// <summary>A day belonging to the month either side of it.</summary>
        public const string DayOther = "peek.day.other";
        public const string Today = "peek.today";
        public const string TodayText = "peek.today.text";
        /// <summary>A day cell under the pointer.</summary>
        public const string Hover = "peek.hover";
        /// <summary>The line under the grid, and the lighter row above it that gives it depth.</summary>
        public const string Rule = "peek.rule";
        public const string RuleSoft = "peek.rule.soft";
        /// <summary>The agenda's day name, times and subjects.</summary>
        public const string Text = "peek.text";
        /// <summary>The second line of an agenda entry — where it is, or who called it.</summary>
        public const string TextDim = "peek.text.dim";
        /// <summary>The ground a Tentative entry's diagonals are drawn over.</summary>
        public const string Hatch = "peek.hatch";

        /// <summary>The floating popup's ground.</summary>
        public const string PopBackground = "peek.pop.background";
        /// <summary>The broad light frame round the popup, and the hairline round that.</summary>
        public const string PopFrame = "peek.pop.frame";
        public const string PopOutline = "peek.pop.outline";
        public const string PopTitle = "peek.pop.title";
        public const string PopDay = "peek.pop.day";
        public const string PopDayOther = "peek.pop.day.other";
        public const string PopToday = "peek.pop.today";
        public const string PopTodayText = "peek.pop.today.text";
        public const string PopHover = "peek.pop.hover";
        public const string PopText = "peek.pop.text";
        public const string PopTextDim = "peek.pop.text.dim";
        public const string PopHatch = "peek.pop.hatch";

        /// <summary>The docked half, which every theme states for itself.</summary>
        public static readonly IReadOnlyList<string> Docked =
        [
            Background, Divider, Title, Day, DayOther, Today, TodayText, Hover,
            Rule, RuleSoft, Text, TextDim, Hatch,
        ];

        /// <summary>The floating half, which the desktop states and every theme repeats.</summary>
        public static readonly IReadOnlyList<string> Floating =
        [
            PopBackground, PopFrame, PopOutline, PopTitle, PopDay, PopDayOther,
            PopToday, PopTodayText, PopHover, PopText, PopTextDim, PopHatch,
        ];

        public static readonly IReadOnlyList<string> All = [.. Docked, .. Floating];
    }

    public static class Typography
    {
        public const string UiFamily = "type.ui.family";
        public const string UiSize = "type.ui.size";
        public const string UiSizeSmall = "type.ui.size.small";
        public const string UiSizeLarge = "type.ui.size.large";
        public const string ContentFamily = "type.content.family";
        public const string ContentSize = "type.content.size";
        public const string MonoFamily = "type.mono.family";
    }

    /// <summary>
    /// Every semantic and component token the shell requires. The coverage audit asserts a
    /// theme defines all of these before it is allowed to load.
    /// </summary>
    public static IReadOnlyList<string> Required { get; } =
    [
        Surface.Ground, Surface.Raised, Surface.Sunken, Surface.Overlay,
        Text.Primary, Text.Secondary, Text.Disabled, Text.OnAccent, Text.Link,
        Accent.Rest, Accent.Hover, Accent.Pressed, Accent.Subtle, Accent.Disabled,
        Border.Subtle, Border.Strong, Border.Focus,
        State.Hover, State.Selected, State.SelectedInactive, State.Pressed,
        Status.Success, Status.Warning, Status.Danger, Status.Info,
        TitleBar.Background, TitleBar.Foreground, TitleBar.Search,
        TitleBar.SearchBorder, TitleBar.SearchText, TitleBar.Height,
        TitleBar.SearchWidth, TitleBar.SearchHeight, TitleBar.SearchOffset,
        Avatar.Background, Avatar.Foreground,
        Category.Red, Category.Orange, Category.Yellow,
        Category.Green, Category.Blue, Category.Purple,
        Rail.Background, Rail.ItemText, Rail.ItemActive, Rail.Width, Rail.ItemHeight,
        Rail.Indicator, Rail.IndicatorWidth, Rail.IndicatorHeight, Rail.IndicatorInset,
        Backstage.Rail, Backstage.RailText, Backstage.RailDisabled,
        Backstage.RailRule, Backstage.Field,
        Ribbon.Background, Ribbon.TabStripBackground, Ribbon.TabRest, Ribbon.TabHover,
        Ribbon.TabSelected, Ribbon.TabUnderline, Ribbon.TabText, Ribbon.TabTextSelected, Ribbon.GroupLabel,
        Ribbon.GroupSeparator, Ribbon.GalleryBackground, Ribbon.GalleryBorder,
        Ribbon.ButtonOpen, Ribbon.ButtonOpenBorder, Ribbon.ButtonHover, Ribbon.ButtonSplitBorder,
        Ribbon.Height, Ribbon.TabStripHeight,
        .. RibbonIcon.All,
        Workspace.Inset,
        Compose.BodyBackground, Compose.BodyText,
        Compose.HeaderBackground, Compose.FieldRule,
        Nav.Background, Nav.ItemText, Nav.ItemHover, Nav.ItemSelected, Nav.UnreadCount, Nav.Width,
        List.Background, List.RowBackground, List.HeaderBackground, List.HeaderText,
        List.RowHeight, List.RowHeightCompact, List.RowHover, List.RowSelected,
        List.UnreadBar, List.UnreadBarWidth, List.UnreadText, List.ReadText, List.PreviewText,
        List.GroupHeaderBackground, List.GroupHeaderText, List.GroupHeaderHeight,
        List.Separator, List.Width, List.OverdueText,
        Reading.Background, Reading.HeaderBackground, Reading.InfoBarBackground,
        Reading.InfoBarText, Reading.InfoBarWarningBackground,
        StatusBar.Background, StatusBar.Foreground, StatusBar.Height, StatusBar.Slider,
        Dialog.Background, Dialog.Foreground, Dialog.ForegroundSubtle,
        Dialog.Surface, Dialog.SurfaceText,
        Dialog.Border, Dialog.Selection,
        .. SystemDialog.All,
        Calendar.Background, Calendar.PastFill, Calendar.TodayFill, Calendar.TodayText, Calendar.SelectedFill,
        Calendar.WorkingHoursFill, Calendar.NonWorkingFill,
        Calendar.GridLine, Calendar.CurrentTimeIndicator, Calendar.AllDayBandBackground,
        Calendar.HeaderBackground, Calendar.HeaderText, Calendar.HeaderLine,
        Calendar.DayText, Calendar.PastText, Calendar.HourText,
        Calendar.ToolbarText, Calendar.ToolbarButton, Calendar.ToolbarButtonBorder, Calendar.ToolbarButtonText,
        Calendar.Link,
        Calendar.ChipDefault, Calendar.ChipGround, Calendar.ChipTint, Calendar.ChipText,
        Calendar.ChipFreeFill, Calendar.ChipFreeStripe, Calendar.ChipHatch,
        Calendar.ChipEdgeGround, Calendar.ChipEdgeSoft, Calendar.ChipEdgeStrong, Calendar.OutOfOffice,
        Calendar.NavigatorBackground, Calendar.NavigatorText,
        Calendar.NavigatorRange, Calendar.NavigatorRangeText, Calendar.NavigatorToday,
        .. Peek.All,
        Typography.UiFamily, Typography.UiSize, Typography.UiSizeSmall, Typography.UiSizeLarge,
        Typography.ContentFamily, Typography.ContentSize, Typography.MonoFamily,
    ];
}
