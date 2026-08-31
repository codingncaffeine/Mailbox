using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// The compose surface's harness doors: what a pose sets, what it presses, and what it reads back.
/// </summary>
/// <remarks>
/// Everything below is reachable only from a <c>MAILBOX_</c> variable. The audit's own rule is
/// press-it-and-read-it-back, and the compose window had half of that: poses could fill the header
/// and press Send, but nothing could press an arbitrary compose command, put a real file on a
/// message without the desktop's picker, or ask the surface what it currently holds. A capture
/// cannot answer any of those — the info bar is one wrapped line, the attachment strip is text,
/// the From menu is a popup — so they are asked rather than photographed.
/// <para>
/// Held apart from <see cref="ComposeSurface"/>'s own file because these are the audit's doors
/// rather than the surface's behaviour, and because that file is 2,600 lines of the thing being
/// audited.
/// </para>
/// </remarks>
public sealed partial class ComposeSurface
{
    /// <summary>
    /// Fills the whole address block, Bcc included, for a pose that has to control every field.
    /// </summary>
    /// <remarks>
    /// <see cref="PoseHeader"/> reaches To, Cc and Subject only, and it refuses a subject that
    /// looks like a window title — both of which are right for the capture it was written for and
    /// wrong for a pose whose whole point is what those fields put on the wire. A display name
    /// carrying a comma is the case that needs it most: it has to survive the splitter, the parse
    /// and the encoder, and nothing could put one on the line to find out.
    /// <para>
    /// Bcc shows itself when it is given something, exactly as a <c>mailto:</c> link's does. A
    /// field filled under a hidden row would be a message with a recipient nobody can see.
    /// </para>
    /// </remarks>
    public void PoseRecipients(string? to, string? cc, string? bcc, string? subject)
    {
        if (to is not null) _to.Text = to;
        if (cc is not null) _cc.Text = cc;
        if (bcc is not null) _bcc.Text = bcc;
        if (subject is not null) _subject.Text = subject;

        if (!string.IsNullOrWhiteSpace(_bcc.Text)) _bccRow.IsVisible = true;

        UpdateTitle();
        UpdateStatus();
        RaiseEnablementChanged();
    }

    /// <summary>The four address fields as they stand, so a pose can read back what a command did to them.</summary>
    public (string To, string Cc, string Bcc, string Subject) HarnessFields
        => (_to.Text ?? string.Empty, _cc.Text ?? string.Empty,
            _bcc.Text ?? string.Empty, _subject.Text ?? string.Empty);

    /// <summary>
    /// What the info bar says, and whether it is showing at all.
    /// </summary>
    /// <remarks>
    /// The surface answers a command it cannot carry out by writing here rather than by throwing,
    /// so this line is the verdict on every press — and it wraps, so a capture reads it back
    /// unreliably where a string does not.
    /// </remarks>
    public (bool IsVisible, string Text) HarnessStatus
        => (_infoBar.IsVisible, _status.Text ?? string.Empty);

    /// <summary>Which of the two optional address rows are showing.</summary>
    public (bool Bcc, bool From) HarnessOptionalRows => (_bccRow.IsVisible, _fromRow.IsVisible);

    /// <summary>The address this message will be sent from, as the From row reports it.</summary>
    public string HarnessFrom => _fromAddress.Text ?? string.Empty;

    /// <summary>The attachment strip's text, and the files behind it.</summary>
    public (bool IsVisible, string Text, IReadOnlyList<string> Files) HarnessAttachments
        => (_attachmentRow.IsVisible, AttachedSummary(),
            [.. _attachments.Select(f => f.Name), .. _carried.Select(c => c.Name)]);

    /// <summary>Whether this message is signed, sealed, both or neither; and the delayed-delivery time.</summary>
    public (string Protection, DateTimeOffset? NotBefore, bool PlainText, long? DraftId) HarnessState
        => (_protection.ToString(), _notBefore, _plainText, _draftId);

    /// <summary>
    /// Presses a compose command through the same entry point a ribbon button uses.
    /// </summary>
    /// <remarks>
    /// <see cref="Invoke"/> is the one route a host's ribbon, the Send button and a keystroke all
    /// arrive at, so pressing it here is pressing the button. The pose that existed before this
    /// reached exactly three commands — Send, Sign and Encrypt — which left Check Names, Show Bcc,
    /// Show From, the receipts, Delay Delivery, the importance pair and the format switch provable
    /// only by reading their handlers.
    /// </remarks>
    public void PressCommand(string id) => Invoke(new CommandId(id));

    /// <summary>Whether a command is usable right now, for the run that checks the ribbon's greying.</summary>
    public bool HarnessEnabled(string id) => IsCommandEnabled(new CommandId(id));

    /// <summary>Removes the first attachment whose name carries the words — the chip's own Remove.</summary>
    public string PoseRemoveAttachment(string named)
    {
        if (_attachments.FirstOrDefault(f => f.Name.Contains(named, StringComparison.OrdinalIgnoreCase)) is { } file)
        {
            _attachments.Remove(file);
            AfterAttachmentRemoved(file.Name);
            return file.Name;
        }

        if (_carried.FirstOrDefault(c => c.Name.Contains(named, StringComparison.OrdinalIgnoreCase)) is { } part)
        {
            _carried.Remove(part);
            AfterAttachmentRemoved(part.Name);
            return part.Name;
        }

        return $"nothing is attached under “{named}”";
    }

    /// <summary>
    /// Attaches real files by path, without the desktop's picker.
    /// </summary>
    /// <remarks>
    /// The picker is a modal owned by the desktop and no pose can answer one, so the add half of
    /// the attachment strip had no door at all. The files go through
    /// <see cref="IStorageProvider.TryGetFileFromPathAsync"/> so what lands in the list is the same
    /// <see cref="IStorageFile"/> the picker would have handed over, and the send path reads it the
    /// same way — which is the point: attaching a file the send cannot open would prove nothing.
    /// </remarks>
    public async Task PoseAttachAsync(IEnumerable<string> paths)
    {
        if (Owner is not { StorageProvider: { } storage }) return;

        var files = new List<IStorageFile>();
        foreach (var path in paths)
        {
            var file = await storage.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(path)));
            if (file is null)
            {
                Log.Warn($"Harness: could not open {path} to attach it.");
                continue;
            }

            files.Add(file);
        }

        // The same tail the picker and a drop run, so a posed file lands the way a real one does.
        AddAttachments(files);
    }

    /// <summary>
    /// The From button's menu, described rather than photographed.
    /// </summary>
    /// <remarks>
    /// A flyout is a separate surface and never appears in a capture (rule 6), and this one is
    /// built full and then shown — so the only way to see whether it lists the accounts and ticks
    /// the sending one is to build it and read it.
    /// </remarks>
    public IReadOnlyList<string> HarnessAccountMenu()
        => [.. AccountMenuItems().Select(item =>
            $"{item.Header}{(item.Icon is null ? string.Empty : "  [ticked]")}"
            + (item.IsEnabled ? string.Empty : "  [greyed]"))];

    /// <summary>
    /// What the Auto-Complete List offers on Cc and Bcc, not only To.
    /// </summary>
    /// <remarks>
    /// All three lines get a list of their own and they are separate objects; the pose that
    /// existed read the first one, so a list attached to the wrong field would have looked right.
    /// </remarks>
    public (bool IsOpen, int Offered, IReadOnlyList<string> Entries) HarnessCompletion(int line)
        => line >= 0 && line < _completions.Count
            ? (_completions[line].IsOpen, _completions[line].Offered, _completions[line].Describe())
            : (false, 0, []);

    /// <summary>Types into Cc or Bcc as a person would, so their lists can be asked too.</summary>
    public void PoseTypingInto(int line, string text)
    {
        var box = line switch { 1 => _cc, 2 => _bcc, _ => _to };
        box.Focus();
        box.Text = text;
        box.CaretIndex = text.Length;
        if (line >= 0 && line < _completions.Count) _completions[line].Refresh();
    }

    /// <summary>Takes an address out of the Auto-Complete List, which is what the ✕ on a suggestion does.</summary>
    public void PoseForget(string address) => ForgetRecipient(address);

    /// <summary>Saves to Drafts and hands back the row it wrote, for a pose that reads the store after.</summary>
    public async Task<long?> PoseSaveDraftAsync()
    {
        await SaveDraftAsync();
        return _draftId;
    }

    /// <summary>
    /// The message this window would send, built through the real path and written out as bytes.
    /// </summary>
    /// <remarks>
    /// For the draft round trip: a draft is proved identical by comparing what a reopened window
    /// builds against what the first one wrote, and going through the outbox for that would mean
    /// sending it. Same builder, same account, same everything the send uses.
    /// </remarks>
    public async Task<byte[]?> HarnessBuildAsync()
    {
        if (SendingAccount() is not { } account) return null;

        var message = await BuildMessageAsync(account);
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        return buffer.ToArray();
    }
}
