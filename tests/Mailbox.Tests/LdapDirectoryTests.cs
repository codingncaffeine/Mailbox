using Mailbox.Contacts;
using Mailbox.Contacts.Directory;
using Mailbox.Core.Settings;

namespace Mailbox.Tests;

/// <summary>
/// The directory address book's parts that can be checked with no directory to talk to: the
/// filter, the mapping from a directory entry to a card, and what is stored about a directory.
/// </summary>
/// <remarks>
/// The connection itself is proven against a real OpenLDAP rather than here — a mock LDAP server
/// would prove that the mock works. What is here is the half that is ours, and it is the half
/// where the bugs are: an unescaped filter is a search that silently widens or a server that
/// refuses, and a mismapped attribute is a contact card with somebody's job title in the wrong
/// field.
/// </remarks>
public class LdapDirectoryTests
{
    // ---- The filter --------------------------------------------------------------------------

    [Fact]
    public void TypingAsksEveryNameAttributeAndTheAddress()
    {
        var filter = LdapFilter.ForTyping("smith");

        Assert.NotNull(filter);
        foreach (var attribute in LdapFilter.SearchedAttributes)
        {
            Assert.Contains($"({attribute}=smith*)", filter, StringComparison.Ordinal);
        }

        // A person, not a printer or a meeting room.
        Assert.Contains("objectClass=inetOrgPerson", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingTypedAsksNobody()
    {
        Assert.Null(LdapFilter.ForTyping(null));
        Assert.Null(LdapFilter.ForTyping(string.Empty));
        Assert.Null(LdapFilter.ForTyping("   "));
    }

    /// <summary>
    /// RFC 4515 §3. A name with a bracket in it builds a filter the server rejects; one with a
    /// star in it silently widens the search.
    /// </summary>
    [Fact]
    public void TheFourSpecialCharactersAreEscaped()
    {
        Assert.Equal(@"\28", LdapFilter.Escape("("));
        Assert.Equal(@"\29", LdapFilter.Escape(")"));
        Assert.Equal(@"\2a", LdapFilter.Escape("*"));
        Assert.Equal(@"\5c", LdapFilter.Escape(@"\"));
        Assert.Equal(@"O\28Brien\29", LdapFilter.Escape("O(Brien)"));

        // And nothing else: an accented name travels as itself.
        Assert.Equal("Åsa Østergård", LdapFilter.Escape("Åsa Østergård"));
    }

    /// <summary>
    /// The injection: brackets closing the filter early and opening a clause of somebody else's.
    /// They are escaped whether or not the rest of what was typed is treated as a pattern, which
    /// is what makes the star's exemption safe.
    /// </summary>
    [Fact]
    public void ATypedBracketCannotBreakOutOfTheFilter()
    {
        // Everything typed stays inside the one clause it was put in — brackets and all.
        var withStar = LdapFilter.ForTyping("a)(objectClass=*");
        Assert.NotNull(withStar);
        Assert.Contains(@"(cn=a\29\28objectClass=*)", withStar, StringComparison.Ordinal);

        var withoutStar = LdapFilter.ForTyping("a)(uid=admin");
        Assert.NotNull(withoutStar);
        Assert.Contains(@"(cn=a\29\28uid=admin*)", withoutStar, StringComparison.Ordinal);

        // And the clause that decides this is a person is still the filter's own, not one the
        // typing replaced.
        Assert.StartsWith("(&(|(objectClass=inetOrgPerson)", withoutStar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Somebody who types a star means it. Everything else in what they typed is still escaped.
    /// </summary>
    [Fact]
    public void AStarSomebodyTypedIsTheirOwnPattern()
    {
        var filter = LdapFilter.ForTyping("*smith*");

        Assert.NotNull(filter);
        Assert.Contains("(cn=*smith*)", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("(cn=*smith**)", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void AddressingAMessageInsistsOnAnAddress()
    {
        Assert.Contains("(mail=*)", LdapFilter.ForTyping("smith", onlyAddressable: true)!, StringComparison.Ordinal);
        Assert.DoesNotContain("(mail=*)", LdapFilter.ForTyping("smith")!, StringComparison.Ordinal);
    }

    // ---- The mapping -------------------------------------------------------------------------

    private static Dictionary<string, IReadOnlyList<string>> Entry(params (string Name, string Value)[] values)
    {
        var entry = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            entry[name] = entry.TryGetValue(name, out var held) ? [.. held, value] : [value];
        }

        return entry;
    }

    [Fact]
    public void AnEntryBecomesACard()
    {
        var card = LdapEntries.ToContact(
            "cn=A. Person,ou=people,dc=example,dc=org",
            Entry(
                ("cn", "A. Person"), ("displayName", "A. Person"), ("givenName", "A."), ("sn", "Person"),
                ("mail", "a.person@example.org"), ("mail", "a.person@example.net"),
                ("telephoneNumber", "+44 20 7946 0001"), ("mobile", "+44 7700 900001"),
                ("title", "Release Manager"), ("o", "Example Ltd"), ("ou", "Engineering")));

        Assert.NotNull(card);

        // The distinguished name is the identity: unique on that server, and never confusable
        // with a card in the local book.
        Assert.Equal("cn=A. Person,ou=people,dc=example,dc=org", card.Uid);
        Assert.Equal("A. Person", card.DisplayName);
        Assert.Equal("A.", card.FirstName);
        Assert.Equal("Person", card.LastName);
        Assert.Equal("Release Manager", card.JobTitle);
        Assert.Equal("Example Ltd", card.Company);
        Assert.Equal("Engineering", card.Department);
        Assert.Equal(["a.person@example.org", "a.person@example.net"], card.Emails.Select(e => e.Address));
        Assert.Equal([PhoneKind.Business, PhoneKind.Mobile], card.Phones.Select(p => p.Kind));
    }

    /// <summary>Active Directory writes <c>company</c> and <c>department</c>; OpenLDAP writes <c>o</c> and <c>ou</c>.</summary>
    [Fact]
    public void EitherSpellingOfTheCompanyIsRead()
    {
        var ad = LdapEntries.ToContact("cn=x", Entry(("cn", "X"), ("company", "Contoso"), ("department", "Sales")));
        Assert.Equal("Contoso", ad!.Company);
        Assert.Equal("Sales", ad.Department);
    }

    [Fact]
    public void AnEntryWithNoNameAndNoAddressIsNotAPerson()
    {
        Assert.Null(LdapEntries.ToContact("cn=nothing", Entry(("objectClass", "person"))));
        Assert.Null(LdapEntries.ToContact(string.Empty, Entry(("cn", "Somebody"))));
    }

    /// <summary>An entry with an address and no name at all is still somebody who can be written to.</summary>
    [Fact]
    public void AnAddressAloneIsEnough()
    {
        var card = LdapEntries.ToContact("cn=list", Entry(("mail", "team@example.org")));

        Assert.NotNull(card);
        Assert.Equal("team@example.org", card.Emails.Single().Address);
    }

    [Fact]
    public void EmptyAttributeValuesAreDropped()
    {
        var card = LdapEntries.ToContact("cn=x", Entry(("cn", "X"), ("title", "  "), ("mail", "x@example.org")));

        Assert.NotNull(card);
        Assert.Equal(string.Empty, card.JobTitle);
    }

    // ---- What is stored ----------------------------------------------------------------------

    private static Directories Fresh() => new(SettingsStore.Transient());

    [Fact]
    public void ADirectoryIsStoredAndReadBack()
    {
        var settings = SettingsStore.Transient();
        var directories = new Directories(settings);

        directories.Save(new LdapDirectory
        {
            Name = "Example Ltd",
            Host = "ldap.example.org",
            Port = 636,
            BaseDn = "ou=people,dc=example,dc=org",
            BindDn = "cn=reader,dc=example,dc=org",
            Scope = DirectoryScope.OneLevel,
            MaxResults = 25,
        });

        var read = new Directories(settings).All().Single();
        Assert.Equal("Example Ltd", read.Name);
        Assert.Equal(636, read.Port);
        Assert.Equal(DirectoryScope.OneLevel, read.Scope);
        Assert.Equal(25, read.MaxResults);
        Assert.True(read.UseTls);
        Assert.Equal("ldaps://ldap.example.org:636/ou=people,dc=example,dc=org", read.Where());
    }

    /// <summary>
    /// The password is never one of the stored fields: a settings file that is copied or backed
    /// up must carry no credential.
    /// </summary>
    [Fact]
    public void ThePasswordIsNotStoredBesideTheRest()
    {
        var settings = SettingsStore.Transient();
        new Directories(settings).Save(new LdapDirectory
        {
            Name = "Example",
            Host = "ldap.example.org",
            BaseDn = "dc=example,dc=org",
            BindDn = "cn=reader,dc=example,dc=org",
        });

        var written = settings.GetString(Directories.Key);
        Assert.Contains("cn=reader", written, StringComparison.Ordinal);
        Assert.DoesNotContain("password", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChangingOneKeepsItsPlaceAndCanRenameIt()
    {
        var directories = Fresh();
        directories.Save(new LdapDirectory { Name = "One", Host = "a", BaseDn = "dc=a" });
        directories.Save(new LdapDirectory { Name = "Two", Host = "b", BaseDn = "dc=b" });

        directories.Save(new LdapDirectory { Name = "Uno", Host = "a", BaseDn = "dc=a" }, replacing: "One");

        Assert.Equal(["Uno", "Two"], directories.All().Select(d => d.Name));
    }

    [Fact]
    public void ADirectoryWithNoServerIsNeverSearched()
    {
        var directories = Fresh();
        directories.Save(new LdapDirectory { Name = "Half", Host = "a" });
        directories.Save(new LdapDirectory { Name = "Off", Host = "b", BaseDn = "dc=b", IsEnabled = false });
        directories.Save(new LdapDirectory { Name = "Whole", Host = "c", BaseDn = "dc=c" });

        Assert.Equal(["Whole"], directories.Searchable().Select(d => d.Name));
    }

    [Fact]
    public void TwoDirectoriesCannotShareAName()
    {
        var directories = Fresh();
        directories.Save(new LdapDirectory { Name = "Example", Host = "a", BaseDn = "dc=a" });

        Assert.True(directories.IsTaken("example"));
        Assert.False(directories.IsTaken("Example", except: "Example"));
        Assert.False(directories.IsTaken("Other"));
    }

    [Fact]
    public void RemovingOneForgetsIt()
    {
        var directories = Fresh();
        directories.Save(new LdapDirectory { Name = "Example", Host = "a", BaseDn = "dc=a" });

        Assert.True(directories.Remove("example"));
        Assert.Empty(directories.All());
        Assert.False(directories.Remove("example"));
    }

    /// <summary>One unreadable entry costs that directory, not the application starting.</summary>
    [Fact]
    public void MalformedStoredTextCostsOneDirectory()
    {
        var settings = SettingsStore.Transient();
        settings.Set(Directories.Key, """[{"host":"a"},{"name":"Good","host":"b","baseDn":"dc=b"}]""");

        Assert.Equal(["Good"], new Directories(settings).All().Select(d => d.Name));

        settings.Set(Directories.Key, "not json at all");
        Assert.Empty(new Directories(settings).All());
    }

    /// <summary>
    /// Two directories on one machine must not overwrite each other's password in the keyring.
    /// </summary>
    [Fact]
    public void ThePasswordKeyNamesTheUserAndTheServer()
    {
        var one = new LdapDirectory { Host = "ldap.a.org", Port = 389, BindDn = "cn=reader" };
        var two = new LdapDirectory { Host = "ldap.b.org", Port = 389, BindDn = "cn=reader" };

        Assert.NotEqual(one.PasswordKey, two.PasswordKey);
    }

    // ---- The suggestions cache ---------------------------------------------------------------

    /// <summary>
    /// Nothing may block the typing, and a prefix too short to be worth a round trip is not one.
    /// </summary>
    [Fact]
    public void AShortPrefixAsksNothing()
    {
        var asked = 0;
        var cache = new DirectorySuggestions(
            _ => { asked++; return Task.FromResult(new DirectoryResult([])); },
            work => work());

        Assert.Empty(cache.Offer("ab", () => { }));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task AnAnswerArrivesLaterAndIsThenHeld()
    {
        var found = new DirectoryResult(
        [
            new Contact { Uid = "cn=A", DisplayName = "A. Person", Emails = [new ContactEmail("a@example.org")] },
        ]);

        var asked = 0;
        var landed = new TaskCompletionSource();
        var cache = new DirectorySuggestions(
            _ => { Interlocked.Increment(ref asked); return Task.FromResult(found); },
            work => work());

        // Nothing on the keystroke, and the callback when it lands.
        Assert.Empty(cache.Offer("person", landed.SetResult));
        await landed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var offered = cache.Offer("person", () => { });
        Assert.Equal("a@example.org", offered.Single().Address);
        Assert.Equal("Directory", offered.Single().Detail);

        // Held, so the same prefix is not asked twice.
        Assert.Equal(1, asked);
    }

    /// <summary>A search that threw is not allowed to reach the typing.</summary>
    [Fact]
    public async Task AFailedSearchOffersNothingAndDoesNotThrow()
    {
        var cache = new DirectorySuggestions(
            _ => Task.FromException<DirectoryResult>(new InvalidOperationException("the server fell over")),
            work => work());

        Assert.Empty(cache.Offer("person", () => { }));

        // Given a moment, it has recorded why and still offers nothing.
        for (var i = 0; i < 50 && cache.LastRefusal.Length == 0; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.Contains("fell over", cache.LastRefusal, StringComparison.Ordinal);
        Assert.Empty(cache.Offer("person", () => { }));
    }
}
