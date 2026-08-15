using Mailbox.Core.Compose;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The Auto-Complete List: fed by what was sent, offered back by what is typed. Half of it is
/// the store — weight, recency, name-follows-address — and half is the arithmetic on the To
/// line, which is where completions go wrong in practice: the wrong entry replaced, or the
/// rest of the line lost.
/// </summary>
public class AutoCompleteTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static (MailStore Store, MailRepository Repo) Fresh()
    {
        var store = MailStore.Transient();
        return (store, new MailRepository(store));
    }

    // ---- The store ------------------------------------------------------------------------

    [Fact]
    public void ARecipientIsRememberedAndOfferedBackByAddressOrName()
    {
        var (store, repo) = Fresh();
        using var _ = store;

        repo.RecordRecipients([("Alice@Example.com", "Alice Liddell")], Now);

        var byAddress = Assert.Single(repo.SuggestRecipients("ali"));
        Assert.Equal("alice@example.com", byAddress.Address);
        Assert.Equal("Alice Liddell", byAddress.DisplayName);
        Assert.Equal("Alice Liddell <alice@example.com>", byAddress.Formatted);

        // A surname is what people type as often as not, so a word start inside the name counts.
        Assert.Single(repo.SuggestRecipients("lidd"));

        // Case is not signal.
        Assert.Single(repo.SuggestRecipients("ALICE"));
    }

    [Fact]
    public void TheMostUsedComesFirstAndRecencyBreaksTies()
    {
        var (store, repo) = Fresh();
        using var _ = store;

        repo.RecordRecipients([("a.one@example.com", "A One")], Now.AddDays(-2));
        repo.RecordRecipients([("a.two@example.com", "A Two")], Now.AddDays(-1));
        repo.RecordRecipients([("a.two@example.com", "A Two")], Now.AddDays(-1));
        repo.RecordRecipients([("a.three@example.com", "A Three")], Now);

        var suggested = repo.SuggestRecipients("a.");

        Assert.Equal(
            ["a.two@example.com", "a.three@example.com", "a.one@example.com"],
            suggested.Select(s => s.Address));
        Assert.Equal(2, suggested[0].Weight);
    }

    [Fact]
    public void ANameFollowsTheAddressButABlankOneNeverOverwritesIt()
    {
        var (store, repo) = Fresh();
        using var _ = store;

        repo.RecordRecipients([("bob@example.com", "Bob")], Now);
        repo.RecordRecipients([("bob@example.com", "Robert Builder")], Now.AddMinutes(1));
        Assert.Equal("Robert Builder", Assert.Single(repo.SuggestRecipients("bob")).DisplayName);

        // A reply typed as a bare address is not a reason to forget what someone is called.
        repo.RecordRecipients([("bob@example.com", null)], Now.AddMinutes(2));
        var entry = Assert.Single(repo.SuggestRecipients("bob"));
        Assert.Equal("Robert Builder", entry.DisplayName);
        Assert.Equal(3, entry.Weight);
    }

    [Fact]
    public void ForgetTakesOneOutAndClearTakesEverything()
    {
        var (store, repo) = Fresh();
        using var _ = store;

        repo.RecordRecipients([("one@example.com", ""), ("two@example.com", "")], Now);
        Assert.Equal(2, repo.RecipientCount());

        repo.ForgetRecipient("ONE@example.com");
        Assert.Equal(1, repo.RecipientCount());
        Assert.Equal("two@example.com", Assert.Single(repo.SuggestRecipients("t")).Address);

        Assert.Equal(1, repo.ClearRecipients());
        Assert.Equal(0, repo.RecipientCount());
    }

    [Fact]
    public void NothingIsSuggestedForNothingTypedAndJunkIsNotRecorded()
    {
        var (store, repo) = Fresh();
        using var _ = store;

        repo.RecordRecipients([("not an address", "X"), ("", "Y"), ("  ", null)], Now);
        Assert.Equal(0, repo.RecipientCount());

        repo.RecordRecipients([("real@example.com", "Real")], Now);
        Assert.Empty(repo.SuggestRecipients("   "));
    }

    [Fact]
    public void TypedWildcardsAreLettersNotPatterns()
    {
        var (store, repo) = Fresh();
        using var _ = store;

        repo.RecordRecipients([("a_b@example.com", ""), ("axb@example.com", "")], Now);

        // An underscore in LIKE matches any character; typed, it must match an underscore.
        Assert.Equal("a_b@example.com", Assert.Single(repo.SuggestRecipients("a_")).Address);
        Assert.Empty(repo.SuggestRecipients("%"));
    }

    // ---- The line -------------------------------------------------------------------------

    [Fact]
    public void TheCurrentEntryIsWhatFollowsTheLastSeparatorBeforeTheCaret()
    {
        var line = "Alice <alice@example.com>; bo";
        var (start, text) = RecipientCompletion.CurrentEntry(line, line.Length, commasSeparate: true);

        Assert.Equal("bo", text);
        Assert.Equal(line.Length - 2, start);
    }

    [Fact]
    public void ACommaOnlySeparatesWhenTheOptionSaysSo()
    {
        var line = "Liddell, Al";

        Assert.Equal("Al", RecipientCompletion.CurrentEntry(line, line.Length, commasSeparate: true).Text);
        Assert.Equal("Liddell, Al", RecipientCompletion.CurrentEntry(line, line.Length, commasSeparate: false).Text);
    }

    [Fact]
    public void ReplacingTheLastEntryClosesItReadyForTheNext()
    {
        var (text, caret) = RecipientCompletion.Replace(
            "Alice <alice@example.com>; bo", 29, "Bob <bob@example.com>", commasSeparate: true);

        Assert.Equal("Alice <alice@example.com>; Bob <bob@example.com>; ", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void ReplacingAnEntryInTheMiddleKeepsTheRestOfTheLine()
    {
        // Caret after "bo", with Carol still to come.
        var line = "bo;carol@example.com";
        var (text, caret) = RecipientCompletion.Replace(line, 2, "Bob <bob@example.com>", commasSeparate: true);

        Assert.Equal("Bob <bob@example.com>; carol@example.com", text);
        Assert.Equal("Bob <bob@example.com>; ".Length, caret);
    }

    [Fact]
    public void ACaretInsideAnEntryReplacesTheWholeEntry()
    {
        // "ali|ce@" — the tail after the caret belongs to the same entry and goes with it.
        var (text, _) = RecipientCompletion.Replace("alice@; dave@example.com", 3, "Alice <alice@example.com>", commasSeparate: true);

        Assert.Equal("Alice <alice@example.com>; dave@example.com", text);
    }

    [Fact]
    public void AFinishedEntryAsksForNothing()
    {
        Assert.False(RecipientCompletion.WantsSuggestions("Alice <alice@example.com>"));
        Assert.False(RecipientCompletion.WantsSuggestions(""));
        Assert.True(RecipientCompletion.WantsSuggestions("a"));
        Assert.True(RecipientCompletion.WantsSuggestions("alice@exam"));
    }
}
