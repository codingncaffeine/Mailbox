namespace Mailbox.App.Views;

/// <summary>
/// Every <c>MAILBOX_PEEK</c> key the shell answers — the audit's door inventory, as a list
/// rather than something recovered by reading the switch back.
/// </summary>
/// <remarks>
/// The list used to be parsed out of <c>MainWindow</c>'s switch by a script, and that failed
/// twice in one afternoon: a fixed line window cut it short and lost nine doors, the
/// send/receive groups and the print preview among them, and a nested switch handed back
/// <c>MAILBOX_PROGRESS_STATE</c>'s own values as though they were doors. Both were silent. A
/// door missing from the inventory is a surface nobody audits, and a door in it that opens onto
/// nothing sends the next reader hunting a bug that is not there.
/// <para>
/// So the names live here, and <c>AuditDoorInventoryTests</c> holds this list against the
/// switch's own case labels. The switch is still where a door is implemented — moving a
/// thousand lines of poses into a registry would be a large change for no behaviour — but the
/// list is now something that fails loudly when it disagrees, rather than a parse that quietly
/// answers wrong.
/// </para>
/// </remarks>
public static class HarnessDoors
{
    public static readonly IReadOnlyList<string> All =
    [
        "calendar", "peoplepeek", "addmenu", "allapps", "windowmenu", "appointment",
        "newmeeting", "recurrence", "editscope", "gotodate", "conflict", "conflicts",
        "contact", "contactgroup", "addressbook", "selectnames", "undosend", "docked",
        "todobar", "todotasks", "todopeople", "backstage", "rowmenu", "overflow",
        "menu", "themeeditor", "options", "confirm", "prompt", "passphrase",
        "newkey", "linkcontacts", "duplicate", "subscribe", "newsletters", "mutefilters",
        "accounts", "modifybutton", "addaccount", "certificate", "subscription", "datafile",
        "quickclickcategory", "quickclickflag", "cleanup", "recover", "searchfolder", "customflag",
        "autoarchive", "readingpane", "keyboard", "signatures", "stationery", "font",
        "editoroptions", "autocorrect", "autocorrectexceptions", "newfolder", "folderprops", "folderarchive",
        "gotofolder", "movefolder", "archive", "viewsettings", "showcolumns", "groupby",
        "viewsort", "viewfilter", "othersettings", "conditionalformatting", "formatcolumns", "manageviews",
        "applyview", "quicksteps", "quickstepedit", "categories", "server", "rules",
        "rulewizard", "createrule", "runrules", "junk", "message", "groups",
        "printlist", "source", "progress", "transferbar",
    ];
}
