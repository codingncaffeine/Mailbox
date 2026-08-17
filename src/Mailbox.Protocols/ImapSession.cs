using MailKit;
using Mailbox.Core.Diagnostics;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>What the server said it can do, of the things the sync cares about.</summary>
[Flags]
public enum ImapFeatures
{
    None = 0,

    /// <summary>CONDSTORE: ask only for flags changed since a modification sequence.</summary>
    CondStore = 1,

    /// <summary>MOVE: a move is one command rather than copy, flag and expunge.</summary>
    Move = 2,

    /// <summary>UIDPLUS: the server says which UID a copied or appended message got.</summary>
    UidPlus = 4,

    /// <summary>IDLE: the server can say when something changes, instead of being asked.</summary>
    Idle = 8,
}

/// <summary>
/// A folder as the server lists it.
/// </summary>
/// <param name="Path">The full name, as IMAP spells it — the key everything is stored under.</param>
/// <param name="Name">The last segment, decoded, which is what the folder pane shows.</param>
/// <param name="ParentPath">The full name of the folder above, or null at the top.</param>
/// <param name="Role">What SPECIAL-USE, XLIST or the server's naming says it is for.</param>
/// <param name="Selectable">False for a folder that only holds other folders.</param>
/// <param name="IsView">
/// True for a folder that is a second view of mail held elsewhere — a mailbox's "all mail",
/// "starred" or "important" — which is listed but never pulled, because it would double the store.
/// </param>
public sealed record RemoteFolder(
    string Path,
    string Name,
    string? ParentPath,
    FolderRole Role,
    bool Selectable,
    bool IsView);

/// <summary>Where a folder stands on the server, read on selecting it.</summary>
public sealed record FolderState(long UidValidity, long UidNext, long HighestModSeq, bool SupportsModSeq, int Count);

/// <summary>What the server holds about a message short of the message itself.</summary>
public sealed record RemoteMessageInfo(long Uid, MessageFlags Flags, DateTimeOffset? InternalDate, long Size);

/// <summary>
/// The part of IMAP this application uses.
/// </summary>
/// <remarks>
/// A seam of our own rather than MailKit's client, for the same reason as the POP3 one: what a
/// sync does is a dozen operations, and a test double should have to fake a dozen operations
/// rather than the whole protocol. Message operations act on the folder most recently opened
/// with <see cref="OpenAsync"/>, as they do on the wire.
/// </remarks>
public interface IImapSession : IDisposable
{
    bool IsConnected { get; }

    /// <summary>What the server advertised. Meaningful once authenticated.</summary>
    ImapFeatures Features { get; }

    Task ConnectAsync(ServerSettings server, CancellationToken cancellation);

    Task AuthenticateAsync(ServerSettings server, CancellationToken cancellation);

    Task DisconnectAsync(CancellationToken cancellation);

    /// <summary>Every folder the account can see, the Inbox among them.</summary>
    Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellation);

    /// <summary>Makes a folder at the top of the account's own namespace, or under a folder by its path.</summary>
    Task<RemoteFolder> CreateFolderAsync(string name, CancellationToken cancellation, string? parentPath = null);

    /// <summary>Renames a folder in place; the folders under it move with it. Returns it as it is now.</summary>
    Task<RemoteFolder> RenameFolderAsync(string path, string newName, CancellationToken cancellation);

    /// <summary>
    /// Puts a folder under another, or at the top of the account's own namespace when the parent
    /// is null, keeping its name; the folders under it move with it. Returns it as it is now.
    /// </summary>
    Task<RemoteFolder> MoveFolderAsync(string path, string? newParentPath, CancellationToken cancellation);

    /// <summary>Deletes a folder and what it holds — the folders under it too.</summary>
    Task DeleteFolderAsync(string path, CancellationToken cancellation);

    /// <summary>Selects a folder for the operations that follow, and says where it stands.</summary>
    Task<FolderState> OpenAsync(string path, CancellationToken cancellation);

    /// <summary>Every UID in the open folder.</summary>
    Task<IReadOnlyList<long>> SearchAllAsync(CancellationToken cancellation);

    /// <summary>The UIDs in the open folder carrying a Message-ID header, for finding a message the server did not name.</summary>
    Task<IReadOnlyList<long>> SearchByMessageIdAsync(string messageId, CancellationToken cancellation);

    /// <summary>Flags, arrival time and size for these UIDs in the open folder.</summary>
    Task<IReadOnlyList<RemoteMessageInfo>> FetchInfoAsync(IReadOnlyList<long> uids, CancellationToken cancellation);

    /// <summary>Flags for every message whose flags changed since a modification sequence. Needs CONDSTORE.</summary>
    Task<IReadOnlyList<RemoteMessageInfo>> FetchFlagsChangedSinceAsync(long modSeq, CancellationToken cancellation);

    /// <summary>The whole message, or null if it has gone since it was listed.</summary>
    Task<MimeMessage?> GetMessageAsync(long uid, CancellationToken cancellation);

    Task StoreFlagsAsync(IReadOnlyList<long> uids, MessageFlags flags, bool set, CancellationToken cancellation);

    /// <summary>
    /// Moves messages out of the open folder. Returns the old-to-new UID map when the server
    /// gave one, or an empty map when it did not.
    /// </summary>
    Task<IReadOnlyDictionary<long, long>> MoveAsync(IReadOnlyList<long> uids, string destinationPath, CancellationToken cancellation);

    /// <summary>Removes messages from the open folder for good.</summary>
    Task ExpungeAsync(IReadOnlyList<long> uids, CancellationToken cancellation);

    /// <summary>Puts a message into a folder. Returns its UID there when the server said.</summary>
    Task<long?> AppendAsync(string path, byte[] raw, MessageFlags flags, DateTimeOffset? date, CancellationToken cancellation);

    /// <summary>
    /// Waits in the open folder until the server reports a change or <paramref name="done"/>
    /// is cancelled. Needs IDLE.
    /// </summary>
    Task IdleAsync(CancellationToken done, CancellationToken cancellation);

    /// <summary>Raised from <see cref="IdleAsync"/> when the open folder changes.</summary>
    event EventHandler? FolderChanged;
}

/// <summary>MailKit behind the IMAP seam.</summary>
public sealed class MailKitImapSession : IImapSession
{
    private readonly ImapClient _client =
        ProtocolDiagnostics.For("imap") is { } log ? new ImapClient(log) : new ImapClient();
    private IMailFolder? _open;

    public bool IsConnected => _client.IsConnected;

    public ImapFeatures Features
    {
        get
        {
            var features = ImapFeatures.None;
            if (_client.Capabilities.HasFlag(ImapCapabilities.CondStore)) features |= ImapFeatures.CondStore;
            if (_client.Capabilities.HasFlag(ImapCapabilities.Move)) features |= ImapFeatures.Move;
            if (_client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) features |= ImapFeatures.UidPlus;
            if (_client.Capabilities.HasFlag(ImapCapabilities.Idle)) features |= ImapFeatures.Idle;
            return features;
        }
    }

    public event EventHandler? FolderChanged;

    public Task ConnectAsync(ServerSettings server, CancellationToken cancellation)
    {
        OAuth.SaslAuthentication.UseTrust(_client, server);
        return _client.ConnectAsync(server.Host, server.Port, server.Security, cancellation);
    }

    public Task AuthenticateAsync(ServerSettings server, CancellationToken cancellation)
        => OAuth.SaslAuthentication.AuthenticateAsync(_client, server, cancellation);

    public Task DisconnectAsync(CancellationToken cancellation)
        => _client.DisconnectAsync(true, cancellation);

    public async Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellation)
    {
        var folders = new List<IMailFolder>();
        foreach (var ns in _client.PersonalNamespaces)
        {
            folders.AddRange(await _client.GetFoldersAsync(ns, StatusItems.None, false, cancellation));
        }

        if (!folders.Any(f => f.IsNamespace is false && string.Equals(f.FullName, _client.Inbox.FullName, StringComparison.Ordinal)))
        {
            folders.Insert(0, _client.Inbox);
        }

        // The server may mark special folders (SPECIAL-USE, XLIST); where it does not, MailKit
        // knows the names the common servers use, and that is asked second.
        var special = new Dictionary<string, FolderRole>(StringComparer.Ordinal);
        foreach (var (kind, role) in new[]
                 {
                     (SpecialFolder.Sent, FolderRole.Sent),
                     (SpecialFolder.Drafts, FolderRole.Drafts),
                     (SpecialFolder.Trash, FolderRole.Deleted),
                     (SpecialFolder.Junk, FolderRole.Junk),
                     (SpecialFolder.Archive, FolderRole.Archive),
                 })
        {
            try
            {
                if (_client.GetFolder(kind) is { } found) special.TryAdd(found.FullName, role);
            }
            catch (Exception)
            {
                // A server with no such folder and no way to guess one. Nothing to record.
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RemoteFolder>();
        foreach (var folder in folders)
        {
            if (!seen.Add(folder.FullName)) continue;

            var attributes = folder.Attributes;
            var role = FolderRole.None;
            if (attributes.HasFlag(FolderAttributes.Inbox) || folder == _client.Inbox) role = FolderRole.Inbox;
            else if (attributes.HasFlag(FolderAttributes.Sent)) role = FolderRole.Sent;
            else if (attributes.HasFlag(FolderAttributes.Drafts)) role = FolderRole.Drafts;
            else if (attributes.HasFlag(FolderAttributes.Trash)) role = FolderRole.Deleted;
            else if (attributes.HasFlag(FolderAttributes.Junk)) role = FolderRole.Junk;
            else if (attributes.HasFlag(FolderAttributes.Archive)) role = FolderRole.Archive;
            else if (special.TryGetValue(folder.FullName, out var guessed)) role = guessed;

            var isView = attributes.HasFlag(FolderAttributes.All)
                         || attributes.HasFlag(FolderAttributes.Flagged)
                         || attributes.HasFlag(FolderAttributes.Important);

            var selectable = !attributes.HasFlag(FolderAttributes.NoSelect)
                             && !attributes.HasFlag(FolderAttributes.NonExistent);

            var parent = folder.ParentFolder;
            var parentPath = parent is null || parent.IsNamespace || parent.FullName.Length == 0
                ? null
                : parent.FullName;

            result.Add(new RemoteFolder(folder.FullName, folder.Name, parentPath, role, selectable, isView));
        }

        return result;
    }

    public async Task<RemoteFolder> CreateFolderAsync(string name, CancellationToken cancellation, string? parentPath = null)
    {
        var root = parentPath is { Length: > 0 }
            ? await _client.GetFolderAsync(parentPath, cancellation)
            : _client.GetFolder(_client.PersonalNamespaces[0]);
        var created = await root.CreateAsync(name, true, cancellation)
            ?? throw new InvalidOperationException($"The server did not create \"{name}\".");
        try
        {
            await created.SubscribeAsync(cancellation);
        }
        catch (Exception)
        {
            // Subscription is a courtesy to other clients; a server that refuses it still made the folder.
        }

        return new RemoteFolder(created.FullName, created.Name, parentPath is { Length: > 0 } ? parentPath : null, FolderRole.None, true, false);
    }

    public async Task<RemoteFolder> RenameFolderAsync(string path, string newName, CancellationToken cancellation)
    {
        var folder = await _client.GetFolderAsync(path, cancellation);
        var parent = folder.ParentFolder ?? _client.GetFolder(_client.PersonalNamespaces[0]);
        await folder.RenameAsync(parent, newName, cancellation);
        var parentPath = folder.ParentFolder is { FullName.Length: > 0 } up ? up.FullName : null;
        return new RemoteFolder(folder.FullName, folder.Name, parentPath, FolderRole.None, true, false);
    }

    public async Task<RemoteFolder> MoveFolderAsync(string path, string? newParentPath, CancellationToken cancellation)
    {
        var folder = await _client.GetFolderAsync(path, cancellation);
        var parent = newParentPath is { Length: > 0 } up
            ? await _client.GetFolderAsync(up, cancellation)
            : _client.GetFolder(_client.PersonalNamespaces[0]);
        // RENAME with a new parent is how IMAP moves a folder; MailKit spells it the same way.
        await folder.RenameAsync(parent, folder.Name, cancellation);
        var parentPath = folder.ParentFolder is { FullName.Length: > 0 } now ? now.FullName : null;
        return new RemoteFolder(folder.FullName, folder.Name, parentPath, FolderRole.None, true, false);
    }

    public async Task DeleteFolderAsync(string path, CancellationToken cancellation)
    {
        var folder = await _client.GetFolderAsync(path, cancellation);
        await folder.DeleteAsync(cancellation);
    }

    public async Task<FolderState> OpenAsync(string path, CancellationToken cancellation)
    {
        var folder = await _client.GetFolderAsync(path, cancellation);
        if (_open is not null && !ReferenceEquals(_open, folder))
        {
            Unhook(_open);
        }

        if (!folder.IsOpen || folder.Access != FolderAccess.ReadWrite)
        {
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellation);
        }
        else
        {
            // Already selected, and the library will not send a second SELECT for a folder it
            // believes is open — so UIDNEXT and HIGHESTMODSEQ would still be whatever the first
            // one reported, however long ago. The count keeps itself current from the untagged
            // EXISTS the server volunteers; those two do not, and a state that is half fresh is
            // worse than one that is plainly stale because nothing about it looks wrong.
            //
            // Found against a real server: appending a message left UIDNEXT where it was while
            // the count moved. Nothing decides anything on UIDNEXT today, but it is written into
            // the store as a fact, and the obvious use for it — "has anything arrived?" — would
            // have been quietly wrong from the first day somebody wrote it.
            try
            {
                await folder.StatusAsync(
                    StatusItems.UidValidity | StatusItems.UidNext | StatusItems.HighestModSeq | StatusItems.Count,
                    cancellation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // RFC 3501 says a client SHOULD NOT ask a server about the mailbox it has
                // selected, and a server within its rights to refuse leaves the numbers as they
                // were. Said out loud rather than silently, because it means the state below is
                // as of the last select.
                Log.Info($"“{path}” would not answer STATUS while selected, so its state is as of the last select: {ex.Message}");
            }
        }

        _open = folder;
        Hook(folder);

        return new FolderState(
            folder.UidValidity,
            folder.UidNext?.Id ?? 0,
            (long)folder.HighestModSeq,
            folder.Supports(FolderFeature.ModSequences),
            folder.Count);
    }

    private void Hook(IMailFolder folder)
    {
        folder.CountChanged += OnChanged;
        folder.MessageFlagsChanged += OnChanged;
        folder.MessagesVanished += OnChanged;
        folder.MessageExpunged += OnChanged;
    }

    private void Unhook(IMailFolder folder)
    {
        folder.CountChanged -= OnChanged;
        folder.MessageFlagsChanged -= OnChanged;
        folder.MessagesVanished -= OnChanged;
        folder.MessageExpunged -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => FolderChanged?.Invoke(this, EventArgs.Empty);

    private IMailFolder Open => _open ?? throw new InvalidOperationException("No folder is open.");

    public async Task<IReadOnlyList<long>> SearchAllAsync(CancellationToken cancellation)
        => [.. (await Open.SearchAsync(SearchQuery.All, cancellation)).Select(u => (long)u.Id)];

    public async Task<IReadOnlyList<long>> SearchByMessageIdAsync(string messageId, CancellationToken cancellation)
        => [.. (await Open.SearchAsync(SearchQuery.HeaderContains("Message-Id", messageId), cancellation)).Select(u => (long)u.Id)];

    public async Task<IReadOnlyList<RemoteMessageInfo>> FetchInfoAsync(IReadOnlyList<long> uids, CancellationToken cancellation)
    {
        if (uids.Count == 0) return [];

        var summaries = await Open.FetchAsync(
            [.. uids.Select(u => new UniqueId((uint)u))],
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Size,
            cancellation);

        return [.. summaries.Select(Info)];
    }

    public async Task<IReadOnlyList<RemoteMessageInfo>> FetchFlagsChangedSinceAsync(long modSeq, CancellationToken cancellation)
    {
        var summaries = await Open.FetchAsync(
            new UniqueIdRange(UniqueId.MinValue, UniqueId.MaxValue),
            (ulong)modSeq,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.ModSeq,
            cancellation);

        return [.. summaries.Select(Info)];
    }

    private static RemoteMessageInfo Info(IMessageSummary summary) => new(
        summary.UniqueId.Id,
        summary.Flags ?? MessageFlags.None,
        summary.InternalDate,
        summary.Size ?? 0);

    public async Task<MimeMessage?> GetMessageAsync(long uid, CancellationToken cancellation)
    {
        try
        {
            return await Open.GetMessageAsync(new UniqueId((uint)uid), cancellation);
        }
        catch (MessageNotFoundException)
        {
            return null;
        }
    }

    public Task StoreFlagsAsync(IReadOnlyList<long> uids, MessageFlags flags, bool set, CancellationToken cancellation)
    {
        IList<UniqueId> ids = [.. uids.Select(u => new UniqueId((uint)u))];
        return set
            ? Open.AddFlagsAsync(ids, flags, true, cancellation)
            : Open.RemoveFlagsAsync(ids, flags, true, cancellation);
    }

    public async Task<IReadOnlyDictionary<long, long>> MoveAsync(IReadOnlyList<long> uids, string destinationPath, CancellationToken cancellation)
    {
        var destination = await _client.GetFolderAsync(destinationPath, cancellation);
        IList<UniqueId> ids = [.. uids.Select(u => new UniqueId((uint)u))];

        // MailKit does the copy-flag-expunge dance itself where MOVE is missing.
        var map = await Open.MoveToAsync(ids, destination, cancellation);

        var result = new Dictionary<long, long>();
        foreach (var pair in map) result[pair.Key.Id] = pair.Value.Id;
        return result;
    }

    public async Task ExpungeAsync(IReadOnlyList<long> uids, CancellationToken cancellation)
    {
        IList<UniqueId> ids = [.. uids.Select(u => new UniqueId((uint)u))];
        await Open.AddFlagsAsync(ids, MessageFlags.Deleted, true, cancellation);
        await Open.ExpungeAsync(ids, cancellation);
    }

    public async Task<long?> AppendAsync(string path, byte[] raw, MessageFlags flags, DateTimeOffset? date, CancellationToken cancellation)
    {
        var folder = await _client.GetFolderAsync(path, cancellation);
        using var stream = new MemoryStream(raw);
        var message = await MimeMessage.LoadAsync(stream, cancellation);

        var uid = date is { } when
            ? await folder.AppendAsync(message, flags, when, cancellation)
            : await folder.AppendAsync(message, flags, cancellation);

        return uid?.Id;
    }

    public Task IdleAsync(CancellationToken done, CancellationToken cancellation)
        => _client.IdleAsync(done, cancellation);

    public void Dispose()
    {
        if (_open is not null) Unhook(_open);
        _client.Dispose();
    }
}
