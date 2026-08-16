using Mailbox.Core.Ribbon;

namespace Mailbox.Tests;

/// <summary>
/// The reference's large-button labels, each read off its classic ribbon.
/// </summary>
public class LargeButtonLabelTests
{
    [Theory]
    [InlineData("New Email", "New", "Email")]
    [InlineData("New Items", "New", "Items")]
    [InlineData("Reply All", "Reply", "All")]
    [InlineData("Read Aloud", "Read", "Aloud")]
    [InlineData("All Apps", "All", "Apps")]
    [InlineData("Address Book", "Address", "Book")]
    public void ATwoWordLabelIsTwoLines(string label, string first, string second)
        => Assert.Equal([first, second], LargeButtonLabel.Lines(label));

    [Theory]
    [InlineData("Delete")]
    [InlineData("Archive")]
    [InlineData("Forward")]
    [InlineData("Signature")]
    public void AWordIsNeverBroken(string label)
        => Assert.Equal([label], LargeButtonLabel.Lines(label));

    [Fact]
    public void ASlashIsAPlaceToBreakAndStaysOnTheFirstLine()
        => Assert.Equal(["Unread/", "Read"], LargeButtonLabel.Lines("Unread/Read"));

    [Fact]
    public void TheBreakThatMakesTheButtonNarrowestWins()
        => Assert.Equal(["Send/Receive", "All Folders"], LargeButtonLabel.Lines("Send/Receive All Folders"));

    [Fact]
    public void TheBalanceIsByMeasuredWidthWhenAMeasureIsGiven()
    {
        // By character count "Ill Wide" would break as evenly as anything; by width the "Ill" is
        // narrow enough that the split still stands — and a measure that calls the first word
        // enormous moves the break.
        Assert.Equal(["Ill", "Wide"], LargeButtonLabel.Lines("Ill Wide", s => s.Length));

        double Wide(string s) => s.Contains("Send/Receive", StringComparison.Ordinal) ? 200 : s.Length;
        Assert.Equal(["Send/", "Receive All Folders"], LargeButtonLabel.Lines("Send/Receive All Folders", Wide));
    }

    [Fact]
    public void NeverMoreThanTwoLines()
    {
        var lines = LargeButtonLabel.Lines("One Two Three Four Five");
        Assert.Equal(2, lines.Count);
        Assert.Equal("One Two Three Four Five", string.Join(' ', lines));
    }

    [Fact]
    public void SurroundingSpaceIsNotALine()
        => Assert.Equal(["New", "Email"], LargeButtonLabel.Lines("  New Email  "));

    [Fact]
    public void AnEmptyLabelIsOneEmptyLine()
        => Assert.Equal([string.Empty], LargeButtonLabel.Lines("   "));
}
