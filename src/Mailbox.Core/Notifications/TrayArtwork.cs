namespace Mailbox.Core.Notifications;

/// <summary>
/// Which mailbox the notification area shows: the empty one, or the one with post in it.
/// </summary>
/// <remarks>
/// Two drawings rather than one with a mark on it, because that is what the panel is for — a
/// glance at the corner of the screen should answer "is there anything for me" without reading
/// a number. The full box has letters in it and its flag up; the empty one is open and bare.
/// <para>
/// The rule is the reader's own: mail arriving fills the box, and opening it or marking it read
/// empties it again. So the state is the unread count and nothing else — no separate memory of
/// what has been "seen", which would need a rule for what happens when a message is marked
/// unread again and would disagree with the count in the folder pane the moment it did.
/// </para>
/// </remarks>
public static class TrayArtwork
{
    /// <summary>The empty mailbox: nothing waiting.</summary>
    public const string Empty = "mailbox-tray-empty";

    /// <summary>The mailbox with post in it and the flag up: something is waiting.</summary>
    public const string Full = "mailbox-tray-full";

    /// <summary>Which of the two a given unread count calls for.</summary>
    public static string For(int unread) => unread > 0 ? Full : Empty;

    /// <summary>The asset that drawing lives in, at the size the tray is being given.</summary>
    public static string AssetFor(int unread, int size) => $"{For(unread)}-{size}.png";
}
