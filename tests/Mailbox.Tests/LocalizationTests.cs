using System.Text;
using Mailbox.Core.Localization;

namespace Mailbox.Tests;

/// <summary>
/// The translation machinery: the catalogue format, the plural rules, and the promise that
/// absence changes nothing.
/// </summary>
/// <remarks>
/// Worth testing hard because every mistake here is silent in the language it is made in. Nobody
/// reading English will ever see a Polish plural picked wrongly, and the person who would is not
/// in a position to report it as a bug rather than as "this application is badly translated".
/// </remarks>
public class LocalizationTests
{
    private static Localizer From(string po)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mailbox-locale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "xx.po"), po, Encoding.UTF8);
        return Localizer.Load(directory, "xx");
    }

    // ---- Absence is harmless -------------------------------------------------------------------

    [Fact]
    public void WithNoCatalogueEverythingIsItsOwnEnglish()
    {
        var localizer = Localizer.Load(Path.Combine(Path.GetTempPath(), "mailbox-no-such-locale"), "xx");

        Assert.Equal("New Email", localizer.T("New Email"));
        Assert.Equal("Open", localizer.T("the Open button", "Open"));
        Assert.Equal("1 message", localizer.Plural("1 message", "{0} messages", 1));
        Assert.Equal("{0} messages", localizer.Plural("1 message", "{0} messages", 5));
    }

    [Fact]
    public void ThePassthroughAnswersItsOwnKeys()
    {
        Assert.Equal("Favourites", Localizer.Passthrough.T("Favourites"));
        Assert.Equal(0, Localizer.Passthrough.Count);
    }

    /// <summary>
    /// An entry a translator has opened but not filled in must not shadow the English with
    /// nothing — which is how a half-finished catalogue blanks an interface.
    /// </summary>
    [Fact]
    public void AnUntranslatedEntryIsNotATranslation()
    {
        var localizer = From("""
            msgid "New Email"
            msgstr ""
            """);

        Assert.Equal("New Email", localizer.T("New Email"));
    }

    // ---- The catalogue -------------------------------------------------------------------------

    [Fact]
    public void AStringIsReadBackByItsEnglish()
    {
        var localizer = From("""
            msgid "New Email"
            msgstr "Neue E-Mail"

            msgid "Send / Receive"
            msgstr "Senden / Empfangen"
            """);

        Assert.Equal("Neue E-Mail", localizer.T("New Email"));
        Assert.Equal("Senden / Empfangen", localizer.T("Send / Receive"));
        Assert.Equal(2, localizer.Count);
    }

    /// <summary>One English word that means two things gets two translations.</summary>
    [Fact]
    public void AContextTellsTwoUsesOfOneWordApart()
    {
        var localizer = From("""
            msgctxt "the button that opens a message"
            msgid "Open"
            msgstr "Öffnen"

            msgctxt "a folder's state"
            msgid "Open"
            msgstr "Geöffnet"
            """);

        Assert.Equal("Öffnen", localizer.T("the button that opens a message", "Open"));
        Assert.Equal("Geöffnet", localizer.T("a folder's state", "Open"));

        // And a plain lookup finds neither, rather than one of them at random.
        Assert.Equal("Open", localizer.T("Open"));
    }

    /// <summary>
    /// A string translated before anybody decided it needed a context still applies to the
    /// contextual lookup — otherwise adding a context to a call site silently unloads its
    /// translation.
    /// </summary>
    [Fact]
    public void APlainTranslationStillAnswersAContextualLookup()
    {
        var localizer = From("""
            msgid "Open"
            msgstr "Öffnen"
            """);

        Assert.Equal("Öffnen", localizer.T("the button that opens a message", "Open"));
    }

    [Fact]
    public void ALongStringSplitOverSeveralLinesIsOneString()
    {
        var localizer = From("""
            msgid ""
            "This message carries no modification detection code, so there is no way to tell "
            "whether it was altered in transit."
            msgstr ""
            "Diese Nachricht enthält keinen Änderungserkennungscode, daher lässt sich nicht "
            "feststellen, ob sie unterwegs verändert wurde."
            """);

        Assert.Equal(
            "Diese Nachricht enthält keinen Änderungserkennungscode, daher lässt sich nicht "
            + "feststellen, ob sie unterwegs verändert wurde.",
            localizer.T(
                "This message carries no modification detection code, so there is no way to tell "
                + "whether it was altered in transit."));
    }

    [Fact]
    public void TheEscapesGettextWritesAreUndone()
    {
        var localizer = From("""
            msgid "Line\none\ttabbed \"quoted\" back\\slash"
            msgstr "Zeile\neins\ttabuliert \"zitiert\" zurück\\schrägstrich"
            """);

        Assert.Equal(
            "Zeile\neins\ttabuliert \"zitiert\" zurück\\schrägstrich",
            localizer.T("Line\none\ttabbed \"quoted\" back\\slash"));
    }

    /// <summary>What a translator's tool leaves behind when a string goes away.</summary>
    [Fact]
    public void CommentsAndObsoleteEntriesAreNotTranslations()
    {
        var localizer = From("""
            # A translator's note.
            #: src/Mailbox.App/Views/MainWindow.axaml.cs:412
            #, fuzzy
            msgid "New Email"
            msgstr "Neue E-Mail"

            #~ msgid "Old Email"
            #~ msgstr "Alte E-Mail"
            """);

        Assert.Equal("Neue E-Mail", localizer.T("New Email"));
        Assert.Equal("Old Email", localizer.T("Old Email"));
        Assert.Equal(1, localizer.Count);
    }

    // ---- Plurals -------------------------------------------------------------------------------

    [Fact]
    public void EnglishHasTwoFormsAndOnlyOneOfThemIsForOne()
    {
        Assert.Equal(2, PluralRule.English.Forms);
        Assert.Equal(1, PluralRule.English.Form(0));
        Assert.Equal(0, PluralRule.English.Form(1));
        Assert.Equal(1, PluralRule.English.Form(2));
        Assert.Equal(1, PluralRule.English.Form(101));
    }

    /// <summary>
    /// Polish: three forms, chosen on the last two digits. The case an application that asks for
    /// "singular and plural" gets wrong for every number above four.
    /// </summary>
    [Fact]
    public void PolishPicksOnTheLastTwoDigits()
    {
        var rule = PluralRule.Read(
            "nplurals=3; plural=(n==1 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);");

        Assert.Equal(3, rule.Forms);
        Assert.Equal(0, rule.Form(1));
        Assert.Equal(1, rule.Form(2));
        Assert.Equal(1, rule.Form(4));
        Assert.Equal(2, rule.Form(5));
        Assert.Equal(2, rule.Form(11));
        Assert.Equal(2, rule.Form(12));
        Assert.Equal(1, rule.Form(22));
        Assert.Equal(2, rule.Form(25));
        Assert.Equal(1, rule.Form(102));
        Assert.Equal(2, rule.Form(112));
    }

    /// <summary>Arabic: six forms, and one of them is for zero.</summary>
    [Fact]
    public void ArabicHasSixFormsIncludingOneForNone()
    {
        var rule = PluralRule.Read(
            "nplurals=6; plural=(n==0 ? 0 : n==1 ? 1 : n==2 ? 2 : n%100>=3 && n%100<=10 ? 3 "
            + ": n%100>=11 ? 4 : 5);");

        Assert.Equal(6, rule.Forms);
        Assert.Equal(0, rule.Form(0));
        Assert.Equal(1, rule.Form(1));
        Assert.Equal(2, rule.Form(2));
        Assert.Equal(3, rule.Form(3));
        Assert.Equal(3, rule.Form(10));
        Assert.Equal(4, rule.Form(11));
        Assert.Equal(4, rule.Form(99));
        Assert.Equal(5, rule.Form(101));
    }

    /// <summary>Japanese: one form, so a count never changes the words.</summary>
    [Fact]
    public void OneFormMeansTheWordsNeverChange()
    {
        var rule = PluralRule.Read("nplurals=1; plural=0;");

        Assert.Equal(1, rule.Forms);
        Assert.Equal(0, rule.Form(0));
        Assert.Equal(0, rule.Form(1));
        Assert.Equal(0, rule.Form(1000));
    }

    /// <summary>French: two, and it puts zero with the singular where English does not.</summary>
    [Fact]
    public void FrenchCountsZeroAsOne()
    {
        var rule = PluralRule.Read("nplurals=2; plural=(n > 1);");

        Assert.Equal(0, rule.Form(0));
        Assert.Equal(0, rule.Form(1));
        Assert.Equal(1, rule.Form(2));
    }

    [Fact]
    public void ARuleThatCannotBeReadFallsBackToEnglish()
    {
        foreach (var broken in new[]
        {
            null,
            string.Empty,
            "nplurals=2;",
            "plural=(n != 1);",
            "nplurals=2; plural=(n != 1;",
            "nplurals=2; plural=system(\"rm\");",
            "nplurals=2; plural=n = 1;",
            "nplurals=0; plural=0;",
        })
        {
            var rule = PluralRule.Read(broken);
            Assert.Equal(2, rule.Forms);
            Assert.Equal(0, rule.Form(1));
            Assert.Equal(1, rule.Form(7));
        }
    }

    /// <summary>
    /// A header that claims fewer forms than its expression can answer must never index past the
    /// translations somebody actually wrote.
    /// </summary>
    [Fact]
    public void AFormIsNeverOutsideTheLanguagesOwnRange()
    {
        var rule = PluralRule.Read("nplurals=2; plural=(n%10);");

        for (var n = 0; n < 40; n++)
        {
            Assert.InRange(rule.Form(n), 0, 1);
        }
    }

    [Fact]
    public void APluralIsReadBackInTheFormItsLanguageUses()
    {
        var localizer = From("""
            msgid ""
            msgstr "Plural-Forms: nplurals=3; plural=(n==1 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);\n"

            msgid "1 message"
            msgid_plural "{0} messages"
            msgstr[0] "{0} wiadomość"
            msgstr[1] "{0} wiadomości"
            msgstr[2] "{0} wiadomości"
            """);

        Assert.Equal(3, localizer.Plurals.Forms);
        Assert.Equal("{0} wiadomość", localizer.Plural("1 message", "{0} messages", 1));
        Assert.Equal("{0} wiadomości", localizer.Plural("1 message", "{0} messages", 2));
        Assert.Equal("{0} wiadomości", localizer.Plural("1 message", "{0} messages", 5));
    }

    /// <summary>The number is put in by the localizer, in its own culture's digits and grouping.</summary>
    [Fact]
    public void ACountIsFormattedInTheCultureItIsWrittenFor()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mailbox-locale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "de.po"),
            """
            msgid "1 message"
            msgid_plural "{0} messages"
            msgstr[0] "{0} Nachricht"
            msgstr[1] "{0} Nachrichten"
            """,
            Encoding.UTF8);

        var localizer = Localizer.Load(directory, "de-DE");

        Assert.Equal("1 Nachricht", localizer.Counted("1 message", "{0} messages", 1));

        // German groups thousands with a full stop, which is the whole point of formatting here
        // rather than at the call site.
        Assert.Equal("1.234 Nachrichten", localizer.Counted("1 message", "{0} messages", 1234));
    }

    /// <summary>A typo in somebody's translation costs that string, never the surface.</summary>
    [Fact]
    public void AMalformedPlaceholderFallsBackToTheEnglish()
    {
        var localizer = From("""
            msgid "1 message"
            msgid_plural "{0} messages"
            msgstr[0] "{0} wiadomość"
            msgstr[1] "{1} wiadomości"
            """);

        Assert.Equal("5 messages", localizer.Counted("1 message", "{0} messages", 5));
    }

    /// <summary>A regional catalogue only has to carry what it changes.</summary>
    [Fact]
    public void ARegionReadsItsLanguagesCatalogueUnderneathItsOwn()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mailbox-locale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "de.po"),
            "msgid \"New Email\"\nmsgstr \"Neue E-Mail\"\n\nmsgid \"Reply\"\nmsgstr \"Antworten\"\n",
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(directory, "de-AT.po"),
            "msgid \"New Email\"\nmsgstr \"Neues E-Mail\"\n",
            Encoding.UTF8);

        var localizer = Localizer.Load(directory, "de-AT");

        Assert.Equal("Neues E-Mail", localizer.T("New Email"));
        Assert.Equal("Antworten", localizer.T("Reply"));
    }
}
