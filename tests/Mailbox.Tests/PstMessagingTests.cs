using Mailbox.Pst;
using Mailbox.Pst.Messaging;

namespace Mailbox.Tests;

/// <summary>
/// The messaging layer against the real corpus: stores open, folder trees walk, and every
/// message gives up its subject, recipients and attachments. Same bargain as the other corpus
/// suites — skipped without files to read.
/// </summary>
public class PstMessagingTests
{
    private static string[] CorpusFiles()
    {
        var posed = Environment.GetEnvironmentVariable("MAILBOX_PST_CORPUS");
        var directory = posed is { Length: > 0 } ? (Directory.Exists(posed) ? posed : null) : Beside();
        Assert.SkipWhen(directory is null, "Set MAILBOX_PST_CORPUS, or keep files in specs/pst-corpus, to run against real PST files.");
        var files = Directory.GetFiles(directory!, "*.pst");
        Assert.SkipWhen(files.Length == 0, $"No .pst files in {directory}.");
        return files;

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
    public void EveryCorpusStoreOpensAndWalksItsWholeTree()
    {
        var totalMessages = 0;
        var totalEmbedded = 0;

        foreach (var path in CorpusFiles())
        {
            using var file = PstFile.Open(path);
            var store = PstStore.Open(file);

            var folders = 0;
            var messages = 0;
            var embedded = 0;
            Walk(store.MailRoot, 0, ref folders, ref messages, ref embedded);

            // Every real file carries the standard folder set under its mail root; the
            // messages themselves vary, so their floor is across the corpus, not per file.
            Assert.True(folders >= 3, $"{Path.GetFileName(path)} walked only {folders} folders.");

            if (Path.GetFileName(path).Contains("submessage", StringComparison.OrdinalIgnoreCase))
                Assert.True(embedded > 0, "submessage.pst is the embedded-message fixture and yielded none.");

            totalMessages += messages;
            totalEmbedded += embedded;
        }

        Assert.True(totalMessages > 5, $"The corpus yielded only {totalMessages} messages.");
        Assert.True(totalEmbedded > 0, "The corpus yielded no embedded messages at all.");
    }

    private static void Walk(PstFolder folder, int depth, ref int folders, ref int messages, ref int embedded)
    {
        Assert.True(depth < 50, "The folder tree runs deeper than any real mailbox: a cycle.");
        folders++;

        _ = folder.Name;
        foreach (var message in folder.Messages())
        {
            messages++;
            Read(message, 0, ref embedded);
        }

        foreach (var child in folder.Subfolders())
            Walk(child, depth + 1, ref folders, ref messages, ref embedded);
    }

    private static void Read(PstMessage message, int depth, ref int embedded)
    {
        Assert.True(depth < 10, "Messages nest deeper than any real mail: a cycle.");

        // Materialise everything a MIME assembly will want; a wrong offset fails loudly here.
        _ = message.Subject;
        _ = message.MessageClass;
        _ = message.BodyText;
        _ = message.HtmlBody;
        _ = message.TransportHeaders;
        _ = message.Delivered;
        _ = message.Submitted;
        _ = message.SenderName;
        _ = (message.IsRead, message.HasAttachments);

        foreach (var recipient in message.Recipients())
            Assert.True(recipient.Type is >= 0 and <= 3, $"Recipient type {recipient.Type} is outside the line set.");

        foreach (var attachment in message.Attachments())
        {
            _ = attachment.FileName;
            _ = attachment.MimeType;

            if (attachment.EmbeddedMessage is { } inner)
            {
                embedded++;
                Read(inner, depth + 1, ref embedded);
            }
            else
            {
                _ = attachment.Content.Length;
            }
        }
    }

    [Fact]
    public void TheMailRootIsTheStoresStatedSubtreeNotTheRawRoot()
    {
        foreach (var path in CorpusFiles())
        {
            using var file = PstFile.Open(path);
            var store = PstStore.Open(file);

            // The raw root folder sits above the mail subtree and has no display name of its
            // own; the mail root is a real named folder in every corpus file.
            Assert.NotEqual(store.MailRoot.Nid, PstStore.RootFolderNid);

            // And the root still opens, holding the mail root among its children.
            var childNids = store.RootFolder.Subfolders().Select(child => child.Nid).ToList();
            Assert.Contains(store.MailRoot.Nid, childNids);
        }
    }
}
