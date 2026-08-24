using Mailbox.Contacts;
using Mailbox.Import;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.Tests;

/// <summary>
/// The PST importer, pressed against the real corpus and read back out of the stores — mail by
/// count and wire form, and the PIM half item by item: the appointment in the calendar, the
/// contact with its email, the distribution list as a group. Skipped without corpus files, like
/// every PST suite.
/// </summary>
public class PstImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mailbox-pst-import-tests", Guid.NewGuid().ToString("n"));

    public PstImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string? Corpus()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_PST_CORPUS") is { Length: > 0 } posed)
            return Directory.Exists(posed) ? posed : null;

        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "Mailbox.slnx")))
            {
                var corpus = Path.Combine(at.FullName, "specs", "pst-corpus");
                return Directory.Exists(corpus) ? corpus : null;
            }
        }

        return null;
    }

    private static string CorpusFile(string name)
    {
        var corpus = Corpus();
        Assert.SkipWhen(corpus is null, "Set MAILBOX_PST_CORPUS, or keep files in specs/pst-corpus, to run against real PST files.");
        var path = Path.Combine(corpus!, name);
        Assert.SkipWhen(!File.Exists(path), $"{name} is not in the corpus.");
        return path;
    }

    private (MailRepository Mail, long AccountId) Fresh()
    {
        var store = new MailStore(Path.Combine(_root, Guid.NewGuid().ToString("n") + ".db"));
        var mail = new MailRepository(store);
        var account = mail.AddAccount("a@example.net", "A", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        return (mail, account.Id);
    }

    [Fact]
    public void AnEmbeddedMessageSurvivesTheTripAsMessageRfc822()
    {
        var (mail, account) = Fresh();
        var report = new PstImporter(mail, account).Run(CorpusFile("submessage.pst"),
            cancellation: TestContext.Current.CancellationToken);

        Assert.True(report.Mail.Imported >= 1, report.Summary);

        // Read the wire form back out of the store: the claim is the bytes, not the report.
        var carried = mail.Folders(account)
            .SelectMany(folder => mail.Messages(folder.Id).Select(m => mail.LoadRaw(m.Id)))
            .Where(raw => raw is not null)
            .Select(raw => MimeKit.MimeMessage.Load(new MemoryStream(raw!)))
            .ToList();

        Assert.Contains(carried, message =>
            message.BodyParts.OfType<MimeKit.MessagePart>().Any());
    }

    [Fact]
    public void ARealPstImportsAndReadsBackByCount()
    {
        var (mail, account) = Fresh();
        var report = new PstImporter(mail, account).Run(CorpusFile("sample1.pst"),
            cancellation: TestContext.Current.CancellationToken);

        Assert.True(report.Mail.Imported >= 1, report.Summary);

        var stored = mail.Folders(account).Sum(folder => mail.Messages(folder.Id).Count);
        Assert.Equal(report.Mail.Imported, stored);
    }

    [Fact]
    public void WithoutAPimStoreThePimContentIsLeftBehindByName()
    {
        var (mail, account) = Fresh();
        var report = new PstImporter(mail, account).Run(CorpusFile("dist-list.pst"),
            cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.Mail.Imported);
        Assert.Equal(0, report.Pim.Imported);
        var left = Assert.Single(report.Mail.Notes, note => note.Contains("Left for the calendar"));
        Assert.Contains("Calendar", left);
        Assert.Contains("Contacts", left);
    }

    [Fact]
    public void ThePimHalfImportsAndReadsBackItemByItem()
    {
        var (mail, account) = Fresh();
        using var pimStore = PimStore.Transient();
        var pim = new PimRepository(pimStore);
        var report = new PstImporter(mail, account, pim).Run(CorpusFile("dist-list.pst"),
            cancellation: TestContext.Current.CancellationToken);

        // dist-list.pst holds one contact, one distribution list, no mail — and one appointment
        // that is secretly a whole series: weekly on Tuesdays at 8:00 Pacific, one occurrence
        // deleted and two moved, so it imports as a master and two overrides. The counts are
        // the claim and the stores are the proof, moved times included.
        Assert.Equal(0, report.Mail.Imported);
        Assert.Equal(3, report.Pim.Events);
        Assert.Equal(2, report.Pim.Contacts);

        var calendar = Assert.Single(pim.Collections(CollectionKind.Events));
        var rows = pim.Items(calendar.Id);
        Assert.Equal(3, rows.Count);

        var master = Assert.Single(rows, row => !row.IsOverride);
        Assert.Equal("Test appointment", master.Summary);
        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", master.Rrule);
        Assert.Equal(new DateTimeOffset(2016, 8, 2, 15, 0, 0, TimeSpan.Zero), master.StartsUtc);

        // The moved occurrences: 8:00 became 9:00 and 10:00 Pacific — 16:00 and 17:00 UTC —
        // which is the recurrence blob's local clock recovered from the master's own offset.
        var moved = rows.Where(row => row.IsOverride).Select(row => row.StartsUtc).OrderBy(t => t).ToList();
        Assert.Equal(
            [new DateTimeOffset(2016, 8, 23, 16, 0, 0, TimeSpan.Zero), new DateTimeOffset(2016, 8, 30, 17, 0, 0, TimeSpan.Zero)],
            moved);

        var book = new ContactBook(pim);
        var people = book.People().ToList();
        var person = Assert.Single(people, p => !p.Contact.IsGroup);
        Assert.Equal("contact name 1", person.Contact.DisplayName);
        Assert.NotEmpty(person.Contact.Emails);

        // A list row carries its columns; the members live in the card itself, read on open.
        var groupRow = Assert.Single(people, p => p.Contact.IsGroup);
        var group = book.Full(groupRow.Id);
        Assert.NotNull(group);
        Assert.Equal("test dist list", group.DisplayName);
        Assert.NotEmpty(group.Members);
        Assert.All(group.Members, member => Assert.Contains('@', member.Address));
    }

    [Fact]
    public void ThePimHalfTopsUpInsteadOfDoublingToo()
    {
        var (mail, account) = Fresh();
        using var pimStore = PimStore.Transient();
        var pim = new PimRepository(pimStore);
        var path = CorpusFile("dist-list.pst");

        var first = new PstImporter(mail, account, pim).Run(path, cancellation: TestContext.Current.CancellationToken);
        var again = new PstImporter(mail, account, pim).Run(path, cancellation: TestContext.Current.CancellationToken);

        Assert.True(first.Pim.Imported >= 3, first.Summary);
        Assert.Equal(0, again.Pim.Imported);
        Assert.Equal(first.Pim.Imported, again.Pim.AlreadyHere);
    }

    [Fact]
    public void ARerunTopsUpInsteadOfDoubling()
    {
        var (mail, account) = Fresh();
        var path = CorpusFile("test_unicode.pst");

        var first = new PstImporter(mail, account).Run(path, cancellation: TestContext.Current.CancellationToken);
        var again = new PstImporter(mail, account).Run(path, cancellation: TestContext.Current.CancellationToken);

        Assert.True(first.Mail.Imported >= 1, first.Summary);
        Assert.True(again.Mail.AlreadyHere >= again.Mail.Imported,
            $"A re-run should skip what is here: {again.Summary}");

        var stored = mail.Folders(account).Sum(folder => mail.Messages(folder.Id).Count);
        Assert.True(stored <= first.Mail.Imported + again.Mail.Imported, "The re-run doubled messages.");
    }

    [Fact]
    public void TheAnsiLayoutImportsToo()
    {
        var (mail, account) = Fresh();
        var report = new PstImporter(mail, account).Run(CorpusFile("test_ansi.pst"),
            cancellation: TestContext.Current.CancellationToken);

        Assert.True(report.Mail.Imported >= 1, report.Summary);
    }
}
