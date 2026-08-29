using System.Text;
using Mailbox.Editor;

namespace Mailbox.Tests;

/// <summary>
/// Spelling, against a dictionary this file writes.
/// </summary>
/// <remarks>
/// The machine this was written on has no dictionary installed, which is the ordinary state of a
/// great many desktops and the reason half of these tests are about behaving well without one.
/// The rest write a tiny Hunspell dictionary to a scratch directory and point <c>DICPATH</c> at
/// it, so what is exercised is the real loader and the real checker rather than a stand-in.
/// </remarks>
public class SpellCheckTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mailbox-dic-" + Guid.NewGuid().ToString("n"));

    // The host's own dictionaries are taken out of the search for the class's lifetime: half
    // these tests answer for a machine with none, and the first CI run proved the host's
    // /usr/share/hunspell otherwise answers instead.
    private readonly string[] _systemDirectories = SpellCheck.SystemDirectories;

    public SpellCheckTests() => SpellCheck.SystemDirectories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A dictionary of a handful of words, in the format Hunspell actually reads: a count, then
    /// one word per line, beside an affix file declaring the encoding.
    /// </summary>
    private string WriteDictionary(string language, params string[] words)
    {
        Directory.CreateDirectory(_directory);

        var dic = new StringBuilder().AppendLine(words.Length.ToString());
        foreach (var word in words) dic.AppendLine(word);

        File.WriteAllText(Path.Combine(_directory, $"{language}.dic"), dic.ToString(), Encoding.UTF8);
        File.WriteAllText(Path.Combine(_directory, $"{language}.aff"), "SET UTF-8\n", Encoding.UTF8);

        return _directory;
    }

    private async Task<SpellCheck> LoadAsync(string language = "en_GB", string? personal = null)
    {
        Environment.SetEnvironmentVariable("DICPATH", _directory);

        try
        {
            return await SpellCheck.LoadAsync(language, personal, Ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DICPATH", null);
        }
    }

    // ---- With a dictionary ---------------------------------------------------------------------

    [Fact]
    public async Task AKnownWordIsCorrect()
    {
        WriteDictionary("en_GB", "colour", "message", "the");
        var spelling = await LoadAsync();

        Assert.True(spelling.IsAvailable);
        Assert.Equal("en_GB", spelling.Language);
        Assert.True(spelling.IsCorrect("colour"));
    }

    [Fact]
    public async Task AnUnknownWordIsNot()
    {
        WriteDictionary("en_GB", "colour");
        Assert.False((await LoadAsync()).IsCorrect("colur"));
    }

    [Fact]
    public async Task ThePositionOfEachOneIsReported()
    {
        WriteDictionary("en_GB", "the", "message");
        var found = (await LoadAsync()).Check("the mesage");

        var only = Assert.Single(found);
        Assert.Equal("mesage", only.Word);
        Assert.Equal(4, only.Offset);
    }

    // ---- What is not prose, and must not be underlined -------------------------------------------

    /// <summary>
    /// A checker that underlines every address, URL, acronym and version number is one people
    /// switch off, and then it checks nothing at all.
    /// </summary>
    [Theory]
    [InlineData("Write to a.person@example.com today")]
    [InlineData("See https://example.com/somewhere for it")]
    [InlineData("The file is at /usr/share/hunspell/en_GB.dic")]
    [InlineData("Built with SDK 10.0.100 today")]
    [InlineData("The HTTP and SMTP the parts")]
    public async Task WhatIsNotProseIsNotChecked(string text)
    {
        WriteDictionary("en_GB", "write", "to", "see", "for", "it", "the", "file", "is", "at",
            "built", "with", "today", "and", "parts", "a");

        Assert.Empty((await LoadAsync()).Check(text));
    }

    /// <summary>An apostrophe is part of the word. Splitting on it reports "t" as a misspelling.</summary>
    [Fact]
    public async Task AnApostropheDoesNotSplitAWord()
    {
        WriteDictionary("en_GB", "don't", "it");

        Assert.Empty((await LoadAsync()).Check("don't"));
    }

    [Fact]
    public async Task ASingleLetterIsNotChecked()
    {
        WriteDictionary("en_GB", "the");
        Assert.Empty((await LoadAsync()).Check("a I x"));
    }

    // ---- The personal dictionary -------------------------------------------------------------------

    /// <summary>
    /// Written beside the mail store rather than into the system dictionary, which is not ours
    /// to edit and which a package update would overwrite.
    /// </summary>
    [Fact]
    public async Task AWordAddedIsRememberedAndWrittenDown()
    {
        WriteDictionary("en_GB", "the");
        var path = Path.Combine(_directory, "personal.dic");

        var spelling = await LoadAsync(personal: path);
        Assert.False(spelling.IsCorrect("Mailbox"));

        spelling.Add("Mailbox");
        Assert.True(spelling.IsCorrect("Mailbox"));

        // And it survives, which is the half that has to reach the disk.
        Assert.Contains("Mailbox", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
        Assert.True((await LoadAsync(personal: path)).IsCorrect("Mailbox"));
    }

    // ---- Choosing one ----------------------------------------------------------------------------

    /// <summary>
    /// en_GB against a machine carrying only en_US should check spelling and disagree about a
    /// few words, not check nothing.
    /// </summary>
    [Fact]
    public async Task AVariantOfTheSameLanguageWillDo()
    {
        WriteDictionary("en_US", "color");
        var spelling = await LoadAsync("en_GB");

        Assert.True(spelling.IsAvailable);
        Assert.Equal("en_US", spelling.Language);
    }

    [Fact]
    public async Task ALocaleWithAnEncodingOnItStillMatches()
    {
        WriteDictionary("en_GB", "colour");
        Assert.Equal("en_GB", (await LoadAsync("en_GB.UTF-8")).Language);
    }

    [Fact]
    public async Task EveryDictionaryPresentCanBeListed()
    {
        WriteDictionary("en_GB", "colour");
        Environment.SetEnvironmentVariable("DICPATH", _directory);

        try
        {
            Assert.Contains("en_GB", SpellCheck.Available());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DICPATH", null);
        }
    }

    // ---- Without one ------------------------------------------------------------------------------

    /// <summary>
    /// The ordinary state of a great many desktops, and of the one this was written on. A mail
    /// client that refuses to run, or nags, over a missing word list is worse than one that
    /// quietly cannot check spelling — so nothing here throws and nothing is reported wrong.
    /// </summary>
    [Fact]
    public async Task NoDictionaryIsNotAnError()
    {
        var spelling = await LoadAsync();

        Assert.False(spelling.IsAvailable);
        Assert.Null(spelling.Language);
        Assert.True(spelling.IsCorrect("definitelynotaword"));
        Assert.Empty(spelling.Check("definitelynotaword either"));
        Assert.Empty(spelling.Suggest("definitelynotaword"));
    }

    /// <summary>
    /// A dictionary needs both halves. A <c>.dic</c> with no <c>.aff</c> beside it is what a
    /// half-finished install leaves behind, and it is not a dictionary.
    /// </summary>
    [Fact]
    public async Task ADictionaryMissingItsAffixFileIsNotOne()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "en_GB.dic"), "1\ncolour\n", Ct);

        Assert.False((await LoadAsync()).IsAvailable);
        Assert.DoesNotContain("en_GB", SpellCheck.Available());
    }

    /// <summary>
    /// An empty word list knows nothing, and a checker that knows nothing calls every word in
    /// the message wrong — which is worse than not checking, and reads as the application being
    /// broken rather than the file.
    /// </summary>
    [Fact]
    public async Task ADictionaryWithNoWordsInItIsTreatedAsNone()
    {
        WriteDictionary("en_GB");

        var spelling = await LoadAsync();

        Assert.False(spelling.IsAvailable);
        Assert.Empty(spelling.Check("anything at all"));
    }

    public void Dispose()
    {
        SpellCheck.SystemDirectories = _systemDirectories;
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
            // A scratch directory that will not delete is not a test failure.
        }
    }

    // ---- The Proofing switches ----------------------------------------------------------------

    [Fact]
    public async Task TheIgnoreSwitchesAreOnByDefaultAndCanBeTurnedOff()
    {
        WriteDictionary("en_GB", "the", "report", "is", "at");
        var spelling = await LoadAsync();

        // On: an acronym, a code and an address are not words to check.
        Assert.Empty(spelling.Check("the NASA report R2D2 is at http://example.com/x"));

        spelling.Options = new SpellCheckOptions(IgnoreUppercase: false, IgnoreWithNumbers: false, IgnoreAddresses: false);
        var found = spelling.Check("the NASA report R2D2 is at http://example.com/x");
        Assert.Contains(found, m => m.Word == "NASA");
        Assert.Contains(found, m => m.Word == "R2D2");
        Assert.Contains(found, m => m.Word == "http");
        Assert.DoesNotContain(found, m => m.Word == "report");
    }

    [Fact]
    public async Task ARepeatedWordIsFlaggedOnceAndOnlyWhenAsked()
    {
        WriteDictionary("en_GB", "the", "cat", "sat");
        var spelling = await LoadAsync();

        var found = spelling.Check("the the cat sat sat  sat");
        var repeated = found.Where(m => m.IsRepeated).ToList();
        Assert.Equal(["the", "sat", "sat"], repeated.Select(m => m.Word));
        Assert.Equal(4, repeated[0].Offset);
        // The words themselves are spelled correctly, so nothing else is reported.
        Assert.Equal(3, found.Count);

        // A repeated word across a line of punctuation, or of capitals, is not a slip.
        Assert.Empty(spelling.Check("the. The cat"));
        Assert.Empty(spelling.Check("NASA NASA"));

        spelling.Options = new SpellCheckOptions(FlagRepeatedWords: false);
        Assert.Empty(spelling.Check("the the cat"));
    }

    [Fact]
    public async Task APersonalWordCanBeListedAndForgotten()
    {
        WriteDictionary("en_GB", "the");
        var personal = Path.Combine(_directory, "personal.txt");
        var spelling = await LoadAsync(personal: personal);

        spelling.Add("Mailbox");
        spelling.Add("Selawik");
        Assert.Equal(["Mailbox", "Selawik"], spelling.PersonalWords);
        Assert.True(spelling.IsCorrect("Selawik"));

        Assert.True(spelling.Remove("Selawik"));
        Assert.False(spelling.Remove("Selawik"));
        Assert.Equal(["Mailbox"], spelling.PersonalWords);
        Assert.False(spelling.IsCorrect("Selawik"));

        // The list on disk follows.
        var back = await LoadAsync(personal: personal);
        Assert.Equal(["Mailbox"], back.PersonalWords);
    }

    /// <summary>
    /// The cut behind "Ignore original message text in reply or forward": the reply's own words
    /// are checked and the untouched quote below them is not.
    /// </summary>
    [Fact]
    public void ThePristineTailOfAReplyIsNotChecked()
    {
        var pristine = "\n\nA. Person\n-----Original Message-----\nThe figures are misspeled here.";

        // Typed above the quote: only the typing survives the cut.
        Assert.Equal(
            "Thanks, looks good.",
            SpellCheck.WithoutPristineTail("Thanks, looks good." + pristine, pristine));

        // Nothing typed at all: nothing left to check.
        Assert.Equal(string.Empty, SpellCheck.WithoutPristineTail(pristine, pristine));

        // A line of the original the writer edited fell out of the shared suffix by itself —
        // editing it made it theirs — while the untouched tail below the edit still goes.
        var edited = "Reply.\n\nA. Person\n-----Original Message-----\nThe figures are corrected here.";
        Assert.Equal(
            "Reply.\n\nA. Person\n-----Original Message-----\nThe figures are correct",
            SpellCheck.WithoutPristineTail(edited, pristine));

        // A new message has no pristine tail, and the whole of it is checked.
        Assert.Equal("All of it.", SpellCheck.WithoutPristineTail("All of it.", string.Empty));
    }
}
