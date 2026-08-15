using System.Collections.Concurrent;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Rules;
using Mailbox.Store;
using MimeKit;
using MimeKit.Utils;

namespace Mailbox.Protocols;

/// <summary>Something a rule asked the application to show or play — collected here, shown by the shell.</summary>
/// <param name="Kind">The action: <see cref="RuleActionKind.DisplayAlert"/>, <see cref="RuleActionKind.DesktopAlert"/> or <see cref="RuleActionKind.PlaySound"/>.</param>
/// <param name="Text">The alert's words, or the sound file, or empty.</param>
/// <param name="Address">The account the message arrived in.</param>
/// <param name="MessageId">The message, so an alert can open it.</param>
public sealed record RuleAlert(RuleActionKind Kind, string Text, string Address, long MessageId, string RuleName);

/// <summary>
/// Runs the Rules and Alerts wizard's rules over a message: on arrival, as an
/// <see cref="IArrivalHandler"/>, and on demand for Run Rules Now.
/// </summary>
/// <remarks>
/// The rules are read from the account's store each time, so a rule saved in the dialog applies
/// to the next message without a restart. Evaluation is <see cref="RuleEvaluator"/>'s and pure;
/// what this class owns is turning a message into facts and an action into a store operation —
/// a move, a copy, a delete, a flag, a category, a message queued to the outbox. The three that
/// need a screen or a speaker are collected in <see cref="Alerts"/> for the shell to show once
/// the run is over, because a rule runs on the send/receive thread and a toast does not.
/// </remarks>
public sealed class RulesHandler(Func<DateTimeOffset>? now = null) : IArrivalHandler
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>What rules asked to be shown or played, oldest first. Drained by the shell.</summary>
    public ConcurrentQueue<RuleAlert> Alerts { get; } = new();

    /// <inheritdoc />
    public long? Handle(MailRepository mail, Folder folder, long messageId, MimeMessage message)
    {
        // A rule that runs on the server has already run by the time the message is here — as
        // long as the server has the current script. While it is behind, the rule runs here too.
        var serverCurrent = mail.ServerRulesCurrent();
        var rules = mail.Rules().Where(r => r.Enabled && !(r.ServerSide && serverCurrent)).ToList();
        if (rules.Count == 0) return folder.Id;

        return Apply(mail, folder, messageId, message, rules, mail.GetMessage(messageId)).FolderId;
    }

    /// <summary>
    /// Run Rules Now: applies the chosen rules to every message in a folder, and says how many
    /// messages at least one rule acted on.
    /// </summary>
    /// <param name="only">Which messages to consider — All, Unread or Read; null for all.</param>
    public int RunNow(MailRepository mail, Folder folder, IReadOnlyList<MailRule> rules, Func<MessageSummary, bool>? only = null)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Count == 0) return 0;

        var touched = 0;
        foreach (var summary in mail.Messages(folder.Id, int.MaxValue))
        {
            if (only is not null && !only(summary)) continue;
            if (mail.LoadRaw(summary.Id) is not { } raw) continue;

            MimeMessage message;
            try
            {
                using var stream = new MemoryStream(raw);
                message = MimeMessage.Load(stream);
            }
            catch (Exception ex)
            {
                Log.Warn($"Run Rules Now skipped message {summary.Id}: it would not parse.", ex);
                continue;
            }

            if (Apply(mail, folder, summary.Id, message, rules, summary).Fired) touched++;
        }

        return touched;
    }

    /// <summary>Applies the rules in order. Where the message ended up (null when deleted), and whether any fired.</summary>
    private (long? FolderId, bool Fired) Apply(MailRepository mail, Folder folder, long messageId, MimeMessage message,
        IReadOnlyList<MailRule> rules, MessageSummary? summary)
    {
        var fired = false;
        var facts = FactsFor(mail, message, summary, folder);
        var current = folder;
        var address = mail.OwnAddress() ?? string.Empty;

        foreach (var rule in rules)
        {
            if (!RuleEvaluator.Matches(rule, facts)) continue;
            fired = true;

            foreach (var action in rule.Actions)
            {
                long? next;
                try
                {
                    next = Perform(mail, current, messageId, message, action, address, rule.Name);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Rule “{rule.Name}” could not {RuleDescription.Template(action.Kind)}.", ex);
                    continue;
                }

                if (next is null) return (null, true);
                if (next != current.Id) current = mail.GetFolder(next.Value) ?? current;
            }

            if (rule.StopsProcessing) break;
        }

        return (current.Id, fired);
    }

    /// <summary>What a rule can see of a message.</summary>
    public static RuleFacts FactsFor(MailRepository mail, MimeMessage message, MessageSummary? summary, Folder folder)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(message);

        var from = message.From.Mailboxes.FirstOrDefault();
        var categories = summary is null
            ? []
            : mail.CategoriesFor([summary.Id]).GetValueOrDefault(summary.Id)?.Select(c => c.Name).ToList() ?? [];

        var headers = string.Join('\n', message.Headers.Select(h => $"{h.Field}: {h.Value}"));

        return new RuleFacts
        {
            FromAddress = from?.Address ?? string.Empty,
            FromName = from?.Name ?? string.Empty,
            To = [.. message.To.Mailboxes.Select(m => m.Address)],
            Cc = [.. message.Cc.Mailboxes.Select(m => m.Address)],
            Subject = message.Subject ?? string.Empty,
            Body = message.TextBody ?? StripTags(message.HtmlBody ?? string.Empty),
            Headers = headers,
            SizeBytes = summary?.SizeBytes ?? 0,
            HasAttachment = summary?.HasAttachment ?? message.Attachments.Any(),
            Importance = message.Importance switch
            {
                MessageImportance.Low => 0,
                MessageImportance.High => 2,
                _ => 1,
            },
            Sensitivity = (message.Headers["Sensitivity"] ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "personal" => 1,
                "private" => 2,
                "company-confidential" or "confidential" => 3,
                _ => 0,
            },
            Received = summary?.Received ?? DateTimeOffset.UtcNow,
            Categories = categories,
            IsFlagged = summary?.IsFlagged ?? false,
            OwnAddresses = mail.OwnAddress() is { } own ? [own] : [],
        };
    }

    /// <summary>Does one action. Returns the folder the message is in afterwards, or null when it is gone.</summary>
    private long? Perform(MailRepository mail, Folder current, long messageId, MimeMessage message,
        RuleAction action, string address, string ruleName)
    {
        switch (action.Kind)
        {
            case RuleActionKind.MoveToFolder when Target(mail, current, action) is { } target:
                mail.MoveMessages([messageId], target.Id);
                return target.Id;

            case RuleActionKind.CopyToFolder when Target(mail, current, action) is { } target:
                if (mail.LoadRaw(messageId) is { } raw)
                {
                    var flags = mail.Flags(messageId);
                    var copy = MessageMapper.ToSummary(message, null, raw.Length, _now(), flags?.IsRead ?? false, flags?.IsFlagged ?? false);
                    mail.AddMessage(target.Id, copy, raw);
                }
                return current.Id;

            case RuleActionKind.Delete:
                if (mail.FolderWithRole(current.AccountId, FolderRole.Deleted) is { } deleted && deleted.Id != current.Id)
                {
                    mail.MoveMessages([messageId], deleted.Id);
                    return deleted.Id;
                }
                return current.Id;

            case RuleActionKind.PermanentlyDelete:
                mail.DeleteMessages([messageId]);
                return null;

            case RuleActionKind.ForwardTo:
            case RuleActionKind.ForwardAsAttachmentTo:
            case RuleActionKind.RedirectTo:
                Send(mail, current.AccountId, message, action, address);
                return current.Id;

            case RuleActionKind.MarkAsRead:
                mail.SetRead([messageId], true);
                return current.Id;

            case RuleActionKind.FlagForFollowUp:
                mail.SetFollowUp([messageId], action.Level is { } days ? DueAt(days) : null);
                return current.Id;

            case RuleActionKind.ClearFlag:
                mail.ClearFollowUp([messageId]);
                return current.Id;

            case RuleActionKind.AssignCategory:
                foreach (var name in action.Values)
                {
                    var category = mail.Categories().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (category is not null) mail.Assign([messageId], category.Id);
                }
                return current.Id;

            case RuleActionKind.ClearCategories:
                foreach (var category in mail.Categories()) mail.Unassign([messageId], category.Id);
                return current.Id;

            case RuleActionKind.DisplayAlert:
            case RuleActionKind.DesktopAlert:
            case RuleActionKind.PlaySound:
                Alerts.Enqueue(new RuleAlert(action.Kind, action.Values.FirstOrDefault() ?? string.Empty, address, messageId, ruleName));
                return current.Id;

            default:
                // MarkImportance and Print are not offered by the wizard; StopProcessing is read
                // by the caller. Anything else is left alone rather than guessed at.
                return current.Id;
        }
    }

    /// <summary>The folder an action names, by id first and by name if the id has gone.</summary>
    private static Folder? Target(MailRepository mail, Folder current, RuleAction action)
    {
        if (action.FolderId is { } id && mail.GetFolder(id) is { } byId && byId.AccountId == current.AccountId) return byId;
        if (action.FolderName is { Length: > 0 } name)
        {
            return mail.Folders(current.AccountId).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private DateTimeOffset DueAt(int days)
    {
        var day = _now().ToLocalTime().Date.AddDays(days);
        return new DateTimeOffset(day.AddHours(17), TimeZoneInfo.Local.GetUtcOffset(day));
    }

    /// <summary>
    /// Forward, forward as attachment, or redirect: a message built from the original and put in
    /// the outbox, to go with the next send. A redirect keeps the original's headers and adds the
    /// Resent- set, which is what redirecting means; the sender then delivers it to the
    /// Resent-To addresses.
    /// </summary>
    private void Send(MailRepository mail, long accountId, MimeMessage original, RuleAction action, string address)
    {
        var recipients = action.Values
            .Select(v => MailboxAddress.TryParse(v, out var parsed) ? parsed : null)
            .Where(m => m is not null)
            .Cast<MailboxAddress>()
            .ToList();
        if (recipients.Count == 0 || address.Length == 0) return;

        MimeMessage outgoing;
        if (action.Kind == RuleActionKind.RedirectTo)
        {
            using var buffer = new MemoryStream();
            original.WriteTo(buffer);
            buffer.Position = 0;
            outgoing = MimeMessage.Load(buffer);
            outgoing.ResentFrom.Add(new MailboxAddress(string.Empty, address));
            outgoing.ResentTo.AddRange(recipients);
            outgoing.ResentDate = _now();
            outgoing.ResentMessageId = MimeUtils.GenerateMessageId();
        }
        else
        {
            outgoing = new MimeMessage();
            outgoing.From.Add(new MailboxAddress(string.Empty, address));
            outgoing.To.AddRange(recipients);
            outgoing.Subject = Prefixed(original.Subject);
            outgoing.Date = _now();
            outgoing.MessageId = MimeUtils.GenerateMessageId();

            if (action.Kind == RuleActionKind.ForwardAsAttachmentTo)
            {
                outgoing.Body = new Multipart("mixed")
                {
                    new TextPart("plain") { Text = string.Empty },
                    new MessagePart { Message = original, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) },
                };
            }
            else
            {
                outgoing.Body = original.Body;
            }
        }

        new SmtpSender(mail).Queue(accountId, outgoing, _now());
    }

    private static string Prefixed(string? subject)
    {
        var text = (subject ?? string.Empty).Trim();
        return text.StartsWith("FW:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase)
            ? text
            : "FW: " + text;
    }

    /// <summary>The words of an HTML body, for a rule that reads the body of a message that has no plain text.</summary>
    private static string StripTags(string html)
    {
        if (html.Length == 0) return html;

        var text = new System.Text.StringBuilder(html.Length);
        var inTag = false;
        foreach (var c in html)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; text.Append(' '); continue; }
            if (!inTag) text.Append(c);
        }

        return System.Net.WebUtility.HtmlDecode(text.ToString());
    }
}
