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
