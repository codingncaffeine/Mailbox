using Mailbox.Core.Commands;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// The appointment and meeting windows' ribbons — their own hosts with their own tab
/// collections, as the compose window has.
/// </summary>
/// <remarks>
/// Transcribed from the two captures. Both windows carry File · <em>Appointment</em> or
/// <em>Meeting</em> · Scheduling Assistant · Insert · Format Text · Review · Help and a "Tell me
/// what you want to do" after the strip, exactly as the compose window does; the first tab is
/// the only one that differs between them, and it differs in three entries.
/// </remarks>
public static class AppointmentRibbonLayout
{
    /// <summary>The Show As box, measured on the capture from x = 771 to x = 866.</summary>
    private const double ShowAsWidth = 96;

    /// <summary>The Reminder box, measured from x = 967 to x = 1067.</summary>
    private const double ReminderWidth = 101;

    private static SimplifiedBar Bar(params RibbonGroup[] groups) => new() { Groups = groups };

    /// <summary>
    /// The groups the two page tabs carry: the reference's Show pair back and forth, the
    /// attendee commands, and — on the assistant — the Options the Meeting tab also offers.
    /// </summary>
    private static IEnumerable<RibbonGroup> PageGroups(bool meeting, bool showOptions)
    {
        yield return new RibbonGroup
        {
            Id = "show",
            Label = "Show",
            KeyTip = "ZH",
            CollapsePriority = 4,
            Items =
            [
                RibbonItem.Large(AppointmentCommands.AppointmentPage.Id),
                RibbonItem.Large(AppointmentCommands.SchedulingAssistant.Id),
            ],
        };

        yield return new RibbonGroup
        {
            Id = "attendees",
            Label = "Attendees",
            KeyTip = "ZT",
            CollapsePriority = 2,
            Items = meeting
                ?
                [
                    RibbonItem.Large(AppointmentCommands.ResponseOptions.Id, RibbonItemKind.DropDown),
                    RibbonItem.Small(AppointmentCommands.Rooms.Id),
                ]
                :
                [
                    RibbonItem.Large(AppointmentCommands.InviteAttendees.Id),
                ],
        };

        if (!showOptions) yield break;

        yield return new RibbonGroup
        {
            Id = "options",
            Label = "Options",
            KeyTip = "ZO",
            CollapsePriority = 1,
            Items =
            [
                RibbonItem.LabelledCombo(AppointmentCommands.ShowAs.Id, ShowAsWidth, "Busy"),
                RibbonItem.LabelledCombo(AppointmentCommands.Reminder.Id, ReminderWidth, "15 minutes"),
                RibbonItem.Small(AppointmentCommands.MakeRecurring.Id),
            ],
        };
    }

    private static RibbonGroup Cluster(string id, string label, params RibbonItem[] items)
        => new() { Id = id, Label = label, Items = items };

    /// <summary>An appointment on your own calendar: Save &amp; Close, no attendees.</summary>
    public static RibbonLayout Appointment { get; } = Build(meeting: false);

    /// <summary>A meeting: Send, the response options, and the attendee fields on the form.</summary>
    public static RibbonLayout Meeting { get; } = Build(meeting: true);

    public static RibbonLayout For(bool meeting) => meeting ? Meeting : Appointment;

    private static RibbonLayout Build(bool meeting) => new()
    {
        Module = MailboxModule.Calendar,
        TellMe = "Tell me what you want to do",

        // The window's own toolbar: Save, Undo, Redo, and the two navigation arrows the
        // reference puts there for stepping through items.
        QuickAccess =
        [
            meeting ? AppointmentCommands.Send.Id : AppointmentCommands.SaveAndClose.Id,
            MailCommands.Undo.Id,
            ViewCommands.Redo.Id,
        ],

        Simplified = new Dictionary<string, SimplifiedBar>
        {
            [meeting ? "meeting" : "appointment"] = Bar(
                Cluster("actions", "Actions",
                    RibbonItem.Small(AppointmentCommands.Delete.Id),
                    RibbonItem.Sheddable(AppointmentCommands.CopyToMyCalendar.Id),
                    RibbonItem.Sheddable(AppointmentCommands.Forward.Id, RibbonItemKind.SplitButton)),

                Cluster("attendees", "Attendees",
                    meeting
                        ? RibbonItem.Sheddable(AppointmentCommands.ResponseOptions.Id, RibbonItemKind.DropDown)
                        : RibbonItem.Sheddable(AppointmentCommands.InviteAttendees.Id)),

                Cluster("options", "Options",
                    RibbonItem.LabelledCombo(AppointmentCommands.ShowAs.Id, ShowAsWidth, "Busy"),
                    RibbonItem.LabelledCombo(AppointmentCommands.Reminder.Id, ReminderWidth, "15 minutes")),

                Cluster("tags", "Tags",
                    RibbonItem.Sheddable(AppointmentCommands.Categorize.Id, RibbonItemKind.DropDown),
                    RibbonItem.Glyph(AppointmentCommands.Private.Id),
                    RibbonItem.Glyph(AppointmentCommands.HighImportance.Id),
                    RibbonItem.Glyph(AppointmentCommands.LowImportance.Id)),

                Cluster("apps", "Apps",
                    RibbonItem.Sheddable(ViewCommands.Apps.Id))),

            ["scheduling"] = Bar(
                Cluster("show", "Show",
                    RibbonItem.Small(AppointmentCommands.AppointmentPage.Id),
                    RibbonItem.Small(AppointmentCommands.SchedulingAssistant.Id)),
                Cluster("attendees", "Attendees",
                    meeting
                        ? RibbonItem.Sheddable(AppointmentCommands.ResponseOptions.Id, RibbonItemKind.DropDown)
                        : RibbonItem.Sheddable(AppointmentCommands.InviteAttendees.Id)),
                Cluster("options", "Options",
                    RibbonItem.Sheddable(AppointmentCommands.MakeRecurring.Id))),

            ["tracking"] = Bar(
                Cluster("show", "Show",
                    RibbonItem.Small(AppointmentCommands.AppointmentPage.Id),
                    RibbonItem.Small(AppointmentCommands.SchedulingAssistant.Id)),
                Cluster("attendees", "Attendees",
                    RibbonItem.Sheddable(AppointmentCommands.ResponseOptions.Id, RibbonItemKind.DropDown))),

            ["insert"] = Bar(
                Cluster("include", "Include",
                    RibbonItem.Small(ComposeCommands.AttachFile.Id, RibbonItemKind.SplitButton),
                    RibbonItem.Sheddable(ComposeCommands.AttachItem.Id, RibbonItemKind.DropDown)),

                Cluster("tables", "Tables",
                    RibbonItem.Sheddable(ComposeCommands.Table.Id, RibbonItemKind.DropDown)),

                Cluster("illustrations", "Illustrations",
                    RibbonItem.Sheddable(ComposeCommands.Pictures.Id, RibbonItemKind.DropDown),
                    RibbonItem.Sheddable(ComposeCommands.Shapes.Id, RibbonItemKind.DropDown)),

                Cluster("links", "Links",
                    RibbonItem.Sheddable(ComposeCommands.Link.Id, RibbonItemKind.DropDown))),

            ["formattext"] = Bar(
                Cluster("basictext", "Basic Text",
                    RibbonItem.Glyph(ComposeCommands.Bold.Id),
                    RibbonItem.Glyph(ComposeCommands.Italic.Id),
                    RibbonItem.Glyph(ComposeCommands.Underline.Id),
                    RibbonItem.Glyph(ComposeCommands.Bullets.Id, RibbonItemKind.SplitButton),
                    RibbonItem.Glyph(ComposeCommands.Numbering.Id, RibbonItemKind.SplitButton))),

            ["review"] = Bar(
                Cluster("proofing", "Proofing",
                    RibbonItem.Small(ComposeCommands.Spelling.Id))),

            ["help"] = DefaultRibbonLayouts.HelpBar,
        },

        Tabs =
        [
            new RibbonTab { Id = "file", Label = "File", KeyTip = "F", IsBackstage = true, Groups = [] },

            new RibbonTab
            {
                Id = meeting ? "meeting" : "appointment",
                Label = meeting ? "Meeting" : "Appointment",
                KeyTip = meeting ? "M" : "A",
                Groups =
                [
                    new RibbonGroup
                    {
                        Id = "actions",
                        Label = "Actions",
                        KeyTip = "ZA",
                        CollapsePriority = 4,
                        Items =
                        [
                            meeting
                                ? RibbonItem.Large(AppointmentCommands.Send.Id)
                                : RibbonItem.Large(AppointmentCommands.SaveAndClose.Id),
                            RibbonItem.Large(AppointmentCommands.Delete.Id),
                            RibbonItem.Large(AppointmentCommands.Forward.Id, RibbonItemKind.SplitButton),
                            RibbonItem.Small(AppointmentCommands.CopyToMyCalendar.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "attendees",
                        Label = "Attendees",
                        KeyTip = "ZT",
                        CollapsePriority = 3,
                        Items = meeting
                            ?
                            [
                                RibbonItem.Large(AppointmentCommands.SchedulingAssistant.Id),
                                RibbonItem.Large(AppointmentCommands.ResponseOptions.Id, RibbonItemKind.DropDown),
                                RibbonItem.Small(AppointmentCommands.Rooms.Id),
                            ]
                            :
                            [
                                RibbonItem.Large(AppointmentCommands.InviteAttendees.Id),
                            ],
                    },

                    new RibbonGroup
                    {
                        Id = "options",
                        Label = "Options",
                        KeyTip = "ZO",
                        CollapsePriority = 1,
                        Items =
                        [
                            RibbonItem.LabelledCombo(AppointmentCommands.ShowAs.Id, ShowAsWidth, "Busy"),
                            RibbonItem.LabelledCombo(AppointmentCommands.Reminder.Id, ReminderWidth, "15 minutes"),
                            RibbonItem.Small(AppointmentCommands.MakeRecurring.Id),
                        ],
                    },

                    new RibbonGroup
                    {
                        Id = "tags",
                        Label = "Tags",
                        KeyTip = "ZG",
                        CollapsePriority = 2,
                        Items =
                        [
                            RibbonItem.Small(AppointmentCommands.Categorize.Id, RibbonItemKind.DropDown),
                            RibbonItem.Small(AppointmentCommands.Private.Id),
                            RibbonItem.Small(AppointmentCommands.HighImportance.Id),
                            RibbonItem.Small(AppointmentCommands.LowImportance.Id),
                        ],
                    },
                ],
            },

            // These two replace the form below the bar — and the bar still holds a ribbon,
            // because a tab whose strip is nothing but an overflow chevron is a shape the
            // reference never shows: its assistant tab carries Show, Attendees and Options.
            new RibbonTab
            {
                Id = "scheduling",
                Label = "Scheduling Assistant",
                KeyTip = "G",
                Groups = [.. PageGroups(meeting, showOptions: true)],
            },

            // Tracking is the organizer's own tab: an appointment nobody was asked to has nothing
            // to track, and the reference does not offer it there either.
            .. meeting
                ? new[]
                {
                    new RibbonTab
                    {
                        Id = "tracking",
                        Label = "Tracking",
                        KeyTip = "K",
                        Groups = [.. PageGroups(meeting: true, showOptions: false)],
                    },
                }
                : [],

            new RibbonTab { Id = "insert", Label = "Insert", KeyTip = "N", Groups = [] },
            new RibbonTab { Id = "formattext", Label = "Format Text", KeyTip = "O", Groups = [] },
            new RibbonTab { Id = "review", Label = "Review", KeyTip = "R", Groups = [] },
            new RibbonTab { Id = "help", Label = "Help", KeyTip = "Y", Groups = DefaultRibbonLayouts.HelpGroups },
        ],
    };
}
