namespace Mailbox.Core.Commands;

/// <summary>
/// The six modules in the navigation pane, in the reference's own order. The numeric values
/// match the Ctrl+1..Ctrl+8 accelerators.
/// </summary>
public enum MailboxModule
{
    Mail = 1,
    Calendar = 2,
    People = 3,
    Tasks = 4,
    Notes = 5,
    Folders = 6,
    Shortcuts = 7,
    Journal = 8,
}

/// <summary>
/// Which modules a command applies to. A command with <see cref="Any"/> is available
/// everywhere — Send/Receive, Options, Help.
/// </summary>
[Flags]
public enum ModuleScope
{
    None = 0,
    Mail = 1 << 0,
    Calendar = 1 << 1,
    People = 1 << 2,
    Tasks = 1 << 3,
    Notes = 1 << 4,
    Journal = 1 << 5,
    Any = Mail | Calendar | People | Tasks | Notes | Journal,
}

public static class ModuleScopeExtensions
{
    public static ModuleScope AsScope(this MailboxModule module) => module switch
    {
        MailboxModule.Mail => ModuleScope.Mail,
        MailboxModule.Calendar => ModuleScope.Calendar,
        MailboxModule.People => ModuleScope.People,
        MailboxModule.Tasks => ModuleScope.Tasks,
        MailboxModule.Notes => ModuleScope.Notes,
        MailboxModule.Journal => ModuleScope.Journal,
        _ => ModuleScope.None,
    };
}
