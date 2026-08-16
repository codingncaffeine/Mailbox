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
