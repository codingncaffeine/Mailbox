using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Protocols;

/// <summary>What saving one address did.</summary>
/// <param name="MessageId">The row it became, or 0 when nothing could be filed.</param>
/// <param name="Card">What the page said about itself.</param>
public sealed record SavedLink(long MessageId, PageCard Card)
{
    /// <summary>True when the address was already saved and this is the row it already had.</summary>
    public bool AlreadyHere { get; init; }

    /// <summary>Why the page could not be read, or empty when it was. Not a failure to save.</summary>
    public string Unreachable { get; init; } = string.Empty;

    public bool Ok => MessageId != 0;
}

/// <summary>
/// Saving any address at all, so a board holds more than what a subscription delivered.
/// </summary>
/// <remarks>
/// This is the half of boards a folder could not do. A reader keeps a board for a subject, and
/// most of what belongs on it arrives through a feed — but some of it is a page somebody sent
/// them, and a collection that can only hold what you were already subscribed to is a filing
/// system for your own subscriptions rather than for the subject.
/// <para>
/// <b>A saved link is a message</b>, for exactly the reason a feed item is one: the article
/// list draws it, the reading pane renders it through the same sanitizer, search finds it,
/// Delete deletes it, and a board holds it through the same join. It is filed under the Feeds
/// module's own subtree rather than a root of its own, because that subtree is what the module
/// owns and a second top-level folder beside Inbox is not what a reader asked for by saving a
/// link.
/// <para>
/// The address is the identity — it goes in the same column a feed entry's id goes in — so
/// saving the same page twice puts the row it already made onto the second board rather than
/// filing it again.
/// </para>
/// <para>
/// <b>The network is not allowed to be the difference between saved and not saved.</b> A page
/// that will not load still gets a row, headed with its address; what is lost is the headline
/// and the picture, and the reader is told which. A bookmark that fails because a site is down
/// is the sort of thing that makes somebody stop trusting a keep pile.
/// </para>
/// </remarks>
public static class SavedLinks
{
    /// <summary>Where saved links are filed, under the Feeds module's own root.</summary>
    /// <remarks>
    /// A reader could in principle file a subscription under a heading of this name, in which
    /// case the two share a folder. Cosmetic, and the alternative — a name nobody would type —
    /// is worse to read in the folder pane every day.
    /// </remarks>
    public const string SavedFolder = "Saved";

    /// <summary>The largest page that will be read for its headline.</summary>
    private const long LargestPage = 4 * 1024 * 1024;

    /// <summary>
    /// Saves an address: reads what the page says about itself, and files it as a message.
    /// </summary>
    /// <param name="fetch">
    /// How to read the page, or null to save the address without reading it — which is what an
    /// offline save does, and what the tests use.
    /// </param>
    public static async Task<SavedLink> SaveAsync(
        OpenAccount account,
        string url,
        FeedFetch? fetch,
        DateTimeOffset now,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (Normalize(url) is not { } address)
        {
            return new SavedLink(0, new PageCard(url)) { Unreachable = "That is not a web address." };
        }

        var card = new PageCard(address);
        var unreachable = string.Empty;

        if (fetch is not null)
        {
            var answer = await fetch.GetAsync(address, cancellation: cancellation).ConfigureAwait(false);

            if (answer.Ok && answer.Text.Length > 0 && answer.Text.Length <= LargestPage)
            {
                // The address the page ended up at, so a link saved through a shortener is kept
                // as where it actually goes — which is also what makes saving it twice one row.
                var final = answer.FinalUrl is { Length: > 0 } moved ? moved : address;
                card = PageCards.Read(answer.Text, final) with { Url = final };
            }
            else
            {
                unreachable = Trouble(answer);
                Log.Info($"Saved links: {address} could not be read — {unreachable}");
            }
        }

        return File(account, card, now) with { Unreachable = unreachable };
    }

    /// <summary>
    /// Files a card as a message, or finds the one the same address already made.
    /// </summary>
    /// <remarks>
    /// Separate from the fetching, and public, for the reason <see cref="FeedReceiver.Deliver"/>
    /// is: what has to be provable is that an address becomes a row exactly once, not that HTTP
    /// works.
    /// </remarks>
    public static SavedLink File(OpenAccount account, PageCard card, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(card);

        var folder = Folder(account);
        var already = account.Mail.ServerUidIndex(folder.Id);

        if (already.TryGetValue(card.Url, out var stored))
        {
            return new SavedLink(stored.Id, card) { AlreadyHere = true };
        }

        var message = Compose(card, now);
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();

        var summary = MessageMapper.ToSummary(message, card.Url, raw.Length, now);
        var id = account.Mail.AddMessage(folder.Id, summary, raw);

        if (id is not { } filed) return new SavedLink(0, card);

        Log.Info($"Saved links: link {filed} filed from {card.Url}.");
        Log.Debug($"Saved links: link {filed} is “{card.Headline}”.");
        return new SavedLink(filed, card);
    }

    /// <summary>
    /// Why a page could not be read, said about a page.
    /// </summary>
    /// <remarks>
    /// Its own sentences rather than the fetch's, which are written about feeds — "the feed is
    /// not there any more" over an address somebody pasted is a sentence about something the
    /// reader never mentioned. The transport failures underneath are already neutral, so those
    /// come through as they are.
    /// </remarks>
    private static string Trouble(FeedFetchResult answer) => (int)answer.Status switch
    {
        404 => "the page is not there (404)",
        410 => "the page has been withdrawn (410)",
        401 or 403 => "the site would not let us read it",
        429 => "the site asked us to come back later (429)",
        >= 500 => $"the site's server has a fault ({(int)answer.Status})",
        0 => answer.Error is { Length: > 0 } why ? why.TrimEnd('.') : "the page could not be read",
        _ when answer.Ok => "the page was too large to read",
        _ => $"the site answered {(int)answer.Status}",
    };

    /// <summary>
    /// The Saved folder under the feeds root, made if it is not there.
    /// </summary>
    /// <remarks>
    /// Public because it is where two things land, not one: a saved link, and an article on a
    /// board that the reader has asked to delete. The second used to look the folder up rather
    /// than make it, so on a profile where nobody had ever saved a link there was nowhere to keep
    /// the article and it was quietly left where it was.
    /// </remarks>
    public static Folder Folder(OpenAccount account)
    {
        var folders = account.Mail.Folders(account.Account.Id);

        var root = folders.FirstOrDefault(f => f.ParentId is null && f.Name == FeedReceiver.RootFolder);
        if (root is null)
        {
            root = account.Mail.AddFolder(account.Account.Id, FeedReceiver.RootFolder);
            folders = account.Mail.Folders(account.Account.Id);
        }

        return folders.FirstOrDefault(f => f.ParentId == root.Id && f.Name == SavedFolder)
               ?? account.Mail.AddFolder(account.Account.Id, SavedFolder, parentId: root.Id);
    }

    /// <summary>
    /// A saved page as a message, shaped like the ones a feed delivers.
    /// </summary>
    /// <remarks>
    /// The same two headers a feed item carries, because the article list reads its thumbnail
    /// and its "open the original" out of those columns and has no business knowing which of the
    /// two kinds of row it is drawing.
    /// </remarks>
    private static MimeMessage Compose(PageCard card, DateTimeOffset now)
    {
        var host = Uri.TryCreate(card.Url, UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 } named
            ? named
            : "saved.invalid";

        var message = new MimeMessage
        {
            Subject = card.Headline,
            Date = now,

            // From the address rather than left to MimeKit's random one, so the same page saved
            // on two machines is the same message and a re-save is recognisable as one.
            MessageId = $"{Fingerprint(card.Url)}.saved@mailbox.invalid",
        };

        message.Headers.Add("X-Mailbox-Feed-Link", card.Url);
        if (card.ImageUrl is { Length: > 0 } picture) message.Headers.Add("X-Mailbox-Feed-Image", picture);
        message.Headers.Add("X-Mailbox-Saved-Link", card.Url);

        message.From.Add(new MailboxAddress(card.Publisher, $"saved@{host}"));
        message.To.Add(new MailboxAddress("Saved", $"reader@{host}"));

        var encodedUrl = System.Net.WebUtility.HtmlEncode(card.Url);
        var body = new BodyBuilder
        {
            HtmlBody = card.Summary is { Length: > 0 } summary
                ? $"<p>{System.Net.WebUtility.HtmlEncode(summary)}</p>"
                  + $"<p><a href=\"{encodedUrl}\">{encodedUrl}</a></p>"
                : $"<p><a href=\"{encodedUrl}\">{encodedUrl}</a></p>",
            TextBody = card.Summary is { Length: > 0 } text ? $"{text}\n\n{card.Url}" : card.Url,
        };

        message.Body = body.ToMessageBody();
        return message;
    }

    /// <summary>A stable, short identity for an address.</summary>
    private static string Fingerprint(string url)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..24];

    /// <summary>
    /// An address as it will be stored, or null when it is not one worth storing.
    /// </summary>
    /// <remarks>
    /// A bare "example.com/page" is what people paste, so a missing scheme is filled in rather
    /// than refused; anything that is not http after that is refused, because this address is
    /// later handed to the desktop to open.
    /// <para>
    /// A scheme that is already there is never prefixed, only judged. Prefixing one is how
    /// <c>mailto:someone@example.com</c> becomes <c>https://mailto:someone@example.com</c> —
    /// which parses, whose host is example.com, and which is a web address nobody asked to
    /// save.
    /// </para>
    /// </remarks>
    public static string? Normalize(string url)
    {
        var trimmed = url?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return null;

        if (!HasScheme(trimmed) && !trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            // An address with an @ in its first segment and no scheme is somebody's email address,
            // not a web address. Prefixing one gives https://a.person@example.org/ — which parses,
            // whose host is example.org, and which would have this fetch somebody's home page and
            // file it as a saved link under an address carrying their name. A mail client's
            // clipboard holds an email address more often than anything else, and the clipboard is
            // what fills this box.
            var firstSlash = trimmed.IndexOf('/');
            var at = trimmed.IndexOf('@');
            if (at > 0 && (firstSlash < 0 || at < firstSlash)) return null;

            trimmed = "https://" + trimmed;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var address)
               && address.Scheme is "http" or "https"
               && address.Host.Contains('.')
            ? address.AbsoluteUri
            : null;
    }

    /// <summary>
    /// Whether the text already names a scheme — "scheme:" at the front, per RFC 3986.
    /// </summary>
    /// <remarks>
    /// With one departure from the grammar, which allows a dot in a scheme: digits straight after
    /// the colon are read as a port, not as a scheme. "example.com:8080/page" is a thing people
    /// paste and a dotted scheme is not, so the ambiguity is settled in favour of the address
    /// somebody actually has.
    /// </remarks>
    private static bool HasScheme(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0) return false;
        if (!char.IsAsciiLetter(text[0])) return false;

        for (var at = 1; at < colon; at++)
        {
            if (!char.IsAsciiLetterOrDigit(text[at]) && text[at] is not ('+' or '-' or '.')) return false;
        }

        return colon + 1 >= text.Length || !char.IsAsciiDigit(text[colon + 1]);
    }
}
