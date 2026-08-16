using MailKit;
using MimeKit;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// An IMAP server in memory: folders, messages with UIDs and flags, UIDVALIDITY, and the few
/// operations a sync performs. Enough to drive <see cref="ImapSynchronizer"/> end to end without
/// a socket, and to check the two things that actually go wrong — that the journal reaches the
/// server in the right order, and that a UIDVALIDITY change is handled rather than trusted.
/// </summary>
internal sealed class FakeImap : IImapSession
{
    internal sealed class ServerMessage(long uid, MimeMessage message)
    {
        public long Uid { get; set; } = uid;
        public MimeMessage Message { get; } = message;
        public MessageFlags Flags { get; set; }
        public DateTimeOffset Internal { get; set; } = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
    }

    internal sealed class ServerFolder(string path, FolderRole role)
    {
        public string Path { get; } = path;
        public string Name { get; init; } = path.Split('/').Last();
        public string? ParentPath { get; init; }
        public FolderRole Role { get; } = role;
        public bool IsView { get; init; }
        public bool Selectable { get; init; } = true;
        public long UidValidity { get; set; } = 1;
        public long NextUid { get; set; } = 1;
        public List<ServerMessage> Messages { get; } = [];
    }

    private readonly Dictionary<string, ServerFolder> _folders = new(StringComparer.Ordinal);
    private ServerFolder? _open;

    public bool IsConnected { get; private set; }
    public ImapFeatures Features { get; set; } =
        ImapFeatures.CondStore | ImapFeatures.Move | ImapFeatures.UidPlus | ImapFeatures.Idle;
    public bool ReturnMoveMap { get; set; } = true;
    public event EventHandler? FolderChanged;

    public FakeImap()
    {
        Folder("INBOX", FolderRole.Inbox);
        Folder("Sent", FolderRole.Sent);
        Folder("Trash", FolderRole.Deleted);
    }

    public ServerFolder Folder(string path, FolderRole role = FolderRole.None, bool isView = false)
    {
        var folder = new ServerFolder(path, role) { IsView = isView };
        _folders[path] = folder;
        return folder;
    }

    public ServerMessage Deliver(string path, string subject, string from = "sender@example.com",
        MessageFlags flags = MessageFlags.None, DateTimeOffset? arrived = null)
    {
        var folder = _folders[path];
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress("Sender", from));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.MessageId = $"{Guid.NewGuid():n}@example.com";
        message.Body = new TextPart("plain") { Text = $"Body of {subject}" };

        var server = new ServerMessage(folder.NextUid++, message) { Flags = flags };
        if (arrived is { } when) server.Internal = when;
        folder.Messages.Add(server);
        return server;
    }

    public IReadOnlyList<ServerMessage> Contents(string path) => _folders[path].Messages;

    /// <summary>Removes a message from the server as an expunge elsewhere would.</summary>
    public void Expunge(string path, long uid) => _folders[path].Messages.RemoveAll(m => m.Uid == uid);

    public Task ConnectAsync(ServerSettings s, CancellationToken c) { IsConnected = true; return Task.CompletedTask; }
    public Task AuthenticateAsync(ServerSettings s, CancellationToken c) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken c) { IsConnected = false; return Task.CompletedTask; }

    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken c)
        => Task.FromResult<IReadOnlyList<RemoteFolder>>(
            [.. _folders.Values.Select(f => new RemoteFolder(f.Path, f.Name, f.ParentPath, f.Role, f.Selectable, f.IsView))]);

    public Task<RemoteFolder> CreateFolderAsync(string name, CancellationToken c, string? parentPath = null)
    {
        var path = parentPath is { Length: > 0 } ? parentPath + "/" + name : name;
        var folder = Folder(path);
        return Task.FromResult(new RemoteFolder(folder.Path, name, parentPath is { Length: > 0 } ? parentPath : null, FolderRole.None, true, false));
    }

    public Task<RemoteFolder> RenameFolderAsync(string path, string newName, CancellationToken c)
    {
        var slash = path.LastIndexOf('/');
        var parent = slash > 0 ? path[..slash] : null;
        var newPath = parent is null ? newName : parent + "/" + newName;

        ServerFolder Moved(ServerFolder from, string to)
        {
            var moved = new ServerFolder(to, from.Role) { IsView = from.IsView, Selectable = from.Selectable, UidValidity = from.UidValidity, NextUid = from.NextUid };
            moved.Messages.AddRange(from.Messages);
            return moved;
        }

        var folder = _folders[path];
        _folders.Remove(path);
        _folders[newPath] = Moved(folder, newPath);

        // The folders under it move with it, as they do on a real server.
        foreach (var child in _folders.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            var to = newPath + child[path.Length..];
            var moved = Moved(_folders[child], to);
            _folders.Remove(child);
            _folders[to] = moved;
        }

        return Task.FromResult(new RemoteFolder(newPath, newName, parent, FolderRole.None, true, false));
    }

    public Task<RemoteFolder> MoveFolderAsync(string path, string? newParentPath, CancellationToken c)
    {
        var slash = path.LastIndexOf('/');
        var name = slash > 0 ? path[(slash + 1)..] : path;
        var newPath = string.IsNullOrEmpty(newParentPath) ? name : newParentPath + "/" + name;

        ServerFolder Moved(ServerFolder from, string to)
        {
            var moved = new ServerFolder(to, from.Role) { IsView = from.IsView, Selectable = from.Selectable, UidValidity = from.UidValidity, NextUid = from.NextUid };
            moved.Messages.AddRange(from.Messages);
            return moved;
        }

        var folder = _folders[path];
        _folders.Remove(path);
        _folders[newPath] = Moved(folder, newPath);

        foreach (var child in _folders.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            var to = newPath + child[path.Length..];
            var moved = Moved(_folders[child], to);
            _folders.Remove(child);
            _folders[to] = moved;
        }

        return Task.FromResult(new RemoteFolder(newPath, name, string.IsNullOrEmpty(newParentPath) ? null : newParentPath, FolderRole.None, true, false));
    }

    public Task DeleteFolderAsync(string path, CancellationToken c)
    {
        _folders.Remove(path);
        foreach (var child in _folders.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) _folders.Remove(child);
        return Task.CompletedTask;
    }

    public Task<FolderState> OpenAsync(string path, CancellationToken c)
    {
        _open = _folders[path];
        return Task.FromResult(new FolderState(
            _open.UidValidity, _open.NextUid, _open.Messages.Count + 1,
            Features.HasFlag(ImapFeatures.CondStore), _open.Messages.Count));
    }

    private ServerFolder Open => _open ?? throw new InvalidOperationException("No folder open.");

    public Task<IReadOnlyList<long>> SearchAllAsync(CancellationToken c)
        => Task.FromResult<IReadOnlyList<long>>([.. Open.Messages.Select(m => m.Uid)]);

    public Task<IReadOnlyList<long>> SearchByMessageIdAsync(string messageId, CancellationToken c)
        => Task.FromResult<IReadOnlyList<long>>(
            [.. Open.Messages.Where(m => m.Message.MessageId == messageId).Select(m => m.Uid)]);

    public Task<IReadOnlyList<RemoteMessageInfo>> FetchInfoAsync(IReadOnlyList<long> uids, CancellationToken c)
        => Task.FromResult<IReadOnlyList<RemoteMessageInfo>>(
            [.. Open.Messages.Where(m => uids.Contains(m.Uid))
                .Select(m => new RemoteMessageInfo(m.Uid, m.Flags, m.Internal, 100))]);

    public Task<IReadOnlyList<RemoteMessageInfo>> FetchFlagsChangedSinceAsync(long modSeq, CancellationToken c)
        => Task.FromResult<IReadOnlyList<RemoteMessageInfo>>(
            [.. Open.Messages.Select(m => new RemoteMessageInfo(m.Uid, m.Flags, m.Internal, 100))]);

    public Task<MimeMessage?> GetMessageAsync(long uid, CancellationToken c)
        => Task.FromResult(Open.Messages.FirstOrDefault(m => m.Uid == uid)?.Message);

    public List<(long Uid, MessageFlags Flag, bool Set)> FlagStores { get; } = [];

    public Task StoreFlagsAsync(IReadOnlyList<long> uids, MessageFlags flags, bool set, CancellationToken c)
    {
        foreach (var uid in uids)
        {
            FlagStores.Add((uid, flags, set));
            if (Open.Messages.FirstOrDefault(m => m.Uid == uid) is { } m)
            {
                m.Flags = set ? m.Flags | flags : m.Flags & ~flags;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<long, long>> MoveAsync(IReadOnlyList<long> uids, string destinationPath, CancellationToken c)
    {
        var destination = _folders[destinationPath];
        var map = new Dictionary<long, long>();

        foreach (var uid in uids)
        {
            if (Open.Messages.FirstOrDefault(m => m.Uid == uid) is not { } message) continue;
            Open.Messages.Remove(message);
            var newUid = destination.NextUid++;
            map[uid] = newUid;
            message.Uid = newUid;
            destination.Messages.Add(message);
        }

        return Task.FromResult<IReadOnlyDictionary<long, long>>(ReturnMoveMap ? map : new Dictionary<long, long>());
    }

    public Task ExpungeAsync(IReadOnlyList<long> uids, CancellationToken c)
    {
        Open.Messages.RemoveAll(m => uids.Contains(m.Uid));
        return Task.CompletedTask;
    }

    public Task<long?> AppendAsync(string path, byte[] raw, MessageFlags flags, DateTimeOffset? date, CancellationToken c)
    {
        var folder = _folders[path];
        using var stream = new MemoryStream(raw);
        var message = MimeMessage.Load(stream);
        var server = new ServerMessage(folder.NextUid++, message) { Flags = flags };
        if (date is { } when) server.Internal = when;
        folder.Messages.Add(server);
        return Task.FromResult<long?>(server.Uid);
    }

    private volatile TaskCompletionSource? _idle;

    /// <summary>True while a caller is blocked in <see cref="IdleAsync"/>.</summary>
    public bool IsIdling => _idle is { Task.IsCompleted: false };

    /// <summary>
    /// Holds until the renewal token fires or <see cref="Raise"/> announces a change, like a
    /// real IDLE that blocks on the connection rather than returning at once.
    /// </summary>
    public Task IdleAsync(CancellationToken done, CancellationToken c)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _idle = gate;
        done.Register(() => gate.TrySetResult());
        c.Register(() => gate.TrySetCanceled(c));
        return gate.Task;
    }

    /// <summary>Announces a change to whatever is idling, as the server breaking IDLE would.</summary>
    public void Raise()
    {
        FolderChanged?.Invoke(this, EventArgs.Empty);
        _idle?.TrySetResult();
    }

    public void Dispose() { }
}
