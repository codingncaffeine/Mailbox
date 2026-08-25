using System.Globalization;
using Mailbox.Editor;

namespace Mailbox.Tests;

/// <summary>
/// Correcting a word as it is finished.
/// </summary>
/// <remarks>
/// Everything the rules decide is decided here, against text rather than a document: what the
/// caller does with an action is the editor's business, and holding the rules to a control that
/// needs a window would mean proving the hard half through the easy one. The editing itself is
/// posed through the compose surface instead — a run that types and reads the body back.
/// </remarks>
public class AutocorrectTests
{
    private static Autocorrect Corrector(
        AutocorrectOptions? options = null,
        AutocorrectTable? table = null,
        AutocorrectExceptions? exceptions = null,
        Func<string, bool>? known = null,
        Func<string, IReadOnlyList<string>>? suggest = null)
        => new(options ?? AutocorrectOptions.Default, table, exceptions, known, suggest,
               CultureInfo.GetCultureInfo("en-GB"));

    /// <summary>What the editor would hold after applying an action to the text before the caret.</summary>
    private static string Apply(string before, AutocorrectAction? action, char terminator)
    {
        if (action is null) return before + terminator;

        var kept = before[..(before.Length - action.Remove)];
        var typed = action.ReplacesInput ? kept + action.Insert : kept + action.Insert + terminator;

        return typed;
    }

    // ---- The table ---------------------------------------------------------------------------

    [Fact]
    public void AMistypedWordIsCorrectedWhenItIsFinished()
    {
        var action = Corrector().AtWordBoundary("teh", ' ');

        Assert.NotNull(action);
        Assert.Equal(3, action.Remove);
        // "The", not "the": the word begins the paragraph, so the sentence rule has its say too.
        Assert.Equal("The", action.Insert);
        Assert.Equal("The ", Apply("teh", action, ' '));
    }

    [Fact]
    public void OnlyTheWordJustTypedIsTouched()
    {
        // The whole point of the rule: "teh" appears twice and the one at the caret is corrected.
        var action = Corrector().AtWordBoundary("teh cat sat on teh", ' ');

        Assert.NotNull(action);
        Assert.Equal(3, action.Remove);
        Assert.Equal("teh cat sat on the ", Apply("teh cat sat on teh", action, ' '));
        Assert.Equal("the", action.Insert);
    }

    [Fact]
    public void TheCorrectionIsTypedInTheCaseTheWordWas()
    {
        Assert.Equal("The", Corrector().AtWordBoundary("wrote Teh", ' ')?.Insert);
        Assert.Equal("THE", Corrector().AtWordBoundary("wrote TEH", ' ')?.Insert);
    }

    [Fact]
    public void ASymbolTypedAsPunctuationBecomesTheCharacter()
    {
        Assert.Equal("©", Corrector().AtWordBoundary("(c)", ' ')?.Insert);
        Assert.Equal("…", Corrector().AtWordBoundary("...", ' ')?.Insert);
        Assert.Equal("→", Corrector().AtWordBoundary("-->", ' ')?.Insert);
    }

    [Fact]
    public void ACorrectionInTheMiddleOfASentenceIsNotCapitalized()
    {
        Assert.Equal("the", Corrector().AtWordBoundary("all teh", ' ')?.Insert);
    }

    [Fact]
    public void AWordTheTableDoesNotKnowIsLeftAlone()
    {
        Assert.Null(Corrector().AtWordBoundary("it reads perfectly", ' '));
    }

    [Fact]
    public void TheMasterSwitchStopsTheTable()
    {
        var options = AutocorrectOptions.Default with { ReplaceAsYouType = false };
        Assert.Null(Corrector(options).AtWordBoundary("all teh", ' '));
    }

    [Fact]
    public void AnApostropheFollowsTheSmartQuotesSwitch()
    {
        Assert.Equal("don’t", Corrector().AtWordBoundary("I dont", ' ')?.Insert);

        var straight = AutocorrectOptions.Default with { SmartQuotes = false };
        Assert.Equal("don't", Corrector(straight).AtWordBoundary("I dont", ' ')?.Insert);
    }

    // ---- Capitals ----------------------------------------------------------------------------

    [Fact]
    public void TwoInitialCapitalsBecomeOne()
    {
        Assert.Equal("Whether", Corrector().AtWordBoundary("asked WHether", ' ')?.Insert);
    }

    [Fact]
    public void AnAcronymsPluralKeepsBothCapitals()
    {
        Assert.Null(Corrector().AtWordBoundary("IDs", ' '));
        Assert.Null(Corrector().AtWordBoundary("PhDs", ' '));
    }

    [Fact]
    public void AWordTheExceptionsListNamesIsLeftAlone()
    {
        var exceptions = new AutocorrectExceptions(initialCaps: ["FRoms"]);
        Assert.Null(Corrector(exceptions: exceptions).AtWordBoundary("FRoms", ' '));
    }

    [Fact]
    public void ACapsLockLeftOnIsUndone()
    {
        Assert.Equal("Hello", Corrector().AtWordBoundary("hELLO", ' ')?.Insert);
        Assert.Equal("Hello", Corrector().AtWordBoundary("said hELLO", ' ')?.Insert);
    }

    [Fact]
    public void AProductNameThatMixesCaseIsNotACapsLock()
    {
        Assert.Null(Corrector().AtWordBoundary("sent from an iPhone", ' '));
        Assert.Null(Corrector().AtWordBoundary("built on macOS", ' '));
    }

    [Fact]
    public void TheFirstWordOfAParagraphIsCapitalized()
    {
        Assert.Equal("Hello", Corrector().AtWordBoundary("hello", ' ')?.Insert);
    }

    [Fact]
    public void TheFirstWordAfterAFullStopIsCapitalized()
    {
        Assert.Equal("Then", Corrector().AtWordBoundary("It arrived. then", ' ')?.Insert);
    }

    [Fact]
    public void AWordInTheMiddleOfASentenceIsNot()
    {
        Assert.Null(Corrector().AtWordBoundary("it arrived and then", ' '));
    }

    [Fact]
    public void AnAbbreviationDoesNotEndTheSentence()
    {
        Assert.Null(Corrector().AtWordBoundary("tea, e.g. green", ' '));
        Assert.Null(Corrector().AtWordBoundary("Mr. smith", ' '));
    }

    [Fact]
    public void AnInitialDoesNotEndTheSentenceEither()
    {
        Assert.Null(Corrector().AtWordBoundary("Written by J. r", ' '));
    }

    [Fact]
    public void ADayIsCapitalized()
    {
        Assert.Equal("Monday", Corrector().AtWordBoundary("see you monday", ' ')?.Insert);
    }

    [Fact]
    public void TheDaysAreThisMachinesLanguageAsWellAsEnglish()
    {
        var french = new Autocorrect(culture: CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal("Mardi", french.AtWordBoundary("à mardi", ' ')?.Insert);
        Assert.Equal("Tuesday", french.AtWordBoundary("see you tuesday", ' ')?.Insert);
    }

    [Fact]
    public void TheFirstWordOfATableCellIsCapitalized()
    {
        Assert.Equal("Total", Corrector().AtWordBoundary("total", ' ', startsCell: true)?.Insert);
    }

    [Fact]
    public void EachCapitalRuleAnswersToItsOwnSwitch()
    {
        Assert.Null(Corrector(AutocorrectOptions.Default with { CapitalizeSentences = false })
            .AtWordBoundary("hello", ' '));

        Assert.Null(Corrector(AutocorrectOptions.Default with { CapitalizeDays = false })
            .AtWordBoundary("see you monday", ' '));

        Assert.Null(Corrector(AutocorrectOptions.Default with { TwoInitialCapitals = false })
            .AtWordBoundary("asked WHether", ' '));

        Assert.Null(Corrector(AutocorrectOptions.Default with { CapsLock = false })
            .AtWordBoundary("said hELLO", ' '));
    }

    // ---- What is not prose -------------------------------------------------------------------

    [Fact]
    public void AnAddressIsNeverCorrected()
    {
        Assert.Null(Corrector().AtWordBoundary("write to teh@example.com", ' '));
        Assert.Null(Corrector().AtWordBoundary("see https://example.com/teh", ' '));
        Assert.Null(Corrector().AtWordBoundary("in ~/teh/notes", ' '));
    }

    [Fact]
    public void AHostnameDoesNotEndASentence()
    {
        Assert.Null(Corrector().AtWordBoundary("at example.com there", ' '));
    }

    [Fact]
    public void NothingIsDoneToAnEmptyParagraph()
    {
        Assert.Null(Corrector().AtWordBoundary(string.Empty, ' '));
    }

    // ---- The marks ---------------------------------------------------------------------------

    [Fact]
    public void AStraightQuoteBecomesACurlyOne()
    {
        var opening = Corrector().AtCharacter("he said ", '"');
        Assert.Equal("“", opening?.Insert);
        Assert.True(opening?.ReplacesInput);

        Assert.Equal("”", Corrector().AtCharacter("he said “hello", '"')?.Insert);
        Assert.Equal("’", Corrector().AtCharacter("dont", '\'')?.Insert);
        Assert.Equal("‘", Corrector().AtCharacter("he said ", '\'')?.Insert);
    }

    [Fact]
    public void TheQuoteStaysStraightWhenTheSwitchIsOff()
    {
        var options = AutocorrectOptions.Default with { SmartQuotes = false };
        Assert.Null(Corrector(options).AtCharacter("he said ", '"'));
    }

    [Fact]
    public void AFractionBecomesOneCharacter()
    {
        Assert.Equal("½", Corrector().AtWordBoundary("1/2", ' ')?.Insert);
        Assert.Null(Corrector(AutocorrectOptions.Default with { Fractions = false })
            .AtWordBoundary("1/2", ' '));
    }

    [Fact]
    public void TwoHyphensBecomeADash()
    {
        Assert.Equal("one—two", Corrector().AtWordBoundary("one--two", ' ')?.Insert);
    }

    [Fact]
    public void ARowOfHyphensIsNotADash()
    {
        Assert.Null(Corrector().AtWordBoundary("---", ' '));
    }

    [Fact]
    public void ARowOfHyphensOnItsOwnLineIsARule()
    {
        var action = Corrector().AtParagraphBreak("---");

        Assert.NotNull(action);
        Assert.Equal(AutocorrectFormat.Divider, action.Format);
        Assert.Equal(3, action.Remove);

        Assert.Null(Corrector().AtParagraphBreak("--"));
        Assert.Null(Corrector().AtParagraphBreak("a---"));
        Assert.Null(Corrector(AutocorrectOptions.Default with { BorderLines = false }).AtParagraphBreak("---"));
    }

    [Fact]
    public void StarsAroundAWordMakeItBold()
    {
        // Read when the word is finished rather than as the closing star is typed: the
        // character that finishes it is what carries the caret back out of the emphasis.
        var action = Corrector().AtWordBoundary("this is *important*", ' ');

        Assert.NotNull(action);
        Assert.Equal(AutocorrectFormat.Bold, action.Format);
        Assert.Equal("important", action.Insert);
        Assert.Equal(11, action.Remove);
        Assert.False(action.ReplacesInput);
    }

    [Fact]
    public void UnderscoresAroundAWordMakeItItalic()
    {
        var action = Corrector().AtWordBoundary("this is _quiet_", ' ');

        Assert.Equal(AutocorrectFormat.Italic, action?.Format);
        Assert.Equal("quiet", action?.Insert);
    }

    [Fact]
    public void EmphasisCanCoverMoreThanOneWord()
    {
        var action = Corrector().AtWordBoundary("say *very important*", ' ');

        Assert.Equal("very important", action?.Insert);
        Assert.Equal(16, action?.Remove);
    }

    [Fact]
    public void AStarInTheMiddleOfSomethingIsJustAStar()
    {
        Assert.Null(Corrector().AtWordBoundary("a 2*3*", ' '));
        Assert.Null(Corrector().AtWordBoundary("a * *", ' '));
        Assert.Null(Corrector().AtWordBoundary("nothing to close*", ' '));
        Assert.Null(Corrector().AtCharacter("this is *important", '*'));
    }

    [Fact]
    public void TheEmphasisRuleAnswersToItsSwitch()
    {
        Assert.Null(Corrector(AutocorrectOptions.Default with { BoldAndItalic = false })
            .AtWordBoundary("this is *important*", ' '));
    }

    [Fact]
    public void AMarkerAtTheStartOfAParagraphStartsAList()
    {
        var bullet = Corrector().AtWordBoundary("*", ' ');
        Assert.Equal(AutocorrectFormat.Bullet, bullet?.Format);
        Assert.Equal(1, bullet?.Remove);

        Assert.Equal(AutocorrectFormat.Bullet, Corrector().AtWordBoundary("-", ' ')?.Format);
        Assert.Equal(AutocorrectFormat.Numbering, Corrector().AtWordBoundary("1.", ' ')?.Format);
        Assert.Equal(AutocorrectFormat.Numbering, Corrector().AtWordBoundary("2)", ' ')?.Format);
        Assert.Equal(AutocorrectFormat.Numbering, Corrector().AtWordBoundary("a.", ' ')?.Format);
    }

    [Fact]
    public void AMarkerInTheMiddleOfALineIsNotAList()
    {
        Assert.Null(Corrector().AtWordBoundary("two things -", ' '));
    }

    [Fact]
    public void TheListRulesAnswerToTheirSwitches()
    {
        Assert.Null(Corrector(AutocorrectOptions.Default with { BulletedLists = false })
            .AtWordBoundary("*", ' '));

        Assert.Null(Corrector(AutocorrectOptions.Default with { NumberedLists = false })
            .AtWordBoundary("1.", ' '));
    }

    [Fact]
    public void MathReplacementsWaitForTheirSwitch()
    {
        Assert.Null(Corrector().AtWordBoundary("\\alpha", ' '));

        var options = AutocorrectOptions.Default with { MathReplacements = true };
        Assert.Equal("α", Corrector(options).AtWordBoundary("\\alpha", ' ')?.Insert);
        Assert.Equal("Δ", Corrector(options).AtWordBoundary("\\Delta", ' ')?.Insert);
    }

    // ---- The spelling checker's suggestions ---------------------------------------------------

    [Fact]
    public void ACloseSuggestionFromTheCheckerIsUsed()
    {
        var corrector = Corrector(
            known: word => word == "wrong",
            suggest: _ => ["wrong"]);

        Assert.Equal("wrong", corrector.AtWordBoundary("a wrnog", ' ')?.Insert);
    }

    [Fact]
    public void ADistantSuggestionIsNot()
    {
        // Two edits away is where a checker starts guessing, and a guess typed into somebody
        // else's mail is worse than leaving the word alone.
        var corrector = Corrector(known: _ => false, suggest: _ => ["wrong"]);

        Assert.Null(corrector.AtWordBoundary("a msg", ' '));
        Assert.Null(corrector.AtWordBoundary("a wrbog", ' '));
    }

    [Fact]
    public void AWordTheCheckerKnowsIsLeftAlone()
    {
        var asked = 0;
        var corrector = Corrector(known: _ => true, suggest: _ => { asked++; return ["something"]; });

        Assert.Null(corrector.AtWordBoundary("it reads perfectly", ' '));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void TheCheckerIsAskedAboutAWordOnlyOnce()
    {
        var asked = 0;
        var corrector = Corrector(
            known: _ => { asked++; return false; },
            suggest: _ => ["wrong"]);

        corrector.AtWordBoundary("a wrnog", ' ');
        corrector.AtWordBoundary("a wrnog", ' ');

        Assert.Equal(1, asked);
    }

    [Fact]
    public void TheSuggestionRuleAnswersToItsSwitch()
    {
        var options = AutocorrectOptions.Default with { UseSpellingSuggestions = false };
        var corrector = Corrector(options, known: _ => false, suggest: _ => ["wrong"]);

        Assert.Null(corrector.AtWordBoundary("a wrnog", ' '));
    }

    [Fact]
    public void WithNoDictionaryTheSuggestionRuleSimplyDoesNothing()
    {
        Assert.Null(Corrector().AtWordBoundary("a wrnog", ' '));
    }

    // ---- The table's storage -----------------------------------------------------------------

    [Fact]
    public void AnAddedEntryIsFound()
    {
        var table = new AutocorrectTable();
        table.Add("mbx", "Mailbox");

        Assert.Equal("Mailbox", table.Lookup("mbx"));
        Assert.Equal("Mailbox", Corrector(table: table).AtWordBoundary("mbx", ' ')?.Insert);
    }

    [Fact]
    public void ARemovedDefaultStopsCorrecting()
    {
        var table = new AutocorrectTable();

        Assert.True(table.Remove("teh"));
        Assert.Null(table.Lookup("teh"));
        Assert.Null(Corrector(table: table).AtWordBoundary("all teh", ' '));
    }

    [Fact]
    public void OnlyTheDifferenceIsStored()
    {
        var table = new AutocorrectTable();
        table.Add("mbx", "Mailbox");
        table.Remove("teh");

        var json = table.ToJson();
        Assert.Contains("mbx", json);
        Assert.DoesNotContain("recieve", json);

        var read = AutocorrectTable.FromJson(json);
        Assert.Equal("Mailbox", read.Lookup("mbx"));
        Assert.Null(read.Lookup("teh"));
        Assert.Equal("the", read.Lookup("hte"));
    }

    [Fact]
    public void ATableThatWillNotParseCostsTheAdditionsAndNothingElse()
    {
        var table = AutocorrectTable.FromJson("{ not json");

        Assert.Equal("the", table.Lookup("teh"));
    }

    [Fact]
    public void EveryDefaultIsAnImprovementOnWhatWasTyped()
    {
        // A row whose replacement is what it replaces would correct a word to itself forever.
        foreach (var entry in AutocorrectTable.Defaults)
        {
            Assert.NotEqual(entry.Replace, entry.With, StringComparer.Ordinal);
            Assert.NotEmpty(entry.With);
        }

        Assert.Equal(
            AutocorrectTable.Defaults.Count,
            AutocorrectTable.Defaults.Select(e => e.Replace.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void NoDefaultCorrectsAWordIntoAnotherDefault()
    {
        // "teh" becoming "the" is a correction; a row whose replacement is itself the left-hand
        // side of another row would be a correction that wants correcting.
        var replaced = AutocorrectTable.Defaults
            .Select(e => e.Replace)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in AutocorrectTable.Defaults)
        {
            // Ordinal: "i" becomes "I", which the table would match again but never change,
            // and a correction that changes nothing is stopped before it is offered.
            Assert.DoesNotContain(entry.With, replaced);
        }
    }

    [Fact]
    public void EverythingOffCorrectsNothing()
    {
        var corrector = Corrector(AutocorrectOptions.Off);

        Assert.Null(corrector.AtWordBoundary("all teh", ' '));
        Assert.Null(corrector.AtWordBoundary("hello", ' '));
        Assert.Null(corrector.AtCharacter("he said ", '"'));
        Assert.Null(corrector.AtParagraphBreak("---"));
    }
}
