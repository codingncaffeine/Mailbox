using Mailbox.Junk;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.App;

/// <summary>
/// The junk filter, wired to the application: the level from the Options page, the lists and the
/// corpus from each account's store.
/// </summary>
/// <remarks>
/// One place tokenizes a message and asks the filter, so a message judged on arrival is judged
/// the same way as one the reader marks by hand, and the corpus a Mark as Junk trains is the one
/// the filter reads. The level is read live, so changing it on the Options page applies to the
/// next message rather than the next launch. Nothing here leaves the machine (§7.8).
/// </remarks>
public sealed class JunkService(Func<FilterLevel> level)
{
    private readonly JunkFilter _filter = new();
    private readonly Func<FilterLevel> _level = level;

    /// <summary>Turns the Options page's 0..3 into a level.</summary>
    public static FilterLevel LevelFrom(int index) => index switch
    {
        0 => FilterLevel.Off,
        2 => FilterLevel.High,
        3 => FilterLevel.SafeListsOnly,
        _ => FilterLevel.Low,
    };

    /// <summary>
    /// Whether an arriving message should be filed as junk, judged against the account's corpus
    /// and lists at the current level. The receiver files it into Junk rather than the inbox
    /// when this is true.
    /// </summary>
    public bool IsJunk(MailRepository mail, MimeMessage message)
    {
        var from = From(message);
        var decision = _filter.Judge(
            _level(),
            Tokens(message),
            new JunkCorpus(mail),
            isSafe: from.Length > 0 && mail.IsSafeSender(from),
            isBlocked: from.Length > 0 && mail.IsBlockedSender(from));

        return decision.IsJunk;
    }

    /// <summary>
    /// Trains a message into the corpus as junk or not junk. Marking not-junk trains ham; marking
    /// junk trains spam. A message re-marked the other way is not un-trained here — the opposing
    /// count rising is what corrects it, which is how the reference's filter behaves.
    /// </summary>
    public void Train(MailRepository mail, MimeMessage message, bool spam)
        => new JunkCorpus(mail).Train(Tokens(message), spam);

    private static IReadOnlyList<string> Tokens(MimeMessage message) => JunkTokenizer.Tokenize(
        From(message),
        message.Subject ?? string.Empty,
        message.TextBody ?? message.HtmlBody ?? string.Empty);

    private static string From(MimeMessage message)
        => message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
}
