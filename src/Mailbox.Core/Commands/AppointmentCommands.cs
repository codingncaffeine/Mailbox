namespace Mailbox.Core.Commands;

/// <summary>
/// The appointment and meeting windows' own command set.
/// </summary>
/// <remarks>
/// Transcribed from the two captures. The Appointment window's bar reads Delete · Copy to My
/// Calendar · Forward | Invite Attendees · Show As · Reminder | Categorize · Private · High
/// Importance · Low Importance | All Apps; the Meeting window swaps Invite Attendees for
/// Response Options and puts Send where Save &amp; Close is.
/// <para>
/// Send to OneNote sits between Forward and Invite Attendees in the reference and is absent
/// here, with the rest of the vendor-cloud integrations, which are left out rather than stubbed.
/// </para>
/// </remarks>
public static class AppointmentCommands
{
    public static readonly MailboxCommand SaveAndClose = new()
    {
        Id = new("appointment.saveclose"),
        Label = "Save & Close",
        Description = "Save this appointment and close the window.",
        Icon = "save",
        Category = "Actions",
        Scope = ModuleScope.Calendar,
        KeyTip = "SC",
        DefaultGesture = "Alt+S",
    };

    public static readonly MailboxCommand Send = new()
    {
        Id = new("appointment.send"),
        Label = "Send",
        Description = "Send this meeting invitation to the people you asked.",
        Icon = "send",
        Category = "Actions",
        Scope = ModuleScope.Calendar,
        KeyTip = "SM",
        DefaultGesture = "Ctrl+Enter",
    };

    public static readonly MailboxCommand Delete = new()
    {
        Id = new("appointment.delete"),
        Label = "Delete",
        Description = "Delete this appointment.",
        Icon = "delete",
        Category = "Actions",
        Scope = ModuleScope.Calendar,
        KeyTip = "D",
    };

    public static readonly MailboxCommand CopyToMyCalendar = new()
    {
        Id = new("appointment.copy"),
        Label = "Copy to My Calendar",
        Description = "Put a copy of this appointment on your own calendar.",
        Icon = "copy",
        Category = "Actions",
        Scope = ModuleScope.Calendar,
        KeyTip = "CC",
    };

    public static readonly MailboxCommand Forward = new()
    {
        Id = new("appointment.forward"),
        Label = "Forward",
        Description = "Send this appointment on as an iCalendar attachment.",
        Icon = "forward",
        IconTint = "ribbon.icon.blue",
        Category = "Actions",
        Scope = ModuleScope.Calendar,
        KeyTip = "FW",
    };

    public static readonly MailboxCommand InviteAttendees = new()
    {
        Id = new("appointment.invite"),
        Label = "Invite Attendees",
        Description = "Ask people to this appointment, which makes it a meeting.",
        Icon = "person-add",
        Category = "Attendees",
        Scope = ModuleScope.Calendar,
        KeyTip = "IA",
    };

    public static readonly MailboxCommand ResponseOptions = new()
    {
        Id = new("appointment.responseoptions"),
        Label = "Response Options",
        Description = "Whether replies are requested and whether new times may be proposed.",
        Icon = "reply",
        IconTint = "ribbon.icon.magenta",
        Category = "Attendees",
        Scope = ModuleScope.Calendar,
        KeyTip = "RO",
    };

    public static readonly MailboxCommand ShowAs = new()
    {
        Id = new("appointment.showas"),
        Label = "Show As",
        Description = "What this appointment says about the time it takes: Free, Tentative, Busy or Out of Office.",
        Icon = "show-as",
        Category = "Options",
        Scope = ModuleScope.Calendar,
        KeyTip = "SA",
    };

    public static readonly MailboxCommand Reminder = new()
    {
        Id = new("appointment.reminder"),
        Label = "Reminder",
        Description = "How long before it starts you are reminded.",
        Icon = "reminder",
        Category = "Options",
        Scope = ModuleScope.Calendar,
        KeyTip = "RM",
    };

    public static readonly MailboxCommand MakeRecurring = new()
    {
        Id = new("appointment.recurrence"),
        Label = "Make Recurring",
        Description = "Repeat this appointment on a pattern.",
        Icon = "recurrence",
        Category = "Options",
        Scope = ModuleScope.Calendar,
        KeyTip = "MR",
    };

    public static readonly MailboxCommand Categorize = new()
    {
        Id = new("appointment.categorize"),
        Label = "Categorize",
        Description = "Put this appointment in a color category.",
        Icon = "categorize",
        IconArtwork = "categorize",
        Category = "Tags",
        Scope = ModuleScope.Calendar,
        KeyTip = "CG",
    };

    public static readonly MailboxCommand Private = new()
    {
        Id = new("appointment.private"),
        Label = "Private",
        Description = "Hide the details from anyone this calendar is shared with.",
        Icon = "private",
        Category = "Tags",
        Scope = ModuleScope.Calendar,
        KeyTip = "PV",
    };

    public static readonly MailboxCommand HighImportance = new()
    {
        Id = new("appointment.importance.high"),
        Label = "High Importance",
        Description = "Mark this appointment as important.",
        Icon = "importance",
        IconTint = "status.danger",
        Category = "Tags",
        Scope = ModuleScope.Calendar,
        KeyTip = "HI",
    };

    public static readonly MailboxCommand LowImportance = new()
    {
        Id = new("appointment.importance.low"),
        Label = "Low Importance",
        Description = "Mark this appointment as low importance.",
        Icon = "importance-low",
        IconTint = "ribbon.icon.blue",
        Category = "Tags",
        Scope = ModuleScope.Calendar,
        KeyTip = "LI",
    };

    public static readonly MailboxCommand Rooms = new()
    {
        Id = new("appointment.rooms"),
        Label = "Rooms",
        Description = "Pick a room from an address book.",
        Icon = "address-book",
        Category = "Attendees",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand SchedulingAssistant = new()
    {
        Id = new("appointment.scheduling"),
        Label = "Scheduling Assistant",
        Description = "See when everyone asked is free.",
        Icon = "schedule-view",
        Category = "Attendees",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand AppointmentPage = new()
    {
        Id = new("appointment.page"),
        Label = "Appointment",
        Description = "Back to the appointment's own form.",
        Icon = "calendar",
        Category = "Show",
        Scope = ModuleScope.Calendar,
        InDefaultLayout = false,
    };

    /// <summary>Every command this class declares, stamped as the appointment window's.</summary>
    public static IEnumerable<MailboxCommand> All =>
        Declared.Select(c => c with { Surface = CommandSurface.Appointment });

    private static IEnumerable<MailboxCommand> Declared =>
    [
        SaveAndClose, Send, Delete, CopyToMyCalendar, Forward,
        InviteAttendees, ResponseOptions, ShowAs, Reminder, MakeRecurring,
        Categorize, Private, HighImportance, LowImportance,
        Rooms, SchedulingAssistant, AppointmentPage,
    ];
}
