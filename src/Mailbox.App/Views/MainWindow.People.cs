using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Mailbox.App.ViewModels;
using Mailbox.Contacts;
using Mailbox.Controls.People;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;
using Mailbox.Core.Ribbon;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The People module in the shell: switching to it, the workspace it puts in the window, and the
/// commands its ribbon presses.
/// </summary>
/// <remarks>
/// A partial of the shell for the reason the calendar's half is: it needs the window's ribbon,
/// its dialogs and its status line.
/// </remarks>
public partial class MainWindow
{
    private PeopleWorkspace? _people;

    /// <summary>The People ribbon: the shipped layout with the reader's edits over it.</summary>
    private static RibbonLayout PeopleRibbon() => App.RibbonEdits.Apply(DefaultRibbonLayouts.People);

    private PeopleWorkspace EnsurePeople(ShellViewModel shell)
    {
        if (_people is not null) return _people;

        var workspace = new PeopleWorkspace(App.Contacts, App.PeopleOptions)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.Changed += (_, _) => shell.ModuleStatusLeft = workspace.Status;
        workspace.ContactOpened += (_, row) => _ = OpenContactAsync(shell, row);
        workspace.ContactMenuRequested += (_, row) => ShowContactMenu(shell, row);
        workspace.NewRequested += (_, _) => _ = NewContactAsync(shell);
        _people = workspace;
        return workspace;
    }

    /// <summary>
    /// The People module's commands. Returns false for anything it does not own, so the shell's
    /// own list carries on.
    /// </summary>
    private bool RunPeopleCommand(ShellViewModel shell, CommandId id)
    {
        if (id == PeopleCommands.NewContact.Id)
        {
            SwitchModule(shell, MailboxModule.People);
            _ = NewContactAsync(shell);
            return true;
        }

        if (id == PeopleCommands.NewContactGroup.Id)
        {
            SwitchModule(shell, MailboxModule.People);
            _ = NewContactAsync(shell, group: true);
            return true;
        }

        if (id == PeopleCommands.OpenContact.Id) { OpenSelectedContact(shell); return true; }
        if (id == PeopleCommands.Delete.Id) { _ = DeleteSelectedContactAsync(shell); return true; }
        if (id == PeopleCommands.EmailContact.Id) { EmailSelectedContact(shell); return true; }
        if (id == MailCommands.AddressBook.Id) { _ = ShowAddressBookAsync(shell); return true; }
        if (id == PeopleCommands.NewAddressBook.Id) { _ = NewAddressBookAsync(shell); return true; }
        if (id == PeopleCommands.Categorize.Id) { CategorizeContact(shell); return true; }
        if (id == PeopleCommands.DeleteAddressBook.Id) { _ = DeleteAddressBookAsync(shell); return true; }
        if (id == PeopleCommands.Favourite.Id) { FavouriteContact(shell); return true; }
        if (id == PeopleCommands.NewItems.Id) { SwitchModule(shell, MailboxModule.People); ShowNewItemsMenu(); return true; }
        if (id == PeopleCommands.MoveTo.Id) { MoveContact(shell); return true; }
        if (id == PeopleCommands.ForwardContact.Id) { ForwardContact(shell); return true; }
        if (id == PeopleCommands.MeetContact.Id) { MeetContact(shell); return true; }
        if (id == ViewCommands.SearchPeople.Id) { _ = SearchPeopleAsync(shell); return true; }

        // The views, the tags and the rest are placed and say what they wait for, as the
        // calendar's unfinished buttons do (§20).
        if (WaitingPeopleCommand(id) is { } waiting)
        {
            SwitchModule(shell, MailboxModule.People);
            shell.StatusRight = waiting;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Add to Favourites, and take one out again: the short list the To-Do Bar's People section
    /// shows.
    /// </summary>
    /// <remarks>
    /// Kept by the card's UID rather than by its row, and in this reader's settings rather than in
    /// the card — a vCard has no way to say "a favourite of mine", and an invented property would
    /// write one reader's short list into everybody else's address book.
    /// </remarks>
    private void FavouriteContact(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        var favourite = App.ContactFavourites.Toggle(row.Contact.Uid);
        RebuildToDoBar(shell);

        shell.StatusRight = favourite
            ? $"{row.Named()} is in Favourites."
            : $"{row.Named()} is out of Favourites.";
        Log.Info($"Favourites: {row.Named()} ({row.Contact.Uid}) is {(favourite ? "in" : "out")}; "
            + $"the list holds {App.ContactFavourites.All.Count}.");
    }

    /// <summary>
    /// The To-Do Bar's People section: the favourites, drawn by the module's own list.
    /// </summary>
    /// <remarks>
    /// The same <c>ContactListView</c> the module fills the window with, which is what makes this
    /// a third line of composition rather than a third drawn list — with the alphabet index off,
    /// a short list having no Ws to reach.
    /// </remarks>
    private ContactListView? BuildToDoPeople(ShellViewModel shell)
    {
        var favourites = App.ContactFavourites.All;
        var view = new ContactListView
        {
            ShowIndex = false,
            Order = FileAsOrders.FromIndex(App.PeopleOptions.FileAsIndex),
            Rows = favourites.Count == 0
                ? []
                : [.. App.Contacts.Rows()
                    .Where(r => App.ContactFavourites.Contains(r.Contact.Uid))
                    .OrderBy(r => favourites.ToList().FindIndex(u => string.Equals(u, r.Contact.Uid, StringComparison.OrdinalIgnoreCase)))],
        };

        view.ContactActivated += (_, row) => _ = OpenContactAsync(shell, row);
        return view;
    }

    /// <summary>
    /// The People peek: the favourites, on a hover over the rail's People icon.
    /// </summary>
    /// <remarks>
    /// The calendar peek's own machinery — the layer, the dwell, the grace period on the way out —
    /// over a different popup. Its corner button docks the same section into the To-Do Bar, which
    /// is what the calendar peek's does, and its search box is the module's own Search People.
    /// </remarks>
    private void OpenPeoplePeek(ShellViewModel shell)
    {
        var favourites = App.ContactFavourites.All;
        var rows = favourites.Count == 0
            ? []
            : App.Contacts.Rows()
                .Where(r => App.ContactFavourites.Contains(r.Contact.Uid))
                .OrderBy(r => favourites.ToList().FindIndex(u => string.Equals(u, r.Contact.Uid, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        var peek = new PeoplePeek(rows, FileAsOrders.FromIndex(App.PeopleOptions.FileAsIndex));
        peek.ContactOpened += (_, row) => { ClosePeek(); _ = OpenContactAsync(shell, row); };
        peek.SearchRequested += (_, _) => { ClosePeek(); _ = SearchPeopleAsync(shell); };
        peek.DockRequested += (_, _) =>
        {
            ClosePeek();
            ShowToDoPeople(shell, true);
        };

        ShowPeekPopup(peek);
        Log.Info($"People peek: {rows.Count} favourite(s).");
    }

    /// <summary>
    /// The menu a right-click on somebody opens, in the reference's own order.
    /// </summary>
    /// <remarks>
    /// This is where Add to Favourites belongs: the reference's own People peek says so in so
    /// many words — "right-click a person anywhere to add them to your favourites" — which is why
    /// the button is not on its bar and is not on ours.
    /// </remarks>
    private void ShowContactMenu(ShellViewModel shell, ContactRow row)
    {
        var flyout = new MenuFlyout();

        void Entry(string header, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }

        Entry("Open", () => _ = OpenContactAsync(shell, row));
        Entry("E-mail", () => EmailSelectedContact(shell), row.Contact.PrimaryEmail is { Length: > 0 } || row.Contact.IsGroup);
        Entry("Meeting", () => MeetContact(shell));
        flyout.Items.Add(new Separator());

        Entry("Forward Contact", () => ForwardContact(shell));
        Entry("Move", () => MoveContact(shell));
        Entry("Categorize", () => CategorizeContact(shell));
        flyout.Items.Add(new Separator());

        Entry(
            App.ContactFavourites.Contains(row.Contact.Uid) ? "Remove from Favourites" : "Add to Favourites",
            () => FavouriteContact(shell));

        flyout.Items.Add(new Separator());
        Entry("Delete", () => _ = DeleteSelectedContactAsync(shell));

        Log.Info($"People: the menu for “{row.Named()}” is open.");
        flyout.ShowAt(EnsurePeople(shell), showAtPointer: true);
    }

    /// <summary>
    /// Search People: what the box on every module's bar is for — find somebody by name, company,
    /// address or number, and narrow the People list to them.
    /// </summary>
    /// <remarks>
    /// <b>Divergence, stated:</b> the reference types into the box itself. Every field on this
    /// ribbon is a button — the bar has no editable control yet (§20) — so pressing it asks for
    /// the words in a prompt and the list answers. What it does with them is the reference's:
    /// the module's own list narrows, and emptying the box brings everybody back.
    /// </remarks>
    private async Task SearchPeopleAsync(ShellViewModel shell)
    {
        SwitchModule(shell, MailboxModule.People);
        var people = EnsurePeople(shell);

        // The harness poses the words rather than the prompt, a dialog being a surface that
        // blocks a capture run.
        var posed = Environment.GetEnvironmentVariable("MAILBOX_SEARCH");
        var wanted = posed is { Length: > 0 }
            ? posed
            : await Prompt.AskAsync(this, "Search People", "Find:", people.Search);

        if (wanted is null) return;

        people.Search = wanted;
        shell.ModuleStatusLeft = people.Status;
        shell.StatusRight = people.Search.Length == 0
            ? "Showing everybody."
            : $"{people.Rows.Count} of {people.Total.Count} match “{people.Search}”.";
        Log.Info($"People: search “{people.Search}” matched {people.Rows.Count} of {people.Total.Count}.");
    }

    /// <summary>
    /// Move: the address books, in a menu under the button.
    /// </summary>
    /// <remarks>
    /// A contact is not moved the way a note is. Its addresses, its numbers and its photograph are
    /// rows of their own hung off its id, so the move goes through the contact book — which writes
    /// all three in one call — rather than through the store's own <c>MoveItem</c>, which carries
    /// the item and its text and nothing else.
    /// </remarks>
    private void MoveContact(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row || App.Pim.Item(row.Id) is not { } stored)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        var books = App.Contacts.AddressBooks().Where(b => b.Id != stored.CollectionId).ToList();
        if (books.Count == 0)
        {
            shell.StatusRight = "There is nowhere else to keep a contact: this is the only address book.";
            return;
        }

        // A menu is a surface no capture can show, so the harness names the book instead.
        if (Environment.GetEnvironmentVariable("MAILBOX_MOVE")?.Trim() is { Length: > 0 } posed)
        {
            if (books.FirstOrDefault(b => b.DisplayName.Contains(posed, StringComparison.OrdinalIgnoreCase)) is not { } wanted)
            {
                Log.Info($"Harness: no address book matching “{posed}” to move “{row.Named()}” to.");
                return;
            }

            MoveContactTo(shell, row, stored, wanted);
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var book in books)
        {
            var entry = new MenuItem { Header = book.DisplayName };
            var chosen = book;
            entry.Click += (_, _) => MoveContactTo(shell, row, stored, chosen);
            flyout.Items.Add(entry);
        }

        flyout.ShowAt(_ribbon ?? (Control)this, showAtPointer: true);
    }

    private void MoveContactTo(ShellViewModel shell, ContactRow row, PimItem stored, Collection book)
    {
        var contact = App.Contacts.Full(row.Id) ?? row.Contact;

        var made = App.Contacts.Save(contact, book.Id);
        App.PimSync.QueuePut(made);
        App.PimSync.Remove(stored);

        EnsurePeople(shell).Reload();
        shell.StatusRight = $"“{contact.Named()}” moved to {book.DisplayName}.";
        Log.Info($"People: contact {row.Id} moved to {book.DisplayName} as {made.Id}.");
    }

    /// <summary>
    /// Forward Contact: a message with the card attached, which is what the reference sends.
    /// </summary>
    /// <remarks>
    /// A vCard rather than the card's text in the body: what arrives is a file the reader's own
    /// address book can take in, which is the whole point of forwarding somebody.
    /// </remarks>
    private void ForwardContact(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        var contact = App.Contacts.Full(row.Id) ?? row.Contact;
        var card = VCardCodec.Serialize(contact);
        var named = contact.Named() is { Length: > 0 } who ? who : "Contact";
        var name = new string([.. named.Where(c => !Path.GetInvalidFileNameChars().Contains(c))]) + ".vcf";

        var part = new MimeKit.MimePart("text", "vcard")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(card))),
            ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment) { FileName = name },
            ContentTransferEncoding = MimeKit.ContentEncoding.Base64,
            FileName = name,
        };

        var draft = new Mailbox.Rendering.ReplyDraft
        {
            Subject = $"Contact: {contact.Named()}",
            Attachments = [new Mailbox.Rendering.CarriedPart(name, "text/vcard", part)],
        };

        NewMessage(draft, Mailbox.Rendering.ReplyKind.Forward);
        shell.StatusRight = $"“{contact.Named()}” ready to send.";
        Log.Info($"People: forwarding contact {row.Id} as {name} ({card.Length} bytes of vCard).");
    }

    /// <summary>
    /// Meeting: the meeting window with the contact already asked.
    /// </summary>
    /// <remarks>
    /// What this waited for was the Scheduling Assistant, which Phase 12 built — a meeting with
    /// somebody in it is exactly what that tab draws.
    /// </remarks>
    private void MeetContact(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        var contact = App.Contacts.Full(row.Id) ?? row.Contact;
        var asked = contact.IsGroup
            ? contact.Members.Select(m => m.Address).Where(a => a is { Length: > 0 }).ToList()
            : contact.PrimaryEmail is { Length: > 0 } one ? [one] : new List<string>();

        if (asked.Count == 0)
        {
            shell.StatusRight = $"“{contact.Named()}” has no e-mail address to invite.";
            return;
        }

        SwitchModule(shell, MailboxModule.Calendar);
        var calendar = EnsureCalendar(shell);
        _ = NewAppointmentAsync(shell, calendar.Anchor.ToDateTime(NextHalfHour()), allDay: false, meeting: true, asked);
        Log.Info($"People: meeting with {string.Join(", ", asked)}.");
    }

    /// <summary>What a People button that is placed but not yet live says when pressed.</summary>
    private static string? WaitingPeopleCommand(CommandId id)
    {
        if (id == PeopleCommands.BusinessCardView.Id || id == PeopleCommands.CardView.Id
            || id == PeopleCommands.PhoneView.Id || id == PeopleCommands.ListView.Id)
        {
            return "The People list is the People view; the card, phone and list arrangements come with the module's other views.";
        }

        if (id == PeopleCommands.PeopleView.Id) return "This is the People view.";
        if (id == PeopleCommands.MoreCommunicate.Id) return "The other ways to reach somebody arrive with the module's actions.";
        if (id == PeopleCommands.MailMerge.Id) return "Mail merge arrives with Phase 16.";
        if (id == PeopleCommands.ShareContacts.Id) return "Sharing an address book wants CardDAV publishing, which is still to come.";
        if (id == PeopleCommands.OpenSharedContacts.Id) return "A shared address book is a CardDAV account — add one in Account Settings.";
        if (id == PeopleCommands.FollowUp.Id || id == PeopleCommands.Private.Id)
        {
            return "Flagging a contact and marking one private arrive with the module's actions.";
        }

        if (id == PeopleCommands.NewItems.Id) return "New Items arrives with the rest of the modules.";
        return null;
    }

    // ---- Contacts -------------------------------------------------------------------------------

    /// <summary>Makes a contact or a group, opens it for editing, and writes it if it is kept.</summary>
    private async Task NewContactAsync(ShellViewModel shell, bool group = false)
    {
        var people = EnsurePeople(shell);
        var book = App.Contacts.Default();

        var fresh = new Contact
        {
            Uid = Contact.NewUid(),
            IsGroup = group,
        };

        var window = new ContactWindow(App.Commands, fresh, App.Contacts.AddressBooks(), book.Id);
        await window.ShowDialog(this);
        if (window.Result is not { Deleted: false } result) return;

        var written = App.Contacts.Save(result.Contact, result.CollectionId);
        App.PimSync.QueuePut(written);
        people.Reload();
        people.Select(written.Id);

        shell.StatusRight = $"“{result.Contact.Named()}” added to {App.Contacts.AddressBooks().First(b => b.Id == result.CollectionId).DisplayName}.";
        Log.Info($"People: contact {written.Id} added — {result.Contact.Named()}.");
        shell.ModuleStatusLeft = people.Status;
    }

    /// <summary>Opens somebody, and writes what comes back.</summary>
    private async Task OpenContactAsync(ShellViewModel shell, ContactRow row)
    {
        var people = EnsurePeople(shell);
        if (App.Contacts.Repository.Item(row.Id) is not { } stored)
        {
            shell.StatusRight = "That contact is no longer in the address book.";
            people.Reload();
            return;
        }

        var contact = App.Contacts.Full(row.Id) ?? row.Contact;
        var window = new ContactWindow(App.Commands, contact, App.Contacts.AddressBooks(), row.CollectionId);
        await window.ShowDialog(this);
        if (window.Result is not { } result) return;

        if (result.Deleted)
        {
            App.PimSync.Remove(stored);
            shell.StatusRight = $"“{contact.Named()}” deleted.";
            Log.Info($"People: contact {row.Id} deleted.");
        }
        else
        {
            var written = App.Contacts.Save(result.Contact, result.CollectionId, stored);
            App.PimSync.QueuePut(written);
            shell.StatusRight = $"“{result.Contact.Named()}” saved.";
            Log.Info($"People: contact {written.Id} saved.");
        }

        people.Reload();
        shell.ModuleStatusLeft = people.Status;
    }

    private void OpenSelectedContact(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        _ = OpenContactAsync(shell, row);
    }

    private async Task DeleteSelectedContactAsync(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        if (App.Contacts.Repository.Item(row.Id) is not { } stored) return;

        if (!await Confirm.AskAsync(
                this, "Delete Contact",
                $"Are you sure you want to delete “{row.Named()}”?",
                "Delete"))
        {
            return;
        }

        App.PimSync.Remove(stored);
        people.Reload();
        shell.StatusRight = $"“{row.Named()}” deleted.";
        shell.ModuleStatusLeft = people.Status;
        Log.Info($"People: contact {row.Id} deleted.");
    }

    /// <summary>Starts a message to whoever is picked — a group goes to everybody in it.</summary>
    private void EmailSelectedContact(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        var contact = App.Contacts.Full(row.Id) ?? row.Contact;
        var addresses = contact.IsGroup
            ? contact.Members.Select(Recipient).Where(a => a.Length > 0).ToList()
            : contact.PrimaryEmail is { Length: > 0 } one ? [one] : new List<string>();

        if (addresses.Count == 0)
        {
            shell.StatusRight = $"“{contact.Named()}” has no e-mail address.";
            return;
        }

        NewMessage(new Mailbox.Core.Compose.MailtoLink(addresses, [], [], string.Empty, string.Empty));
        Log.Info($"People: composing to {addresses.Count} address(es) from contact {row.Id}.");

        string Recipient(GroupMember member)
        {
            if (member.Address is { Length: > 0 }) return member.Name is { Length: > 0 } named ? $"{named} <{member.Address}>" : member.Address;

            // A member kept by UID: the address comes from the contact it points at.
            var pointed = App.Contacts.Rows().FirstOrDefault(r => r.Contact.Uid == member.Uid);
            return pointed?.Contact.PrimaryEmail ?? string.Empty;
        }
    }

    /// <summary>
    /// The Address Book window, which the ribbon's own button and Ctrl+Shift+B open: the contacts
    /// to look through, with no message to put them on.
    /// </summary>
    private async Task ShowAddressBookAsync(ShellViewModel shell)
    {
        var dialog = new AddressBookDialog(App.Contacts, picking: false);
        await dialog.ShowDialog(this);
        shell.StatusRight = $"Address Book: {App.Contacts.Rows().Count} contact(s).";
    }

    // ---- Address books --------------------------------------------------------------------------

    private async Task NewAddressBookAsync(ShellViewModel shell)
    {
        var name = await Prompt.AskAsync(this, "Create New Folder", "Name:", "Contacts");
        if (string.IsNullOrWhiteSpace(name)) return;

        var book = App.Contacts.Repository.AddCollection(CollectionKind.Contacts, name.Trim());
        shell.StatusRight = $"“{book.DisplayName}” added.";
        Log.Info($"People: address book {book.Id} added.");
        EnsurePeople(shell).Reload();
    }

    private async Task DeleteAddressBookAsync(ShellViewModel shell)
    {
        var books = App.Contacts.AddressBooks();
        if (books.Count <= 1)
        {
            shell.StatusRight = "The last address book cannot be deleted.";
            return;
        }

        var people = EnsurePeople(shell);
        var chosen = people.Selected?.CollectionId ?? books[^1].Id;
        var book = books.First(b => b.Id == chosen);

        if (!await Confirm.AskAsync(
                this, "Delete Folder",
                $"Are you sure you want to delete “{book.DisplayName}” and everything in it?",
                "Delete"))
        {
            return;
        }

        App.Contacts.Repository.RemoveCollection(book.Id);
        shell.StatusRight = $"“{book.DisplayName}” deleted.";
        people.Reload();
        shell.ModuleStatusLeft = people.Status;
    }

    // ---- The harness ----------------------------------------------------------------------------

    /// <summary>
    /// Poses the People module's own windows, since the harness cannot click.
    /// </summary>
    internal async Task ShowPeoplePeekAsync(string which)
    {
        if (DataContext is not ShellViewModel shell) return;
        SwitchModule(shell, MailboxModule.People);
        var people = EnsurePeople(shell);

        switch (which)
        {
            case "addressbook":
                await ShowAddressBookAsync(shell);
                return;

            // The same window with the three lines a message needs, which is what the compose
            // window's To button opens.
            case "selectnames":
            {
                var picked = await AddressBookDialog.PickAsync(this, App.Contacts);
                Log.Info($"Harness: Select Names came back with {picked?.To.Count ?? 0} on To.");
                return;
            }

            case "contactgroup":
                await NewContactAsync(shell, group: true);
                return;

            case "contact":
            default:
            {
                // MAILBOX_SELECT names who, as it does everywhere else; the pose that acts on it
                // is posted at the same priority as this one, so it is read here rather than
                // waited for.
                var wanted = Environment.GetEnvironmentVariable("MAILBOX_SELECT");
                var row = people.Selected
                          ?? (wanted is { Length: > 0 }
                              ? people.Rows.FirstOrDefault(r => r.Named().Contains(wanted, StringComparison.OrdinalIgnoreCase))
                              : null)
                          ?? people.Rows.FirstOrDefault();

                if (row is null) await NewContactAsync(shell);
                else await OpenContactAsync(shell, row);
                return;
            }
        }
    }

    /// <summary>Puts the People module on screen for a capture, with somebody picked.</summary>
    /// <remarks>
    /// At <see cref="DispatcherPriority.Loaded"/> — before Background, which is where
    /// <c>MAILBOX_RUN</c> acts — for the reason the message list's posed selection is: a command
    /// pressed on the selection has to find one. Posted at Background this said "Select a contact
    /// first" and then selected somebody, which is a pose that proves nothing.
    /// </remarks>
    private void ApplyPeoplePose(ShellViewModel shell)
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_SELECT") is not { Length: > 0 } wanted) return;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (shell.Module != MailboxModule.People) return;
                var people = EnsurePeople(shell);
                var row = people.Rows.FirstOrDefault(r => r.Named().Contains(wanted, StringComparison.OrdinalIgnoreCase));
                if (row is null) return;

                people.Select(row.Id);
                Log.Info($"Harness: People showing “{row.Named()}” of {people.Rows.Count.ToString(CultureInfo.InvariantCulture)}.");
            },
            DispatcherPriority.Loaded);
    }
}
