using Mailbox.Core.Rules;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// The Rules and Alerts wizard's rules: the evaluator over facts (pure), the description the
/// dialog shows, the JSON the store keeps, and the handler that acts on a message as it arrives
/// and for Run Rules Now.
/// </summary>
public class RulesTests
{
    private static RuleFacts Facts() => new()
    {
        FromAddress = "alice@example.com",
        FromName = "Alice Chen",
        To = ["you@example.com", "team@example.org"],
        Cc = ["bob@example.net"],
        Subject = "Re: Q3 numbers",
        Body = "The variance on line 14 is the one to talk through.",
        Headers = "List-Id: <team.example.org>\nX-Priority: 3",
        SizeBytes = 40 * 1024,
        HasAttachment = true,
        Importance = 2,
        Sensitivity = 0,
        Received = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        Categories = ["Blue Category"],
        IsFlagged = false,
        OwnAddresses = ["you@example.com"],
    };

    private static MailRule Rule(params RuleCondition[] conditions) => new() { Name = "Test", Conditions = conditions };

    [Theory]
    [InlineData(RuleConditionKind.From, "alice@example.com", true)]
    [InlineData(RuleConditionKind.From, "Alice Chen", true)]
    [InlineData(RuleConditionKind.From, "Alice Chen <alice@example.com>", true)]
    [InlineData(RuleConditionKind.From, "@example.com", true)]
    [InlineData(RuleConditionKind.From, "someone@else.example", false)]
    [InlineData(RuleConditionKind.SubjectContains, "q3", true)]
    [InlineData(RuleConditionKind.SubjectContains, "budget", false)]
    [InlineData(RuleConditionKind.BodyContains, "variance", true)]
    [InlineData(RuleConditionKind.SubjectOrBodyContains, "line 14", true)]
    [InlineData(RuleConditionKind.HeaderContains, "list-id", true)]
    [InlineData(RuleConditionKind.SenderAddressContains, "example.com", true)]
    [InlineData(RuleConditionKind.RecipientAddressContains, "example.org", true)]
    [InlineData(RuleConditionKind.SentTo, "team@example.org", true)]
    [InlineData(RuleConditionKind.SentTo, "bob@example.net", true)]
    [InlineData(RuleConditionKind.SentTo, "nobody@example.com", false)]
    [InlineData(RuleConditionKind.AssignedToCategory, "blue category", true)]
    [InlineData(RuleConditionKind.AssignedToCategory, "Red Category", false)]
    public void ConditionsWithValuesMatchAsTheReferenceDoes(RuleConditionKind kind, string value, bool expected)
    {
        var condition = new RuleCondition(kind) { Values = [value] };
        Assert.Equal(expected, RuleEvaluator.Holds(condition, Facts()));
    }

    [Fact]
    public void TheMyNameConditionsReadTheReadersOwnAddresses()
    {
        var facts = Facts();
        Assert.True(RuleEvaluator.Holds(new(RuleConditionKind.MyNameInTo), facts));
        Assert.False(RuleEvaluator.Holds(new(RuleConditionKind.MyNameInCc), facts));
        Assert.True(RuleEvaluator.Holds(new(RuleConditionKind.MyNameInToOrCc), facts));
        Assert.False(RuleEvaluator.Holds(new(RuleConditionKind.MyNameNotInTo), facts));
        Assert.False(RuleEvaluator.Holds(new(RuleConditionKind.SentOnlyToMe), facts));

        var onlyMe = facts with { To = ["you@example.com"], Cc = [] };
        Assert.True(RuleEvaluator.Holds(new(RuleConditionKind.SentOnlyToMe), onlyMe));
    }

    [Fact]
    public void TheOtherConditionsReadTheFacts()
    {
        var facts = Facts();
        Assert.True(RuleEvaluator.Holds(new(RuleConditionKind.HasAttachment), facts));
        Assert.True(RuleEvaluator.Holds(new(RuleConditionKind.Importance) { Level = 2 }, facts));
        Assert.False(RuleEvaluator.Holds(new(RuleConditionKind.Importance) { Level = 1 }, facts));
        Assert.True(RuleEvaluator.Holds(new(RuleConditionKind.Sensitivity) { Level = 0 }, facts));
        Assert.True(RuleEvaluator.Holds(new(RuleConditionKind.SizeBetween) { Min = 10, Max = 100 }, facts));
        Assert.False(RuleEvaluator.Holds(new(RuleConditionKind.SizeBetween) { Min = 100 }, facts));
        Assert.True(RuleEvaluator.Holds(new(RuleConditionKind.ReceivedBetween)
        {
            After = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            Before = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero),
        }, facts));
        Assert.False(RuleEvaluator.Holds(new(RuleConditionKind.Flagged), facts));
    }

    [Fact]
    public void ARuleNeedsEveryConditionAndNoException()
    {
        var facts = Facts();
        var both = Rule(
            new RuleCondition(RuleConditionKind.From) { Values = ["alice@example.com"] },
            new RuleCondition(RuleConditionKind.HasAttachment));
        Assert.True(RuleEvaluator.Matches(both, facts));

        var oneFails = Rule(
            new RuleCondition(RuleConditionKind.From) { Values = ["alice@example.com"] },
            new RuleCondition(RuleConditionKind.SubjectContains) { Values = ["lunch"] });
        Assert.False(RuleEvaluator.Matches(oneFails, facts));

        var excepted = both with { Exceptions = [new RuleCondition(RuleConditionKind.SubjectContains) { Values = ["Q3"] }] };
        Assert.False(RuleEvaluator.Matches(excepted, facts));

        // No conditions: matches everything, as the reference's does.
        Assert.True(RuleEvaluator.Matches(new MailRule { Name = "Everything" }, facts));
    }

    [Fact]
    public void TheDefinitionRoundTripsThroughJson()
    {
        var rule = new MailRule
        {
            Id = 3,
            Name = "File Alice",
            Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["alice@example.com"] }],
            Actions =
            [
                new RuleAction(RuleActionKind.MoveToFolder) { FolderId = 9, FolderName = "Projects" },
                new RuleAction(RuleActionKind.StopProcessing),
            ],
            Exceptions = [new RuleCondition(RuleConditionKind.SizeBetween) { Min = 1024 }],
        };

        var json = rule.DefinitionJson();
        var back = MailRule.FromDefinition(3, "File Alice", true, 0, json);

        // Records compare their lists by reference, so the round trip is checked as text.
        Assert.Equal(json, back.DefinitionJson());
        Assert.Equal(RuleConditionKind.From, Assert.Single(back.Conditions).Kind);
        Assert.Equal("alice@example.com", Assert.Single(back.Conditions).Values.Single());
        Assert.Equal(9, back.Actions[0].FolderId);
        Assert.Equal("Projects", back.Actions[0].FolderName);
        Assert.Equal(1024, Assert.Single(back.Exceptions).Min);
        Assert.True(back.StopsProcessing);

        // A definition that will not parse is a rule that matches nothing, not a crash.
        var broken = MailRule.FromDefinition(4, "Broken", true, 1, "{not json");
        Assert.Empty(broken.Actions);
        Assert.False(RuleEvaluator.Matches(broken, Facts()) && broken.Actions.Count > 0);
    }

    [Fact]
    public void TheDescriptionReadsLikeTheReferences()
    {
        var rule = new MailRule
        {
            Name = "File Alice",
            Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["Alice Chen"] }],
            Actions =
            [
                new RuleAction(RuleActionKind.MoveToFolder) { FolderName = "Projects" },
                new RuleAction(RuleActionKind.StopProcessing),
            ],
            Exceptions =
            [
                new RuleCondition(RuleConditionKind.SubjectContains) { Values = ["lunch", "coffee"] },
                new RuleCondition(RuleConditionKind.HasAttachment),
            ],
        };

        var clauses = RuleDescription.Describe(rule);
        Assert.Equal("Apply this rule after the message arrives", clauses[0].Text);
        Assert.Equal("from Alice Chen", clauses[1].Text);
        Assert.Equal("Alice Chen", clauses[1].Editable);
        Assert.Equal("move it to the Projects folder", clauses[2].Text);
        Assert.Equal("stop processing more rules", clauses[3].Text);
        Assert.Equal("except if with \"lunch\" or \"coffee\" in the subject", clauses[4].Text);
        Assert.Equal("or which has an attachment", clauses[5].Text);

        Assert.Contains("from Alice Chen", RuleDescription.Sentence(rule));
    }

    // ---- The handler, over a store --------------------------------------------------------------

    private static (MailStore Store, MailRepository Repo, Folder Inbox) Fresh()
    {
        var store = MailStore.Transient();
        var repo = new MailRepository(store);
        var account = repo.AddAccount("you@example.com", "You", MailProtocol.Pop3);
        repo.CreateStandardFolders(account.Id);
        return (store, repo, repo.FolderWithRole(account.Id, FolderRole.Inbox)!);
    }

    private static (long Id, MimeMessage Message) Deliver(MailRepository repo, Folder inbox, string from, string subject, string body = "Hello")
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress("Sender", from));
        message.To.Add(new MailboxAddress("You", "you@example.com"));
        message.Body = new TextPart("plain") { Text = body };
        message.MessageId = $"<{Guid.NewGuid():n}@example.com>";

        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();
        var summary = MessageMapper.ToSummary(message, Guid.NewGuid().ToString("n"), raw.Length, DateTimeOffset.UtcNow);
        return (repo.AddMessage(inbox.Id, summary, raw)!.Value, message);
    }

    [Fact]
    public void ARuleMovesAnArrivingMessageAndStopsTheOnesAfterIt()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var projects = repo.AddFolder(inbox.AccountId, "Projects");
        var archive = repo.FolderWithRole(inbox.AccountId, FolderRole.Archive)!;

        repo.AddRule(new MailRule
        {
            Name = "File Alice",
            Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["alice@example.com"] }],
            Actions =
            [
                new RuleAction(RuleActionKind.MoveToFolder) { FolderId = projects.Id, FolderName = "Projects" },
                new RuleAction(RuleActionKind.StopProcessing),
            ],
        }, DateTimeOffset.UtcNow);
        repo.AddRule(new MailRule
        {
            Name = "Archive everything",
            Actions = [new RuleAction(RuleActionKind.MoveToFolder) { FolderId = archive.Id, FolderName = "Archive" }],
        }, DateTimeOffset.UtcNow);

        var handler = new RulesHandler();
        var (fromAlice, aliceMessage) = Deliver(repo, inbox, "alice@example.com", "Numbers");
        var (fromBob, bobMessage) = Deliver(repo, inbox, "bob@example.net", "Lunch");

        Assert.Equal(projects.Id, handler.Handle(repo, inbox, fromAlice, aliceMessage));
        Assert.Equal(archive.Id, handler.Handle(repo, inbox, fromBob, bobMessage));

        Assert.Equal(projects.Id, repo.GetMessage(fromAlice)!.FolderId);
        Assert.Equal(archive.Id, repo.GetMessage(fromBob)!.FolderId);
    }

    [Fact]
    public void ADisabledRuleDoesNothingAndAMissingFolderIsFoundByName()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var projects = repo.AddFolder(inbox.AccountId, "Projects");

        var rule = repo.AddRule(new MailRule
        {
            Name = "File",
            Enabled = false,
            Actions = [new RuleAction(RuleActionKind.MoveToFolder) { FolderId = 999, FolderName = "Projects" }],
        }, DateTimeOffset.UtcNow);

        var handler = new RulesHandler();
        var (id, message) = Deliver(repo, inbox, "alice@example.com", "Hello");
        Assert.Equal(inbox.Id, handler.Handle(repo, inbox, id, message));

        repo.SetRuleEnabled(rule.Id, true);
        Assert.Equal(projects.Id, handler.Handle(repo, inbox, id, message));
    }

    [Fact]
    public void FlagCategoryReadAndAlertActionsAct()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        repo.AddRule(new MailRule
        {
            Name = "Mark up",
            Conditions = [new RuleCondition(RuleConditionKind.SubjectContains) { Values = ["urgent"] }],
            Actions =
            [
                new RuleAction(RuleActionKind.MarkAsRead),
                new RuleAction(RuleActionKind.FlagForFollowUp) { Level = 0 },
                new RuleAction(RuleActionKind.AssignCategory) { Values = ["Red Category"] },
                new RuleAction(RuleActionKind.DisplayAlert) { Values = ["Something urgent came in"] },
                new RuleAction(RuleActionKind.PlaySound),
            ],
        }, DateTimeOffset.UtcNow);

        var handler = new RulesHandler();
        var (id, message) = Deliver(repo, inbox, "alice@example.com", "URGENT: server down");
        handler.Handle(repo, inbox, id, message);

        var row = repo.GetMessage(id)!;
        Assert.True(row.IsRead);
        Assert.True(row.IsFlagged);
        Assert.NotNull(row.FollowUpDue);
        Assert.Equal("Red Category", Assert.Single(repo.CategoriesFor([id])[id]).Name);

        Assert.Equal(2, handler.Alerts.Count);
        Assert.True(handler.Alerts.TryDequeue(out var alert));
        Assert.Equal(RuleActionKind.DisplayAlert, alert!.Kind);
        Assert.Equal("Something urgent came in", alert.Text);
        Assert.Equal(id, alert.MessageId);
    }

    [Fact]
    public void ForwardAndRedirectQueueMailInTheOutbox()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        repo.AddRule(new MailRule
        {
            Name = "Forward",
            Actions =
            [
                new RuleAction(RuleActionKind.ForwardTo) { Values = ["colleague@example.org"] },
                new RuleAction(RuleActionKind.RedirectTo) { Values = ["archive@example.org"] },
            ],
        }, DateTimeOffset.UtcNow);

        var handler = new RulesHandler();
        var (id, message) = Deliver(repo, inbox, "alice@example.com", "Numbers", "See attached.");
        handler.Handle(repo, inbox, id, message);

        var queued = repo.Outbox(inbox.AccountId);
        Assert.Equal(2, queued.Count);

        var forwarded = MimeMessage.Load(new MemoryStream(repo.LoadBlob(queued[0].BlobId)!), TestContext.Current.CancellationToken);
        Assert.Equal("FW: Numbers", forwarded.Subject);
        Assert.Equal("colleague@example.org", forwarded.To.Mailboxes.Single().Address);
        Assert.Equal("you@example.com", forwarded.From.Mailboxes.Single().Address);
        Assert.Contains("See attached.", forwarded.TextBody);

        var redirected = MimeMessage.Load(new MemoryStream(repo.LoadBlob(queued[1].BlobId)!), TestContext.Current.CancellationToken);
        Assert.Equal("Numbers", redirected.Subject);
        Assert.Equal("alice@example.com", redirected.From.Mailboxes.Single().Address);
        Assert.Equal("archive@example.org", redirected.ResentTo.Mailboxes.Single().Address);
        Assert.Equal("you@example.com", redirected.ResentFrom.Mailboxes.Single().Address);
    }

    [Fact]
    public void RunRulesNowAppliesChosenRulesToAFolder()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var projects = repo.AddFolder(inbox.AccountId, "Projects");

        Deliver(repo, inbox, "alice@example.com", "One");
        Deliver(repo, inbox, "alice@example.com", "Two");
        Deliver(repo, inbox, "bob@example.net", "Three");

        var rule = new MailRule
        {
            Name = "File Alice",
            Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["alice@example.com"] }],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder) { FolderId = projects.Id, FolderName = "Projects" }],
        };

        var moved = new RulesHandler().RunNow(repo, inbox, [rule]);

        Assert.Equal(2, moved);
        Assert.Equal(2, repo.Messages(projects.Id).Count);
        Assert.Equal("Three", Assert.Single(repo.Messages(inbox.Id)).Subject);
    }

    [Fact]
    public void RulesAreStoredInOrderAndReordered()
    {
        var (store, repo, _) = Fresh();
        using var __ = store;

        var a = repo.AddRule(new MailRule { Name = "A" }, DateTimeOffset.UtcNow);
        var b = repo.AddRule(new MailRule { Name = "B" }, DateTimeOffset.UtcNow);
        Assert.Equal(["A", "B"], repo.Rules().Select(r => r.Name));

        repo.OrderRules([b.Id, a.Id]);
        Assert.Equal(["B", "A"], repo.Rules().Select(r => r.Name));

        repo.UpdateRule(a with { Name = "A renamed", Enabled = false });
        var renamed = repo.Rules().Single(r => r.Id == a.Id);
        Assert.Equal("A renamed", renamed.Name);
        Assert.False(renamed.Enabled);

        repo.DeleteRule(b.Id);
        Assert.Single(repo.Rules());
    }

    /// <summary>
    /// The pipeline the application runs — the junk filter, then the rules — hands each handler
    /// the folder the last one left the message in, and stops when one deletes it.
    /// </summary>
    [Fact]
    public void ThePipelineChainsHandlersAndStopsAtADelete()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;
        var projects = repo.AddFolder(inbox.AccountId, "Projects");
        var seen = new List<string>();

        var first = new Recording("first", seen, (mail, folder, id) => { mail.MoveMessages([id], projects.Id); return projects.Id; });
        var second = new Recording("second", seen, (_, folder, _) => folder.Id);
        var (id, message) = Deliver(repo, inbox, "alice@example.com", "Hello");

        var ended = new ArrivalPipeline(first, second).Handle(repo, inbox, id, message);

        Assert.Equal(projects.Id, ended);
        Assert.Equal(["first:Inbox", "second:Projects"], seen);

        seen.Clear();
        var deleter = new Recording("deleter", seen, (mail, _, mid) => { mail.DeleteMessages([mid]); return null; });
        var (other, otherMessage) = Deliver(repo, inbox, "bob@example.net", "Bye");
        Assert.Null(new ArrivalPipeline(deleter, second).Handle(repo, inbox, other, otherMessage));
        Assert.Equal(["deleter:Inbox"], seen);
        Assert.Null(repo.GetMessage(other));
    }

    private sealed class Recording(string name, List<string> seen, Func<MailRepository, Folder, long, long?> act) : IArrivalHandler
    {
        public long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message)
        {
            seen.Add($"{name}:{folder.Name}");
            return act(mail, folder, messageId);
        }
    }

    [Fact]
    public void AFeedRuleMatchesItsOwnFeedAndNoOther()
    {
        // Every feed on a host sends as rss@<host>, so matching the sender would sweep up a
        // whole site's worth. The receiver stamps the address and this is what reads it.
        var rule = new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.FromFeed) { Values = ["https://example.com/news.xml"] }],
        };

        Assert.True(RuleEvaluator.Matches(rule, new RuleFacts { FeedUrl = "https://example.com/news.xml" }));
        Assert.False(RuleEvaluator.Matches(rule, new RuleFacts { FeedUrl = "https://example.com/jobs.xml" }));
        Assert.False(RuleEvaluator.Matches(rule, new RuleFacts()));
    }

    [Fact]
    public void ASendRuleAndAnArrivalRuleAreDifferentRules()
    {
        // The wizard starts them from different blank rules and they never mix: an inbox rule
        // let loose on Sent Items is how somebody loses their own replies.
        var arrival = new MailRule { Name = "Inbox" };
        var sent = new MailRule { Name = "Outbox", AppliesToSent = true };

        Assert.False(arrival.AppliesToSent);
        Assert.True(sent.AppliesToSent);

        // And it survives the document, which is where it is kept — there is no column for it.
        Assert.True(MailRule.FromDefinition(1, "Outbox", true, 0, sent.DefinitionJson()).AppliesToSent);
        Assert.False(MailRule.FromDefinition(1, "Inbox", true, 0, arrival.DefinitionJson()).AppliesToSent);
    }

    [Fact]
    public void ARuleWrittenBeforeSendRulesExistedAppliesToArrivingMail()
    {
        // A definition with no AppliesToSent is every rule anybody wrote until now, and all of
        // them were arrival rules. Reading one as a send rule would stop it running.
        const string Old = "{\"Conditions\":[],\"Actions\":[],\"Exceptions\":[]}";

        Assert.False(MailRule.FromDefinition(1, "Old", true, 0, Old).AppliesToSent);
    }

    // ---- Rules over what this machine sends -------------------------------------------------

    [Fact]
    public void ASendRuleFilesTheCopyInSentItemsAndAnArrivalRuleNeverSeesIt()
    {
        var (store, repo, inbox) = Fresh();
        using var _ = store;

        var accountId = repo.Accounts()[0].Id;
        var sent = repo.FolderWithRole(accountId, FolderRole.Sent)!;
        var filed = repo.AddFolder(accountId, "Filed");

        // Two rules that would both fire on the same message, told apart only by which set they
        // belong to. Only the send one may act on a copy in Sent Items.
        repo.AddRule(new MailRule
        {
            Name = "Arrival",
            AppliesToSent = false,
            Actions = [new RuleAction(RuleActionKind.MarkImportance) { Level = 2 }],
        }, DateTimeOffset.UtcNow);

        repo.AddRule(new MailRule
        {
            Name = "Send",
            AppliesToSent = true,
            Conditions = [new RuleCondition(RuleConditionKind.SubjectContains) { Values = ["invoice"] }],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder) { FolderId = filed.Id, FolderName = "Filed" }],
        }, DateTimeOffset.UtcNow);

        var (id, message) = Deliver(repo, sent, "you@example.com", "Your invoice");
        var handler = new RulesHandler();

        Assert.Equal(filed.Id, handler.HandleSent(repo, sent, id, message));

        // And the arrival path leaves a send rule alone: a copy delivered to the Inbox does not
        // move, because the only rule that files is the one written for messages I send.
        var (arrived, arrivedMessage) = Deliver(repo, inbox, "someone@example.com", "Your invoice");
        Assert.Equal(inbox.Id, handler.Handle(repo, inbox, arrived, arrivedMessage));
    }

    [Fact]
    public void ASendRuleThatIsOffDoesNothing()
    {
        var (store, repo, _) = Fresh();
        using var _s = store;

        var accountId = repo.Accounts()[0].Id;
        var sent = repo.FolderWithRole(accountId, FolderRole.Sent)!;
        var filed = repo.AddFolder(accountId, "Filed");

        repo.AddRule(new MailRule
        {
            Name = "Send",
            Enabled = false,
            AppliesToSent = true,
            Actions = [new RuleAction(RuleActionKind.MoveToFolder) { FolderId = filed.Id, FolderName = "Filed" }],
        }, DateTimeOffset.UtcNow);

        var (id, message) = Deliver(repo, sent, "you@example.com", "Anything");
        Assert.Equal(sent.Id, new RulesHandler().HandleSent(repo, sent, id, message));
    }
}
