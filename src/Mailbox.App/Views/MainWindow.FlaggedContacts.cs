using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The flagged contacts on the to-do list: what a row stands for, and what acting on one does.
/// </summary>
/// <remarks>
/// The same join the flagged mail made, over this machine's own PIM store rather than an
/// account's — so it is shorter, there being no second store to find and no server flag to
/// journal. A contact's flag lives beside the card (<c>pim.db</c> step 6) because when somebody
/// means to ring a person back is their business and not the address book's, and writing one is
/// therefore an ordinary contact save that the DAV queue carries like any other.
/// <para>
/// <b>Delete is a stated divergence.</b> On a message it deletes the message, and the obvious
/// reading here would delete the person. No capture shows what the reference does with Delete on
/// a flagged contact, and removing somebody from an address book because a to-do was ticked off
/// is not a thing to guess at — so Delete clears the flag, the same as Remove from List, and the
/// status line says the contact is still in the address book.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The stored row behind a flagged-contact row, or null if it has since gone.</summary>
    private static PimItem? StoredContact(FlaggedContact contact)
        => App.Contacts.Repository.Item(contact.ItemId);

    /// <summary>
    /// The tick box, and Mark Complete, on a flagged contact: the follow-up is completed and the
    /// flag is kept, which is what makes it a row the Simple List can still show.
    /// </summary>
    private void ToggleFlaggedContact(ShellViewModel shell, TaskRow row, bool? complete = null)
    {
        if (row.Contact is not { } flagged || StoredContact(flagged) is not { } stored) return;
        if (App.Contacts.Full(flagged.ItemId) is not { } contact) return;

        var done = complete ?? !row.IsComplete;
        SaveFlaggedContact(shell, contact with { FollowUpComplete = done }, stored);

        shell.StatusRight = done
            ? $"“{row.Summary}” marked complete."
            : $"“{row.Summary}” put back on the list.";
        Log.Info($"Flagged contact: {flagged.ItemId} {(done ? "completed" : "reopened")}.");
    }

    /// <summary>
    /// The flag menu on a flagged contact, and Remove from List, which is the same write with no
    /// date: the flag goes and the person stays.
    /// </summary>
    private void FlagFlaggedContact(ShellViewModel shell, TaskRow row, DateTimeOffset? due)
    {
        if (row.Contact is not { } flagged || StoredContact(flagged) is not { } stored) return;
        if (App.Contacts.Full(flagged.ItemId) is not { } contact) return;

        SaveFlaggedContact(shell, contact with { FollowUpDue = due, FollowUpComplete = false }, stored);

        shell.StatusRight = due is { } when
            ? $"“{row.Summary}” is due {when.LocalDateTime:d}."
            : $"“{row.Summary}” taken off the list; the contact is still in the address book.";
        Log.Info($"Flagged contact: {flagged.ItemId} due {due?.LocalDateTime.ToString("yyyy-MM-dd") ?? "—"}.");
    }

    /// <summary>Opening a flagged-contact row opens the card, as double-clicking it in People does.</summary>
    private void OpenFlaggedContact(ShellViewModel shell, TaskRow row)
    {
        if (row.Contact is not { } flagged || StoredContact(flagged) is not { } stored) return;
        if (App.Contacts.Full(flagged.ItemId) is not { } contact)
        {
            shell.StatusRight = "That contact is no longer in the address book.";
            return;
        }

        var window = new ContactWindow(App.Commands, contact, App.Contacts.AddressBooks(), stored.CollectionId);
        WireContactWindow(shell, window);
        window.Show(this);
        Log.Info($"Flagged contact: opened {flagged.ItemId}.");
    }

    /// <summary>Writes the card, queues it, and refreshes everything showing to-dos.</summary>
    private void SaveFlaggedContact(ShellViewModel shell, Mailbox.Contacts.Contact contact, PimItem stored)
    {
        App.PimSync.QueuePut(App.Contacts.Save(contact, stored.CollectionId, stored));
        _people?.Reload();
        AfterFlaggedChange(shell);
    }
}
