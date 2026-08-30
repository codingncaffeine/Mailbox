namespace Mailbox.Core.Commands;

/// <summary>
/// The People module's commands: making and opening contacts and groups, the five views
/// the reference offers, and what can be done with somebody once they are picked.
/// </summary>
/// <remarks>
/// Its own class rather than more of <see cref="MailCommands"/>, for the reason the calendar's
/// is: the ids say which module a command belongs to, the key map picks between two commands of
/// the same name by which module is open, and Customize Ribbon groups by the same thing.
/// <para>
/// Categorize, Follow Up and Private appear here as well as on the mail bar. The categories are
/// one set across the modules but the command is not: pressing Categorize with a contact
/// picked has to tag a contact, and a shared id would reach the mail module's handler.
/// </para>
/// </remarks>
public static class PeopleCommands
{
    // ---- New ---------------------------------------------------------------------------------

    public static readonly MailboxCommand NewContact = new()
    {
        Id = new("people.new.contact"),
        Label = "New Contact",
        Description = "Create a new contact in your address book.",
        Icon = "contact-card",
        Category = "New",

        // Every module, as the reference's creation chords are: Ctrl+Shift+C makes a contact
        // from wherever the reader is.
        Scope = ModuleScope.Any,
        KeyTip = "NC",
        DefaultGesture = "Ctrl+Shift+C",

        // Ctrl+N makes the open module's new thing, which here is a contact. GestureHome is what
        // says so — see CalendarCommands.NewAppointment for why Scope cannot.
        AlsoGestures = ["Ctrl+N"],
        GestureHome = ModuleScope.People,
    };

    public static readonly MailboxCommand NewContactGroup = new()
    {
        Id = new("people.new.group"),
        Label = "New Contact Group",
        Description = "Create a group you can send to by one name.",
        Icon = "contact-group",
        Category = "New",
        Scope = ModuleScope.Any,
        KeyTip = "NG",
        DefaultGesture = "Ctrl+Shift+L",
    };

    public static readonly MailboxCommand NewItems = new()
    {
        Id = new("people.new.items"),
        Label = "New Items",
        Description = "Create a new item of any type.",
        Icon = "new-items",
        Category = "New",
        Scope = ModuleScope.People,
        KeyTip = "NI",
    };

    // ---- Delete and open ----------------------------------------------------------------------

    public static readonly MailboxCommand Delete = new()
    {
        Id = new("people.delete"),
        Label = "Delete",
        Description = "Delete the selected contact.",
        Icon = "delete",
        IconTint = "ribbon.icon.delete",
        Category = "Delete",
        Scope = ModuleScope.People,
        KeyTip = "D",
        RequiresSelection = true,

        // Delete means the open module's thing, as it does in the calendar — and as its own
        // shortcut, which is how the calendar, tasks, notes, journal and feeds all declare theirs.
        // Declared as a second chord it lost: mail.delete reaches every module and holds Delete
        // outright, and a command's own shortcut is answered before anybody's second chord, so
        // Delete in People threw a message away rather than the contact in front of the reader.
        DefaultGesture = "Delete",
    };

    public static readonly MailboxCommand OpenContact = new()
    {
        Id = new("people.open"),
        Label = "Open",
        Description = "Open the selected contact.",
        Icon = "open-item",
        Category = "Actions",
        Scope = ModuleScope.People,
        RequiresSingleSelection = true,
        InDefaultLayout = false,

        // Its own, as Journal, Notes and Tasks declare theirs. Nothing else claims Ctrl+O today,
        // so a second chord reached it — until something did, and then it would not have.
        DefaultGesture = "Ctrl+O",
    };

    // ---- Communicate ---------------------------------------------------------------------------

    public static readonly MailboxCommand EmailContact = new()
    {
        Id = new("people.email"),
        Label = "E-mail",
        Description = "Start a message to the selected contact.",
        Icon = "new-email",
        Category = "Communicate",
        Scope = ModuleScope.People,
        KeyTip = "EM",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand MeetContact = new()
    {
        Id = new("people.meeting"),
        Label = "Meeting",
        Description = "Invite the selected contact to a new meeting.",
        Icon = "meeting",
        Category = "Communicate",
        Scope = ModuleScope.People,
        KeyTip = "MG",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand MoreCommunicate = new()
    {
        Id = new("people.communicate.more"),
        Label = "More",
        Description = "Other ways to reach the selected contact.",
        Icon = "more",
        Category = "Communicate",
        Scope = ModuleScope.People,
        KeyTip = "MO",
    };

    // ---- Current View --------------------------------------------------------------------------

    public static readonly MailboxCommand PeopleView = new()
    {
        Id = new("people.view.people"),
        Label = "People",
        Description = "Show contacts as a list of names with their cards beside them.",
        Icon = "people",
        Category = "Current View",
        Scope = ModuleScope.People,
        KeyTip = "VP",
    };

    public static readonly MailboxCommand BusinessCardView = new()
    {
        Id = new("people.view.businesscard"),
        Label = "Business Card",
        Description = "Show contacts as business cards.",
        Icon = "business-card",
        Category = "Current View",
        Scope = ModuleScope.People,
        KeyTip = "VB",
    };

    public static readonly MailboxCommand CardView = new()
    {
        Id = new("people.view.card"),
        Label = "Card",
        Description = "Show contacts as cards, alphabetically.",
        Icon = "contact-card",
        Category = "Current View",
        Scope = ModuleScope.People,
        KeyTip = "VC",
    };

    public static readonly MailboxCommand PhoneView = new()
    {
        Id = new("people.view.phone"),
        Label = "Phone",
        Description = "Show contacts as a table of telephone numbers.",
        Icon = "phone",
        Category = "Current View",
        Scope = ModuleScope.People,
        KeyTip = "VH",
    };

    public static readonly MailboxCommand ListView = new()
    {
        Id = new("people.view.list"),
        Label = "List",
        Description = "Show contacts as a table, grouped by company.",
        Icon = "list-view",
        Category = "Current View",
        Scope = ModuleScope.People,
        KeyTip = "VL",
    };

    // ---- Actions -------------------------------------------------------------------------------

    public static readonly MailboxCommand MoveTo = new()
    {
        Id = new("people.move"),
        Label = "Move",
        Description = "Move the selected contact to another address book.",
        Icon = "move-to",
        IconTint = "ribbon.icon.move",
        Category = "Actions",
        Scope = ModuleScope.People,
        KeyTip = "MV",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand MailMerge = new()
    {
        Id = new("people.mailmerge"),
        Label = "Mail Merge",
        Description = "Write one message to many contacts, each addressed by name.",
        Icon = "mail-merge",
        Category = "Actions",
        Scope = ModuleScope.People,
        KeyTip = "MM",
    };

    public static readonly MailboxCommand ForwardContact = new()
    {
        Id = new("people.forward"),
        Label = "Forward Contact",
        Description = "Send the selected contact to somebody as a vCard.",
        Icon = "forward",
        IconTint = "ribbon.icon.forward",
        Category = "Actions",
        Scope = ModuleScope.People,
        KeyTip = "FC",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand ShareContacts = new()
    {
        Id = new("people.share"),
        Label = "Share Contacts",
        Description = "Send an address book, or invite somebody to see one.",
        Icon = "share",
        Category = "Actions",
        Scope = ModuleScope.People,
        KeyTip = "SC",
    };

    public static readonly MailboxCommand OpenSharedContacts = new()
    {
        Id = new("people.open.shared"),
        Label = "Open Shared Contacts",
        Description = "Open an address book somebody has shared with you.",
        Icon = "open-folder",
        Category = "Actions",
        Scope = ModuleScope.People,
        KeyTip = "OS",
    };

    public static readonly MailboxCommand NewAddressBook = new()
    {
        Id = new("people.book.new"),
        Label = "New Address Book",
        Description = "Make another address book to keep contacts in.",
        Icon = "new-folder",
        Category = "Actions",
        Scope = ModuleScope.People,
        InDefaultLayout = false,
    };

    public static readonly MailboxCommand DeleteAddressBook = new()
    {
        Id = new("people.book.delete"),
        Label = "Delete Address Book",
        Description = "Delete an address book and every contact in it.",
        Icon = "delete-folder",
        Category = "Actions",
        Scope = ModuleScope.People,
        InDefaultLayout = false,
    };

    // ---- Tags ----------------------------------------------------------------------------------

    public static readonly MailboxCommand Categorize = new()
    {
        Id = new("people.categorize"),
        Label = "Categorize",
        Description = "Give the selected contact a colour category.",
        Icon = "categorize",
        IconArtwork = "categorize",
        Category = "Tags",
        Scope = ModuleScope.People,
        KeyTip = "CG", // Not G: the contact window draws this beside its General page, which is G.
        RequiresSelection = true,
    };

    public static readonly MailboxCommand FollowUp = new()
    {
        Id = new("people.followup"),
        Label = "Follow Up",
        Description = "Flag the selected contact for follow-up.",
        Icon = "follow-up",
        IconArtwork = "followup",
        Category = "Tags",
        Scope = ModuleScope.People,
        KeyTip = "U",
        RequiresSelection = true,
    };

    public static readonly MailboxCommand Private = new()
    {
        Id = new("people.private"),
        Label = "Private",
        Description = "Mark the selected contact private, so a shared address book does not show it.",
        Icon = "private",
        Category = "Tags",
        Scope = ModuleScope.People,
        KeyTip = "PV",
        RequiresSelection = true,
    };

    /// <summary>
    /// Add to Favourites, which is what fills the To-Do Bar's People section.
    /// </summary>
    /// <remarks>
    /// One command rather than two, because it is one gesture: a contact is in the short list or
    /// it is not, and the bar says which. The list is this reader's own preference and is not
    /// written into the card — see <c>ContactFavourites</c>.
    /// </remarks>
    public static readonly MailboxCommand Favourite = new()
    {
        Id = new("people.favourite"),
        Label = "Add to Favourites",
        Description = "Keep the contact in the To-Do Bar's People section.",
        Icon = "star",
        Category = "Tags",
        Scope = ModuleScope.People,
        KeyTip = "AF",
        RequiresSelection = true,
    };

    // The Find cluster's Search People box is ViewCommands.SearchPeople, which every module's
    // bar carries — the box searches the address book from wherever the reader is, so one
    // command rather than one per module.

    public static IEnumerable<MailboxCommand> All =>
    [
        NewContact, NewContactGroup, NewItems, Delete, OpenContact,
        EmailContact, MeetContact, MoreCommunicate,
        PeopleView, BusinessCardView, CardView, PhoneView, ListView,
        MoveTo, MailMerge, ForwardContact, ShareContacts, OpenSharedContacts,
        NewAddressBook, DeleteAddressBook,
        Categorize, FollowUp, Private, Favourite,
    ];
}
