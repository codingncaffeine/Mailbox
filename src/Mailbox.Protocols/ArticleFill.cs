using Mailbox.Core.Diagnostics;
using Mailbox.Core.Feeds;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Protocols;

/// <summary>
/// Fills in an article that was filed as a teaser, by reading the publisher's page for it.
/// </summary>
/// <remarks>
/// The poll does this for everything arriving from a feed that sends teasers, and that covers
/// the future. This covers the past — every article already in the store from before the reader
/// turned it on, or from before this existed — and it is also the honest answer to what a reader
/// means by clicking an article: show me more of this.
/// <para>
/// <b>Once, and then it is in the store.</b> The message is rewritten in place, keeping its row,
/// its flags, its categories, its boards and its place in the folder — the same rewrite a
/// publisher's revision gets. So the second opening costs nothing, the text is in the search
/// index afterwards, and it is still there offline.
/// </para>
/// <para>
/// <b>On opening rather than on polling, for the ones already here.</b> One request per article
/// somebody actually read is a great deal politer to a publisher than one per article delivered,
/// and a reader who never opens an article never asks their server for anything.
/// </para>
/// </remarks>
public static class ArticleFill
{
    /// <summary>What the message says when its body came from the publisher's page.</summary>
    public const string FilledHeader = "X-Mailbox-Feed-Fulltext";

    /// <summary>Below this, what is stored is a teaser and the page is worth reading.</summary>
    private const int TeaserLength = 1000;

    /// <summary>The least an extracted article can be and still be worth putting in place of one.</summary>
    private const int WorthReplacing = 400;

    /// <summary>
    /// A whole article, in words. The same figure the article list uses to decide whether a
    /// reading time is worth showing: a thousand characters of prose is about a hundred and
    /// seventy words, and a thousand characters is what the poll calls a teaser.
    /// </summary>
    private const int WholeArticleWords = 170;

    /// <summary>
    /// Whether this article is worth reading the publisher's page for.
    /// </summary>
    /// <remarks>
    /// Cheap on purpose — it runs when a row is opened, and the expensive half is the request it
    /// decides against making. The words rather than the bytes, and the difference is the whole
    /// feature: the size of the message used to stand in for the length of its body, and it is
    /// the wrong stand-in for exactly the publishers this exists for. Measured on the eight feeds
    /// the seed subscribes to, TechRadar's fifty most recent articles are a median 270 characters
    /// of text inside a median eight kilobytes of markup, and Ars Technica's are 207 characters
    /// inside four — so every one of them was over the byte ceiling, and opening one never filled
    /// it in. The poll's own test has always counted the words for this reason.
    /// <para>
    /// The word count is a column, written when the article was filed. A row from before it was
    /// recorded still falls back to the size.
    /// </para>
    /// </remarks>
    public static bool LooksLikeTeaser(MessageSummary article)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (article.FeedLink.Length == 0) return false;

        return article.FeedWords > 0 ? article.FeedWords < WholeArticleWords : article.SizeBytes < 3 * 1024;
    }

    /// <summary>
    /// Reads the page behind an article and puts what it says in place of the teaser.
    /// </summary>
    /// <returns>How many characters of article were written, or 0 when nothing was.</returns>
    public static async Task<int> FillAsync(
        OpenAccount account,
        long messageId,
        FeedFetch fetch,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(fetch);

        if (account.Mail.GetMessage(messageId) is not { } article) return 0;
        if (article.FeedLink is not { Length: > 0 } link) return 0;
        if (account.Mail.LoadRaw(messageId) is not { } raw) return 0;

        MimeMessage message;
        try
        {
            using var source = new MemoryStream(raw);
            message = MimeMessage.Load(source, cancellation);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            return 0;
        }

        // Already filled in by a poll, or by an earlier opening.
        if (message.Headers[FilledHeader] is { Length: > 0 }) return 0;

        var written = (message.TextBody ?? FeedParser.PlainText(message.HtmlBody ?? string.Empty)).Trim().Length;
        if (written >= TeaserLength) return 0;

        var answer = await fetch.GetAsync(link, cancellation: cancellation).ConfigureAwait(false);
        if (!answer.Ok || answer.Text.Length == 0)
        {
            Log.Info($"Feeds: the page behind article {messageId} could not be read — {answer.Error}");
            return 0;
        }

        var body = ArticleText.Extract(answer.Text, link);
        if (!body.Found || body.Length <= Math.Max(WorthReplacing, written * 2))
        {
            Log.Debug($"Feeds: the page behind “{article.Subject}” gave {body.Length} characters, "
                + $"which is not enough more than the {written} already stored.");
            return 0;
        }

        // A picture too, for a feed that sends neither. The row draws from this column, so the
        // list fills in as well as the pane.
        var picture = article.FeedImage.Length > 0
            ? article.FeedImage
            : PageCards.Read(answer.Text, link).ImageUrl;

        Rewrite(message, body, link, picture);

        using var buffer = new MemoryStream();
        await message.WriteToAsync(buffer, cancellation).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        // The row's own state is the store's, not the message's: the rewrite must not mark a read
        // article unread again, or drop the flag that put it on Read Later.
        var summary = MessageMapper.ToSummary(message, article.ServerUid, bytes.Length, article.Received,
            article.IsRead, article.IsFlagged);

        if (!account.Mail.ReplaceMessage(messageId, summary, bytes)) return 0;

        Log.Info($"Feeds: read {body.Length} characters for article {messageId}.");
        Log.Debug($"Feeds: article {messageId} is “{article.Subject}”, read from {link}.");
        return body.Length;
    }

    /// <summary>Puts the article in the message, keeping everything the message already said.</summary>
    private static void Rewrite(MimeMessage message, ArticleBody body, string link, string picture)
    {
        message.Headers.Add(FilledHeader, link);

        if (picture.Length > 0 && message.Headers["X-Mailbox-Feed-Image"] is not { Length: > 0 })
        {
            message.Headers.Add("X-Mailbox-Feed-Image", picture);
        }

        var encoded = System.Net.WebUtility.HtmlEncode(link);
        var builder = new BodyBuilder
        {
            HtmlBody = body.Html + $"<p><a href=\"{encoded}\">View article</a></p>",
            TextBody = $"{body.Text}\n\n{link}",
        };

        // What the entry brought with it stays with it: an enclosure a reader asked to have
        // downloaded is not a casualty of filling in the words around it.
        foreach (var attachment in message.Attachments.ToList()) builder.Attachments.Add(attachment);

        message.Body = builder.ToMessageBody();
    }
}
