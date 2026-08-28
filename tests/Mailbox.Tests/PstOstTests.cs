using Mailbox.Import;
using Mailbox.Pst;
using Mailbox.Pst.Messaging;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The 4K OST layout, best-effort by stated scope, against a real 2013-era file: 4096-byte
/// pages, blocks in 512-byte steps with the longer footer, zlib-compressed block data, and the
/// OST's own way of naming its mail subtree. The deep NDB and LTP sweeps in the corpus suites
/// already walk this file; what is here is the OST-specific behaviour.
/// </summary>
public class PstOstTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mailbox-ost-tests", Guid.NewGuid().ToString("n"));

    public PstOstTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string OstFile()
    {
        var posed = Environment.GetEnvironmentVariable("MAILBOX_PST_CORPUS");
        var directory = posed is { Length: > 0 } && Directory.Exists(posed) ? posed : Beside();
        Assert.SkipWhen(directory is null, "Set MAILBOX_PST_CORPUS, or keep files in specs/pst-corpus, to run against real files.");
        var path = Directory.GetFiles(directory!, "*.ost").FirstOrDefault();
        Assert.SkipWhen(path is null, $"No .ost file in {directory}.");
        return path!;

        static string? Beside()
        {
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
    }

    [Fact]
    public void TheMailRootIsTheIpmSubtreeNotTheRawOstRoot()
    {
        using var file = PstFile.Open(OstFile());
        var store = PstStore.Open(file);

        // An OST's store carries no subtree pointer; the mail lives under the folder MAPI
        // itself names IPM_SUBTREE, and the empty public-folder twin must not win.
        Assert.Equal("IPM_SUBTREE", store.MailRoot.Name);
        Assert.NotEqual(store.MailRoot.Nid, PstStore.RootFolderNid);
        Assert.True(store.MailRoot.Subfolders().Count() >= 3);
    }

    [Fact]
    public void TheWholeMessagingTreeWalksAndItsMailReads()
    {
        using var file = PstFile.Open(OstFile());
        var store = PstStore.Open(file);
        var folders = 0;
        var messages = 0;
        var withHeaders = 0;

        Walk(store.MailRoot);
        void Walk(PstFolder folder)
        {
            folders++;
            foreach (var message in folder.Messages())
            {
                messages++;
                _ = message.Subject;
                _ = message.BodyText;
                _ = message.Recipients().ToList();
                if (message.TransportHeaders.Length > 0) withHeaders++;
            }

            foreach (var child in folder.Subfolders()) Walk(child);
        }

        // This walk crosses zlib-compressed blocks — the file stores some property streams
        // deflated, and a wrong inflation fails the reads rather than any assertion here.
        Assert.True(folders >= 10, $"walked only {folders} folders");
        Assert.True(messages >= 3, $"read only {messages} messages");
        Assert.True(withHeaders >= 3, "the mail lost its transport headers");
    }

    [Fact]
    public void AnOstImportsItsMailLikeAnyOtherFile()
    {
        var mailStore = new MailStore(Path.Combine(_root, "m.db"));
        var mail = new MailRepository(mailStore);
        var account = mail.AddAccount("a@example.net", "A", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);

        var importer = new PstImporter(mail, account.Id);
        var first = importer.Run(OstFile(), cancellation: TestContext.Current.CancellationToken);
        var again = new PstImporter(mail, account.Id).Run(OstFile(), cancellation: TestContext.Current.CancellationToken);

        Assert.True(first.Mail.Imported >= 3, first.Summary);
        Assert.Equal(0, again.Mail.Imported);

        var stored = mail.Folders(account.Id).Sum(folder => mail.Messages(folder.Id).Count);
        Assert.Equal(first.Mail.Imported, stored);
    }
}
