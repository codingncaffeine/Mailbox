using Mailbox.Core.Rules;

namespace Mailbox.Tests;

/// <summary>
/// Rules as a file: what the Rules and Alerts dialog's Options button writes and reads.
/// </summary>
/// <remarks>
/// A set of rules is the thing somebody with a busy mailbox would most want to carry to another
/// machine, and a half-read import is worse than none — the half that is missing is the half
/// nobody notices until the mail it was filing piles up. So the failures are asserted as
/// carefully as the successes.
/// </remarks>
public class RuleTransferTests
{
    private static MailRule Rule(string name) => new()
    {
        Id = 42,
        Name = name,
        Enabled = false,
        Ordinal = 7,
        ServerSide = true,
        Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["a.person@example.com"] }],
        Actions =
        [
            new RuleAction(RuleActionKind.MoveToFolder) { FolderId = 3, FolderName = "Projects" },
            new RuleAction(RuleActionKind.StopProcessing),
        ],
        Exceptions = [new RuleCondition(RuleConditionKind.HasAttachment)],
    };

    [Fact]
    public void ARuleSurvivesTheRoundTrip()
    {
        var read = RuleTransfer.Read(RuleTransfer.Write([Rule("Projects")]));

        var only = Assert.Single(read);
        Assert.Equal("Projects", only.Name);
        Assert.False(only.Enabled);
        Assert.Equal(RuleConditionKind.From, Assert.Single(only.Conditions).Kind);
        Assert.Equal("a.person@example.com", Assert.Single(only.Conditions).Values[0]);
        Assert.Equal(2, only.Actions.Count);
        Assert.Equal("Projects", only.Actions[0].FolderName);
        Assert.True(only.StopsProcessing);
        Assert.Equal(RuleConditionKind.HasAttachment, Assert.Single(only.Exceptions).Kind);
    }

    [Fact]
    public void NothingAboutOneStoreTravels()
    {
        // An id is this store's own numbering and a server-side flag is a fact about the account
        // a rule lands in, not about the rule: carried across, both would describe a rule that
        // claims to run somewhere it does not.
        var only = Assert.Single(RuleTransfer.Read(RuleTransfer.Write([Rule("Projects")])));

        Assert.Equal(0, only.Id);
        Assert.False(only.ServerSide);
    }

    [Fact]
    public void TheOrderInTheFileIsTheOrderTheyRunIn()
    {
        // Rules are applied in the order shown, so an export that lost the order would hand
        // somebody the same rules doing something different.
        var read = RuleTransfer.Read(RuleTransfer.Write([Rule("First"), Rule("Second"), Rule("Third")]));

        Assert.Equal(["First", "Second", "Third"], read.Select(r => r.Name));
        Assert.Equal([0, 1, 2], read.Select(r => r.Ordinal));
    }

    [Fact]
    public void ASendRuleStaysASendRule()
    {
        var sent = Rule("Filed") with { AppliesToSent = true };

        Assert.True(Assert.Single(RuleTransfer.Read(RuleTransfer.Write([sent]))).AppliesToSent);
        Assert.False(Assert.Single(RuleTransfer.Read(RuleTransfer.Write([Rule("Arrived")]))).AppliesToSent);
    }

    [Fact]
    public void AFileFromANewerVersionIsRefusedRatherThanGuessedAt()
    {
        var written = RuleTransfer.Write([Rule("Projects")]).Replace("\"Version\": 1", "\"Version\": 99", StringComparison.Ordinal);

        var thrown = Assert.Throws<System.Text.Json.JsonException>(() => RuleTransfer.Read(written));
        Assert.Contains("newer version", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("{\"Version\":1,\"Rules\":\"a string\"}")]
    public void AFileThatIsNotRulesThrowsRatherThanImportingNothingQuietly(string document)
    {
        Assert.ThrowsAny<Exception>(() => RuleTransfer.Read(document));
    }

    [Fact]
    public void ARuleWithNoNameIsGivenOne()
    {
        // A hand-edited file is allowed to be wrong; a nameless row in the list is not something
        // anyone could then select and fix.
        var read = RuleTransfer.Read("{\"Version\":1,\"Rules\":[{\"Name\":\"\",\"Enabled\":true}]}");

        Assert.Equal("Rule 1", Assert.Single(read).Name);
        Assert.Empty(Assert.Single(read).Conditions);
    }
}
