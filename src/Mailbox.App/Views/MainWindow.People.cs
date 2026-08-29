using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Mailbox.App.ViewModels;
using Mailbox.Contacts;
using Mailbox.Controls.People;
using Mailbox.Core.Calendars;
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
    private static RibbonLayout PeopleRibbon() => App.RibbonEdits.Apply(App.Plugins.InjectRibbon(DefaultRibbonLayouts.People));

    private PeopleWorkspace EnsurePeople(ShellViewModel shell)
    {
        if (_people is not null) return _people;

        var workspace = new PeopleWorkspace(App.Contacts, App.PeopleOptions)
        {
            IsNavVisible = shell.NavVisible,
        };

        workspace.Changed += (_, _) =>
        {
            shell.ModuleStatusLeft = workspace.Status;

            // A module's own selection decides what its ribbon can do, the same way the message
            // list's does.
            RefreshCommandEnablement();
        };
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
        if (id == PeopleCommands.Private.Id) { PrivateContact(shell); return true; }
        if (id == PeopleCommands.FollowUp.Id) { FlagContact(shell); return true; }
        if (id == PeopleCommands.ShareContacts.Id) { SwitchModule(shell, MailboxModule.People); ShowShareContactsMenu(shell); return true; }

        // The Current View group: five arrangements over the same rows.
        if (ArrangementFor(id) is { } arrangement)
        {
            SwitchModule(shell, MailboxModule.People);
            var people = EnsurePeople(shell);
            people.Arrangement = arrangement;
            shell.ModuleStatusLeft = people.Status;
            shell.StatusRight = $"{arrangement} view.";
            Log.Info($"People: showing the {arrangement} arrangement.");
            return true;
        }


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
    /// What the Contact window asks the shell for: a message to this person, the address book,
    /// the vCard, and the map.
    /// </summary>
    /// <remarks>
    /// The window knows about one contact and nothing else — no accounts, no compose window, no
    /// desktop — so anything that reaches past the form comes back here, which is the same seam
    /// the compose window's own commands go through.
    /// </remarks>
    private void WireContactWindow(ShellViewModel shell, ContactWindow window)
    {
        WirePhase7ADoors(window);

        window.ShellCommandRequested += (_, id) =>
        {
            if (id == ContactCommands.Email.Id)
            {
                var contact = window.Surface.Current();
                if (contact.PrimaryEmail is { Length: > 0 } address)
                {
                    NewMessage(new Mailbox.Core.Compose.MailtoLink([address], [], [], string.Empty, string.Empty));
                }

                return;
            }

            if (id == ContactCommands.AddressBook.Id) { _ = ShowAddressBookAsync(shell); return; }
            if (id == ContactCommands.CheckNames.Id) { CheckContactNames(shell, window); return; }
            if (id == ContactCommands.Forward.Id) { ForwardCard(shell, window.Surface.Current()); return; }
            if (id == ContactCommands.BusinessCard.Id)
            {
                shell.StatusRight = "The card is drawn beside the form; designing one is not built yet.";
                return;
            }

            if (id == ContactCommands.Picture.Id) { _ = ChoosePictureAsync(shell, window); return; }
        };

        window.MapRequested += (_, address) =>
        {
            if (address.Trim().Length == 0)
            {
                shell.StatusRight = "There is no address to map.";
                return;
            }

            // The desktop's own map, which on Linux is whatever answers a geo: URI — the same
            // xdg-open the reading pane hands a link to.
            var uri = "geo:0,0?q=" + Uri.EscapeDataString(address.Replace('\n', ' ').Trim());
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        ArgumentList = { uri },
                        UseShellExecute = false,
                    },
                };

                process.Start();
                shell.StatusRight = "Asked the desktop to map that address.";
            }
            catch (Exception ex)
            {
                shell.StatusRight = "Nothing on this desktop answers a map request.";
                Log.Warn("Could not open a map.", ex);
            }

            Log.Info($"People: asked the desktop for {uri}.");
        };
    }

    /// <summary>Check Names on the form: what the address book knows about who has been typed.</summary>
    private void CheckContactNames(ShellViewModel shell, ContactWindow window)
    {
        var contact = window.Surface.Current();
        var typed = contact.Named();
        if (typed.Length == 0)
        {
            shell.StatusRight = "Type a name first.";
            return;
        }

        var matches = App.Contacts.Matching(typed, 5);
        shell.StatusRight = matches.Count switch
        {
            0 => $"Nobody in the address book matches “{typed}”.",
            1 => $"“{typed}” is {matches[0].Named()}.",
            _ => $"{matches.Count} people match “{typed}”.",
        };

        Log.Info($"People: Check Names on “{typed}” matched {matches.Count}.");
    }

    /// <summary>Picture: a photograph off the disk, or the one there taken away.</summary>
    private async Task ChoosePictureAsync(ShellViewModel shell, ContactWindow window)
    {
        if (window.Surface.HasPhoto)
        {
            window.Surface.SetPhoto(null);
            shell.StatusRight = "The photograph has been removed.";
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Contact Picture",
            AllowMultiple = false,
            FileTypeFilter = [Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll],
        });

        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        window.Surface.SetPhoto(new ContactPhoto(buffer.ToArray(), MediaTypeOf(files[0].Name)));
        shell.StatusRight = $"{files[0].Name} is the contact's picture.";
    }

    private static string MediaTypeOf(string name)
        => Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

    /// <summary>
    /// Private on a contact: kept to oneself when the address book is shared.
    /// </summary>
    /// <remarks>
    /// Into the card, because a card is what travels: vCard 3.0's CLASS where the version has it,
    /// and <c>X-MAILBOX-PRIVATE</c> beside it for 4.0, which dropped the property. The store keeps
    /// a column of it too, so a list can draw the mark without parsing every card.
    /// </remarks>
    private void PrivateContact(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row || App.Pim.Item(row.Id) is not { } stored)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        var contact = App.Contacts.Full(row.Id) ?? row.Contact;
        var now = !contact.IsPrivate;

        SaveContactRow(shell, contact with { IsPrivate = now, LastModified = DateTimeOffset.UtcNow }, stored);

        shell.StatusRight = now ? $"{contact.Named()} is private." : $"{contact.Named()} is no longer private.";
        Log.Info($"People: contact {row.Id} is {(now ? "private" : "not private")}.");
    }

    /// <summary>
    /// Follow Up on a contact: the same flag menu the to-do list opens, over the same dates.
    /// </summary>
    /// <remarks>
    /// The flag is kept beside the card rather than in it (see <c>Contact.FollowUpDue</c>): when
    /// somebody means to ring a person back is their own business and not the address book's.
    /// </remarks>
    private void FlagContact(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row || App.Pim.Item(row.Id) is not { } stored)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        var contact = App.Contacts.Full(row.Id) ?? row.Contact;

        ShowFlagMenu(
            contact.Named(),
            contact.FollowUpDue,
            due =>
            {
                SaveContactRow(shell, contact with { FollowUpDue = due, FollowUpComplete = false }, stored);
                shell.StatusRight = due is { } when
                    ? $"{contact.Named()} is flagged, due {when.LocalDateTime:d}."
                    : $"The flag is off {contact.Named()}.";
                Log.Info($"People: contact {row.Id} due {due?.LocalDateTime.ToString("yyyy-MM-dd") ?? "—"}.");
            },
            () =>
            {
                SaveContactRow(shell, contact with { FollowUpComplete = true }, stored);
                shell.StatusRight = $"{contact.Named()} marked complete.";
                Log.Info($"People: contact {row.Id} follow-up complete.");
            });
    }

    /// <summary>Writes a contact over its own row, queues it, and shows it again.</summary>
    private void SaveContactRow(ShellViewModel shell, Contact contact, PimItem stored)
    {
        App.PimSync.QueuePut(
            Persisted("The contact", () => App.Contacts.Save(contact, stored.CollectionId, stored)));
        var people = EnsurePeople(shell);
        people.Reload();
        people.Select(stored.Id);
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
        var flyout = ContactMenu(shell, row);
        Log.Info($"People: the menu for “{row.Named()}” is open.");
        flyout.ShowAt(EnsurePeople(shell), showAtPointer: true);
    }

    /// <summary>The entries themselves, built once so a harness run can press one of them.</summary>
    private MenuFlyout ContactMenu(ShellViewModel shell, ContactRow row)
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

        // A distribution list is not a person, however it is named, so a group cannot link.
        Entry("Link Contacts…", () => _ = LinkContactsAsync(shell, row), !row.Contact.IsGroup);
        flyout.Items.Add(new Separator());

        Entry(
            App.ContactFavourites.Contains(row.Contact.Uid) ? "Remove from Favourites" : "Add to Favourites",
            () => FavouriteContact(shell));

        flyout.Items.Add(new Separator());
        Entry("Delete", () => _ = DeleteSelectedContactAsync(shell));

        return flyout;
    }

    /// <summary>
    /// The Linked Contacts manager for one person. Link and unlink write both cards at once and
    /// queue both to their servers, so the dialog commits as it goes and Done is just a close.
    /// </summary>
    private async Task LinkContactsAsync(ShellViewModel shell, ContactRow row)
    {
        var people = EnsurePeople(shell);

        var changed = await LinkContactsDialog.ManageAsync(
            this, App.Contacts, App.PimSync.QueuePut, row);

        if (!changed) return;

        people.Reload();
        people.Select(row.Id);
        shell.StatusRight = $"Linked contacts for “{row.Named()}” updated.";
        shell.ModuleStatusLeft = people.Status;
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

        ForwardCard(shell, App.Contacts.Full(row.Id) ?? row.Contact);
    }

    /// <summary>The vCard on a message, whether it came from the list or from the window.</summary>
    private void ForwardCard(ShellViewModel shell, Contact contact)
    {
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
        Log.Info($"People: forwarding “{contact.Named()}” as {name} ({card.Length} bytes of vCard).");
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

    /// <summary>Which arrangement a Current View button asks for, or null for anything else.</summary>
    private static ContactArrangement? ArrangementFor(CommandId id)
    {
        if (id == PeopleCommands.PeopleView.Id) return ContactArrangement.People;
        if (id == PeopleCommands.BusinessCardView.Id) return ContactArrangement.BusinessCard;
        if (id == PeopleCommands.CardView.Id) return ContactArrangement.Card;
        if (id == PeopleCommands.PhoneView.Id) return ContactArrangement.Phone;
        if (id == PeopleCommands.ListView.Id) return ContactArrangement.List;
        return null;
    }

    /// <summary>What a People button that is placed but not yet live says when pressed.</summary>
    private static string? WaitingPeopleCommand(CommandId id)
    {
        if (id == PeopleCommands.MoreCommunicate.Id) return "The other ways to reach somebody arrive with the module's actions.";
        if (id == PeopleCommands.MailMerge.Id) return "Mail merge needs a word processor to merge into, which is out of scope here.";

        if (id == PeopleCommands.OpenSharedContacts.Id) return "A shared address book is a CardDAV account — add one in Account Settings.";
        if (id == PeopleCommands.NewItems.Id) return "New Items arrives with the rest of the modules.";
        return null;
    }

    /// <summary>
    /// Share Contacts: publishing an address book, which is the Linux-native reading of it.
    /// </summary>
    /// <remarks>
    /// The reference's own Share Contacts sends a sharing invitation, and the thing it invites
    /// somebody into is a tenant's directory — out of scope by §3, and there is nothing here to
    /// invite them to. Rule 2 translates it: publish the book to an address, the same way a
    /// calendar is published, and whoever wants it fetches it. What goes up is every card in the
    /// book as one vCard document.
    /// <para>
    /// A menu rather than a button because the reference draws a chevron here, and because
    /// publishing has a second thing to say once it has started: where it goes, and how to stop.
    /// </para>
    /// </remarks>
    private void ShowShareContactsMenu(ShellViewModel shell)
    {
        var book = App.Contacts.Default();
        var already = App.Published.For(book.Id);

        var flyout = new MenuFlyout();

        void Entry(string header, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }

        Entry(already is null ? "_Publish This Address Book…" : "_Change Where It Is Published…",
            () => _ = PublishAddressBookAsync(shell, book));

        Entry("_Stop Publishing", () =>
        {
            App.Published.Remove(book.Id);
            shell.StatusRight = $"“{book.DisplayName}” is no longer published. What is already at {already?.Url} stays there.";
            Log.Info($"People: collection {book.Id} is no longer published.");
        }, already is not null);

        flyout.Items.Add(new Separator());
        Entry("_Open Shared Contacts…", () =>
            shell.StatusRight = "A shared address book is a CardDAV account — add one in Account Settings.");

        if (already is { } entry)
        {
            flyout.Items.Add(new Separator());
            flyout.Items.Add(new MenuItem { Header = $"Published to {entry.Url}", IsEnabled = false });
        }

        _ribbon.OpenMenuUnder(PeopleCommands.ShareContacts.Id, flyout, this);
    }

    private async Task PublishAddressBookAsync(ShellViewModel shell, Mailbox.Store.Pim.Collection book)
    {
        var already = App.Published.For(book.Id);
        var typed = await Prompt.AskAsync(
            this,
            "Publish Address Book",
            $"Address to publish “{book.DisplayName}” to:",
            already?.Url ?? string.Empty);
        if (string.IsNullOrWhiteSpace(typed)) return;

        if (!CalendarSubscription.TryAddress(typed, out var address))
        {
            await Confirm.TellAsync(this, "Publish Address Book", "That is not an address a book can be written to.");
            return;
        }

        App.Published.Set(book.Id, address.ToString(), book.DisplayName);
        shell.StatusRight = $"Publishing “{book.DisplayName}” to {address.Host}…";
        shell.StatusRight = await App.PimSync.PublishAsync(book.Id).ConfigureAwait(true);
    }

    // ---- Contacts -------------------------------------------------------------------------------

    /// <summary>Makes a contact or a group, opens it for editing, and writes it if it is kept.</summary>
    /// <remarks>
    /// A loop rather than a single pass, for the duplicate prompt's Cancel: cancelling the
    /// prompt means "back to the form", and a form that reopens empty has thrown away everything
    /// the reader typed to get here.
    /// </remarks>
    private async Task NewContactAsync(ShellViewModel shell, bool group = false)
    {
        var people = EnsurePeople(shell);

        var draft = new Contact
        {
            Uid = Contact.NewUid(),
            IsGroup = group,
        };
        var bookId = App.Contacts.Default().Id;

        while (true)
        {
            var window = new ContactWindow(App.Commands, draft, App.Contacts.AddressBooks(), bookId);
            WireContactWindow(shell, window);
            await window.ShowDialog(this);
            if (window.Result is not { Deleted: false } result)
            {
                if (window.Another) _ = NewContactAsync(shell, group);
                return;
            }

            // The duplicate check, on new contacts only — the Options page's own switch, and its
            // own words: "when saving new contacts". An edit is already somebody in particular.
            if (App.PeopleOptions.CheckDuplicates
                && App.Contacts.Duplicates(result.Contact) is { Count: > 0 } matches)
            {
                var choice = await DuplicateContactDialog.AskAsync(this, result.Contact, matches);

                if (choice.Answer == DuplicateAnswer.Cancel)
                {
                    draft = result.Contact;
                    bookId = result.CollectionId;

                    if (Mailbox.App.Theming.WindowCapture.IsRequested)
                    {
                        Log.Info($"Harness: the duplicate prompt was cancelled; the form comes back "
                                 + $"holding “{draft.Named()}”, {draft.Emails.Count} address(es), "
                                 + $"{draft.Phones.Count} number(s), company “{draft.Company}”.");
                    }

                    continue;
                }

                if (choice is { Answer: DuplicateAnswer.Update, Existing: { } existing })
                {
                    // The existing card takes the new information and keeps its identity: the
                    // uid is what its server knows it by, and what every link to it names.
                    //
                    // A merge and not a replacement. The words on the prompt are "update the
                    // selected contact with the new information", and writing the typed card over
                    // the stored one instead threw away everything the stored one knew that the
                    // typed one did not — an address, a birthday, a photograph, the other numbers.
                    var stored = App.Contacts.Repository.Item(existing.Id);
                    var whole = App.Contacts.Full(existing.Id) ?? existing.Contact;
                    var kept = ContactMerge.Update(whole, result.Contact) with { Uid = existing.Contact.Uid };
                    var updated = App.Contacts.Save(kept, existing.CollectionId, stored);
                    App.PimSync.QueuePut(updated);

                    people.Reload();
                    people.Select(updated.Id);
                    shell.StatusRight = $"“{kept.Named()}” updated.";
                    Log.Info($"People: contact {updated.Id} updated from a duplicate save.");
                    shell.ModuleStatusLeft = people.Status;
                    return;
                }
            }

            var written = App.Contacts.Save(result.Contact, result.CollectionId);
            App.PimSync.QueuePut(written);
            people.Reload();
            people.Select(written.Id);

            shell.StatusRight = $"“{result.Contact.Named()}” added to {App.Contacts.AddressBooks().First(b => b.Id == result.CollectionId).DisplayName}.";
            Log.Info($"People: contact {written.Id} added — {result.Contact.Named()}.");
            shell.ModuleStatusLeft = people.Status;
            return;
        }
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
        WireContactWindow(shell, window);
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
    /// <summary>
    /// The Address Book, as the ribbon's button opens it: to look people up rather than to pick
    /// them for a message.
    /// </summary>
    /// <remarks>
    /// The window does its own work — new entries, properties, deletes — because it holds the
    /// book and nothing else needs to know. The two it cannot do are the two that belong to the
    /// shell: writing a message, and opening the page where address books are managed.
    /// </remarks>
    private async Task ShowAddressBookAsync(ShellViewModel shell)
    {
        var dialog = new AddressBookDialog(App.Contacts, picking: false);

        var write = false;
        var options = false;
        dialog.NewMessageRequested += (_, _) => write = true;
        dialog.OptionsRequested += (_, _) => options = true;

        // The harness presses its menus once it is up: MAILBOX_ADDRESSBOOK=select:0,properties.
        if (Environment.GetEnvironmentVariable("MAILBOX_ADDRESSBOOK") is { Length: > 0 } actions)
        {
            dialog.Opened += (_, _) => _ = dialog.HarnessAsync(actions);
        }

        await dialog.ShowDialog(this);

        // After it has closed, not while it is open: a compose window opened over a modal one
        // would be trapped behind it, and Account Settings is a window of the same rank.
        // A blank message from the account the reader sends as by default, which is what
        // NewMessage with no link opens — the same window the New Email button gives.
        if (write)
        {
            NewMessage();
            return;
        }

        if (options)
        {
            var accounts = new AccountSettingsDialog("Address Books");
            await accounts.ShowDialog(this);
            EnsurePeople(shell).Reload();
        }

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
