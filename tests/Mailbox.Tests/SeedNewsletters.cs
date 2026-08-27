using System.Text;
using Mailbox.Core.Settings;
using Mailbox.Protocols;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.Tests;

/// <summary>
/// Puts newsletters and ordinary mail into a scratch profile's inbox, so the newsletters dialog
/// can be photographed with something real to find.
/// </summary>
/// <remarks>
/// Invented senders and invented text, as everything seeded here is. Runs only when
/// <c>MAILBOX_SEED_NEWS</c> names a directory.
/// </remarks>
public class SeedNewsletters
{
    [Fact]
    public void SeedNewslettersOnRequest()
    {
        var target = Environment.GetEnvironmentVariable("MAILBOX_SEED_NEWS");
        if (string.IsNullOrWhiteSpace(target)) return;

        var settings = new SettingsStore(Path.Combine(target, "mailbox", "settings.json"));
        var order = new SettingsAccountOrder(settings);
        using var stores = new AccountStores(Path.Combine(target, "accounts"), order);

        var account = stores.All.FirstOrDefault() ?? stores.Add("you@example.com", "You", MailProtocol.Pop3);
        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox)!;

        (string From, string? ListId, string Subject, int Issues)[] posts =
        [
            ("The Weekly Ledger <issues@ledger.example>", "The Weekly Ledger <ledger.example>",
                "What the quarter actually showed", 6),
            ("Field Notes <hello@fieldnotes.example>", null,
                "Notes from a week of walking", 4),
            ("Tuesday Briefing <briefing@dispatch.example>", "Tuesday Briefing <briefing.dispatch.example>",
                "Tuesday: three things worth reading", 9),
            ("The Long Read <post@longread.example>", null,
                "An essay about maps", 2),
        ];

        var day = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

        foreach (var (from, listId, subject, issues) in posts)
        {
            for (var n = 0; n < issues; n++)
            {
                Store(account, inbox, Bulk(from, listId, $"{subject} — {n + 1}", day.AddDays(-n)));
            }
        }

        // And ordinary correspondence, which must not be swept up.
        Store(account, inbox, Plain("A. Person <alice@example.com>", "Thursday still good?", day));
        Store(account, inbox, Plain("B. Colleague <bob@example.net>", "Re: the draft", day.AddDays(-1)));

        Console.WriteLine($"Seeded {posts.Sum(p => p.Issues)} newsletter issues and 2 letters into {target}.");
        Assert.True(account.Mail.Messages(inbox.Id).Count > 0);
    }

    private static void Store(OpenAccount account, Folder inbox, byte[] raw)
    {
        using var stream = new MemoryStream(raw);
        var message = MimeMessage.Load(stream, TestContext.Current.CancellationToken);
        account.Mail.AddMessage(
            inbox.Id, MessageMapper.ToSummary(message, null, raw.Length, message.Date), raw);
    }

    private static byte[] Bulk(string from, string? listId, string subject, DateTimeOffset when)
    {
        var text = new StringBuilder();
        text.Append("From: ").Append(from).Append("\r\n");
        text.Append("To: you@example.com\r\n");
        text.Append("Subject: ").Append(subject).Append("\r\n");
        text.Append("Date: ").Append(when.ToString("r")).Append("\r\n");
        text.Append("Message-Id: <").Append(Guid.NewGuid().ToString("n")).Append("@example.com>\r\n");
        if (listId is not null) text.Append("List-Id: ").Append(listId).Append("\r\n");
        text.Append("List-Unsubscribe: <https://example.com/unsubscribe>\r\n");
        text.Append("Content-Type: text/html; charset=utf-8\r\n\r\n");
        text.Append("<h1>").Append(subject).Append("</h1><p>An issue of a newsletter.</p>\r\n");

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static byte[] Plain(string from, string subject, DateTimeOffset when)
    {
        var text = new StringBuilder();
        text.Append("From: ").Append(from).Append("\r\n");
        text.Append("To: you@example.com\r\n");
        text.Append("Subject: ").Append(subject).Append("\r\n");
        text.Append("Date: ").Append(when.ToString("r")).Append("\r\n");
        text.Append("Message-Id: <").Append(Guid.NewGuid().ToString("n")).Append("@example.com>\r\n");
        text.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
        text.Append("A short letter from a person.\r\n");

        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
