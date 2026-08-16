using Mailbox.Contacts;

namespace Mailbox.Tests;

/// <summary>
/// vCard in and out: the three versions a real address book arrives in, the fields the reference's
/// card shows, and a distribution list that survives a round trip through either version.
/// </summary>
/// <remarks>
/// Written against text rather than against the library's own model, because what has to be right
/// is what other clients read — a test that asserts on our own parse of our own output proves the
/// two agree with each other and nothing else.
/// </remarks>
public class VCardTests
{
    private static readonly string ThreeOh = """
        BEGIN:VCARD
        VERSION:3.0
        UID:person-1@example.com
        FN:A. Person
        N:Person;A.;Q.;Dr.;PhD
        ORG:Example Ltd.;Research
        TITLE:Principal Engineer
        EMAIL;TYPE=INTERNET,PREF:a.person@example.com
        EMAIL;TYPE=INTERNET:a.person@example.net
        TEL;TYPE=WORK,VOICE:+44 20 7946 0000
        TEL;TYPE=CELL:+44 7700 900000
        TEL;TYPE=WORK,FAX:+44 20 7946 0001
        ADR;TYPE=WORK:;;1 Example Street;London;;EC1A 1AA;United Kingdom
        URL:https://example.com/a.person
        NOTE:Prefers e-mail.
        BDAY:1980-04-01
        CATEGORIES:Colleagues,Research
        REV:2026-08-16T09:12:00Z
        END:VCARD
        """.ReplaceLineEndings("\r\n");

    /// <summary>
    /// A 2.1 card as they really arrive: no UID, quoted-printable, a charset parameter, and the
    /// TYPE parameters written bare rather than as TYPE=.
    /// </summary>
    private static readonly string TwoOne = """
        BEGIN:VCARD
        VERSION:2.1
        N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Beispiel;J=C3=BCrgen;;;
        FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:J=C3=BCrgen Beispiel
        ORG;CHARSET=UTF-8:Beispiel GmbH
        TEL;WORK;VOICE:+49 30 901820
        TEL;HOME:+49 30 901821
        EMAIL;INTERNET:juergen@example.de
        ADR;HOME;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:;;Musterstra=C3=9Fe 1;Berlin;;10115;Deutschland
        END:VCARD
        """.ReplaceLineEndings("\r\n");

    [Fact]
    public void AThreeOhCardReadsIntoEveryFieldTheCardShows()
    {
        var contact = VCardCodec.ParseOne(ThreeOh);

        Assert.Equal("person-1@example.com", contact.Uid);
        Assert.Equal("A. Person", contact.DisplayName);
        Assert.Equal("Person", contact.LastName);
        Assert.Equal("A.", contact.FirstName);
        Assert.Equal("Dr.", contact.Prefix);
        Assert.Equal("PhD", contact.Suffix);
        Assert.Equal("Example Ltd.", contact.Company);
        Assert.Equal("Research", contact.Department);
        Assert.Equal("Principal Engineer", contact.JobTitle);
        Assert.Equal("Prefers e-mail.", contact.Notes);
        Assert.Equal(new DateOnly(1980, 4, 1), contact.Birthday);
        Assert.Equal(["Colleagues", "Research"], contact.Categories);
        Assert.Equal("https://example.com/a.person", Assert.Single(contact.Urls));
        Assert.False(contact.IsGroup);
    }

    [Fact]
    public void TheAddressesAndNumbersKeepTheLabelsTheCardPutsOnThem()
    {
        var contact = VCardCodec.ParseOne(ThreeOh);

        Assert.Equal(["a.person@example.com", "a.person@example.net"], contact.Emails.Select(e => e.Address));

        // A fax at work is a business fax, not a business number: the TYPE parameters arrive in
        // every combination and the order they are read in is the answer.
        Assert.Contains(contact.Phones, p => p.Kind == PhoneKind.Business && p.Number.EndsWith("0000", StringComparison.Ordinal));
        Assert.Contains(contact.Phones, p => p.Kind == PhoneKind.Mobile);
        Assert.Contains(contact.Phones, p => p.Kind == PhoneKind.BusinessFax);

        var address = Assert.Single(contact.Addresses);
        Assert.Equal(AddressKind.Business, address.Kind);
        Assert.Equal("1 Example Street", address.Street);
        Assert.Equal("London", address.City);
        Assert.Equal("EC1A 1AA", address.PostalCode);
        Assert.Equal("United Kingdom", address.Country);
    }

    /// <summary>
    /// 2.1 is the version that still turns up in exports from old software: quoted-printable,
    /// a charset per property, and bare TYPE words.
    /// </summary>
    [Fact]
    public void ATwoOneCardReadsThroughItsEncodingAndCharset()
    {
        var contact = VCardCodec.ParseOne(TwoOne);

        Assert.Equal("Jürgen Beispiel", contact.DisplayName);
        Assert.Equal("Jürgen", contact.FirstName);
        Assert.Equal("Beispiel", contact.LastName);
        Assert.Equal("Beispiel GmbH", contact.Company);
        Assert.Contains(contact.Phones, p => p.Kind == PhoneKind.Home);
        Assert.Contains(contact.Phones, p => p.Kind == PhoneKind.Business);
        Assert.Equal("juergen@example.de", contact.PrimaryEmail);

        var address = Assert.Single(contact.Addresses);
        Assert.Equal(AddressKind.Home, address.Kind);
        Assert.Equal("Musterstraße 1", address.Street);
        Assert.Equal("Berlin", address.City);

        // No UID in the file, so one is invented rather than left empty: everything downstream
        // is keyed by it.
        Assert.NotEmpty(contact.Uid);
    }

    [Fact]
    public void AContactSurvivesARoundTripInBothVersions()
    {
        var original = VCardCodec.ParseOne(ThreeOh);

        foreach (var version in new[] { VCardVersion.V3, VCardVersion.V4 })
        {
            var text = VCardCodec.Serialize(original, version);
            var back = VCardCodec.ParseOne(text);

            Assert.Equal(original.Uid, back.Uid);
            Assert.Equal(original.DisplayName, back.DisplayName);
            Assert.Equal(original.LastName, back.LastName);
            Assert.Equal(original.FirstName, back.FirstName);
            Assert.Equal(original.Company, back.Company);
            Assert.Equal(original.Department, back.Department);
            Assert.Equal(original.JobTitle, back.JobTitle);
            Assert.Equal(original.Emails.Select(e => e.Address), back.Emails.Select(e => e.Address));
            Assert.Equal(original.Phones.Select(p => p.Kind), back.Phones.Select(p => p.Kind));
            Assert.Equal(original.Addresses, back.Addresses);
            Assert.Equal(original.Birthday, back.Birthday);
            Assert.Equal(original.Categories, back.Categories);
            Assert.Equal(original.Notes, back.Notes);
        }
    }

    [Fact]
    public void AVersionIsWrittenAsItWasAskedFor()
    {
        var contact = VCardCodec.ParseOne(ThreeOh);

        Assert.Contains("VERSION:3.0", VCardCodec.Serialize(contact, VCardVersion.V3), StringComparison.Ordinal);
        Assert.Contains("VERSION:4.0", VCardCodec.Serialize(contact, VCardVersion.V4), StringComparison.Ordinal);
    }

    // ---- Distribution lists ---------------------------------------------------------------------

    private static Contact Group() => new()
    {
        Uid = "team@example.com",
        DisplayName = "Research team",
        IsGroup = true,
        Members =
        [
            new GroupMember(Uid: "person-1@example.com"),
            new GroupMember("b.person@example.com", "B. Person"),
        ],
    };

    /// <summary>
    /// 4.0 states a group with KIND and MEMBER; 3.0 has neither, so a group written without
    /// Apple's extension is a card with a name and nobody in it.
    /// </summary>
    [Fact]
    public void AGroupIsWrittenTheWayItsVersionStatesOne()
    {
        var four = VCardCodec.Serialize(Group(), VCardVersion.V4);
        Assert.Contains("KIND:group", four, StringComparison.Ordinal);
        Assert.Contains("urn:uuid:person-1@example.com", four, StringComparison.Ordinal);

        var three = VCardCodec.Serialize(Group(), VCardVersion.V3);
        Assert.Contains("X-ADDRESSBOOKSERVER-KIND:group", three, StringComparison.Ordinal);
        Assert.Contains("X-ADDRESSBOOKSERVER-MEMBER", three, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\nKIND:group", three, StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupComesBackWithEveryoneInItInBothVersions()
    {
        foreach (var version in new[] { VCardVersion.V3, VCardVersion.V4 })
        {
            var back = VCardCodec.ParseOne(VCardCodec.Serialize(Group(), version));

            Assert.True(back.IsGroup);
            Assert.Equal(2, back.Members.Count);
            Assert.Contains(back.Members, m => m.Uid == "person-1@example.com");
            Assert.Contains(back.Members, m => m.Address == "b.person@example.com" && m.Name == "B. Person");
        }
    }

    /// <summary>Apple's own group file, as an address book really writes one.</summary>
    [Fact]
    public void AnApplesGroupCardIsReadAsAGroup()
    {
        var apple = """
            BEGIN:VCARD
            VERSION:3.0
            UID:group-1
            FN:Research team
            N:Research team;;;;
            X-ADDRESSBOOKSERVER-KIND:group
            X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:person-1
            X-ADDRESSBOOKSERVER-MEMBER:mailto:b.person@example.com
            END:VCARD
            """.ReplaceLineEndings("\r\n");

        var group = VCardCodec.ParseOne(apple);

        Assert.True(group.IsGroup);
        Assert.Equal(2, group.Members.Count);
        Assert.Contains(group.Members, m => m.Uid == "person-1");
        Assert.Contains(group.Members, m => m.Address == "b.person@example.com");
    }

    // ---- Photographs -----------------------------------------------------------------------------

    [Fact]
    public void APhotographGoesOutAndComesBackAsTheSameBytes()
    {
        var pixels = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        var contact = new Contact
        {
            Uid = "photo@example.com",
            DisplayName = "A. Person",
            Photo = new ContactPhoto(pixels, "image/png"),
        };

        var back = VCardCodec.ParseOne(VCardCodec.Serialize(contact, VCardVersion.V4));

        Assert.NotNull(back.Photo);
        Assert.Equal(pixels, back.Photo!.Bytes);
    }

    // ---- Filing and the index --------------------------------------------------------------------

    [Fact]
    public void AContactFilesTheWayTheOrderAsks()
    {
        var contact = new Contact
        {
            Uid = "file@example.com",
            DisplayName = "A. Person",
            FirstName = "A.",
            LastName = "Person",
            Company = "Example Ltd.",
        };

        Assert.Equal("Person, A.", contact.FiledAs());
        Assert.Equal("A. Person", contact.FiledAs(FileAsOrder.FirstLast));
        Assert.Equal("Example Ltd.", contact.FiledAs(FileAsOrder.Company));
        Assert.Equal("Person, A. (Example Ltd.)", contact.FiledAs(FileAsOrder.LastFirstCompany));

        // A stored File As is a decision somebody made and outlives the order.
        Assert.Equal("The Person", (contact with { FileAs = "The Person" }).FiledAs());
    }

    [Fact]
    public void TheIndexPutsAnythingThatIsNotALetterUnderTheDigits()
    {
        var person = new Contact { Uid = "1", LastName = "Person", FirstName = "A." };
        var company = new Contact { Uid = "2", DisplayName = "3M Supplies", Company = "3M Supplies" };

        Assert.Equal('P', person.IndexLetter());
        Assert.Equal('#', company.IndexLetter());
    }

    [Fact]
    public void AContactWithNoNameAtAllStillReadsAsSomething()
    {
        Assert.Equal("Example Ltd.", new Contact { Uid = "1", Company = "Example Ltd." }.Named());
        Assert.Equal("a@example.com", new Contact { Uid = "2", Emails = [new ContactEmail("a@example.com")] }.Named());
        Assert.Equal("(no name)", new Contact { Uid = "3" }.Named());
    }

    [Fact]
    public void TextThatIsNotAVCardIsRefusedRatherThanGuessedAt()
    {
        Assert.Throws<FormatException>(() => VCardCodec.Parse("Dear sir, I am not a vCard."));
    }

    [Fact]
    public void AFileOfSeveralContactsReadsAsSeveral()
    {
        var many = VCardCodec.SerializeMany(
            [
                new Contact { Uid = "1", DisplayName = "A. Person" },
                new Contact { Uid = "2", DisplayName = "B. Person" },
            ],
            VCardVersion.V3);

        var back = VCardCodec.Parse(many);

        Assert.Equal(2, back.Count);
        Assert.Equal(["A. Person", "B. Person"], back.Select(c => c.DisplayName));
    }
}
