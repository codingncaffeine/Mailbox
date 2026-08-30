using Mailbox.App.Views;
using Mailbox.Core.Search;

namespace Mailbox.HeadlessTests;

/// <summary>
/// Advanced Find composes queries the search grammar actually parses — every field the dialog
/// offers must land in the <see cref="SearchQuery"/> slot it promised, through the real parser.
/// </summary>
/// <remarks>
/// The dialog owns no matching of its own: it writes the box's grammar and the box runs it. So
/// the one thing that can rot is the translation — a span word the parser stopped knowing, a
/// quoting shape it reads as two tokens — and that rot would be invisible in the dialog, which
/// happily composes strings all day. These parse everything back through the grammar itself.
/// </remarks>
public class AdvancedFindComposeTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryFieldLandsInThePromisedSlot()
    {
        var composed = MailAdvancedFindDialog.Compose(
            words: "quarterly numbers", wordsIn: 0,
            from: "Dana Okafor", sentTo: "you@example.com",
            timeField: 0, timeSpan: 1,
            attachments: true, unread: true, flagged: true);

        var query = SearchQuery.Parse(composed, Anchor);

        Assert.Equal(["quarterly", "numbers"], query.Words);
        Assert.Contains("Dana Okafor", query.From);
        Assert.Contains("you@example.com", query.To);
        Assert.NotNull(query.Received);
        Assert.True(query.HasAttachment);
        Assert.False(query.IsRead);
        Assert.True(query.IsFlagged);
    }

    [Fact]
    public void SubjectOnlyAndBodyOnlySendTheirTokensWhereTheySaid()
    {
        var subjectOnly = SearchQuery.Parse(
            MailAdvancedFindDialog.Compose("release notes", 1, null, null, 0, 0, false, false, false), Anchor);
        Assert.Equal(["release", "notes"], subjectOnly.Subject);
        Assert.Empty(subjectOnly.Words);

        var bodyOnly = SearchQuery.Parse(
            MailAdvancedFindDialog.Compose("\"font substitution\"", 2, null, null, 0, 0, false, false, false), Anchor);
        Assert.Equal(["font substitution"], bodyOnly.Body);
        Assert.Empty(bodyOnly.Words);
    }

    [Fact]
    public void TheSentHalfOfTheTimeComboReachesTheSentSpan()
    {
        var query = SearchQuery.Parse(
            MailAdvancedFindDialog.Compose(null, 0, null, null, timeField: 1, timeSpan: 4, false, false, false),
            Anchor);

        Assert.NotNull(query.Sent);
        Assert.Null(query.Received);
    }

    /// <summary>
    /// Every span the combo offers is a word the grammar still knows. A vocabulary drift —
    /// the parser renaming last7days, say — would otherwise compose a token that parses as
    /// nothing and silently widens the search to everything.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EverySpanTheComboOffersParsesIntoABoundedSpan(int index)
    {
        var query = SearchQuery.Parse(
            MailAdvancedFindDialog.Compose(null, 0, null, null, 0, index, false, false, false), Anchor);

        Assert.NotNull(query.Received);
        Assert.True(query.Received!.Value.After is not null || query.Received.Value.Before is not null);
    }
}
