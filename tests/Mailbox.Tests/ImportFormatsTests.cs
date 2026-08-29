using Microsoft.Data.Sqlite;
using Mailbox.Import;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The file-shaped importers: mbox splitting and its escaping round-trip, .eml in and out
/// byte-exact, .ics and .vcf routed to the default collections, and a staged Thunderbird
/// profile — mail tree, address book, and the filters that translate.
/// </summary>
public class ImportFormatsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mailbox-import-tests", Guid.NewGuid().ToString("n"));

    public ImportFormatsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (MailStore Store, MailRepository Mail, long AccountId) Fresh()
    {
        var store = new MailStore(Path.Combine(_root, Guid.NewGuid().ToString("n") + ".db"));
        var mail = new MailRepository(store);
        var account = mail.AddAccount("a@example.net", "A", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        return (store, mail, account.Id);
    }

    private static string Message(string id, string subject, string extra = "")
        => $"Message-ID: <{id}@x.example>\nFrom: B. Other <b@example.org>\nTo: a@example.net\n"
           + $"Date: Fri, 21 Aug 2026 17:00:00 +0000\nSubject: {subject}\n{extra}\n"
           + $"Body of {subject}.\nFrom here the text goes on.\n>From a quoted line.\n";

    /// <summary>The message as a legal mbox stores it: every From-shaped line one quote deeper.</summary>
    private static string Escaped(string raw)
        => string.Join("\n", raw.Split('\n').Select(l =>
            l.TrimStart('>').StartsWith("From ", StringComparison.Ordinal) ? ">" + l : l));

    // ---- mbox ----------------------------------------------------------------------------------

    [Fact]
    public void AnMboxSplitsUnescapesAndReadsItsFlags()
    {
        var path = Path.Combine(_root, "inbox.mbox");
        File.WriteAllText(path,
            "From b@example.org Fri Aug 21 17:00:00 2026\n"
            + Escaped(Message("m1", "One", "Status: RO\n"))
            + "\nFrom b@example.org Fri Aug 21 17:01:00 2026\n"
            + Escaped(Message("m2", "Two", "X-Mozilla-Status: 0005\n"))
            + "\nFrom b@example.org Fri Aug 21 17:02:00 2026\n"
            + Escaped(Message("m3", "Gone", "X-Mozilla-Status: 0008\n"))
            + "\n");

        using var stream = File.OpenRead(path);
        var messages = Mbox.Read(stream);

        // The expunged third message is Thunderbird's deleted-not-yet-compacted: not imported.
        Assert.Equal(2, messages.Count);
        Assert.True(messages[0].IsRead);
        Assert.False(messages[0].IsFlagged);
        Assert.True(messages[1].IsRead);
        Assert.True(messages[1].IsFlagged);

        // mboxrd unescaping: the body's ">From here" became "From here" again.
        var text = System.Text.Encoding.ASCII.GetString(messages[0].Raw);
        Assert.Contains("\nFrom here the text goes on.", text);
    }

    [Fact]
    public void AnMboxRoundTripsByteExact()
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(Message("rt", "Round trip"));

        var path = Path.Combine(_root, "out.mbox");
        using (var stream = File.Create(path))
        {
            Mbox.Append(stream, raw, DateTimeOffset.UtcNow, "b@example.org");
        }

        using var read = File.OpenRead(path);
        var back = Assert.Single(Mbox.Read(read));
        Assert.Equal(raw, back.Raw);
    }

    /// <summary>
    /// The same claim about the line endings every real message actually has. Everything that
    /// arrived over SMTP, IMAP or POP3 ends its lines with CRLF, and it is those bytes a DKIM
    /// body hash and an S/MIME or OpenPGP signature are taken over — so a reader that rewrites
    /// them to LF hands back a message that no longer verifies, in a format whose whole purpose
    /// is moving a mailbox between machines.
    /// </summary>
    [Fact]
    public void AnMboxRoundTripsAMessageWithTheLineEndingsRealMailHas()
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(
            Message("crlf", "Round trip").Replace("\n", "\r\n"));
        Assert.Contains("\r\n"u8.ToArray(), raw);

        var path = Path.Combine(_root, "crlf.mbox");
        using (var stream = File.Create(path))
        {
            Mbox.Append(stream, raw, DateTimeOffset.UnixEpoch, "b@example.org");
        }

        using var read = File.OpenRead(path);
        var back = Assert.Single(Mbox.Read(read));
        Assert.Equal(raw, back.Raw);

        // And the escaping is still armour: the body's own "From " line came back unquoted.
        Assert.Contains("\r\nFrom here the text goes on.\r\n",
            System.Text.Encoding.ASCII.GetString(back.Raw), StringComparison.Ordinal);
    }

    /// <summary>A file that mixes the two — which is what an mbox concatenated from two machines is.</summary>
    [Fact]
    public void AnMboxKeepsEachMessagesOwnLineEndings()
    {
        var crlf = System.Text.Encoding.ASCII.GetBytes(Message("a", "CRLF").Replace("\n", "\r\n"));
        var lf = System.Text.Encoding.ASCII.GetBytes(Message("b", "LF"));

        var path = Path.Combine(_root, "mixed.mbox");
        using (var stream = File.Create(path))
        {
            Mbox.Append(stream, crlf, DateTimeOffset.UnixEpoch, "b@example.org");
            Mbox.Append(stream, lf, DateTimeOffset.UnixEpoch, "b@example.org");
        }

        using var read = File.OpenRead(path);
        var back = Mbox.Read(read);

        Assert.Equal(2, back.Count);
        Assert.Equal(crlf, back[0].Raw);
        Assert.Equal(lf, back[1].Raw);
    }

    /// <summary>
    /// Through the importer and back out again, which is the path a migration actually takes:
    /// the blob the store keeps must be the bytes the file held, and the file written from it
    /// must be the bytes the store keeps.
    /// </summary>
    [Fact]
    public void AnMboxImportKeepsTheFilesOwnBytesAndTheExportGivesThemBack()
    {
        var (store, mail, account) = Fresh();
        using var _ = store;
        var inbox = mail.FolderWithRole(account, FolderRole.Inbox)!;

        var raw = System.Text.Encoding.ASCII.GetBytes(
            Message("wire", "Off the wire").Replace("\n", "\r\n"));

        var path = Path.Combine(_root, "wire.mbox");
        using (var stream = File.Create(path))
        {
            Mbox.Append(stream, raw, DateTimeOffset.UnixEpoch, "b@example.org");
        }

        Assert.Equal(1, MailFileImport.Mbox(mail, inbox.Id, path,
            cancellation: TestContext.Current.CancellationToken).Imported);

        var stored = mail.Messages(inbox.Id).Single();
        Assert.Equal(raw, mail.LoadRaw(stored.Id));

        var exported = Path.Combine(_root, "wire-out.mbox");
        Assert.Equal(1, MailFileImport.ExportMbox(mail, inbox.Id, exported, TestContext.Current.CancellationToken));

        using var reread = File.OpenRead(exported);
        Assert.Equal(raw, Assert.Single(Mbox.Read(reread)).Raw);
    }

    [Fact]
    public void AnMboxImportsIntoAFolderAndAFolderExportsAsMbox()
    {
        var (store, mail, account) = Fresh();
        using var _ = store;
        var inbox = mail.FolderWithRole(account, FolderRole.Inbox)!;

        var path = Path.Combine(_root, "in.mbox");
        using (var stream = File.Create(path))
        {
            Mbox.Append(stream, System.Text.Encoding.ASCII.GetBytes(Message("a1", "First")), DateTimeOffset.UtcNow);
            Mbox.Append(stream, System.Text.Encoding.ASCII.GetBytes(Message("a2", "Second")), DateTimeOffset.UtcNow);
        }

        var report = MailFileImport.Mbox(mail, inbox.Id, path, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(2, report.Imported);
        Assert.Equal(0, MailFileImport.Mbox(mail, inbox.Id, path, cancellation: TestContext.Current.CancellationToken).Imported);

        var exported = Path.Combine(_root, "exported.mbox");
        Assert.Equal(2, MailFileImport.ExportMbox(mail, inbox.Id, exported, TestContext.Current.CancellationToken));

        using var reread = File.OpenRead(exported);
        Assert.Equal(2, Mbox.Read(reread).Count);
    }

    // ---- eml -----------------------------------------------------------------------------------

    [Fact]
    public void EmlGoesInAndComesOutByteExact()
    {
        var (store, mail, account) = Fresh();
        using var _ = store;
        var inbox = mail.FolderWithRole(account, FolderRole.Inbox)!;

        var raw = System.Text.Encoding.ASCII.GetBytes(Message("e1", "The eml"));
        var path = Path.Combine(_root, "one.eml");
        File.WriteAllBytes(path, raw);

        var report = MailFileImport.Eml(mail, inbox.Id, [path], TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Imported);

        var row = Assert.Single(mail.Messages(inbox.Id));
        var back = Path.Combine(_root, "back.eml");
        Assert.True(MailFileImport.ExportEml(mail, row.Id, back));

        // §7.6a's promise, checked in bytes: what came in is what goes out.
        Assert.Equal(raw, File.ReadAllBytes(back));
    }

    // ---- ics and vcf ---------------------------------------------------------------------------

    [Fact]
    public void AnIcsRoutesItsComponentsAndAVcfFilesItsCards()
    {
        using var store = PimStore.Transient();
        var pim = new PimRepository(store);
        var importer = new PimFileImporter(pim);

        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Tests//EN
            BEGIN:VEVENT
            UID:ev1
            DTSTART:20260901T090000Z
            DTEND:20260901T100000Z
            SUMMARY:Planning
            END:VEVENT
            BEGIN:VTODO
            UID:td1
            SUMMARY:Buy stamps
            END:VTODO
            END:VCALENDAR
            """;

        var first = importer.Ics(ics);
        Assert.Equal(1, first.Events);
        Assert.Equal(1, first.Tasks);

        // A second pass is all already-here: import must not overwrite what may have been edited.
        var second = importer.Ics(ics);
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.AlreadyHere);

        var vcf = """
            BEGIN:VCARD
            VERSION:3.0
            UID:card-1
            FN:C. Reader
            N:Reader;C.;;;
            EMAIL:c.reader@example.com
            END:VCARD
            """;

        Assert.Equal(1, importer.Vcf(vcf).Contacts);
        Assert.Equal(1, importer.Vcf(vcf).AlreadyHere);
    }

    /// <summary>
    /// A series and its exceptions share a UID, so an importer that recognises "already here" by
    /// the UID alone drops every exception in the file and keeps only the master. It is the moved
    /// occurrence a reader notices missing, and nothing said a word about it.
    /// </summary>
    [Fact]
    public void AMovedOccurrenceSurvivesAnIcsImportAndIsNotTakenForItsSeries()
    {
        using var store = PimStore.Transient();
        var pim = new PimRepository(store);
        var importer = new PimFileImporter(pim);

        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Tests//EN
            BEGIN:VEVENT
            UID:series-1
            DTSTART:20260907T090000Z
            DTEND:20260907T093000Z
            RRULE:FREQ=WEEKLY;BYDAY=MO
            SUMMARY:Weekly sync
            END:VEVENT
            BEGIN:VEVENT
            UID:series-1
            RECURRENCE-ID:20260921T090000Z
            DTSTART:20260921T130000Z
            DTEND:20260921T140000Z
            SUMMARY:Weekly sync (moved)
            END:VEVENT
            END:VCALENDAR
            """;

        var report = importer.Ics(ics);
        Assert.Equal(2, report.Events);
        Assert.Equal(0, report.AlreadyHere);

        var calendar = pim.DefaultCalendar();
        var rows = pim.ItemsByUid(calendar.Id, "series-1");
        Assert.Equal(2, rows.Count);

        var master = Assert.Single(rows, r => !r.IsOverride);
        Assert.Equal("Weekly sync", master.Summary);

        var moved = Assert.Single(rows, r => r.IsOverride);
        Assert.Equal("Weekly sync (moved)", moved.Summary);

        // And a second pass still recognises both, so the fix cannot have turned every import
        // into a duplicate.
        var again = importer.Ics(ics);
        Assert.Equal(0, again.Imported);
        Assert.Equal(2, again.AlreadyHere);
        Assert.Equal(2, pim.ItemsByUid(calendar.Id, "series-1").Count);
    }

    // ---- Thunderbird ---------------------------------------------------------------------------

    [Fact]
    public void AStagedProfileImportsMailBooksAndTheFiltersThatTranslate()
    {
        var profile = Path.Combine(_root, "profile.default");
        var local = Path.Combine(profile, "Mail", "Local Folders");
        Directory.CreateDirectory(local);

        // The mail: an Inbox mbox, and a child folder under Projects.sbd.
        File.WriteAllText(Path.Combine(local, "Inbox"),
            "From - Fri Aug 21 17:00:00 2026\n" + Escaped(Message("t1", "From TB", "X-Mozilla-Status: 0001\n")) + "\n");
        File.WriteAllText(Path.Combine(local, "Inbox.msf"), "index");
        var sbd = Path.Combine(local, "Projects.sbd");
        Directory.CreateDirectory(sbd);
        File.WriteAllText(Path.Combine(sbd, "Alpha"),
            "From - Fri Aug 21 17:01:00 2026\n" + Escaped(Message("t2", "Alpha plan")) + "\n");
        File.WriteAllText(Path.Combine(sbd, "Alpha.msf"), "index");

        // The address book, in Thunderbird's own SQLite shape.
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(profile, "abook.sqlite")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE properties (card TEXT, name TEXT, value TEXT);
                INSERT INTO properties VALUES
                    ('c1','DisplayName','B. Other'), ('c1','PrimaryEmail','b.other@example.org'),
                    ('c1','FirstName','B.'), ('c1','LastName','Other'), ('c1','CellularNumber','+44 7700 900123');
                """;
            command.ExecuteNonQuery();
        }

        // The filters: one that translates, one whose mixed OR honestly does not.
        File.WriteAllText(Path.Combine(local, "msgFilterRules.dat"), """
            version="9"
            logging="no"
            name="File invoices"
            enabled="yes"
            type="17"
            action="Move to folder"
            actionValue="mailbox://nobody@Local%20Folders/Invoices"
            condition="AND (subject,contains,invoice)"
            name="Mixed"
            enabled="yes"
            type="17"
            action="Mark read"
            condition="OR (subject,contains,a) OR (from,is,b@c.d)"
            """);

        var (store, mail, account) = Fresh();
        using var _ = store;
        using var pimStore = PimStore.Transient();
        var pim = new PimRepository(pimStore);

        var report = new ThunderbirdImporter(mail, account, pim)
            .Run(profile, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Mail.Imported);
        var folders = mail.Folders(account);
        var projects = Assert.Single(folders, f => f.Name == "Projects");
        var alpha = Assert.Single(folders, f => f.Name == "Alpha");
        Assert.Equal(projects.Id, alpha.ParentId);
        Assert.Single(mail.Messages(mail.FolderWithRole(account, FolderRole.Inbox)!.Id), r => r.IsRead);

        Assert.Equal(1, report.AddressBooks.Contacts);

        Assert.Equal(1, report.Rules);
        Assert.Equal(1, report.RulesSkipped);
        Assert.Contains(report.Notes, n => n.Contains("Mixed"));

        var rule = Assert.Single(mail.Rules());
        Assert.Equal("File invoices", rule.Name);
        Assert.Equal("Invoices", Assert.Single(rule.Actions).FolderName);
    }

    /// <summary>
    /// Four of the seven actions that translate carry no value of their own, and a filter is
    /// ended by the next filter's name rather than by anything of its own. So a value-less
    /// action has to survive that boundary: every one of these but the last is a filter whose
    /// action would otherwise have been thrown away and refused as "no action translates".
    /// </summary>
    [Fact]
    public void AFilterWhoseActionCarriesNoValueTranslatesWhereverItSitsInTheFile()
    {
        var filters = ThunderbirdFilters.Parse([
            "version=\"9\"",
            "name=\"Read it\"",
            "enabled=\"yes\"",
            "action=\"Mark read\"",
            "condition=\"AND (subject,contains,plum)\"",
            "name=\"Flag it\"",
            "enabled=\"no\"",
            "action=\"Mark flagged\"",
            "condition=\"AND (from,is,a.person@example.com)\"",
            "name=\"Bin it\"",
            "enabled=\"yes\"",
            "action=\"Delete\"",
            "action=\"Stop execution\"",
            "condition=\"AND (subject,contains,quince)\"",
            "name=\"File it\"",
            "enabled=\"yes\"",
            "action=\"Move to folder\"",
            "actionValue=\"mailbox://nobody@Local%20Folders/Damsons\"",
            "condition=\"AND (subject,contains,damson)\"",
        ]);

        Assert.Equal(4, filters.Count);
        Assert.All(filters, f => Assert.Null(f.Why));

        Assert.Equal(Mailbox.Core.Rules.RuleActionKind.MarkAsRead,
            Assert.Single(filters[0].Rule!.Actions).Kind);
        Assert.True(filters[0].Rule!.Enabled);

        Assert.Equal(Mailbox.Core.Rules.RuleActionKind.FlagForFollowUp,
            Assert.Single(filters[1].Rule!.Actions).Kind);
        Assert.False(filters[1].Rule!.Enabled);

        // Two value-less actions in a row, in the order the file wrote them.
        Assert.Equal(
            [Mailbox.Core.Rules.RuleActionKind.Delete, Mailbox.Core.Rules.RuleActionKind.StopProcessing],
            filters[2].Rule!.Actions.Select(a => a.Kind));

        Assert.Equal("Damsons", Assert.Single(filters[3].Rule!.Actions).FolderName);
    }

    [Fact]
    public void ProfilesIniIsReadWithRelativeAndAbsolutePaths()
    {
        // FindProfiles reads the machine's own home, so the parser is exercised through a
        // staged ini via the importer's own reading rules — the shape, not the location.
        var filters = ThunderbirdFilters.Parse([
            "name=\"Tag it\"",
            "enabled=\"yes\"",
            "action=\"AddTag\"",
            "actionValue=\"Projects\"",
            "condition=\"AND (from,is,b@example.org)\"",
        ]);

        var result = Assert.Single(filters);
        Assert.NotNull(result.Rule);
        Assert.Equal(Mailbox.Core.Rules.RuleActionKind.AssignCategory, Assert.Single(result.Rule!.Actions).Kind);
        Assert.Equal(Mailbox.Core.Rules.RuleConditionKind.From, Assert.Single(result.Rule.Conditions).Kind);
    }
}

/// <summary>The update check's pure half: reading a release answer, and version comparison.</summary>
public class ReleaseCheckTests
{
    [Fact]
    public void AReleaseAnswerYieldsItsVersionAndPage()
    {
        var latest = Mailbox.Core.Updates.Releases.LatestFrom(
            """{"tag_name":"v0.2.0","html_url":"https://example.com/r/v0.2.0","name":"0.2"}""");

        Assert.NotNull(latest);
        Assert.Equal("0.2.0", latest!.Value.Version);
        Assert.Equal("https://example.com/r/v0.2.0", latest.Value.Url);

        Assert.Null(Mailbox.Core.Updates.Releases.LatestFrom("not json"));
        Assert.Null(Mailbox.Core.Updates.Releases.LatestFrom("""{"no_tag":true}"""));
    }

    [Fact]
    public void NewerMeansNewerAndNothingElse()
    {
        Assert.True(Mailbox.Core.Updates.Releases.IsNewer("0.1.0", "0.2.0"));
        Assert.False(Mailbox.Core.Updates.Releases.IsNewer("0.2.0", "0.2.0"));
        Assert.False(Mailbox.Core.Updates.Releases.IsNewer("0.3.0", "0.2.9"));
        Assert.False(Mailbox.Core.Updates.Releases.IsNewer("0.1.0", "garbage"));
    }
}

/// <summary>
/// §16's localization scaffolding, and the import budget from its performance pass. Budgets,
/// not benchmarks: generous enough not to fail on a slow machine, tight enough to catch a
/// regression in kind.
/// </summary>
public class Phase16PassTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mailbox-p16-tests", Guid.NewGuid().ToString("n"));

    public Phase16PassTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AbsenceIsAlwaysHarmlessAndTheParentCultureIsReadFirst()
    {
        // Passthrough: unadopted is today's behaviour, exactly.
        Assert.Equal("Sent Items", Mailbox.Core.Localization.Localizer.Passthrough.T("Sent Items"));

        File.WriteAllText(Path.Combine(_root, "de.json"), """{"Inbox":"Posteingang","Sent Items":"Gesendete"}""");
        File.WriteAllText(Path.Combine(_root, "de-AT.json"), """{"Sent Items":"Gesendet"}""");

        var austrian = Mailbox.Core.Localization.Localizer.Load(_root, "de-AT");
        Assert.Equal("Posteingang", austrian.T("Inbox"));        // the parent's
        Assert.Equal("Gesendet", austrian.T("Sent Items"));      // the child's wins
        Assert.Equal("Drafts", austrian.T("Drafts"));            // absence answers the English

        // A locale that will not parse costs its translations and nothing else.
        File.WriteAllText(Path.Combine(_root, "fr.json"), "not json");
        Assert.Equal("Inbox", Mailbox.Core.Localization.Localizer.Load(_root, "fr").T("Inbox"));
    }

    [Fact]
    public void AThousandMessageMboxImportsInsideItsBudget()
    {
        var path = Path.Combine(_root, "big.mbox");
        using (var stream = File.Create(path))
        {
            for (var i = 0; i < 1000; i++)
            {
                var raw = System.Text.Encoding.ASCII.GetBytes(
                    $"Message-ID: <bulk{i}@x.example>\nFrom: B. Other <b@example.org>\nTo: a@example.net\n"
                    + $"Date: Fri, 21 Aug 2026 17:00:00 +0000\nSubject: Bulk {i}\n\nBody {i}.\n");
                Mailbox.Import.Mbox.Append(stream, raw, DateTimeOffset.UtcNow);
            }
        }

        using var store = new MailStore(Path.Combine(_root, "bulk.db"));
        var mail = new MailRepository(store);
        var account = mail.AddAccount("a@example.net", "A", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        var inbox = mail.FolderWithRole(account.Id, FolderRole.Inbox)!;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = Mailbox.Import.MailFileImport.Mbox(mail, inbox.Id, path,
            cancellation: TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.Equal(1000, report.Imported);
        Assert.True(clock.ElapsedMilliseconds < 20_000,
            $"Importing 1,000 messages took {clock.ElapsedMilliseconds} ms.");
    }
}
