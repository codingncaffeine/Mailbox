using Mailbox.Core.Settings;
using Mailbox.Store;
using MimeKit;
using MimeKit.Utils;

namespace Mailbox.Tests;

/// <summary>
/// A search corpus with known answers: six messages whose facts are chosen so that every
/// operator of the search grammar has exactly one expected result set.
/// </summary>
/// <remarks>
/// The ordinary seed (<see cref="SeedHarness"/>) is shaped for the reading pane and the list, so
/// what it happens to hold answers no question about search: "did <c>importance:high</c> work?"
/// cannot be told from "is there any high-importance mail?". This one is built the other way
/// round — each message differs from the others in exactly the facts an operator tests, so a
/// wrong count names the operator that is wrong.
/// <para>
/// Two accounts and a sub-folder, so the three scopes give three different answers to one word:
/// This Folder 4, Current Mailbox 5, All Mailboxes 6.
/// </para>
/// <para>
/// Dated against <c>MAILBOX_TODAY</c> exactly as the ordinary seed is, so the date operators can
/// be asked a question with a fixed answer. Every address and name is invented.
/// </para>
/// </remarks>
public class SeedSearchCorpus
{
    /// <summary>The day the corpus is dated against — <c>MAILBOX_TODAY</c>, or the real day.</summary>
    private static DateOnly Today()
        => Environment.GetEnvironmentVariable("MAILBOX_TODAY") is { Length: > 0 } pinned
           && DateOnly.TryParseExact(pinned, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)
            ? day
            : DateOnly.FromDateTime(DateTime.Today);

    private static DateTimeOffset At(DateOnly day, int hour)
    {
        var wall = day.ToDateTime(new TimeOnly(hour, 0));
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }

    [Fact]
    public void SeedSearchOnRequest()
    {
        var target = Environment.GetEnvironmentVariable("MAILBOX_SEED_SEARCH");
        if (string.IsNullOrWhiteSpace(target)) return;

        var today = Today();
        var order = new SettingsAccountOrder(new SettingsStore(Path.Combine(target, "settings.json")));
        using var stores = new AccountStores(Path.Combine(target, "accounts"), order);

        // ---- Account A: the corpus's own account, Inbox and a sub-folder ------------------------
        // work@example.net rather than you@example.com because the folder pane opens on the
        // alphabetically first account, and the corpus has to be the one that is open for
        // "Current Mailbox" to mean it.
        var a = stores.Add("work@example.net", "work@example.net", MailProtocol.Pop3);
        var inbox = a.Mail.FolderWithRole(a.Account.Id, FolderRole.Inbox)!;
        var projects = a.Mail.AddFolder(a.Account.Id, "Projects", FolderRole.None, inbox.Id);

        // alpha — read, no attachment, normal, small, today, sent today.
        File(a, inbox.Id,
            Message("Alice Chen", "alice@example.com", ["work@example.net"], [],
                "Corpus alpha budget", "corpusword alphaword", At(today, 9)),
            At(today, 9), read: true);

        // beta — unread, attachment, high, yesterday, Cc to me, To a group, flagged due today,
        // and the one carrying a category.
        var beta = File(a, inbox.Id,
            Attached(
                Message("Dana Okafor", "dana@example.org", ["team@example.com"], ["work@example.net"],
                    "Corpus beta invoice", "corpusword betaword", At(today.AddDays(-1), 11),
                    MessageImportance.High),
                "invoice.pdf", "application/pdf", 40_000),
            At(today.AddDays(-1), 11));

        // gamma — unread, a big attachment, low importance, received nine days ago but sent
        // thirty, which is what tells received: and sent: apart.
        File(a, inbox.Id,
            Attached(
                Message("Sam Reyes", "sam@shop.example", ["work@example.net"], [],
                    "Corpus gamma report", "corpusword gammaword", At(today.AddDays(-30), 15),
                    MessageImportance.Low),
                "report.bin", "application/octet-stream", 1_200_000),
            At(today.AddDays(-9), 15));

        // delta — read, no attachment, normal, last month, overdue follow-up, a second category.
        var delta = File(a, inbox.Id,
            Message("B. Other", "b.other@example.net", ["work@example.net"], [],
                "Corpus delta notes", "corpusword deltaword", At(today.AddDays(-41), 8)),
            At(today.AddDays(-40), 8), read: true);

        // epsilon — the sub-folder's only message, so This Folder and Current Mailbox differ.
        File(a, projects.Id,
            Message("Priya Raman", "priya@example.net", ["work@example.net"], [],
                "Corpus epsilon plan", "corpusword epsilonword", At(today.AddDays(-2), 12)),
            At(today.AddDays(-2), 12));

        if (beta is { } flagged) a.Mail.SetFollowUp([flagged], At(today, 17));
        if (delta is { } overdue) a.Mail.SetFollowUp([overdue], At(today.AddDays(-5), 17));

        Categorise(a, beta, "Blue Category");
        Categorise(a, delta, "Green Category");

        // ---- Account B: a second account, so All Mailboxes is a third answer -------------------
        var b = stores.Add("you@example.com", "you@example.com", MailProtocol.Pop3);
        var other = b.Mail.FolderWithRole(b.Account.Id, FolderRole.Inbox)!;
        File(b, other.Id,
            Message("C. Reader", "c.reader@example.org", ["you@example.com"], [],
                "Corpus zeta memo", "corpusword zetaword", At(today.AddDays(-1), 16)),
            At(today.AddDays(-1), 16));
    }

    private static void Categorise(OpenAccount account, long? messageId, string name)
    {
        if (messageId is not { } id) return;
        var category = account.Mail.Categories().FirstOrDefault(c => c.Name == name);
        if (category is not null) account.Mail.Assign([id], category.Id);
    }

    private static long? File(OpenAccount account, long folderId, MimeMessage message,
        DateTimeOffset received, bool read = false)
    {
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();

        var summary = Mailbox.Protocols.MessageMapper.ToSummary(
            message, Guid.NewGuid().ToString("n"), raw.Length, received, read);

        return account.Mail.AddMessage(folderId, summary, raw);
    }

    private static MimeMessage Message(
        string name, string address, string[] to, string[] cc, string subject, string body,
        DateTimeOffset sent, MessageImportance importance = MessageImportance.Normal)
    {
        var message = new MimeMessage { Subject = subject, Date = sent };
        message.From.Add(new MailboxAddress(name, address));
        foreach (var one in to) message.To.Add(new MailboxAddress(string.Empty, one));
        foreach (var one in cc) message.Cc.Add(new MailboxAddress(string.Empty, one));
        message.MessageId = MimeUtils.GenerateMessageId("example.com");
        message.Importance = importance;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private static MimeMessage Attached(MimeMessage message, string fileName, string type, int size)
    {
        var text = message.Body!;
        message.Body = new Multipart("mixed")
        {
            text,
            new MimePart(ContentType.Parse(type))
            {
                FileName = fileName,
                Content = new MimeContent(new MemoryStream(new byte[size])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };

        return message;
    }
}
