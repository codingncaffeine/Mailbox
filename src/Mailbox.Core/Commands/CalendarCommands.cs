namespace Mailbox.Core.Commands;

/// <summary>
/// The Calendar module's command set, mirroring the reference's Home, Send/Receive and View
/// tabs for that module.
/// </summary>
/// <remarks>
/// Transcribed from the calendar captures: the Home bar reads New Appointment · New Meeting ·
/// Add Focus Time | Today · Next 7 Days | Day · Work Week · Week · Month · Schedule View |
/// Add · Share, with the rules measured at x = 493, 712, 1190 and 1365.
/// <para>
/// As in <see cref="MailCommands"/>, an entry with <see cref="MailboxCommand.InDefaultLayout"/>
/// false is a real command that the shipped ribbon simply does not place — searchable, bindable
/// and one drag away in Customize Ribbon.
/// </para>
/// </remarks>
public static class CalendarCommands
{
    // ---- New ---------------------------------------------------------------------------
    public static readonly MailboxCommand NewAppointment = new()
    {
        Id = new("calendar.new.appointment"),
        Label = "New Appointment",
        Description = "Create a new appointment in your calendar.",
        Icon = "new-appointment",
        Category = "New",
        Scope = ModuleScope.Calendar,
        KeyTip = "NA",
        DefaultGesture = "Ctrl+Shift+A",
    };

    public static readonly MailboxCommand NewMeeting = new()
    {
        Id = new("calendar.new.meeting"),
        Label = "New Meeting",
        Description = "Invite people to a new appointment and track their replies.",
        Icon = "meeting",
        Category = "New",
        Scope = ModuleScope.Calendar,
        KeyTip = "NM",
        DefaultGesture = "Ctrl+Shift+Q",
    };

    public static readonly MailboxCommand NewItems = new()
    {
        Id = new("calendar.new.items"),
        Label = "New Items",
        Description = "Create a new item of any type.",
        Icon = "new-items",
        Category = "New",
        Scope = ModuleScope.Calendar,
        KeyTip = "NI",
    };

    /// <summary>
    /// Books the next free block of the working day so nothing else can take it.
    /// </summary>
    /// <remarks>
    /// The reference's own button reaches a service this application has no equivalent of, so
    /// what it does here is what the name says and nothing more: the next block of the length
    /// the Options page names, in a gap on the calendar, marked Busy. Rule 2 — the feature is
    /// the reference's, the mechanism is ours.
    /// </remarks>
    public static readonly MailboxCommand AddFocusTime = new()
    {
        Id = new("calendar.focustime"),
        Label = "Add Focus Time",
        Description = "Book the next free block of your working day for focused work.",
        Icon = "focus-time",
        Category = "New",
        Scope = ModuleScope.Calendar,
        KeyTip = "FT",
    };

    // ---- Go To -------------------------------------------------------------------------
    public static readonly MailboxCommand Today = new()
    {
        Id = new("calendar.today"),
        Label = "Today",
        Description = "Go to today.",
        Icon = "today",
        Category = "Go To",
        Scope = ModuleScope.Calendar,
        KeyTip = "OD",
    };

    public static readonly MailboxCommand Next7Days = new()
    {
        Id = new("calendar.next7"),
        Label = "Next 7 Days",
        Description = "Show the next seven days.",
        Icon = "next-7",
        Category = "Go To",
        Scope = ModuleScope.Calendar,
        KeyTip = "O7",
    };

    public static readonly MailboxCommand GoToDate = new()
    {
        Id = new("calendar.goto"),
        Label = "Go to Date",
        Description = "Show a date you choose.",
        Icon = "goto-date",
        Category = "Go To",
        Scope = ModuleScope.Calendar,
        KeyTip = "OG",
        DefaultGesture = "Ctrl+G",
    };

    public static readonly MailboxCommand Back = new()
    {
        Id = new("calendar.back"),
        Label = "Back",
        Description = "Move the view back one period.",
        Icon = "chevron-left",
        Category = "Go To",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand Forward = new()
    {
        Id = new("calendar.forward"),
        Label = "Forward",
        Description = "Move the view on one period.",
        Icon = "chevron-right",
        Category = "Go To",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    // ---- Arrange -----------------------------------------------------------------------
    public static readonly MailboxCommand DayView = new()
    {
        Id = new("calendar.view.day"),
        Label = "Day",
        Description = "Show one day.",
        Icon = "day-view",
        Category = "Arrange",
        Scope = ModuleScope.Calendar,
        KeyTip = "AD",
        DefaultGesture = "Ctrl+Alt+1",
    };

    public static readonly MailboxCommand WorkWeekView = new()
    {
        Id = new("calendar.view.workweek"),
        Label = "Work Week",
        Description = "Show your working week.",
        Icon = "work-week",
        Category = "Arrange",
        Scope = ModuleScope.Calendar,
        KeyTip = "AW",
        DefaultGesture = "Ctrl+Alt+2",
    };

    public static readonly MailboxCommand WeekView = new()
    {
        Id = new("calendar.view.week"),
        Label = "Week",
        Description = "Show a whole week.",
        Icon = "week-view",
        Category = "Arrange",
        Scope = ModuleScope.Calendar,
        KeyTip = "AK",
        DefaultGesture = "Ctrl+Alt+3",
    };

    public static readonly MailboxCommand MonthView = new()
    {
        Id = new("calendar.view.month"),
        Label = "Month",
        Description = "Show a month at a time.",
        Icon = "month-view",
        Category = "Arrange",
        Scope = ModuleScope.Calendar,
        KeyTip = "AM",
        DefaultGesture = "Ctrl+Alt+4",
    };

    public static readonly MailboxCommand ScheduleView = new()
    {
        Id = new("calendar.view.schedule"),
        Label = "Schedule View",
        Description = "Lay the day out sideways, a row per calendar.",
        Icon = "schedule-view",
        Category = "Arrange",
        Scope = ModuleScope.Calendar,
        KeyTip = "AS",
    };

    public static readonly MailboxCommand TimeScale = new()
    {
        Id = new("calendar.timescale"),
        Label = "Time Scale",
        Description = "Set how much time each row of the day and week views covers.",
        Icon = "time-scale",
        Category = "Arrange",
        Scope = ModuleScope.Calendar,
        KeyTip = "AT",
    };

    public static readonly MailboxCommand Overlay = new()
    {
        Id = new("calendar.overlay"),
        Label = "Overlay",
        Description = "Draw the shown calendars over one another rather than side by side.",
        Icon = "overlay",
        Category = "Arrange",
        Scope = ModuleScope.Calendar,
        KeyTip = "AO",
    };

    // ---- Manage Calendars --------------------------------------------------------------
    public static readonly MailboxCommand OpenCalendar = new()
    {
        Id = new("calendar.open"),
        Label = "Add",
        Description = "Add a calendar: one of your own, one from a file, or one on the internet.",
        Icon = "add",
        Category = "Manage Calendars",
        Scope = ModuleScope.Calendar,
        KeyTip = "MA",
    };

    public static readonly MailboxCommand NewCalendar = new()
    {
        Id = new("calendar.new.calendar"),
        Label = "Create New Blank Calendar",
        Description = "Make another calendar of your own.",
        Icon = "calendar",
        Category = "Manage Calendars",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand OpenFromInternet = new()
    {
        Id = new("calendar.open.internet"),
        Label = "From Internet",
        Description = "Subscribe to a calendar published on the web.",
        Icon = "publish-calendar",
        Category = "Manage Calendars",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand OpenFromFile = new()
    {
        Id = new("calendar.open.file"),
        Label = "Open Calendar File",
        Description = "Read an iCalendar file into a calendar of your own.",
        Icon = "folder-open",
        Category = "Manage Calendars",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand CalendarGroups = new()
    {
        Id = new("calendar.groups"),
        Label = "Calendar Groups",
        Description = "Keep sets of calendars you look at together.",
        Icon = "calendar-groups",
        Category = "Manage Calendars",
        Scope = ModuleScope.Calendar,
        KeyTip = "MG",
    };

    public static readonly MailboxCommand DeleteCalendar = new()
    {
        Id = new("calendar.delete.calendar"),
        Label = "Delete Calendar",
        Description = "Remove a calendar and everything on it.",
        Icon = "delete",
        Category = "Manage Calendars",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand CalendarColour = new()
    {
        Id = new("calendar.colour"),
        Label = "Colour",
        Description = "Choose the colour this calendar's appointments are drawn in.",
        Icon = "calendar-color",
        Category = "Manage Calendars",
        Scope = ModuleScope.Calendar,
        KeyTip = "MC",
    };

    // ---- Share -------------------------------------------------------------------------
    public static readonly MailboxCommand Share = new()
    {
        Id = new("calendar.share"),
        Label = "Share",
        Description = "Send a calendar on, or publish it for others to subscribe to.",
        Icon = "share",
        Category = "Share",
        Scope = ModuleScope.Calendar,
        KeyTip = "SH",
    };

    public static readonly MailboxCommand EmailCalendar = new()
    {
        Id = new("calendar.share.email"),
        Label = "E-mail Calendar",
        Description = "Send a stretch of your calendar in a message.",
        Icon = "email-calendar",
        Category = "Share",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand PublishCalendar = new()
    {
        Id = new("calendar.share.publish"),
        Label = "Publish Online",
        Description = "Put this calendar on a server others can subscribe to.",
        Icon = "publish-calendar",
        Category = "Share",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    // ---- An appointment ----------------------------------------------------------------
    public static readonly MailboxCommand OpenItem = new()
    {
        Id = new("calendar.item.open"),
        Label = "Open",
        Description = "Open the selected appointment.",
        Icon = "folder-open",
        Category = "Actions",
        Scope = ModuleScope.Calendar,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand DeleteItem = new()
    {
        Id = new("calendar.item.delete"),
        Label = "Delete",
        Description = "Delete the selected appointment.",
        Icon = "delete",
        Category = "Actions",
        Scope = ModuleScope.Calendar,
        RequiresSelection = true,
        DefaultGesture = "Delete",
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand Recurrence = new()
    {
        Id = new("calendar.item.recurrence"),
        Label = "Recurrence",
        Description = "Make the selected appointment repeat, or change how it repeats.",
        Icon = "recurrence",
        Category = "Actions",
        Scope = ModuleScope.Calendar,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand Categorize = new()
    {
        Id = new("calendar.item.categorize"),
        Label = "Categorize",
        Description = "Put the selected appointment in a colour category.",
        Icon = "categorize",
        IconArtwork = "categorize",
        Category = "Actions",
        Scope = ModuleScope.Calendar,
        RequiresSelection = true,
        InDefaultLayout = false,
    };

    // ---- The View tab ------------------------------------------------------------------
    public static readonly MailboxCommand DailyTaskList = new()
    {
        Id = new("calendar.view.tasklist"),
        Label = "Daily Task List",
        Description = "Show the day's tasks beneath the calendar.",
        Icon = "daily-task-list",
        Category = "Layout",
        Scope = ModuleScope.Calendar,
        KeyTip = "VT",
    };

    public static readonly MailboxCommand CalendarOptions = new()
    {
        Id = new("calendar.options"),
        Label = "Calendar Options",
        Description = "Open the Calendar page of Options.",
        Icon = "calendar-settings",
        Category = "Arrange",
        Scope = ModuleScope.Calendar,
    };

    public static IEnumerable<MailboxCommand> All =>
    [
        NewAppointment, NewMeeting, NewItems, AddFocusTime,
        Today, Next7Days, GoToDate, Back, Forward,
        DayView, WorkWeekView, WeekView, MonthView, ScheduleView, TimeScale, Overlay,
        OpenCalendar, NewCalendar, OpenFromInternet, OpenFromFile, CalendarGroups, DeleteCalendar, CalendarColour,
        Share, EmailCalendar, PublishCalendar,
        OpenItem, DeleteItem, Recurrence, Categorize,
        DailyTaskList, CalendarOptions,
    ];
}
