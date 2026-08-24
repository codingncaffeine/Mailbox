using Mailbox.Import;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The PST importer, pressed against the real corpus and read back out of the store — the
/// counts a migration is checked by, plus the two promises the format forces: an embedded
/// message survives as message/rfc822, and calendars and contacts are left behind by name
/// rather than mangled into mail. Skipped without corpus files, like every PST suite.
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

        Assert.True(report.Imported >= 1, report.Summary);

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

        Assert.True(report.Imported >= 1, report.Summary);

        var stored = mail.Folders(account).Sum(folder => mail.Messages(folder.Id).Count);
        Assert.Equal(report.Imported, stored);
    }

    [Fact]
    public void PimFoldersAreLeftBehindByNameNotMangledIntoMail()
    {
        // dist-list.pst holds only PIM content: an appointment, a contact, a distribution
        // list. None of it may arrive as mail, and the report must say where it stayed.
        var (mail, account) = Fresh();
        var report = new PstImporter(mail, account).Run(CorpusFile("dist-list.pst"),
            cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.Imported);
        var left = Assert.Single(report.Notes, note => note.Contains("Left for the calendar"));
        Assert.Contains("Calendar", left);
        Assert.Contains("Contacts", left);
    }

    [Fact]
    public void ARerunTopsUpInsteadOfDoubling()
    {
        var (mail, account) = Fresh();
        var importer = new PstImporter(mail, account);
        var path = CorpusFile("test_unicode.pst");

        var first = importer.Run(path, cancellation: TestContext.Current.CancellationToken);
        var again = new PstImporter(mail, account).Run(path, cancellation: TestContext.Current.CancellationToken);

        Assert.True(first.Imported >= 1, first.Summary);
        Assert.True(again.AlreadyHere >= again.Imported,
            $"A re-run should skip what is here: {again.Summary}");

        var stored = mail.Folders(account).Sum(folder => mail.Messages(folder.Id).Count);
        Assert.True(stored <= first.Imported + again.Imported, "The re-run doubled messages.");
    }

    [Fact]
    public void TheAnsiLayoutImportsToo()
    {
        var (mail, account) = Fresh();
        var report = new PstImporter(mail, account).Run(CorpusFile("test_ansi.pst"),
            cancellation: TestContext.Current.CancellationToken);

        Assert.True(report.Imported >= 1, report.Summary);
    }
}
