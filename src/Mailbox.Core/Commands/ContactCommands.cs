namespace Mailbox.Core.Commands;

/// <summary>
/// The Contact window's own commands — what its bar carries when one person is open.
/// </summary>
/// <remarks>
/// Transcribed from the reference's own Contact window: Save &amp; Close | Delete | Save &amp; New |
/// Forward | Send to OneNote | General · Details · Activities · Certificates · All Fields | E-mail | Address
/// Book | Check Names | Business Card | Picture. Its own class for the reason the appointment
/// window's is: these act on the window in front of the reader and never on the module behind it,
/// and the key map picks between two commands of the same name by which window is open.
/// <para>
/// <b>Send to OneNote is not here.</b> It reaches a product that is out of scope, and a button
/// that cannot do what it says is worse than one that is absent — the same call the mail and
/// People bars made.
/// </para>
/// </remarks>
public static class ContactCommands
{
    // ---- Actions -------------------------------------------------------------------------------

    public static readonly MailboxCommand SaveAndClose = new()
    {
        Id = new("contact.save"),
        Label = "Save & Close",
        Description = "Save the contact and close the window.",
        Icon = "save",
        Category = "Actions",
        Surface = CommandSurface.Contact,
        KeyTip = "SC",
        DefaultGesture = "Alt+S",
    };

    public static readonly MailboxCommand Delete = new()
    {
        Id = new("contact.delete"),
        Label = "Delete",
        Description = "Delete this contact.",
        Icon = "delete",
        IconTint = "ribbon.icon.delete",
        Category = "Actions",
        Surface = CommandSurface.Contact,
        KeyTip = "DL", // Not D: it would fire before DE, the Details page on the same tab.
    };

    public static readonly MailboxCommand SaveAndNew = new()
    {
        Id = new("contact.save.new"),
        Label = "Save & New",
        Description = "Save this contact and start another.",
        Icon = "contact-card",
        Category = "Actions",
        Surface = CommandSurface.Contact,
        KeyTip = "SN",
    };

    public static readonly MailboxCommand Forward = new()
    {
        Id = new("contact.forward"),
        Label = "Forward",
        Description = "Send this contact to somebody as a vCard.",
        Icon = "forward",
        IconTint = "ribbon.icon.forward",
        Category = "Actions",
        Surface = CommandSurface.Contact,
        KeyTip = "FW",
    };

    // ---- Show — the pages the form can be turned to ---------------------------------------------

    public static readonly MailboxCommand General = new()
    {
        Id = new("contact.show.general"),
        Label = "General",
        Description = "The contact's own page: names, addresses and numbers.",
        Icon = "contact-card",
        Category = "Show",
        Surface = CommandSurface.Contact,
        KeyTip = "G",
    };

    public static readonly MailboxCommand Details = new()
    {
        Id = new("contact.show.details"),
        Label = "Details",
        Description = "Department, manager, birthday and the rest.",
        Icon = "list-view",
        Category = "Show",
        Surface = CommandSurface.Contact,
        KeyTip = "DE",
    };

    /// <summary>
    /// A page of our own: what the journal has recorded about this person.
    /// </summary>
    /// <remarks>
    /// A deliberate divergence. The reference's Show group is four — General, Details,
    /// Certificates, All Fields — and the page that answered this question was dropped from it
    /// several versions before the one being cloned. It is added back because the module it reads
    /// is here and is otherwise write-only: entries record who they were about and nothing could
    /// ever ask. Four buttons become five rather than one being replaced.
    /// </remarks>
    public static readonly MailboxCommand Activities = new()
    {
        Id = new("contact.show.activities"),
        Label = "Activities",
        Description = "What the journal has recorded about this person.",
        Icon = "journal-entry",
        Category = "Show",
        Surface = CommandSurface.Contact,
        KeyTip = "AC",
    };

    public static readonly MailboxCommand Certificates = new()
    {
        Id = new("contact.show.certificates"),
        Label = "Certificates",
        Description = "The certificates this contact signs and encrypts with.",
        Icon = "sign",
        Category = "Show",
        Surface = CommandSurface.Contact,
        KeyTip = "CE",
    };

    public static readonly MailboxCommand AllFields = new()
    {
        Id = new("contact.show.allfields"),
        Label = "All Fields",
        Description = "Every field the card holds, including the ones no page shows.",
        Icon = "table",
        Category = "Show",
        Surface = CommandSurface.Contact,
        KeyTip = "AF",
    };

    // ---- Communicate and names -------------------------------------------------------------------

    public static readonly MailboxCommand Email = new()
    {
        Id = new("contact.email"),
        Label = "Email",
        Description = "Start a message to this contact.",
        Icon = "new-email",
        Category = "Communicate",
        Surface = CommandSurface.Contact,
        KeyTip = "EM",
    };

    /// <summary>
    /// Meeting and More, which the classic Contact tab's Communicate group carries beside Email.
    /// </summary>
    public static readonly MailboxCommand Meeting = new()
    {
        Id = new("contact.meeting"),
        Label = "Meeting",
        Description = "Invite this person to a meeting.",
        Icon = "meeting",
        IconTint = "ribbon.icon.blue",
        Category = "Communicate",
        Scope = ModuleScope.People,
        Surface = CommandSurface.Contact,
        KeyTip = "MT",
    };

    public static readonly MailboxCommand More = new()
    {
        Id = new("contact.more"),
        Label = "More",
        Description = "The other ways of reaching this person.",
        Icon = "more",
        Category = "Communicate",
        Scope = ModuleScope.People,
        Surface = CommandSurface.Contact,
        KeyTip = "MR",
    };

    public static readonly MailboxCommand AddressBook = new()
    {
        Id = new("contact.addressbook"),
        Label = "Address Book",
        Description = "Look somebody up.",
        Icon = "address-book",
        Category = "Names",
        Surface = CommandSurface.Contact,
        KeyTip = "AB",
    };

    public static readonly MailboxCommand CheckNames = new()
    {
        Id = new("contact.checknames"),
        Label = "Check Names",
        Description = "Resolve what has been typed against the address book.",
        Icon = "check-names",
        Category = "Names",
        Surface = CommandSurface.Contact,
        KeyTip = "CN",
    };

    // ---- Options ---------------------------------------------------------------------------------

    public static readonly MailboxCommand BusinessCard = new()
    {
        Id = new("contact.businesscard"),
        Label = "Business Card",
        Description = "How the card looks when it is sent.",
        Icon = "business-card",
        Category = "Options",
        Surface = CommandSurface.Contact,
        KeyTip = "BC",
    };

    public static readonly MailboxCommand Picture = new()
    {
        Id = new("contact.picture"),
        Label = "Picture",
        Description = "Add, change or remove the contact's photograph.",
        Icon = "avatar",
        Category = "Options",
        Surface = CommandSurface.Contact,
        KeyTip = "PI",
    };

    public static IReadOnlyList<MailboxCommand> All { get; } =
    [
        SaveAndClose, Delete, SaveAndNew, Forward,
        General, Details, Activities, Certificates, AllFields,
        Email, Meeting, More, AddressBook, CheckNames,
        BusinessCard, Picture,
    ];
}
