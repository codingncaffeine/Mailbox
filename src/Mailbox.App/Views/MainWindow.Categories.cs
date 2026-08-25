using Avalonia.Controls;
using Mailbox.App.ViewModels;
using Mailbox.Contacts;
using Mailbox.Core.Diagnostics;
using Mailbox.Scheduling;
using Mailbox.Store;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The one set of colour categories where it meets the modules: the menu every module's
/// Categorize button opens, and what a rename or a delete does to the items that carried it.
/// </summary>
/// <remarks>
/// A partial of the shell because it needs all four codecs and the sync queue at once — a
/// category is the one thing in the application that every module's items carry, and putting it
/// right on a rename means writing an appointment, a task, a note and a card by the same rule.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The Categorize menu for anything that is not a message: the set with a tick against what
    /// this item already carries, Clear All Categories at the head, and All Categories… under it.
    /// </summary>
    /// <remarks>
    /// The same shape the mail module's menu has, which is the reference's own — the difference
    /// is only what is being tagged. What comes back is the whole list the item should carry, so
    /// a module writes it through its own save path and nothing here knows about payloads.
    /// </remarks>
    private void ShowItemCategorizeMenu(
        string subject,
        IReadOnlyList<string> carried,
        Action<IReadOnlyList<string>> apply)
        => ItemCategoryMenu.Show(
            App.Categories,
            _ribbon ?? (Control)this,
            subject,
            carried,
            apply,
            () => _ = new ColorCategoriesDialog(App.Categories, RewriteCategoryOnItems).ShowDialog(this));

    /// <summary>
    /// Puts a renamed or removed category right on the items that carried it.
    /// </summary>
    /// <remarks>
    /// Through the codecs and the modules' own save paths rather than by editing the column: an
    /// item's categories are written into its own iCalendar or vCard text, and that text is what
    /// goes to the server. Writing it again also queues it, so the rename reaches the other
    /// clients rather than being a local relabelling that the next pull undoes.
    /// </remarks>
    private void RewriteCategoryOnItems(IReadOnlyList<PimItem> items, string from, string? to)
    {
        if (items.Count == 0) return;

        var written = 0;
        foreach (var item in items)
        {
            if (App.Pim.Item(item.Id) is not { } current) continue;

            switch (current.Kind)
            {
                case CollectionKind.Events:
                {
                    var appointment = PimEventCodec.FromItem(current);
                    var categories = CategoryBook.Rewrite(appointment.Categories, from, to);
                    if (Same(appointment.Categories, categories)) continue;
                    SaveAppointment(appointment with { Categories = categories, LastModified = DateTimeOffset.UtcNow }, current, current.CollectionId);
                    break;
                }

                case CollectionKind.Tasks:
                {
                    var task = PimTodoCodec.FromItem(current);
                    var categories = CategoryBook.Rewrite(task.Categories, from, to);
                    if (Same(task.Categories, categories)) continue;
                    SaveTask(task with { Categories = categories, LastModified = DateTimeOffset.UtcNow }, current, current.CollectionId);
                    break;
                }

                case CollectionKind.Journal:
                {
                    var entry = PimJournalCodec.FromItem(current);
                    var categories = CategoryBook.Rewrite(entry.Categories, from, to);
                    if (Same(entry.Categories, categories)) continue;
                    SaveNote(entry with { Categories = categories, LastModified = DateTimeOffset.UtcNow }, current, current.CollectionId);
                    break;
                }

                case CollectionKind.Contacts:
                {
                    var contact = PimContactCodec.FromItem(current);
                    var categories = CategoryBook.Rewrite(contact.Categories, from, to);
                    if (Same(contact.Categories, categories)) continue;
                    SaveContact(contact with { Categories = categories }, current.CollectionId, current);
                    break;
                }

                default:
                    continue;
            }

            written++;
        }

        Log.Info($"Categories: “{from}” {(to is null ? "removed from" : $"renamed to “{to}” on")} {written} item(s).");
    }

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.SequenceEqual(b, StringComparer.Ordinal);

    /// <summary>
    /// The harness's way at the set itself: <c>rename:Old&gt;New</c>, <c>delete:Name</c> or
    /// <c>add:Name=category.token</c>, each followed by what every store then holds.
    /// </summary>
    /// <remarks>
    /// The dialog does this behind a prompt, and a prompt blocks a capture run — so the pose
    /// calls the same two things the dialog calls, in the same order, and reads the items back.
    /// That is the claim a screenshot of a menu could never make.
    /// </remarks>
    private void PoseCategoryOp(string spec)
    {
        var text = spec.Trim();

        if (text.StartsWith("add:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text["add:".Length..];
            var name = rest.Contains('=', StringComparison.Ordinal) ? rest[..rest.IndexOf('=', StringComparison.Ordinal)] : rest;
            var token = rest.Contains('=', StringComparison.Ordinal) ? rest[(rest.IndexOf('=', StringComparison.Ordinal) + 1)..] : "category.blue";
            var made = App.Categories.Add(name.Trim(), token.Trim());
            Log.Info($"Harness: the set now holds {App.Categories.All().Count}, newest “{made.Name}” ({made.ColourToken}).");
            return;
        }

        if (text.StartsWith("rename:", StringComparison.OrdinalIgnoreCase))
        {
            var pair = text["rename:".Length..].Split('>', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || App.Categories.Named(pair[0]) is not { } category)
            {
                Log.Info($"Harness: nothing in the set is called “{pair[0]}” — say rename:Old>New.");
                return;
            }

            var carried = App.Categories.Rename(category.Id, pair[1]);
            Log.Info($"Harness: “{category.Name}” carried {carried.Count} item(s).");
            RewriteCategoryOnItems(carried, category.Name, pair[1]);
            ReadCategoriesBack(carried, pair[1]);
            return;
        }

        if (text.StartsWith("delete:", StringComparison.OrdinalIgnoreCase))
        {
            var name = text["delete:".Length..].Trim();
            if (App.Categories.Named(name) is not { } category)
            {
                Log.Info($"Harness: nothing in the set is called “{name}”.");
                return;
            }

            var carried = App.Categories.Delete(category.Id);
            Log.Info($"Harness: “{category.Name}” carried {carried.Count} item(s).");
            RewriteCategoryOnItems(carried, category.Name, null);
            ReadCategoriesBack(carried, null);
            return;
        }

        Log.Info($"Harness: “{text}” is not a category operation — say add:, rename: or delete:.");
    }

    /// <summary>What the items say after a rename or a delete, read out of the store again.</summary>
    private static void ReadCategoriesBack(IReadOnlyList<PimItem> items, string? expected)
    {
        foreach (var item in items)
        {
            if (App.Pim.Item(item.Id) is not { } after) continue;

            var stillInText = after.RawPayload.Contains("CATEGORIES", StringComparison.OrdinalIgnoreCase)
                && expected is { Length: > 0 }
                && after.RawPayload.Contains(expected, StringComparison.OrdinalIgnoreCase);

            Log.Info($"Harness: item {after.Id} “{after.Summary}” now carries "
                + $"[{after.Categories}], sync {after.SyncState}"
                + (expected is { Length: > 0 } ? $", in its own text: {stillInText}" : string.Empty) + ".");
        }
    }

    // ---- The modules' own Categorize buttons ----------------------------------------------------

    /// <summary>Categorize on the selected task.</summary>
    private void CategorizeTask(ShellViewModel shell)
    {
        var tasks = EnsureTasks(shell);
        if (tasks.Selected is not { } row || App.Pim.Item(row.ItemId) is not { } item)
        {
            shell.StatusRight = "Select a task first.";
            return;
        }

        var task = PimTodoCodec.FromItem(item);
        ShowItemCategorizeMenu(task.Summary, task.Categories, categories =>
        {
            SaveTask(task with { Categories = categories, LastModified = DateTimeOffset.UtcNow }, item, item.CollectionId);
            shell.StatusRight = Said(task.Summary, categories);
        });
    }

    /// <summary>Categorize on the selected note, which is also what colours it.</summary>
    private void CategorizeNote(ShellViewModel shell)
    {
        var notes = EnsureNotes(shell);
        if (notes.Selected is not { } row || App.Pim.Item(row.ItemId) is not { } item)
        {
            shell.StatusRight = "Select a note first.";
            return;
        }

        var note = PimJournalCodec.FromItem(item);
        ShowItemCategorizeMenu(note.Titled(), note.Categories, categories =>
        {
            SaveNote(note with { Categories = categories, LastModified = DateTimeOffset.UtcNow }, item, item.CollectionId);
            shell.StatusRight = Said(note.Titled(), categories);
        });
    }

    /// <summary>Categorize on the selected journal entry.</summary>
    private void CategorizeJournalEntry(ShellViewModel shell)
    {
        var journal = EnsureJournal(shell);
        if (journal.Selected is not { } row || App.Pim.Item(row.ItemId) is not { } item)
        {
            shell.StatusRight = "Select an entry first.";
            return;
        }

        var entry = PimJournalCodec.FromItem(item);
        ShowItemCategorizeMenu(entry.Summary, entry.Categories, categories =>
        {
            SaveJournalEntry(entry with { Categories = categories, LastModified = DateTimeOffset.UtcNow }, item, item.CollectionId);
            shell.StatusRight = Said(entry.Summary, categories);
        });
    }

    /// <summary>Categorize on the selected contact.</summary>
    private void CategorizeContact(ShellViewModel shell)
    {
        var people = EnsurePeople(shell);
        if (people.Selected is not { } row || App.Pim.Item(row.Id) is not { } item)
        {
            shell.StatusRight = "Select a contact first.";
            return;
        }

        var contact = PimContactCodec.FromItem(item);
        ShowItemCategorizeMenu(contact.Named(), contact.Categories, categories =>
        {
            SaveContact(contact with { Categories = categories }, item.CollectionId, item);
            people.Reload();
            shell.StatusRight = Said(contact.Named(), categories);
        });
    }

    /// <summary>Writes a contact and queues it, which is what every other path here does.</summary>
    private void SaveContact(Contact contact, long collectionId, PimItem existing)
        => App.PimSync.QueuePut(
            Persisted("The contact", () => App.Contacts.Save(contact, collectionId, existing)));

    /// <summary>Categorize on the selected appointment.</summary>
    private void CategorizeAppointment(ShellViewModel shell)
    {
        var calendar = EnsureCalendar(shell);
        if (calendar.SelectedEntry is not { } entry || App.Pim.Item(entry.ItemId) is not { } item)
        {
            shell.StatusRight = "Select an appointment first.";
            return;
        }

        var appointment = PimEventCodec.FromItem(item);
        ShowItemCategorizeMenu(appointment.Summary, appointment.Categories, categories =>
        {
            SaveAppointment(appointment with { Categories = categories, LastModified = DateTimeOffset.UtcNow }, item, item.CollectionId);
            shell.StatusRight = Said(appointment.Summary, categories);
        });
    }

    private static string Said(string subject, IReadOnlyList<string> categories)
        => categories.Count == 0
            ? $"Categories cleared on “{subject}”."
            : $"“{subject}” categorised {string.Join(", ", categories)}.";
}
