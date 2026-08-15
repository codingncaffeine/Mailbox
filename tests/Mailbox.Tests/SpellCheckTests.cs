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
}
