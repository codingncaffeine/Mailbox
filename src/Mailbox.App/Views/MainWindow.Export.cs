using Avalonia.Platform.Storage;
using Mailbox.App.ViewModels;
using Mailbox.Contacts;
using Mailbox.Core.Diagnostics;
using Mailbox.Import;
using Mailbox.Scheduling;
using Mailbox.Store.Pim;

namespace Mailbox.App.Views;

/// <summary>
/// The Save As page's four exports. Mail leaves as its stored bytes, verbatim — the design's
/// promise, and the reason none of this re-serializes a message it received. The PIM exports
/// serialize through the same codecs the sync writes with, so what leaves is what a server
/// would have been sent.
/// </summary>
public partial class MainWindow
{
    private async Task ExportEmlAsync(ShellViewModel shell)
    {
        if (shell.SelectedMessage is not { } row || shell.CurrentMail is not { } mail)
        {
            shell.StatusRight = "Select a message to save.";
            return;
        }

        // The same bargain the calendar and contacts exports make: a picker is a desktop window
        // a headless run cannot answer, so "it wrote nothing" and "it did nothing" were the same
        // evidence. The design's promise — that what leaves is the stored bytes, verbatim — could
        // therefore not be checked through the command at all, only through the method under it.
        var path = HarnessSavePath("eml");
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Message As",
                SuggestedFileName = SafeName(row.Subject, "message") + ".eml",
                FileTypeChoices = [new FilePickerFileType("Email message") { Patterns = ["*.eml"] }],
            });

            path = file?.TryGetLocalPath();
        }

        if (path is null) return;

        shell.StatusRight = MailFileImport.ExportEml(mail, row.Id, path)
            ? $"Saved to {System.IO.Path.GetFileName(path)}."
            : "That message's stored bytes are not to hand.";
    }

    private async Task ExportMboxAsync(ShellViewModel shell)
    {
        if (shell.CurrentMail is not { } mail || shell.CurrentFolder is not { } folder)
        {
            shell.StatusRight = "Open a folder to save.";
            return;
        }

        var folderId = folder.Id;
        var path = HarnessSavePath("mbox");
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Folder As mbox",
                SuggestedFileName = SafeName(folder.Name, "folder") + ".mbox",
                FileTypeChoices = [new FilePickerFileType("mbox mailbox") { Patterns = ["*.mbox"] }],
            });

            path = file?.TryGetLocalPath();
        }

        if (path is null) return;

        var written = await Task.Run(() => MailFileImport.ExportMbox(mail, folderId, path));
        shell.StatusRight = $"{written:N0} message(s) saved to {System.IO.Path.GetFileName(path)}.";
        Log.Info($"Export: folder {folderId} → {path} ({written}).");
    }

    private async Task ExportIcsAsync(ShellViewModel shell)
    {
        var calendar = App.Pim.DefaultCalendar();

        // A picker is a desktop window and a headless run cannot answer one, so "it wrote
        // nothing" and "it did nothing" were the same evidence. The harness names the file
        // instead; a reader is always asked.
        var path = HarnessSavePath("ics");
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Calendar As",
                SuggestedFileName = SafeName(calendar.DisplayName, "calendar") + ".ics",
                FileTypeChoices = [new FilePickerFileType("iCalendar") { Patterns = ["*.ics"] }],
            });

            path = file?.TryGetLocalPath();
        }

        if (path is null) return;

        var count = await Task.Run(() =>
        {
            var events = App.Pim.Items(calendar.Id)
                .Where(i => i.Kind == CollectionKind.Events)
                .Select(PimEventCodec.FromItem)
                .ToList();
            File.WriteAllText(path, ICalendarCodec.SerializeCalendar(events));
            return events.Count;
        });

        shell.StatusRight = $"{count:N0} appointment(s) saved to {System.IO.Path.GetFileName(path)}.";
        Log.Info($"Export: calendar {calendar.Id} → {path} ({count}).");
    }

    private async Task ExportVcfAsync(ShellViewModel shell)
    {
        var book = App.Contacts.Default();

        // The same bargain the calendar export makes: a picker is a desktop window a headless run
        // cannot answer, so the harness names the file and a reader is always asked.
        var path = HarnessSavePath("vcf");
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Contacts As",
                SuggestedFileName = SafeName(book.DisplayName, "contacts") + ".vcf",
                FileTypeChoices = [new FilePickerFileType("vCard") { Patterns = ["*.vcf"] }],
            });

            path = file?.TryGetLocalPath();
        }

        if (path is null) return;

        var count = await Task.Run(() =>
        {
            var cards = App.Contacts.Rows([book.Id])
                .Select(r => App.Contacts.Full(r.Id) ?? r.Contact)
                .ToList();
            File.WriteAllText(path, VCardCodec.SerializeMany(cards));
            return cards.Count;
        });

        shell.StatusRight = $"{count:N0} contact(s) saved to {System.IO.Path.GetFileName(path)}.";
        Log.Info($"Export: address book {book.Id} → {path} ({count}).");
    }

    /// <summary>A file name out of a title: the unusable characters dropped, never empty.</summary>
    private static string SafeName(string? title, string fallback)
    {
        var kept = new string((title ?? string.Empty)
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return kept.Length > 0 ? kept : fallback;
    }
}
