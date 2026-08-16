using Mailbox.Core.Diagnostics;
using Mailbox.Store;

namespace Mailbox.Protocols;

/// <summary>
/// The folder pane's New Folder, Rename Folder and Delete Folder: on the store for a POP3
/// account, and on the server first for an IMAP one — a folder made, renamed or removed here
/// alone would be undone by the next sync, which takes the server's list as the truth.
/// </summary>
/// <remarks>
/// Done at once rather than journalled: a folder is a rare, deliberate act, and a person who
/// makes one wants to file into it now, so a server that cannot be reached is an answer rather
/// than a queue. The messages of a deleted folder go with it here as they do on the server.
/// </remarks>
public sealed class FolderManager(MailRepository repository)
{
    private readonly MailRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>Lets a test supply a fake server. Null uses MailKit.</summary>
    public Func<IImapSession>? SessionFactory { get; set; }

    /// <summary>Makes a folder, under a parent or at the top. On IMAP the server makes it first.</summary>
    public async Task<Folder> CreateAsync(AccountConnection? connection, long accountId, string name, long? parentId, CancellationToken cancellation = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A folder needs a name.", nameof(name));

        var parent = parentId is { } id ? _repository.GetFolder(id) : null;

        if (connection is { Protocol: MailProtocol.Imap })
        {
            var session = SessionFactory?.Invoke() ?? new MailKitImapSession();
            try
            {
                await session.ConnectAsync(connection.Incoming, cancellation);
                await session.AuthenticateAsync(connection.Incoming, cancellation);
                var made = await session.CreateFolderAsync(trimmed, cancellation, parent?.ImapPath);
                var folder = _repository.AddFolder(accountId, made.Name, FolderRole.None, parent?.Id, made.Path);
                Log.Info($"Folder \"{made.Path}\" created on {connection.Address}.");
                return folder;
            }
            finally
            {
                await Quietly(session, cancellation);
            }
        }

        return _repository.AddFolder(accountId, trimmed, FolderRole.None, parent?.Id);
    }

    /// <summary>Renames a folder; the folders under it keep their place. On IMAP the server renames first.</summary>
    public async Task RenameAsync(AccountConnection? connection, Folder folder, string newName, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed == folder.Name) return;

        if (connection is { Protocol: MailProtocol.Imap } && folder.ImapPath is { } path)
        {
            var session = SessionFactory?.Invoke() ?? new MailKitImapSession();
            try
            {
                await session.ConnectAsync(connection.Incoming, cancellation);
                await session.AuthenticateAsync(connection.Incoming, cancellation);
                var renamed = await session.RenameFolderAsync(path, trimmed, cancellation);
                _repository.RenameFolder(folder.Id, renamed.Name, renamed.Path);
                Log.Info($"Folder \"{path}\" renamed to \"{renamed.Path}\" on {connection.Address}.");
                return;
            }
            finally
            {
                await Quietly(session, cancellation);
            }
        }

        _repository.RenameFolder(folder.Id, trimmed, null);
    }

    /// <summary>
    /// Puts a folder under another, or at the top when <paramref name="newParentId"/> is null,
    /// keeping its name; the folders under it come along. On IMAP the server renames the tree
    /// first. Returns false when the move is refused — into itself, or under one of its own.
    /// </summary>
    public async Task<bool> MoveAsync(AccountConnection? connection, Folder folder, long? newParentId, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (newParentId == folder.ParentId) return true;

        for (var up = newParentId; up is { } id;)
        {
            if (id == folder.Id) return false;
            up = _repository.GetFolder(id)?.ParentId;
        }

        var parent = newParentId is { } pid ? _repository.GetFolder(pid) : null;

        if (connection is { Protocol: MailProtocol.Imap } && folder.ImapPath is { } path)
        {
            var session = SessionFactory?.Invoke() ?? new MailKitImapSession();
            try
            {
                await session.ConnectAsync(connection.Incoming, cancellation);
                await session.AuthenticateAsync(connection.Incoming, cancellation);
                var moved = await session.MoveFolderAsync(path, parent?.ImapPath, cancellation);
                var done = _repository.MoveFolder(folder.Id, parent?.Id, moved.Path);
                Log.Info($"Folder \"{path}\" moved to \"{moved.Path}\" on {connection.Address}.");
                return done;
            }
            finally
            {
                await Quietly(session, cancellation);
            }
        }

        return _repository.MoveFolder(folder.Id, parent?.Id, null);
    }

    /// <summary>
    /// Copies a folder — its mail and the folders under it — under another folder, or to the
    /// top when <paramref name="newParentId"/> is null. A copy is a new folder with the same
    /// name and new rows over the same bytes; on IMAP each new folder is created on the server
    /// first and each copied message is journalled to be appended there.
    /// </summary>
    /// <returns>The copy of the folder itself.</returns>
    public async Task<Folder> CopyAsync(AccountConnection? connection, long accountId, Folder folder, long? newParentId, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        // Copying a folder into itself would copy the copy; refuse the same way a move does.
        for (var up = newParentId; up is { } id;)
        {
            if (id == folder.Id) throw new InvalidOperationException("A folder cannot be copied into itself.");
            up = _repository.GetFolder(id)?.ParentId;
        }

        Folder? made = null;
        var all = _repository.Folders(accountId);

        async Task CopyTree(Folder source, long? intoParent)
        {
            var copy = await CreateAsync(connection, accountId, source.Name, intoParent, cancellation);
            made ??= copy;

            var ids = _repository.Messages(source.Id, int.MaxValue).Select(m => m.Id).ToList();
            if (ids.Count > 0) _repository.CopyMessages(ids, copy.Id);

            foreach (var child in all.Where(f => f.ParentId == source.Id).OrderBy(f => f.Ordinal).ThenBy(f => f.Id))
            {
                await CopyTree(child, copy.Id);
            }
        }

        await CopyTree(folder, newParentId);
        return made!;
    }

    /// <summary>Deletes a folder, its subfolders and their mail. On IMAP the server deletes first.</summary>
    public async Task DeleteAsync(AccountConnection? connection, Folder folder, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (connection is { Protocol: MailProtocol.Imap } && folder.ImapPath is { } path)
        {
            var session = SessionFactory?.Invoke() ?? new MailKitImapSession();
            try
            {
                await session.ConnectAsync(connection.Incoming, cancellation);
                await session.AuthenticateAsync(connection.Incoming, cancellation);
                await session.DeleteFolderAsync(path, cancellation);
                Log.Info($"Folder \"{path}\" deleted on {connection.Address}.");
            }
            finally
            {
                await Quietly(session, cancellation);
            }
        }

        _repository.RemoveFolderTree(folder.Id);
    }

    private static async Task Quietly(IImapSession session, CancellationToken cancellation)
    {
        try
        {
            await session.DisconnectAsync(cancellation);
        }
        catch (Exception)
        {
            // Leaving is a courtesy; a session that will not say goodbye is disposed all the same.
        }

        session.Dispose();
    }
}
