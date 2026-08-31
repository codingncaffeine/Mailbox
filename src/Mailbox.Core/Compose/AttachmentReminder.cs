using System.Text.RegularExpressions;

namespace Mailbox.Core.Compose;

/// <summary>
/// Whether a message's own words speak of an attachment — the send-time reminder's question,
/// asked of a message that has none attached.
/// </summary>
/// <remarks>
/// The words are the attach stem alone — attach, attached, attaches, attaching, attachment,
/// attachments — as whole words, in any case. Not "enclosed", not "included": each of those
/// says something ordinary far more often than it promises a file, and a reminder that fires
/// on ordinary sentences is a reminder people turn off. A false positive still costs one
/// dialog with a Send Anyway on it; the phrasing "may be missing" is the honest account of
/// that.
/// </remarks>
public static partial class AttachmentReminder
{
    [GeneratedRegex(@"\battach(?:ed|es|ing|ment|ments)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachStem();

    /// <summary>Whether the text mentions an attachment by the attach stem, as a whole word.</summary>
    public static bool MentionsAttachment(string text)
        => !string.IsNullOrEmpty(text) && AttachStem().IsMatch(text);
}
