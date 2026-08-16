using System.Globalization;
using Mailbox.Core.Compose;

namespace Mailbox.Contacts;

/// <summary>
/// The address book's half of the Auto-Complete List: the people and the groups that match what
/// has been typed, as recipient lines can take them.
/// </summary>
/// <remarks>
/// A person is offered once per address they have, because writing to somebody's home address
/// rather than their work one is a choice the reader makes and not one the address book makes for
/// them. A group is offered once and puts everybody in it on the line: there is no token to leave
/// unresolved on a plain recipient line, and a name that silently means nine people is a name
/// somebody sends to by mistake.
/// </remarks>
public static class ContactSuggestions
{
    /// <summary>What the address book offers for what has been typed.</summary>
    /// <param name="limit">At most this many, the list being merged with the remembered addresses.</param>
    public static IReadOnlyList<RecipientSuggestion> For(ContactBook book, string typed, int limit = 8)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (string.IsNullOrWhiteSpace(typed)) return [];

        var offered = new List<RecipientSuggestion>();

        foreach (var row in book.Matching(typed, limit))
        {
            if (row.Contact.IsGroup)
            {
                if (Group(book, row) is { } group) offered.Add(group);
                continue;
            }

            foreach (var email in row.Contact.Emails)
            {
                if (email.Address is not { Length: > 0 } address) continue;

                var name = row.Contact.Named();
                offered.Add(new RecipientSuggestion(
                    address,
                    name,
                    address,
                    Insert: name.Length > 0 ? $"{name} <{address}>" : address,
                    Detail: "Contact",
                    CanForget: false));
            }
        }

        return offered.Count > limit ? offered.Take(limit).ToList() : offered;
    }

    /// <summary>
    /// A distribution list as one entry: everybody in it, resolved through the address book where
    /// a member is kept by UID rather than by address.
    /// </summary>
    private static RecipientSuggestion? Group(ContactBook book, ContactRow row)
    {
        var group = book.Full(row.Id) ?? row.Contact;
        var addresses = new List<string>();

        foreach (var member in group.Members)
        {
            if (member.Address is { Length: > 0 } address)
            {
                addresses.Add(member.Name is { Length: > 0 } named ? $"{named} <{address}>" : address);
                continue;
            }

            if (member.Uid is not { Length: > 0 } uid) continue;

            // A member kept by UID points at a card: the address is whatever that card now says,
            // which is the point of keeping it that way rather than copying the address in.
            var pointed = book.Rows().FirstOrDefault(r => r.Contact.Uid == uid);
            if (pointed?.Contact.PrimaryEmail is { Length: > 0 } theirs)
            {
                addresses.Add($"{pointed.Contact.Named()} <{theirs}>");
            }
        }

        if (addresses.Count == 0) return null;

        return new RecipientSuggestion(
            "group:" + group.Uid,
            group.Named(),
            Address: string.Empty,
            Insert: string.Join("; ", addresses),
            Detail: addresses.Count == 1
                ? "1 member"
                : $"{addresses.Count.ToString(CultureInfo.CurrentCulture)} members",
            CanForget: false);
    }
}
