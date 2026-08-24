using Mailbox.Import;
using Mailbox.Pst.Msg;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// The .msg reader and its import, against real files written by other people's software: the
/// compound file opens, the properties read, the recipients and attachments come out whole, and
/// the import lands one deduplicated message in the Inbox. Skipped without corpus files, like
/// every corpus suite — these live in specs/msg-corpus.
/// </summary>
public class MsgImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mailbox-msg-import-tests", Guid.NewGuid().ToString("n"));

    public MsgImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string? Corpus()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_MSG_CORPUS") is { Length: > 0 } posed)
            return Directory.Exists(posed) ? posed : null;

        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "Mailbox.slnx")))
            {
                var corpus = Path.Combine(at.FullName, "specs", "msg-corpus");
                return Directory.Exists(corpus) ? corpus : null;
            }
        }

        return null;
    }

    private static string CorpusFile(string name)
    {
        var corpus = Corpus();
        Assert.SkipWhen(corpus is null, "Set MAILBOX_MSG_CORPUS, or keep files in specs/msg-corpus, to run against real .msg files.");
        var path = Path.Combine(corpus!, name);
        Assert.SkipWhen(!File.Exists(path), $"{name} is not in the corpus.");
        return path;
    }

    [Fact]
    public void EveryCorpusMsgOpensAndReadsWhole()
    {
        var corpus = Corpus();
        Assert.SkipWhen(corpus is null, "Set MAILBOX_MSG_CORPUS, or keep files in specs/msg-corpus, to run against real .msg files.");

        foreach (var path in Directory.GetFiles(corpus!, "*.msg"))
        {
            var msg = MsgFile.Open(path);
            Assert.StartsWith("IPM.Note", msg.Message.MessageClass);
            Assert.NotEmpty(msg.Message.Subject);
            Assert.NotEmpty(msg.Message.Recipients().ToList());
            _ = msg.Message.BodyText;
            _ = msg.Message.HtmlBody;
            _ = msg.Message.Delivered;
            _ = msg.Message.Submitted;
        }
    }

    [Fact]
    public void RecipientsComeOutTrimmedAndOnTheirLines()
    {
        var msg = MsgFile.Open(CorpusFile("multi-to.msg"));
        var recipients = msg.Message.Recipients().ToList();

        // This particular writer pads every name and address with spaces; the reader hands
        // them back clean, or the assembled mail would carry "bob@example.com   ".
        Assert.Equal(5, recipients.Count);
        Assert.All(recipients, r => Assert.Equal(r.Address, r.Address.Trim()));
        Assert.Contains(recipients, r => r.Address == "alice@example.com" && r.Type == 1);
        Assert.Contains(recipients, r => r.Address == "dave@example.com" && r.Type == 2);
        Assert.Equal("bob@example.com", msg.Message.SenderAddress);
    }

    [Fact]
    public void AttachmentsCarryTheirWholePayload()
    {
        var msg = MsgFile.Open(CorpusFile("unicode.msg"));
        var attachments = msg.Message.Attachments().ToList();

        Assert.Equal(2, attachments.Count);
        Assert.All(attachments, a =>
        {
            Assert.Equal("image/tiff", a.MimeType);
            Assert.EndsWith(".tif", a.FileName);
            Assert.True(a.Content.Length > 900_000, $"{a.FileName} came out at {a.Content.Length} bytes.");

            // TIFF magic: II*\0 — the payload is the picture, not a mangled copy of it.
            Assert.Equal(0x49, a.Content[0]);
            Assert.Equal(0x49, a.Content[1]);
        });
    }

    [Fact]
    public void AMsgImportsIntoTheInboxOnceAndReadsBack()
    {
        var store = new MailStore(Path.Combine(_root, "m.db"));
        var mail = new MailRepository(store);
        var account = mail.AddAccount("a@example.net", "A", MailProtocol.Pop3);
        mail.CreateStandardFolders(account.Id);
        var path = CorpusFile("unicode.msg");

        var first = MsgImport.Run(path, mail, account.Id, null, null);
        var again = MsgImport.Run(path, mail, account.Id, null, null);

        Assert.Equal("1 message into the Inbox.", first);
        Assert.Equal("already in the Inbox.", again);

        var inbox = mail.FolderWithRole(account.Id, FolderRole.Inbox)!;
        var summary = Assert.Single(mail.Messages(inbox.Id));
        Assert.Equal("Test for TIF files", summary.Subject);

        // The wire form carries both pictures whole.
        var parsed = MimeKit.MimeMessage.Load(new MemoryStream(mail.LoadRaw(summary.Id)!), TestContext.Current.CancellationToken);
        var parts = parsed.BodyParts.OfType<MimeKit.MimePart>()
            .Where(part => part.ContentType.MimeType == "image/tiff")
            .ToList();
        Assert.Equal(2, parts.Count);
    }
}
