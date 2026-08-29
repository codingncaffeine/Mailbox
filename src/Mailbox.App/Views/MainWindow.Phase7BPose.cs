using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.Contacts;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// Answers the duplicate-contact prompt without a pointer. Harness only.
/// </summary>
/// <remarks>
/// The prompt is the last thing between a typed card and the address book, and it could not be
/// answered by anything: it opens over a modal contact window, so the dialog-press door reaches
/// the contact window instead, and a run that pressed Save simply stopped there with a prompt on
/// screen nobody could reach. Both of its outcomes — a second card, or the existing card taking
/// the new information — were therefore claims read off a handler rather than off a store.
/// <para>
/// <c>MAILBOX_DUPLICATE</c> is the route. Entries are separated by <c>|</c> and taken in order,
/// because Cancel means "back to the form" and a form that comes back asks again: a pose that
/// proves the Cancel branch has to say what to do the second time or it never ends. An entry is
/// <c>add</c>, <c>cancel</c>, or <c>update</c> — optionally <c>update:B. Other</c> to say which of
/// several matches the update should land on. When the list runs out the prompt opens normally, so
/// this can never silently swallow a real one, and it is gated on
/// <see cref="WindowCapture.IsRequested"/> as well, so a variable left in a shell cannot reach a
/// reader's own address book.
/// </para>
/// </remarks>
internal static class HarnessDuplicate
{
    private const string Variable = "MAILBOX_DUPLICATE";

    private static readonly Lock Gate = new();
    private static List<string>? _pending;

    /// <summary>What this prompt should answer, or null to let it open and wait.</summary>
    internal static string? Next()
    {
        if (!WindowCapture.IsRequested) return null;

        lock (Gate)
        {
            _pending ??= [.. (Environment.GetEnvironmentVariable(Variable) ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

            if (_pending.Count == 0) return null;

            var entry = _pending[0];
            _pending.RemoveAt(0);
            return entry;
        }
    }
}

/// <summary>
/// The address book's own doors: what every card holds, what the store holds verbatim, and what
/// the duplicate finder says about a card that has not been saved yet.
/// </summary>
/// <remarks>
/// The People module's list reports what it draws — a name, a letter, a filed-as — and that is the
/// right read-back for a list. It is the wrong one for everything this lane asks: whether a merge
/// kept the address the old card had, whether an exported card came back with its photograph,
/// whether a link is in both vCards rather than in a view. Those are questions about the record,
/// so these answer with the record.
/// <list type="bullet">
/// <item><description><c>MAILBOX_BOOK=cards</c> — every card in every book, field by field.</description></item>
/// <item><description><c>MAILBOX_BOOK=vcards</c> — the vCard text the store holds, verbatim, which
/// is what a round trip has to be compared against.</description></item>
/// <item><description><c>MAILBOX_BOOK=duplicates:name=…;email=…</c> — what the finder makes of a
/// candidate, with the strength and the words it would put in the prompt, without saving
/// anything.</description></item>
/// </list>
/// </remarks>
public partial class MainWindow
{
    /// <summary>This lane's shell-side doors, hung off the window opening.</summary>
    private void WireAddressBookDoors()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_BOOK") is not { Length: > 0 } probe) return;

        // Last of all: a report taken before MAILBOX_RUN has pressed anything, before an import
        // has landed or before a duplicate prompt has been answered describes the book as it was
        // rather than as the pose left it — which is the shape of evidence that proves a pose
        // never ran.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => Dispatcher.UIThread.Post(() => ReportAddressBook(probe), DispatcherPriority.ApplicationIdle),
            DispatcherPriority.Background);
    }

    private void ReportAddressBook(string probe)
    {
        try
        {
            foreach (var what in probe.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (what.StartsWith("duplicates:", StringComparison.OrdinalIgnoreCase))
                {
                    ReportDuplicates(what["duplicates:".Length..]);
                    continue;
                }

                switch (what.ToLowerInvariant())
                {
                    case "cards":
                        foreach (var row in App.Contacts.Rows())
                        {
                            Log.Info($"Harness: card {row.Id}\t{Describe(App.Contacts.Full(row.Id) ?? row.Contact, row)}");
                        }

                        break;

                    // Verbatim, one line, with the folds marked: a vCard is a line-oriented format
                    // and a report that reflowed it would hide the very thing an encoding check is
                    // looking at.
                    case "vcards":
                        foreach (var row in App.Contacts.Rows())
                        {
                            if (App.Contacts.Repository.Item(row.Id) is not { } stored) continue;
                            Log.Info($"Harness: vcard {row.Id} “{row.Named()}” {stored.RawPayload.Length} chars: "
                                     + stored.RawPayload.Replace("\r\n", "⏎", StringComparison.Ordinal)
                                         .Replace("\n", "⏎", StringComparison.Ordinal));
                        }

                        break;

                    case "books":
                        foreach (var book in App.Contacts.AddressBooks())
                        {
                            Log.Info($"Harness: book {book.Id} “{book.DisplayName}” "
                                     + $"holds {App.Contacts.Rows([book.Id]).Count} card(s).");
                        }

                        break;

                    default:
                        Log.Info($"Harness: no address-book probe called “{what}”.");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // Logged rather than dropped: a posted action that throws leaves a run with a
            // plausible capture, no error and nothing to grep.
            Log.Warn("Harness: an address-book door failed.", ex);
        }
    }

    /// <summary>One card as a record rather than as a row: everything a merge could lose.</summary>
    private static string Describe(Contact c, ContactRow row)
        => $"book “{row.CollectionName}” uid “{c.Uid}” named “{c.Named()}” filedAs “{c.FiledAs()}” "
           + $"first “{c.FirstName}” last “{c.LastName}” company “{c.Company}” jobTitle “{c.JobTitle}” "
           + $"emails [{string.Join(" | ", c.Emails.Select(e => e.Address))}] "
           + $"phones [{string.Join(" | ", c.Phones.Select(p => $"{p.Kind}:{p.Number}"))}] "
           + $"addresses [{string.Join(" | ", c.Addresses.Select(a => a.OneLine()))}] "
           + $"urls [{string.Join(" | ", c.Urls)}] im [{string.Join(" | ", c.InstantMessaging)}] "
           + $"categories [{string.Join(" | ", c.Categories)}] "
           + $"birthday {c.Birthday?.ToString("yyyy-MM-dd") ?? "none"} "
           + $"photo {(c.Photo is { Bytes.Length: > 0 } p ? $"{p.Bytes!.Length}B {p.MediaType}" : "none")} "
           + $"note “{c.Notes.Replace("\n", "⏎", StringComparison.Ordinal)}” "
           + $"group {c.IsGroup} members {c.Members.Count} private {c.IsPrivate} "
           + $"links [{string.Join(" | ", c.Links)}] "
           + $"flag {(c.FollowUpDue is { } due ? due.LocalDateTime.ToString("yyyy-MM-dd") : "none")}"
           + $"{(c.FollowUpComplete ? " (complete)" : string.Empty)}";

    /// <summary>
    /// What the finder makes of a card that is not in the book: the matches, strongest first, each
    /// with the words the prompt would put on it.
    /// </summary>
    private void ReportDuplicates(string spec)
    {
        var candidate = Candidate(spec);
        var matches = App.Contacts.Duplicates(candidate);

        Log.Info($"Harness: duplicates for “{candidate.Named()}” "
                 + $"[{string.Join(" | ", candidate.Emails.Select(e => e.Address))}] — {matches.Count} match(es).");

        foreach (var match in matches)
        {
            Log.Info($"Harness: duplicate {match.Row.Id} “{match.Row.Named()}” in “{match.Row.CollectionName}” "
                     + $"— {match.Strength}, {match.Reason}");
        }
    }

    /// <summary>A card built from a pose's own words, for asking a question about one.</summary>
    private static Contact Candidate(string spec)
    {
        var contact = new Contact { Uid = "harness-candidate" };
        var emails = new List<ContactEmail>();
        var phones = new List<ContactPhone>();

        foreach (var pair in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;

            var value = pair[(equals + 1)..].Trim();
            switch (pair[..equals].Trim().ToLowerInvariant())
            {
                case "name": contact = contact with { DisplayName = value }; break;
                case "company": contact = contact with { Company = value }; break;
                case "email": emails.Add(new ContactEmail(value)); break;
                case "phone": phones.Add(new ContactPhone(value, PhoneKind.Business)); break;
                case "group": contact = contact with { IsGroup = value is "1" or "true" }; break;
                default: Log.Info($"Harness: a candidate has no field called “{pair[..equals]}”."); break;
            }
        }

        return contact with { Emails = emails, Phones = phones };
    }
}
