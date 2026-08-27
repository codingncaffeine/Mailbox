using Mailbox.Core.Diagnostics;

namespace Mailbox.Store;

/// <summary>What moving one mail account's feeds into the feeds store did.</summary>
/// <param name="Folders">Folders recreated in the feeds store.</param>
/// <param name="Articles">Articles copied across.</param>
public sealed record FeedMove(int Folders, int Articles)
{
    public static readonly FeedMove Nothing = new(0, 0);

    /// <summary>Boards brought over, with what was on them.</summary>
    public int Boards { get; init; }

    /// <summary>True when everything arrived and the originals were taken away.</summary>
    public bool Completed { get; init; }

    public bool DidAnything => Folders > 0 || Articles > 0 || Boards > 0;
}

/// <summary>
/// Moves feeds out of a mail account's store and into the feed reader's own.
/// </summary>
/// <remarks>
/// Feeds were filed into whichever mail account sorted first. This is the one-off that puts
/// somebody's existing subscriptions where they now belong, and it runs on a reader's only copy
/// of things, so its shape is: <b>copy everything, count it, and only then take the old away.</b>
/// Nothing is deleted until the same number of articles is standing in the new store, and a move
/// that cannot say that leaves both copies and says so in the log — a duplicate is an annoyance
/// and a deletion is not.
/// <para>
/// What travels: the folder tree under the feeds root, every article in it with its read and
/// flagged state and the raw message behind it, the colour categories it carried, and the boards
/// with what is on them and when each was saved. Boards live in the store their articles do, so
/// leaving them behind would empty every keep pile the reader had.
/// </para>
/// <para>
/// Idempotent. A second run finds nothing to move; a run interrupted half way finds what it
/// already copied and skips it, matching on the server id within a folder as the poll does.
/// </para>
/// </remarks>
public static class FeedStoreMove
{
    /// <summary>Moves every mail account's feeds into the feeds store.</summary>
    public static FeedMove MoveAll(OpenAccount feeds, IEnumerable<OpenAccount> mailAccounts, string rootFolder)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(mailAccounts);

        var folders = 0;
        var articles = 0;
        var boards = 0;
        var completed = true;

        foreach (var account in mailAccounts)
        {
            if (ReferenceEquals(account, feeds)) continue;

            var moved = Move(feeds, account, rootFolder);
            if (!moved.DidAnything) continue;

            folders += moved.Folders;
            articles += moved.Articles;
            boards += moved.Boards;
            completed &= moved.Completed;
        }

        return new FeedMove(folders, articles) { Boards = boards, Completed = completed };
    }

    /// <summary>Moves one mail account's feeds across.</summary>
    public static FeedMove Move(OpenAccount feeds, OpenAccount from, string rootFolder)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(from);

        var theirs = from.Mail.Folders(from.Account.Id);
        var root = theirs.FirstOrDefault(f => f.ParentId is null && f.Name == rootFolder);
        if (root is null) return FeedMove.Nothing;

        var subtree = Subtree(theirs, root);
        var carried = subtree.Sum(f => from.Mail.Messages(f.Id, limit: 100_000).Count);

        Log.Info($"Feeds: moving {carried} article(s) in {subtree.Count} folder(s) out of "
            + $"{from.Account.Address} and into the feeds store.");

        // Folders first, so every message has somewhere to land. Old id to new id, because a
        // child names its parent by id and the ids are per store.
        var folderFor = new Dictionary<long, long>();
        foreach (var folder in subtree)
        {
            var parent = folder.ParentId is { } up && folderFor.TryGetValue(up, out var mapped) ? mapped : (long?)null;
            folderFor[folder.Id] = Landing(feeds, folder.Name, parent).Id;
        }

        // Then the articles, with the state the reader put on them.
        var messageFor = new Dictionary<long, long>();
        var copied = 0;

        foreach (var folder in subtree)
        {
            var landing = folderFor[folder.Id];
            var already = feeds.Mail.ServerUidIndex(landing);
            var carriedCategories = new Dictionary<long, List<Category>>();

            var here = from.Mail.Messages(folder.Id, limit: 100_000);
            if (here.Count > 0)
            {
                carriedCategories = from.Mail.CategoriesFor([.. here.Select(m => m.Id)]);
            }

            foreach (var article in here)
            {
                // Already carried over by a run that did not finish. Matched the way the poll
                // matches, so an interrupted move costs nothing and duplicates nothing.
                if (article.ServerUid is { Length: > 0 } uid && already.TryGetValue(uid, out var standing))
                {
                    messageFor[article.Id] = standing.Id;
                    continue;
                }

                var raw = from.Mail.LoadRaw(article.Id);
                var landed = feeds.Mail.AddMessage(landing, article with { Id = 0, FolderId = landing }, raw);
                if (landed is not { } id) continue;

                messageFor[article.Id] = id;
                copied++;

                foreach (var category in carriedCategories.GetValueOrDefault(article.Id) ?? [])
                {
                    if (Named(feeds, category) is { } mirrored) feeds.Mail.Assign([id], mirrored.Id);
                }
            }
        }

        var movedBoards = MoveBoards(feeds, from, messageFor);

        // Counted before anything is taken away. A move that cannot account for every article
        // leaves both copies: a duplicate is an annoyance, and a deletion is not.
        var landedAll = subtree.Sum(f => feeds.Mail.Messages(folderFor[f.Id], limit: 100_000).Count) >= carried;

        if (!landedAll)
        {
            Log.Warn($"Feeds: only some of {from.Account.Address}'s articles reached the feeds store, "
                + "so the originals have been left where they are. Nothing has been lost; there are two copies.");

            return new FeedMove(subtree.Count, copied) { Boards = movedBoards, Completed = false };
        }

        foreach (var board in from.Mail.Boards()) from.Mail.DeleteBoard(board.Id);
        from.Mail.RemoveFolderTree(root.Id);

        Log.Info($"Feeds: {copied} article(s), {subtree.Count} folder(s) and {movedBoards} board(s) moved "
            + $"out of {from.Account.Address}.");

        return new FeedMove(subtree.Count, copied) { Boards = movedBoards, Completed = true };
    }

    /// <summary>
    /// Brings the boards over with what is on them.
    /// </summary>
    /// <remarks>
    /// Boards live in the store their articles live in, so a move that left them behind would
    /// empty every keep pile the reader had — silently, because the board would still be listed
    /// and would simply have nothing in it.
    /// </remarks>
    private static int MoveBoards(OpenAccount feeds, OpenAccount from, Dictionary<long, long> messageFor)
    {
        var moved = 0;

        foreach (var board in from.Mail.Boards())
        {
            var landing = feeds.Mail.AddBoard(board.Name, DateTimeOffset.UtcNow, board.Description);

            foreach (var (messageId, savedUtc) in from.Mail.BoardItems(board.Id))
            {
                // Only what came across. Anything else was a message this move did not carry —
                // a mail message somebody had saved to a board — and it stays where it is.
                if (!messageFor.TryGetValue(messageId, out var landed)) continue;

                feeds.Mail.SaveToBoard([landed], landing.Id, savedUtc);
            }

            moved++;
        }

        return moved;
    }

    /// <summary>The folder in the feeds store this one becomes, made if it is not there.</summary>
    private static Folder Landing(OpenAccount feeds, string name, long? parentId)
    {
        var here = feeds.Mail.Folders(feeds.Account.Id);

        return here.FirstOrDefault(f => f.ParentId == parentId && f.Name == name)
               ?? feeds.Mail.AddFolder(feeds.Account.Id, name, parentId: parentId);
    }

    /// <summary>The same colour category in the feeds store, made if it is not there.</summary>
    private static Category? Named(OpenAccount feeds, Category category)
    {
        var here = feeds.Mail.Categories();

        return here.FirstOrDefault(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase))
               ?? feeds.Mail.AddCategory(category.Name, category.ColourToken);
    }

    /// <summary>A folder and everything under it, parents before children.</summary>
    private static List<Folder> Subtree(IReadOnlyList<Folder> all, Folder root)
    {
        var found = new List<Folder> { root };

        for (var at = 0; at < found.Count; at++)
        {
            var here = found[at];
            found.AddRange(all.Where(f => f.ParentId == here.Id));
        }

        return found;
    }
}
