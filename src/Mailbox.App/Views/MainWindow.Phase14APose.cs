using Avalonia.Threading;
using Mailbox.App.ViewModels;
using Mailbox.Core.Diagnostics;
using Mailbox.Import;

namespace Mailbox.App.Views;

/// <summary>
/// The read-back for what an export writes: the file that left, compared with the bytes the
/// store holds, inside the run that wrote it.
/// </summary>
/// <remarks>
/// §7.6a's promise is that a message leaves as its stored bytes, verbatim — an export re-encoded
/// on the way out breaks a signature that was valid a moment before, and nothing on screen would
/// say so. Proving it wants both halves in one place: the file, and the blob it claims to be.
/// A harness comparing them from outside can only reach the file.
/// <para>
/// Both exports also had to gain a save path first. <see cref="HarnessSavePath"/> already served
/// the calendar and contacts; <c>eml</c> and <c>mbox</c> now name themselves through it too, so
/// "it wrote nothing" and "it was never reached" stop being the same evidence.
/// </para>
/// <para>
/// mbox is the interesting half. mboxrd escaping is armour rather than content, so a message
/// written and read back should be the message — the format's own claim, and the one worth
/// attacking, because a mailbox exported to move machines and read back somewhere else is a
/// round trip a reader makes exactly once and cannot check.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this file's doors. Called once, from the constructor.</summary>
    private void WirePhase14ADoors()
    {
        // MAILBOX_EXPORT_REPORT=eml:<path>|mbox:<path> — read after everything else has run, so
        // the file being compared is the one the export command actually finished writing.
        if (Environment.GetEnvironmentVariable("MAILBOX_EXPORT_REPORT") is { Length: > 0 } spec)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => Dispatcher.UIThread.Post(
                    () => ReportExport(spec), DispatcherPriority.ContextIdle),
                DispatcherPriority.Background);
        }
    }

    private void ReportExport(string spec)
    {
        if (DataContext is not ShellViewModel shell || shell.CurrentMail is not { } mail)
        {
            Log.Warn("Harness: export report — no mail account is open.");
            return;
        }

        foreach (var part in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = part.IndexOf(':');
            if (split < 1) continue;
            var kind = part[..split].Trim().ToLowerInvariant();
            var path = part[(split + 1)..].Trim();

            if (!File.Exists(path))
            {
                Log.Warn($"Harness: export report — nothing was written to {path}.");
                continue;
            }

            try
            {
                if (kind == "eml") ReportEml(mail, shell, path);
                else if (kind == "mbox") ReportMbox(mail, shell, path);
                else Log.Warn($"Harness: export report — “{kind}” is not eml or mbox.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Harness: export report — {kind} failed.", ex);
            }
        }
    }

    private static void ReportEml(Mailbox.Store.MailRepository mail, ShellViewModel shell, string path)
    {
        if (shell.SelectedMessage is not { } row)
        {
            Log.Warn("Harness: export report — no message is selected to compare against.");
            return;
        }

        var written = File.ReadAllBytes(path);
        var stored = mail.LoadRaw(row.Id);

        if (stored is null)
        {
            Log.Info($"Harness: export report — eml: message {row.Id} has no stored bytes.");
            return;
        }

        Log.Info($"Harness: export report — eml: message {row.Id} “{row.Subject}”, "
                 + $"stored {stored.Length} byte(s), written {written.Length} byte(s), "
                 + Verdict(stored, written));
    }

    private static void ReportMbox(Mailbox.Store.MailRepository mail, ShellViewModel shell, string path)
    {
        if (shell.CurrentFolder is not { } folder)
        {
            Log.Warn("Harness: export report — no folder is open to compare against.");
            return;
        }

        using var stream = File.OpenRead(path);
        var back = Mbox.Read(stream);
        var stored = mail.Messages(folder.Id, limit: int.MaxValue);

        Log.Info($"Harness: export report — mbox: {folder.Name} holds {stored.Count} message(s), "
                 + $"the file reads back as {back.Count}.");

        var same = 0;
        for (var i = 0; i < Math.Min(back.Count, stored.Count); i++)
        {
            // Messages(...) orders newest first and ExportMbox writes in that same order, so the
            // pairing is positional and the report says so rather than assuming it.
            if (mail.LoadRaw(stored[i].Id) is not { } raw) continue;
            if (raw.AsSpan().SequenceEqual(back[i].Raw)) same++;
            else if (same + 1 == i + 1 || i < 3)
            {
                Log.Info($"Harness: export report — mbox: “{stored[i].Subject}” differs, "
                         + $"stored {raw.Length} byte(s), read back {back[i].Raw.Length}, "
                         + Verdict(raw, back[i].Raw));
            }
        }

        Log.Info($"Harness: export report — mbox: {same} of {Math.Min(back.Count, stored.Count)} "
                 + "message(s) round-tripped byte-identical.");
    }

    /// <summary>Byte-identical, or where and how the two part company.</summary>
    private static string Verdict(byte[] stored, byte[] written)
    {
        if (stored.AsSpan().SequenceEqual(written)) return "byte-identical.";

        var shared = Math.Min(stored.Length, written.Length);
        for (var i = 0; i < shared; i++)
        {
            if (stored[i] == written[i]) continue;
            return $"first difference at byte {i}: stored 0x{stored[i]:X2}, written 0x{written[i]:X2} "
                   + $"(around “{Around(stored, i)}” / “{Around(written, i)}”).";
        }

        return $"identical for {shared} byte(s); "
               + (stored.Length > written.Length
                   ? $"{stored.Length - shared} stored byte(s) did not leave."
                   : $"{written.Length - shared} byte(s) were added on the way out.");
    }

    private static string Around(byte[] bytes, int at)
    {
        var from = Math.Max(0, at - 12);
        var to = Math.Min(bytes.Length, at + 12);
        return string.Concat(bytes[from..to].Select(b => b switch
        {
            (byte)'\r' => "\\r",
            (byte)'\n' => "\\n",
            >= 0x20 and < 0x7F => ((char)b).ToString(),
            _ => $"\\x{b:x2}",
        }));
    }
}
