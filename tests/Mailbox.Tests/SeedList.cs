using Mailbox.Core.Settings;
using Mailbox.Store;

namespace Mailbox.Tests;

/// <summary>
/// A store shaped for the message list rather than for the reading pane: enough variety that
/// every arrangement groups into more than one bucket, every date band is occupied, and the
/// conversation view has both a thread that holds together and one that does not.
/// </summary>
/// <remarks>
/// The ordinary seed is four messages in one inbox, all from the same afternoon. That is the
/// right size for photographing the reading pane — every message in it is real MIME shaped to
/// reach a bar that only certain mail reaches — and the wrong size for auditing the list, where
/// the claims are about grouping, ordering, banding and threading and a corpus of four cannot
/// tell a working bucket from an empty one.
/// <para>
/// Rows here are written as summaries rather than as MIME. The list draws from the summary and
/// never opens the message, so carrying a body per row would cost the seed its speed and prove
/// nothing extra — and the sizes wanted for the size bands run past five megabytes, which is a
/// number to declare rather than to allocate.
/// </para>
/// <para>
/// Dated against <see cref="Mailbox.Core.PosedClock"/>, so the seed and a pinned run agree about
/// what "Today" means. Every name, address and subject is invented.
/// </para>
/// </remarks>
public class SeedList
{
    /// <summary>How many rows the big folder gets, for the recycling checks.</summary>
    /// <remarks>
    /// Thousands rather than tens of thousands by default: the recycling claim is about the list
    /// reusing containers, which a few thousand rows already forces, and a seed a session has to
    /// wait five minutes for is a seed nobody runs. <c>MAILBOX_SEED_LIST_BIG</c> raises it for the
    /// endurance pass.
    /// </remarks>
    private static int BigFolderRows =>
        int.TryParse(Environment.GetEnvironmentVariable("MAILBOX_SEED_LIST_BIG"), out var many) && many > 0
            ? many
            : 5_000;

    [Fact]
    public void SeedListOnRequest()
    {
        var target = Environment.GetEnvironmentVariable("MAILBOX_SEED_LIST");
        if (string.IsNullOrWhiteSpace(target)) return;

        var order = new SettingsAccountOrder(
            new SettingsStore(Path.Combine(target, "settings.json")));

        using var stores = new AccountStores(Path.Combine(target, "accounts"), order);
        var account = stores.Add("you@example.com", "you@example.com", MailProtocol.Pop3);
        var mail = account.Mail;
        var accountId = account.Account.Id;

        var inbox = mail.FolderWithRole(accountId, FolderRole.Inbox)!;
        var threads = mail.AddFolder(accountId, "Threads");
        var big = mail.AddFolder(accountId, "Big");

        SeedBands(mail, inbox.Id);
        SeedThreads(mail, threads.Id);
        SeedBig(mail, big.Id);
    }

    /// <summary>
    /// The inbox: one message per date band, per size band, per importance, and enough senders
    /// and subjects that From and Subject group into more than one letter.
    /// </summary>
    /// <remarks>
    /// Laid out so that each arrangement has something to say. The date bands are the reason for
    /// the odd day offsets — 8, 15 and 23 days back land in Last Week, Two Weeks Ago and Three
    /// Weeks Ago respectively, and the engine counts weeks up to four before it starts naming
    /// months, so 40 days back is Last Month and 200 is a month of its own.
    /// </remarks>
    private static void SeedBands(MailRepository mail, long folderId)
    {
        var now = Mailbox.Core.PosedClock.Now;
        var uid = 0;

        // What a flag has to be written with afterwards. AddMessage's insert names neither
        // follow_up_due nor follow_up_start — a summary carrying them is accepted and the dates
        // are dropped — so the flag dates go on through the setter the Custom Flag dialog uses,
        // which is the path that writes all four of its columns.
        var flags = new List<(long Id, DateTimeOffset? Start, DateTimeOffset? Due)>();
        var categorised = new List<(long Id, string Category)>();

        void Add(
            string fromName,
            string fromAddress,
            string subject,
            int daysAgo,
            int hour,
            long size,
            bool unread = false,
            bool flagged = false,
            bool attachment = false,
            int importance = 1,
            int? dueInDays = null,
            int? startInDays = null,
            string? to = null,
            string? category = null)
        {
            var when = now.AddDays(-daysAgo).Date.AddHours(hour);
            var received = new DateTimeOffset(when, now.Offset);

            var id = mail.AddMessage(folderId, new MessageSummary(
                Id: 0,
                FolderId: folderId,
                ServerUid: $"band-{uid++}",
                MessageId: $"<band-{uid}@example.com>",
                FromName: fromName,
                FromAddress: fromAddress,
                Subject: subject,
                Preview: "Invented text, so the preview line has something to shorten.",
                Sent: received.AddMinutes(-2),
                Received: received,
                SizeBytes: size,
                IsRead: !unread,
                IsFlagged: flagged,
                HasAttachment: attachment)
            {
                Importance = importance,
                To = [to ?? "you@example.com"],
                BodyText = "Invented text, so the search index has something in it.",
            });

            if (id is not { } written) return;
            if (flagged)
            {
                flags.Add((
                    written,
                    startInDays is { } start ? now.AddDays(start) : null,
                    dueInDays is { } due ? now.AddDays(due) : null));
            }

            if (category is { Length: > 0 }) categorised.Add((written, category));
        }

        // ---- The date bands, one message each ---------------------------------------------
        // Today, Yesterday, the named days of the past week, then the counted weeks, then the
        // months. Every band the engine can produce except "Later", which is below.
        Add("Alice Chen", "alice@example.com", "Q3 numbers", 0, 9, 4_200, unread: true);
        Add("Bruno Sala", "bruno@example.net", "Roof quote", 0, 13, 18_000, attachment: true);
        Add("Cara Devlin", "cara@example.org", "Thursday", 1, 11, 6_400);
        Add("Dan Iwu", "dan@example.net", "Parking", 3, 15, 2_100, unread: true);
        Add("Eve Marsh", "eve@example.com", "Kitchen rota", 5, 8, 3_300);
        Add("Femi Ojo", "femi@example.org", "Last week's notes", 8, 16, 12_000);
        Add("Gita Rao", "gita@example.net", "A fortnight back", 15, 10, 45_000, attachment: true);
        Add("Hana Kim", "hana@example.com", "Three weeks now", 23, 14, 90_000);
        Add("Ivan Petrov", "ivan@example.net", "Last month's invoice", 40, 9, 260_000, attachment: true);
        Add("Jo Baxter", "jo@example.org", "Ages ago", 200, 12, 7_800);
        Add("Kit Aldridge", "kit@example.com", "Last year", 400, 17, 5_100);

        // "Later" — a message dated ahead of now, which the band exists for and which nothing in
        // the ordinary seed produces.
        Add("Lena Fischer", "lena@example.net", "Dated ahead", -2, 10, 3_000);

        // ---- The size bands ---------------------------------------------------------------
        // Declared rather than allocated: the list reads the column, and five megabytes of
        // padding would cost the seed a great deal to prove a number the store already holds.
        Add("Mo Haddad", "mo@example.com", "Tiny attachment", 2, 9, 9_000);
        Add("Nia Okonkwo", "nia@example.net", "Small attachment", 2, 10, 20_000, attachment: true);
        Add("Omar Zaki", "omar@example.org", "Medium attachment", 2, 11, 60_000, attachment: true);
        Add("Pia Lindqvist", "pia@example.com", "Large attachment", 2, 12, 300_000, attachment: true);
        Add("Quinn Farrow", "quinn@example.net", "Very large attachment", 2, 13, 2_500_000, attachment: true);
        Add("Rosa Iglesias", "rosa@example.org", "Enormous attachment", 2, 14, 9_000_000, attachment: true);

        // ---- Importance, flags and the flag dates ------------------------------------------
        // Flag: Start Date and Flag: Due Date are arrangements of their own, and a row with no
        // flag sorts to the far end in either direction rather than first in one of them — so
        // there have to be rows of each kind for that to be visible.
        Add("Sam Reyes", "sam@example.net", "Urgent: the roof again", 4, 9, 5_000, importance: 2, unread: true);
        Add("Tomas Vidal", "tomas@example.com", "Low priority", 4, 10, 4_000, importance: 0);
        Add("Ursula Bright", "ursula@example.org", "Flagged, due today", 6, 11, 6_000,
            flagged: true, dueInDays: 0, startInDays: -1);
        Add("Vik Sandhu", "vik@example.net", "Flagged, overdue", 9, 12, 7_000,
            flagged: true, dueInDays: -4, startInDays: -6);
        Add("Wei Zhang", "wei@example.com", "Flagged, no date", 11, 13, 8_000, flagged: true);

        // ---- Recipients, for the To arrangement ---------------------------------------------
        Add("Xan Doherty", "xan@example.org", "Sent to the team", 7, 15, 4_500, to: "team@example.com");
        Add("Yara Nasser", "yara@example.net", "Sent to the list", 7, 16, 4_600, to: "list@example.com");

        // A second message from a sender who already has one, so From groups by more than one
        // row and the tiebreak inside a group can be read.
        Add("Alice Chen", "alice@example.com", "Q3 numbers, again", 2, 8, 4_300);

        // ---- Categories ---------------------------------------------------------------------
        // Two categories over three messages, one of them carrying both, so the Categories
        // arrangement has more than one bucket to make and a row has a strip of more than one
        // colour to draw.
        Add("Zoe Achebe", "zoe@example.com", "Blue business", 3, 9, 4_700, category: "Blue Category");
        Add("Ana Bello", "ana@example.net", "Green business", 3, 10, 4_800, category: "Green Category");
        Add("Ben Costa", "ben@example.org", "Both at once", 3, 11, 4_900, category: "Blue Category");

        foreach (var (id, start, due) in flags) mail.SetCustomFollowUp([id], "Follow up", start, due, null);

        long CategoryNamed(string name, string token)
            => mail.Categories().FirstOrDefault(c => c.Name == name)?.Id ?? mail.AddCategory(name, token).Id;

        var blue = CategoryNamed("Blue Category", "category.blue");
        var green = CategoryNamed("Green Category", "category.green");

        foreach (var (id, name) in categorised)
        {
            mail.Assign([id], name == "Blue Category" ? blue : green);
        }

        // The last one carries both, so a row with a strip of more than one colour exists and
        // the Categories arrangement has a message that could belong to two of its buckets.
        if (categorised.Count > 0) mail.Assign([categorised[^1].Id], green);
    }

    /// <summary>
    /// The conversation corpus: one thread that threads, one that should and does not, and a
    /// message that is not a conversation at all.
    /// </summary>
    /// <remarks>
    /// The engine threads on the normalised subject and nothing else — <c>message_id</c> and
    /// <c>in_reply_to</c> are stored beside it and read by nobody, which the repository says of
    /// itself. That makes two shapes worth having in a corpus and impossible to tell apart
    /// without one: a reply whose subject somebody edited, which threads as its own conversation
    /// though its headers say otherwise, and two unrelated messages that happen to share a
    /// subject, which thread together though nothing connects them.
    /// </remarks>
    private static void SeedThreads(MailRepository mail, long folderId)
    {
        var now = Mailbox.Core.PosedClock.Now;
        var uid = 0;

        void Add(string fromName, string fromAddress, string subject, int hoursAgo, string? inReplyTo = null)
        {
            var received = now.AddHours(-hoursAgo);
            mail.AddMessage(folderId, new MessageSummary(
                Id: 0,
                FolderId: folderId,
                ServerUid: $"thread-{uid++}",
                MessageId: $"<thread-{uid}@example.com>",
                FromName: fromName,
                FromAddress: fromAddress,
                Subject: subject,
                Preview: inReplyTo is null ? "The message that started it." : "A reply to it.",
                Sent: received,
                Received: received,
                SizeBytes: 3_000 + (uid * 100),
                IsRead: uid % 3 != 0,
                IsFlagged: false,
                HasAttachment: false)
            {
                To = ["you@example.com"],
                BodyText = "Invented text.",
            });
        }

        // A thread that holds together: one subject, four messages, prefixes and all.
        Add("Alice Chen", "alice@example.com", "Venue options", 30);
        Add("You", "you@example.com", "Re: Venue options", 26);
        Add("Bruno Sala", "bruno@example.net", "RE: Venue options", 20);
        Add("Alice Chen", "alice@example.com", "Fwd: Venue options", 4);

        // The false split: a reply by every header that matters, whose sender edited the subject.
        // It threads as a conversation of its own.
        Add("Cara Devlin", "cara@example.org", "Budget review", 28);
        Add("Dan Iwu", "dan@example.net", "Re: Budget review — revised figures", 22);

        // The false join: two messages that share a subject and nothing else. Different senders,
        // different recipients, no reply relationship of any kind.
        Add("Eve Marsh", "eve@example.com", "Lunch", 18);
        Add("Femi Ojo", "femi@example.org", "Lunch", 9);

        // A thread of one, which is not a conversation and must not draw an expander.
        Add("Gita Rao", "gita@example.net", "Nothing follows this", 2);
    }

    /// <summary>
    /// A folder long enough that the list has to recycle its rows, and varied enough that
    /// grouping over it does real work.
    /// </summary>
    private static void SeedBig(MailRepository mail, long folderId)
    {
        var now = Mailbox.Core.PosedClock.Now;
        var count = BigFolderRows;

        for (var i = 0; i < count; i++)
        {
            // Senders over fifty, threads of three, and dates spread back over two years, so
            // every arrangement produces many groups rather than one enormous bucket.
            var received = now.AddMinutes(-i * 11);

            mail.AddMessage(folderId, new MessageSummary(
                Id: 0,
                FolderId: folderId,
                ServerUid: $"big-{i}",
                MessageId: $"<big-{i}@example.com>",
                FromName: $"Sender {i % 50}",
                FromAddress: $"sender{i % 50}@example.net",
                Subject: $"Message {i / 3} of the {i % 7} kind",
                Preview: "Invented text, so a preview line has something to shorten.",
                Sent: received,
                Received: received,
                SizeBytes: 1_000 + (i % 900) * 137,
                IsRead: i % 4 != 0,
                IsFlagged: i % 37 == 0,
                HasAttachment: i % 11 == 0)
            {
                Importance = i % 53 == 0 ? 2 : i % 47 == 0 ? 0 : 1,
                To = ["you@example.com"],
                BodyText = "Invented text.",
            });
        }
    }
}
