using Mailbox.Core.Settings;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// Writes a populated multi-account directory for looking at by hand. Skipped in an ordinary
/// run; set MAILBOX_SEED to a directory to produce one.
/// </summary>
public class SeedHarness
{
    [Fact]
    public void SeedOnRequest()
    {
        var target = Environment.GetEnvironmentVariable("MAILBOX_SEED");
        if (string.IsNullOrWhiteSpace(target)) return;

        var order = new SettingsAccountOrder(
            new SettingsStore(Path.Combine(target, "settings.json")));

        using var stores = new AccountStores(Path.Combine(target, "accounts"), order);

        Seed(stores, "you@example.com", ("Alice Chen", "Re: Q3 numbers", false),
            ("Build Notifications", "mailbox/main — build passed", false),
            ("Dana Whitfield", "Lunch Thursday?", true));

        Seed(stores, "work@example.net", ("Priya Raman", "Font substitution question", false),
            ("Sam Reyes", "Draft agenda attached", true));
    }

    private static void Seed(AccountStores stores, string address,
        params (string From, string Subject, bool Read)[] messages)
    {
        var account = stores.Add(address, address, MailProtocol.Pop3);
        var inbox = account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox)!;
        var when = DateTimeOffset.UtcNow;

        foreach (var (from, subject, read) in messages)
        {
            account.Mail.AddMessage(inbox.Id, new MessageSummary(
                0, 0, Guid.NewGuid().ToString("n"), null, from,
                from.Replace(' ', '.').ToLowerInvariant() + "@example.com",
                subject, "Preview of the message body.", when, when, 2048, read, false, false));

            when = when.AddMinutes(-37);
        }
    }
}
