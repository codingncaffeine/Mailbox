namespace Mailbox.Import;

/// <summary>One message found in a maildir: where it files, and what its name said about it.</summary>
/// <remarks>
/// The folder is a path of segments rather than a joined string, because two different worlds
/// join them differently — Maildir++ writes <c>.Work.Projects</c> and a nested tree writes
/// <c>Work/Projects</c> — and the importer wants the hierarchy, not the spelling.
/// </remarks>
public sealed record MaildirMessage(
    IReadOnlyList<string> Folder,
    string Path,
    bool IsRead,
    bool IsFlagged,
    bool IsTrashed);

/// <summary>
/// Reads maildir trees: the one-file-per-message store everything on this desktop writes.
/// </summary>
/// <remarks>
/// Three layouts cover the sources people actually migrate from, and a scan tells them apart by looking rather
/// than by being told:
/// <list type="bullet">
/// <item><b>Plain</b> — <c>cur/new/tmp</c> at the root: one folder.</item>
/// <item><b>Maildir++</b> (Dovecot, Courier) — the root is INBOX and each <c>.Name</c> sibling
/// is a folder, dots inside the name being hierarchy: <c>.Work.Projects</c>.</item>
/// <item><b>Nested</b> (mutt, offlineimap, KMail) — subdirectories that are themselves
/// maildirs, recursively; KMail's <c>.Name.directory</c> children fold into Name's.</item>
/// </list>
/// Flags ride the filename after <c>:2,</c> — S seen, F flagged, T trashed — and anything in
/// <c>new/</c> is unread whatever its name says, that being what <c>new/</c> means. Nothing
/// here opens a message: the scan is names and places, so a tree of gigabytes lists in
/// milliseconds and the reading happens where the report can count it.
/// </remarks>
public static class Maildir
{
    /// <summary>Whether a directory is itself a maildir: <c>cur</c> and <c>new</c> exist.</summary>
    public static bool Looks(string directory)
        => Directory.Exists(System.IO.Path.Combine(directory, "cur"))
           && Directory.Exists(System.IO.Path.Combine(directory, "new"));

    /// <summary>
    /// Whether a directory holds anything a maildir import could read: a maildir at the root,
    /// Maildir++ dot-folders beside it, or maildirs nested below.
    /// </summary>
    public static bool LooksLikeATree(string directory)
        => Directory.Exists(directory) && Scan(directory).Count > 0;

    /// <summary>Every message under the root, with its folder path. Deterministic order.</summary>
    public static IReadOnlyList<MaildirMessage> Scan(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var found = new List<MaildirMessage>();
        if (!Directory.Exists(root)) return found;

        // The root itself: INBOX in Maildir++, the folder's own mail in a plain maildir.
        if (Looks(root)) AddFolder(found, root, ["Inbox"]);

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(directory);
            if (name is "cur" or "new" or "tmp") continue;

            if (name.StartsWith('.'))
            {
                // KMail writes a folder's children beside it in ".Name.directory"; everything
                // found inside is parented under Name. A child of ["Inbox"] is the .directory's
                // own root maildir — mail of the parent folder itself, filed as the parent.
                if (name.EndsWith(".directory", StringComparison.Ordinal))
                {
                    var parent = Segments(name[1..^".directory".Length]);
                    foreach (var child in Scan(directory))
                    {
                        var tail = child.Folder is ["Inbox"] ? [] : child.Folder;
                        found.Add(child with { Folder = [.. parent, .. tail] });
                    }

                    continue;
                }

                // Maildir++: ".Work.Projects" is Work/Projects. A dot-folder that is not a
                // maildir is somebody's stray dotfile, not a folder.
                if (Looks(directory)) AddFolder(found, directory, Segments(name[1..]));
                continue;
            }

            // A nested tree: the directory is a folder if it is a maildir, and either way its
            // own subdirectories may be.
            foreach (var child in Scan(directory))
            {
                found.Add(child with
                {
                    Folder = child.Folder.Count == 1 && child.Folder[0] == "Inbox"
                        ? [name]
                        : [name, .. child.Folder],
                });
            }
        }

        return found;
    }

    private static string[] Segments(string dotted)
        => dotted.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddFolder(List<MaildirMessage> found, string maildir, IReadOnlyList<string> folder)
    {
        foreach (var (subdirectory, unread) in new[] { ("cur", false), ("new", true) })
        {
            var home = System.IO.Path.Combine(maildir, subdirectory);
            if (!Directory.Exists(home)) continue;

            foreach (var file in Directory.EnumerateFiles(home).OrderBy(f => f, StringComparer.Ordinal))
            {
                var flags = Flags(System.IO.Path.GetFileName(file));
                found.Add(new MaildirMessage(
                    folder,
                    file,
                    IsRead: !unread && flags.Contains('S'),
                    IsFlagged: flags.Contains('F'),
                    IsTrashed: flags.Contains('T')));
            }
        }
    }

    /// <summary>The flag characters after <c>:2,</c>, or none — a name without them is legal.</summary>
    private static string Flags(string fileName)
    {
        var marker = fileName.LastIndexOf(":2,", StringComparison.Ordinal);
        return marker < 0 ? string.Empty : fileName[(marker + 3)..];
    }
}
