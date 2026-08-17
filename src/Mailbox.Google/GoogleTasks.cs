using Mailbox.Core.Diagnostics;
using Mailbox.Store.Pim;

namespace Mailbox.Google;

/// <summary>
/// Which collections belong to this engine rather than the DAV one, and how they get here.
/// </summary>
/// <remarks>
/// A collection's <c>dav_url</c> is the discriminator, and it needs no column of its own: a URL on
/// Google's tasks host is a Google task list and there is nothing else it could be. Handing one to
/// the DAV engine would send a PROPFIND to a REST API, so the split happens once, where the
/// collections are gathered.
/// </remarks>
public static class GoogleTasks
{
    /// <summary>The host that settles it.</summary>
    public const string Host = "tasks.googleapis.com";

    /// <summary>True when this collection is a Google task list.</summary>
    public static bool Owns(Collection? collection)
        => collection?.DavUrl is { Length: > 0 } url
           && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
           && parsed.Host.EndsWith(Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The URL a task list is filed under. It is the API's own path for the list, so the id can
    /// be read back out of it and nothing has to be stored twice.
    /// </summary>
    public static string UrlFor(string listId)
        => new Uri(GoogleTasksApi.Root, $"lists/{Uri.EscapeDataString(listId)}").ToString();

    /// <summary>The list id out of that URL, or empty when this is not one of ours.</summary>
    public static string ListId(Collection? collection)
    {
        if (!Owns(collection)) return string.Empty;

        var path = new Uri(collection!.DavUrl!).AbsolutePath;
        var last = path.LastIndexOf('/');
        return last < 0 || last == path.Length - 1 ? string.Empty : Uri.UnescapeDataString(path[(last + 1)..]);
    }

    /// <summary>
    /// Puts every list on the account into the store, and takes away the ones that have gone.
    /// </summary>
    /// <remarks>
    /// Called on connecting and again on each poll, because a list made on a phone should turn up
    /// here without anybody re-connecting anything. A list that has gone from Google takes its
    /// tasks with it — there is nowhere left to sync them to, and leaving them as a local list
    /// under the same name would be a copy that silently stops changing.
    /// <para>
    /// A rename at Google reaches the collection's name here; a rename here does not go back,
    /// this application having no UI for renaming one and the list's title being Google's to keep.
    /// </para>
    /// </remarks>
    public static async Task<int> RefreshListsAsync(
        GoogleTasksApi api, PimRepository repository, string account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(repository);

        var lists = await api.ListsAsync(cancellationToken).ConfigureAwait(false);
        var here = repository.Collections()
            .Where(c => Owns(c) && string.Equals(c.Account, account, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var added = 0;

        foreach (var list in lists)
        {
            var url = UrlFor(list.Id);
            var match = here.FirstOrDefault(c => string.Equals(c.DavUrl, url, StringComparison.Ordinal));

            if (match is null)
            {
                repository.AddCollection(CollectionKind.Tasks, list.Title, "#0078D4", account, url);
                added++;
                continue;
            }

            if (!string.Equals(match.DisplayName, list.Title, StringComparison.Ordinal))
            {
                repository.RenameCollection(match.Id, list.Title);
            }
        }

        foreach (var gone in here.Where(c => !lists.Any(l => string.Equals(c.DavUrl, UrlFor(l.Id), StringComparison.Ordinal))))
        {
            Log.Info($"The Google task list “{gone.DisplayName}” is no longer on the account; removing it.");
            repository.RemoveCollection(gone.Id);
        }

        return added;
    }
}
