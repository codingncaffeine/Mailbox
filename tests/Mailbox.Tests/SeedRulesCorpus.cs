using Mailbox.Core.Rules;
using Mailbox.Core.Settings;
using Mailbox.Store;
using MimeKit;
using MimeKit.Utils;

namespace Mailbox.Tests;

/// <summary>
/// A store that already has rules in it, and mail for them to act on — so Run Rules Now can be
/// pressed for real and what it did read back out of the store.
/// </summary>
/// <remarks>
/// Nothing in the ordinary seed has a rule, and the rules dialog writes nothing until OK, so
/// there was no way to reach Run Rules Now with anything in its list at all: every capture of it
/// was a capture of an empty box. Four rules, each shaped so its effect on the store is a
/// different column — a move, a read flag, a follow-up — plus one that is switched off and one
/// that belongs to the server, since "a rule that does not run" is the half that cannot be told
/// from a rule that ran and did nothing.
/// <para>
/// The account is <c>work@example.net</c> because the folder pane opens on the alphabetically
/// first account and the rules have to be the open account's. Every address is invented.
/// </para>
/// </remarks>
public class SeedRulesCorpus
{
    private static DateTimeOffset When()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_TODAY") is not { Length: > 0 } pinned
            || !DateOnly.TryParseExact(pinned, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
        {
            return DateTimeOffset.Now;
        }

        var wall = day.ToDateTime(new TimeOnly(14, 30));
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }

    [Fact]
    public void SeedRulesOnRequest()
    {
        var target = Environment.GetEnvironmentVariable("MAILBOX_SEED_RULES");
        if (string.IsNullOrWhiteSpace(target)) return;

        var now = When();
        var order = new SettingsAccountOrder(new SettingsStore(Path.Combine(target, "settings.json")));
        using var stores = new AccountStores(Path.Combine(target, "accounts"), order);

        var account = stores.Add("work@example.net", "work@example.net", MailProtocol.Imap);
        var mail = account.Mail;
        var id = account.Account.Id;

        var inbox = mail.FolderWithRole(id, FolderRole.Inbox)!;
        mail.MapFolder(inbox.Id, "INBOX", "Inbox", null);
        var receipts = mail.AddFolder(id, "Receipts", FolderRole.None, inbox.Id, "INBOX/Receipts");

        // Deleted Items on the server too, because "delete it" is the one action whose
        // compilability turns on that folder having a server name — without it every rule that
        // deletes reports that it has to stay on this computer, and the wizard's positive
        // server-side answer cannot be reached at all.
        if (mail.FolderWithRole(id, FolderRole.Deleted) is { } deleted)
        {
            mail.MapFolder(deleted.Id, "Trash", "Deleted Items", null);
        }

        // Four messages, one per rule, so a rule that fires on the wrong one is visible.
        File(mail, inbox.Id, Plain("Orders", "orders@shop.example", "Shop receipt 1001",
            "Thanks for your order."), now);
        File(mail, inbox.Id, Plain("Priya Raman", "priya@example.net", "Team update",
            "Nothing to report this week."), now.AddMinutes(-20));
        File(mail, inbox.Id, Attached(Plain("Sam Reyes", "sam@example.net", "Big attachment",
            "Slides attached."), "slides.pdf", 80_000), now.AddMinutes(-40));
        File(mail, inbox.Id, Plain("The Weekly", "news@example.org", "Server rule target",
            "This one belongs to the server."), now.AddMinutes(-60));

        mail.AddRule(new MailRule
        {
            Name = "Receipts",
            Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["@shop.example"] }],
            Actions =
            [
                new RuleAction(RuleActionKind.MoveToFolder) { FolderId = receipts.Id, FolderName = receipts.Name },
                new RuleAction(RuleActionKind.StopProcessing),
            ],
        }, now);

        mail.AddRule(new MailRule
        {
            Name = "Team notes are read",
            Conditions = [new RuleCondition(RuleConditionKind.SubjectContains) { Values = ["Team"] }],
            Actions = [new RuleAction(RuleActionKind.MarkAsRead)],
        }, now);

        mail.AddRule(new MailRule
        {
            Name = "Flag the big ones",
            Conditions = [new RuleCondition(RuleConditionKind.SizeBetween) { Min = 50 }],
            Actions = [new RuleAction(RuleActionKind.FlagForFollowUp) { Level = 0 }],
        }, now);

        // Switched off, and it would empty the folder if it ran: the loudest possible proof that
        // a rule nobody ticked does not run.
        mail.AddRule(new MailRule
        {
            Name = "Off — would delete everything",
            Enabled = false,
            Actions = [new RuleAction(RuleActionKind.PermanentlyDelete)],
        }, now);

        // The server's, so the client/server split has something to split.
        mail.AddRule(new MailRule
        {
            Name = "On the server",
            ServerSide = true,
            Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["@example.org"] }],
            Actions = [new RuleAction(RuleActionKind.MarkAsRead)],
        }, now);

        // A second account, so the rules dialog's account picker has something to pick.
        stores.Add("you@example.com", "you@example.com", MailProtocol.Pop3);
    }

    private static void File(MailRepository mail, long folderId, MimeMessage message, DateTimeOffset when)
    {
        message.Date = when;
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();
        mail.AddMessage(folderId, Mailbox.Protocols.MessageMapper.ToSummary(
            message, Guid.NewGuid().ToString("n"), raw.Length, when), raw);
    }

    private static MimeMessage Plain(string name, string address, string subject, string body)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress(name, address));
        message.To.Add(new MailboxAddress("You", "work@example.net"));
        message.MessageId = MimeUtils.GenerateMessageId("example.net");
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private static MimeMessage Attached(MimeMessage message, string fileName, int size)
    {
        var text = message.Body!;
        message.Body = new Multipart("mixed")
        {
            text,
            new MimePart(ContentType.Parse("application/pdf"))
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
