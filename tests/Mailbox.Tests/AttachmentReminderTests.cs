using Mailbox.Core.Compose;

namespace Mailbox.Tests;

/// <summary>
/// The send-time reminder's question: does the text speak of an attachment?
/// </summary>
public class AttachmentReminderTests
{
    [Theory]
    [InlineData("Please see the attached file.")]
    [InlineData("I attach the draft for review.")]
    [InlineData("The attachment holds the figures.")]
    [InlineData("Both attachments are final.")]
    [InlineData("Attaching the notes now.")]
    [InlineData("SEE ATTACHED")]
    public void TheAttachStemAsAWordIsAMention(string text)
        => Assert.True(AttachmentReminder.MentionsAttachment(text));

    [Theory]
    [InlineData("")]
    [InlineData("The figures are in the body below.")]
    // The stem inside another word is somebody else's word.
    [InlineData("The bracket reattaches with two screws.")]
    [InlineData("He sent his attaché to the meeting.")]
    [InlineData("The unattached cable hangs loose.")]
    public void OtherWordsAreNot(string text)
        => Assert.False(AttachmentReminder.MentionsAttachment(text));
}
