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
        public const string Height = "ribbon.height";
        public const string TabStripHeight = "ribbon.tabstrip.height";
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

    public static class Calendar
    {
        public const string Background = "calendar.background";
        public const string WorkingHoursFill = "calendar.workinghours.fill";
        public const string NonWorkingFill = "calendar.nonworking.fill";
        public const string GridLine = "calendar.gridline";
        public const string CurrentTimeIndicator = "calendar.currenttime";
        public const string AllDayBandBackground = "calendar.allday.background";
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
        Ribbon.GroupSeparator, Ribbon.Height, Ribbon.TabStripHeight,
        Workspace.Inset,
        Compose.BodyBackground, Compose.BodyText,
        Compose.HeaderBackground, Compose.FieldRule,
        Nav.Background, Nav.ItemText, Nav.ItemHover, Nav.ItemSelected, Nav.UnreadCount, Nav.Width,
        List.Background, List.RowBackground, List.HeaderBackground, List.HeaderText,
        List.RowHeight, List.RowHeightCompact, List.RowHover, List.RowSelected,
        List.UnreadBar, List.UnreadBarWidth, List.UnreadText, List.ReadText, List.PreviewText,
        List.GroupHeaderBackground, List.GroupHeaderText, List.GroupHeaderHeight,
        List.Separator, List.Width,
        Reading.Background, Reading.HeaderBackground, Reading.InfoBarBackground,
        Reading.InfoBarText, Reading.InfoBarWarningBackground,
        StatusBar.Background, StatusBar.Foreground, StatusBar.Height, StatusBar.Slider,
        Dialog.Background, Dialog.Foreground, Dialog.ForegroundSubtle,
        Dialog.Surface, Dialog.SurfaceText,
        Dialog.Border, Dialog.Selection,
        Calendar.Background, Calendar.WorkingHoursFill, Calendar.NonWorkingFill,
        Calendar.GridLine, Calendar.CurrentTimeIndicator, Calendar.AllDayBandBackground,
        Typography.UiFamily, Typography.UiSize, Typography.UiSizeSmall, Typography.UiSizeLarge,
        Typography.ContentFamily, Typography.ContentSize, Typography.MonoFamily,
    ];
}
