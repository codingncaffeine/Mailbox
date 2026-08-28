namespace Mailbox.App.Views;

/// <summary>
/// The compose window's forwarders for the harness doors on <see cref="ComposeSurface"/>.
/// </summary>
/// <remarks>
/// The window is a thin shell over the surface and forwards its public members one for one; these
/// are the same arrangement for the doors, so a pose driving a window and a pose driving an inline
/// reply ask the same questions of the same code.
/// </remarks>
public sealed partial class ComposeWindow
{
    /// <summary>The surface this window hosts, for a pose that drives it directly.</summary>
    internal ComposeSurface Surface => _surface;

    /// <summary>Fills the whole address block, Bcc included.</summary>
    public void PoseRecipients(string? to, string? cc, string? bcc, string? subject)
        => _surface.PoseRecipients(to, cc, bcc, subject);

    /// <summary>Presses a compose command by id, through the dispatcher a ribbon button uses.</summary>
    public void PressCommand(string id) => _surface.PressCommand(id);

    /// <summary>Whether a command is usable right now.</summary>
    public bool HarnessEnabled(string id) => _surface.HarnessEnabled(id);

    /// <summary>Attaches real files by path, without the desktop's picker.</summary>
    public Task PoseAttachAsync(IEnumerable<string> paths) => _surface.PoseAttachAsync(paths);

    public (string To, string Cc, string Bcc, string Subject) HarnessFields => _surface.HarnessFields;

    public (bool IsVisible, string Text) HarnessStatus => _surface.HarnessStatus;

    public (bool Bcc, bool From) HarnessOptionalRows => _surface.HarnessOptionalRows;

    public string HarnessFrom => _surface.HarnessFrom;

    public (bool IsVisible, string Text, IReadOnlyList<string> Files) HarnessAttachments
        => _surface.HarnessAttachments;

    public (string Protection, DateTimeOffset? NotBefore, bool PlainText, long? DraftId) HarnessState
        => _surface.HarnessState;

    public IReadOnlyList<string> HarnessAccountMenu() => _surface.HarnessAccountMenu();

    public (bool IsOpen, int Offered, IReadOnlyList<string> Entries) HarnessCompletion(int line)
        => _surface.HarnessCompletion(line);

    public void PoseTypingInto(int line, string text) => _surface.PoseTypingInto(line, text);

    public void PoseForget(string address) => _surface.PoseForget(address);

    public Task<long?> PoseSaveDraftAsync() => _surface.PoseSaveDraftAsync();

    public Task<byte[]?> HarnessBuildAsync() => _surface.HarnessBuildAsync();
}
