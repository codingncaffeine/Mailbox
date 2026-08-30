using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;
using MimeKit;

namespace Mailbox.App.Views;

/// <summary>
/// The door the security audit needed: an adversarial corpus put through the reading pane, one case at a
/// time, with the document that reached the engine written out for each.
/// </summary>
/// <remarks>
/// <b>Why the pane rather than the sanitizer.</b> The sanitizer has a unit-test entry point and it
/// is not the thing under test. What a bypass costs is paid in the engine, which parses the
/// sanitizer's <em>output</em> with a different parser from the one the sanitizer read the
/// <em>input</em> with — so the only honest read-back is the markup the engine was handed, taken
/// out of a running pane that got there the way a message does: filed in a folder, selected in the
/// list, rendered.
/// <para>
/// <b>Why one run rather than one per case.</b> A corpus is a hundred cases and a capture run is
/// twenty seconds. Each case is filed as its own message and the selection is walked through them
/// inside one process, waiting on a dispatcher pass between each, which is also what proves the
/// thing a per-case run could never prove: that nothing a message is allowed carries to the next
/// one.
/// </para>
/// <para>
/// Everything it files is invented: an address at <c>example.net</c>, a subject naming the case.
/// The corpus itself is the payload, and it lives in the tests.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this file's doors. Called once, from the constructor.</summary>
    private void WirePhase15ADoors()
    {
        // MAILBOX_SANITIZER=<dir of .html cases>, written back to MAILBOX_SANITIZER_OUT.
        // At Background, after the folder pose and the list have settled: the messages have to
        // be in the folder the list is drawing before a row can be selected.
        if (Environment.GetEnvironmentVariable("MAILBOX_SANITIZER") is { Length: > 0 } corpus)
        {
            var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => _ = RunSanitizerCorpusAsync(corpus, hold), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Files each case, then reads each one back through the pane.
    /// </summary>
    private async Task RunSanitizerCorpusAsync(string corpus, IDisposable? hold)
    {
        try
        {
            if (DataContext is not ShellViewModel shell)
            {
                Log.Warn("Harness: sanitizer — there is no shell to deliver into.");
                return;
            }

            if (App.Accounts.All.FirstOrDefault() is not { } account)
            {
                Log.Warn("Harness: sanitizer — no account is open.");
                return;
            }

            if (account.Mail.FolderWithRole(account.Account.Id, FolderRole.Inbox) is not { } inbox)
            {
                Log.Warn($"Harness: sanitizer — {account.Account.Address} has no Inbox.");
                return;
            }

            var cases = Directory.Exists(corpus)
                ? Directory.GetFiles(corpus, "*.html").Order(StringComparer.Ordinal).ToList()
                : [corpus];

            if (cases.Count == 0)
            {
                Log.Warn($"Harness: sanitizer — no .html cases under {corpus}.");
                return;
            }

            var output = Environment.GetEnvironmentVariable("MAILBOX_SANITIZER_OUT");
            if (output is { Length: > 0 }) Directory.CreateDirectory(output);

            var names = new List<string>();
            foreach (var file in cases)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                names.Add(name);
                FileCase(account, inbox.Id, name, File.ReadAllText(file));
            }

            shell.Refresh();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Log.Info($"Harness: sanitizer — {names.Count} case(s) filed into "
                     + $"{account.Account.Address}/Inbox.");

            foreach (var name in names) await ReadCaseBackAsync(shell, name, output);

            Log.Info($"Harness: sanitizer — {names.Count} case(s) read back.");
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the sanitizer corpus failed.", ex);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    /// <summary>Selects one case's message and says what the pane made of it.</summary>
    /// <remarks>
    /// Two dispatcher passes: the selection has to reach the pane, and the pane renders inside its
    /// own refresh. Reading straight after setting the selection reported the previous case, which
    /// is a whole corpus off by one and would have looked like a clean run.
    /// </remarks>
    private async Task ReadCaseBackAsync(ShellViewModel shell, string name, string? output)
    {
        var subject = Subject(name);

        if (shell.Messages.FirstOrDefault(m => string.Equals(m.Subject, subject, StringComparison.Ordinal))
            is not { } row)
        {
            Log.Warn($"Harness: sanitizer case — “{name}” is not in the list.");
            return;
        }

        shell.SelectedMessage = row;
        shell.SelectedRow = row;

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        if (_reading is not { } pane || pane.RenderedDocument is not { } document)
        {
            Log.Warn($"Harness: sanitizer case — “{name}” rendered nothing.");
            return;
        }

        if (output is { Length: > 0 })
        {
            await File.WriteAllTextAsync(Path.Combine(output, name + ".out.html"), document);
        }

        var hosts = pane.RefusedNow
            .Select(r => r.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();

        Log.Info($"Harness: sanitizer case — “{name}”: {document.Length} character(s), "
                 + $"{pane.RefusedNow.Count} refused"
                 + (hosts.Count > 0 ? $", hosts {string.Join(" ", hosts)}" : ", no hosts")
                 + $", pictures {(pane.PicturesAllowedNow ? "allowed" : "held")}"
                 + $", {pane.InlinedNow} inlined.");
    }

    /// <summary>The subject a case's message carries, which is how it is found again.</summary>
    private static string Subject(string name) => "corpus " + name;

    /// <summary>
    /// One case as a message in the Inbox, with the real MIME beside the row.
    /// </summary>
    /// <remarks>
    /// The bytes matter: the pane parses what the store holds, so a row filed without them renders
    /// its preview text and sanitizes nothing at all — which would have read as a corpus that
    /// passed.
    /// </remarks>
    // Not File: this window is one partial class across thirty-odd files, and a member named
    // File on any of them shadows System.IO.File for all of them.
    private static void FileCase(OpenAccount account, long folderId, string name, string html)
    {
        var message = new MimeMessage
        {
            Subject = Subject(name),
            Date = new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero),
        };

        message.From.Add(new MailboxAddress("A. Stranger", "stranger@example.net"));
        message.To.Add(new MailboxAddress("A. Person", account.Account.Address));
        message.MessageId = $"harness-corpus-{name}@example.net";
        message.Body = new TextPart("html") { Text = html };

        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var raw = buffer.ToArray();

        var summary = new MessageSummary(
            0, folderId, $"harness-corpus-{name}", message.MessageId,
            "A. Stranger", "stranger@example.net", message.Subject,
            "An adversarial case.",
            message.Date, message.Date, raw.Length,
            IsRead: false, IsFlagged: false, HasAttachment: false);

        account.Mail.AddMessage(folderId, summary, raw);
    }
}
