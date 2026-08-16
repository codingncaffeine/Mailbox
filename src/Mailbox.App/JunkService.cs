using Mailbox.Core.Settings;
using Mailbox.Junk;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.App;

/// <summary>
/// The junk filter, wired to the application: the level and the switches from the Junk Options
/// dialog, the lists and the corpus from each account's store.
/// </summary>
/// <remarks>
/// One place tokenizes a message and asks the filter, so a message judged on arrival is judged
/// the same way as one the reader marks by hand, and the corpus a Mark as Junk trains is the one
/// the filter reads. The level is read live, so changing it in the dialog applies to the next
/// message rather than the next launch. Nothing here leaves the machine (§7.8).
/// <para>
/// It is an <see cref="IArrivalHandler"/>: the receiver stores a message in the Inbox and hands
/// it over, and this moves it to Junk — or deletes it, if the dialog says so — when the filter
/// judges it junk. On an IMAP account that move is journalled to the server like any other.
/// </para>
/// </remarks>
public sealed class JunkService(MailOptions options, Mailbox.Contacts.ContactBook? contacts = null) : IArrivalHandler
{
    private readonly JunkFilter _filter = new();
    private readonly MailOptions _options = options;

    /// <summary>
    /// The address book, which the dialog's "Also trust email from my Contacts" reads.
    /// </summary>
    /// <remarks>
    /// Optional so the filter can be judged on its own in a test: a corpus and a set of lists are
    /// what it is about, and an address book is a fourth thing that either trusts a sender or
    /// does not.
    /// </remarks>
    private readonly Mailbox.Contacts.ContactBook? _contacts = contacts;

    /// <summary>Turns the dialog's 0..3 into a level.</summary>
    public static FilterLevel LevelFrom(int index) => index switch
    {
        0 => FilterLevel.Off,
        2 => FilterLevel.High,
        3 => FilterLevel.SafeListsOnly,
        _ => FilterLevel.Low,
    };

    /// <summary>The level in force, from the dialog.</summary>
    public FilterLevel Level => LevelFrom(_options.JunkLevelIndex);

    /// <summary>
    /// Whether an arriving message should be filed as junk, judged against the account's corpus
    /// and lists at the current level.
    /// </summary>
    /// <remarks>
    /// The lists first, and they are final, in this order: a blocked top-level domain or a
    /// blocked encoding junks it before anything else is looked at; a safe sender or a safe
    /// recipient — a list the reader belongs to — clears it; a blocked sender junks it; and only
    /// then does the level ask the classifier.
    /// </remarks>
    public JunkDecision Judge(MailRepository mail, MimeMessage message)
    {
        var from = From(message);

        if (from.Length > 0 && mail.IsBlockedTld(from))
        {
            return new JunkDecision(true, JunkReason.BlockedSender, 1);
        }

        if (Charsets(message).Any(mail.IsBlockedEncoding))
        {
            return new JunkDecision(true, JunkReason.BlockedSender, 1);
        }

        var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(m => m.Address).ToList();
        var isSafe = (from.Length > 0 && mail.IsSafeSender(from))
                     || mail.IsSafeRecipient(recipients)
                     || IsContact(from);

        return _filter.Judge(
            Level,
            Tokens(message),
            new JunkCorpus(mail),
            isSafe: isSafe,
            isBlocked: from.Length > 0 && mail.IsBlockedSender(from));
    }

    /// <summary>
    /// Whether the sender is somebody in the address book, when the dialog says that is enough
    /// to clear them.
    /// </summary>
    /// <remarks>
    /// The option has been in the dialog since the junk work landed and did nothing, there being
    /// no contacts to trust. It does now: a message from somebody in the address book is not
    /// junk, whatever the corpus makes of its words.
    /// </remarks>
    private bool IsContact(string from)
    {
        if (from.Length == 0 || !_options.TrustContacts || _contacts is not { } book) return false;

        try
        {
            return book.WithAddress(from).Count > 0;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // A judgement is made on every arriving message; an unreadable address book is a
            // reason to fall back on the lists, not to stop collecting mail.
            return false;
        }
    }

    /// <summary>Whether an arriving message should be filed as junk.</summary>
    public bool IsJunk(MailRepository mail, MimeMessage message) => Judge(mail, message).IsJunk;

    /// <inheritdoc />
    public long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message)
    {
        // Only mail arriving in the Inbox is judged. Mail a rule has already filed elsewhere, or
        // that the server delivered to another folder, is left where it is.
        if (folder.Role != FolderRole.Inbox) return folder.Id;
        if (!IsJunk(mail, message)) return folder.Id;

        if (_options.DeleteSuspectedJunk)
        {
            mail.DeleteMessages([messageId]);
            return null;
        }

        if (mail.FolderWithRole(folder.AccountId, FolderRole.Junk) is not { } junk) return folder.Id;

        mail.MoveMessages([messageId], junk.Id);
        return junk.Id;
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

    /// <summary>The sender's address, lower-cased, or empty when there is none.</summary>
    public static string From(MimeMessage message)
        => message.From.Mailboxes.FirstOrDefault()?.Address?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>The character sets the message's text is written in, for the blocked-encodings list.</summary>
    private static IEnumerable<string> Charsets(MimeMessage message)
    {
        foreach (var part in message.BodyParts.OfType<TextPart>())
        {
            if (part.ContentType.Charset is { Length: > 0 } charset) yield return charset;
        }
    }
}
