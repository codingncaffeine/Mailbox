using Mailbox.Store.Lists;

namespace Mailbox.Tests;

/// <summary>
/// First match wins, in list order, which is what makes the order meaningful and what Move Up
/// and Move Down are for. A rule matching everything at the top makes every rule under it dead,
/// so the order has to behave exactly as written.
/// </summary>
public class ConditionalFormattingTests
{
    private sealed record Row(bool IsUnread, long SizeBytes = 1024, bool IsFlagged = false)
        : IThreadable
    {
        public string DisplayFrom => "Alice";
        public string Subject => "Subject";
        public DateTimeOffset Received => DateTimeOffset.UnixEpoch;
        public bool HasAttachment => false;
        public string ThreadKey => "thread";
        public long FolderId => 1;
    }

    private static FormattingRule Rule(string name, Func<IArrangeable, bool> matches)
        => new(name, matches, new RowFormat(name, Bold: true));

    [Fact]
    public void UnreadIsFormattedOutOfTheBox()
    {
        var formatting = new ConditionalFormatting();

        Assert.Equal("Unread", formatting.For(new Row(IsUnread: true)).Name);
        Assert.Equal(RowFormat.None, formatting.For(new Row(IsUnread: false)));
    }

    /// <summary>Colours are token names, so a rule stays legible when the theme changes.</summary>
    [Fact]
    public void ColoursAreNamedAsTokensRatherThanValues()
    {
        var format = new ConditionalFormatting().For(new Row(IsUnread: true));

        Assert.Equal("list.row.unread.text", format.ColourToken);
        Assert.DoesNotContain("#", format.ColourToken);
    }

    [Fact]
    public void TheFirstMatchingRuleWins()
    {
        var formatting = new ConditionalFormatting([
            Rule("First", _ => true),
            Rule("Second", _ => true),
        ]);

        Assert.Equal("First", formatting.For(new Row(false)).Name);
    }

    [Fact]
    public void MovingARuleChangesWhichOneWins()
    {
        var formatting = new ConditionalFormatting([
            Rule("First", _ => true),
            Rule("Second", _ => true),
        ]);

        formatting.Move("Second", -1);

        Assert.Equal("Second", formatting.For(new Row(false)).Name);
    }

    [Fact]
    public void ADisabledRuleIsSkippedRatherThanRemoved()
    {
        var formatting = new ConditionalFormatting([
            Rule("Off", _ => true) with { IsEnabled = false },
            Rule("On", _ => true),
        ]);

        Assert.Equal("On", formatting.For(new Row(false)).Name);
        Assert.Equal(2, formatting.Rules.Count);
    }

    [Fact]
    public void NoRuleMatchingLeavesTheRowAlone()
        => Assert.Equal(RowFormat.None,
            new ConditionalFormatting([Rule("Never", _ => false)]).For(new Row(false)));

    [Fact]
    public void RulesCanBeAddedAndRemoved()
    {
        var formatting = new ConditionalFormatting([]);
        formatting.Add(Rule("Large", r => r.SizeBytes > 500));

        Assert.Equal("Large", formatting.For(new Row(false, SizeBytes: 1000)).Name);
        Assert.True(formatting.Remove("Large"));
        Assert.Equal(RowFormat.None, formatting.For(new Row(false, SizeBytes: 1000)));
    }

    [Fact]
    public void MovingPastEitherEndDoesNothing()
    {
        var formatting = new ConditionalFormatting([
            Rule("First", _ => true),
            Rule("Second", _ => true),
        ]);

        formatting.Move("First", -1);

        Assert.Equal("First", formatting.Rules[0].Name);
    }
}
