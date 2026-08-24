namespace Mailbox.Plugins.Api;

/// <summary>
/// Mail, read and acted on, across every account. Reads want <c>mail</c>; the acts at the bottom
/// want <c>mail-write</c>.
/// </summary>
/// <remarks>
/// Messages arrive as plain records plus the verbatim RFC822 bytes, not as a parsed object model:
/// the store keeps every message byte-exact, and a plugin that wants structure brings its own MIME
/// parser and reads the same bytes the application does. That keeps this API free of any
/// third-party type, which is what lets the host's libraries move without breaking plugins.
/// <para>
/// A message id is only unique within its account's store — every account is its own file and
/// every store numbers from one — which is why each call names the account.
/// </para>
/// </remarks>
public interface IPluginMail
{
    IReadOnlyList<PluginAccount> Accounts();

    IReadOnlyList<PluginFolder> Folders(string account);

    /// <summary>Newest first, up to <paramref name="limit"/>.</summary>
    IReadOnlyList<PluginMessageSummary> Messages(string account, long folderId, int limit = 100);

    /// <summary>The message as it was received, byte for byte. Null when the id names nothing.</summary>
    byte[]? Raw(string account, long messageId);

    void MoveTo(string account, long messageId, long folderId);

    void Delete(string account, long messageId);

    void SetRead(string account, long messageId, bool read);
}

/// <summary>One account, by the address that also names its store.</summary>
public sealed record PluginAccount(string Address, string DisplayName);

/// <summary>One folder in one account. Role names a well-known folder — "inbox", "sent",
/// "drafts", "deleted", "junk", "archive", "outbox" — and is null for one somebody made.</summary>
public sealed record PluginFolder(string Account, long Id, string Name, string? Role);

/// <summary>One message's row, as the list would describe it.</summary>
public sealed record PluginMessageSummary(
    string Account,
    long Id,
    long FolderId,
    string Subject,
    string From,
    DateTimeOffset Date,
    bool IsRead);

/// <summary>
/// Calendars, task lists, note lists and address books. Reads want <c>pim</c>; the write wants
/// <c>pim-write</c>.
/// </summary>
/// <remarks>
/// Items travel as their own wire text — iCalendar for events, tasks and journal entries, vCard
/// for contacts — because that text is what the store itself treats as the truth of an item, kept
/// verbatim beside the columns the views read. A plugin reads and writes the same standard formats
/// a server would.
/// </remarks>
public interface IPluginPim
{
    IReadOnlyList<PluginCollection> Collections();

    IReadOnlyList<PluginItem> Items(long collectionId);

    /// <summary>
    /// Writes an item — new when the uid is unknown, replaced when it is — and queues it for the
    /// collection's server exactly as the application's own edits are queued.
    /// </summary>
    void Save(long collectionId, string uid, string text);
}

/// <summary>One collection. Kind is "calendar", "tasks", "journal" or "addressbook".</summary>
public sealed record PluginCollection(long Id, string Name, string Kind);

/// <summary>One item: its uid and its verbatim iCalendar or vCard text.</summary>
public sealed record PluginItem(long Id, string Uid, string Text);
