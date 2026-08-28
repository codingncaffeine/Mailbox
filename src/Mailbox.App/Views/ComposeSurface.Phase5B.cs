namespace Mailbox.App.Views;

/// <summary>
/// The editor lane's doors onto the compose surface.
/// </summary>
/// <remarks>
/// One thing, and it is the one the signature rules turn on. <c>SendFromAccount</c> is the
/// shell's way in and runs before the window opens; the <c>From ⌄</c> menu is the reader's, runs
/// on a window that already has a body in it, and lands somewhere else entirely. A signature is
/// chosen per account, so which of the two ran decides whether the message carries the right
/// one — and no pose could press the second, because the menu is built in a private method and a
/// menu item cannot be clicked from outside the popup it lives in.
/// </remarks>
public sealed partial class ComposeSurface
{
    /// <summary>Picks a sending account exactly as the From menu's entry does. Harness only.</summary>
    public void PoseSendFrom(string address) => SendFrom(address);

    /// <summary>The account this message will be sent from, for a read-back.</summary>
    public string? HarnessSendingAddress => _sendingAddress;
}
