using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App;

/// <summary>
/// Puts a window that came up without the keyboard in front of the others once, the way a taskbar
/// raises a window it is asked to, and leaves it an ordinary window after that.
/// </summary>
/// <remarks>
/// The progress toaster used to be kept above everything for as long as it was up. That is how it
/// came up over the windows the reader was working in without taking the keyboard, and it is also
/// how it stayed over them: a click on another application went to that application and left the
/// toaster on top of it, and nothing short of closing the toaster moved it out of the way. What
/// was wanted is a window that comes up in front and then behaves like any other.
/// <para>
/// The window manager will not put it in front by itself. A window refused the keyboard is
/// stacked under the window that has it — KWin's rule and Mutter's alike, the point of refusing
/// the keyboard being that the reader's work is not covered. What does put it in front is
/// <c>_NET_RESTACK_WINDOW</c> with the source a pager or a taskbar gives, which both honour
/// without weighing whose turn it is, and which raises without activating. After that the window
/// is an ordinary one, and the next window the reader clicks goes over it: KWin raises a window on
/// a click by default, the window already being worked in included.
/// </para>
/// <para>
/// Only once the window manager has taken the window on. A request about a window it has not
/// managed yet is a request about nothing, and dropped; <c>WM_STATE</c> is what a window manager
/// puts on a window as it maps it, so the request goes once that is there. Asked over a display
/// connection of its own, because the toolkit's is not reachable from here — through the library
/// the toolkit's X11 backend has already loaded, so the machine needs nothing new.
/// </para>
/// </remarks>
internal static class X11Stacking
{
    private const string LibX11 = "libX11.so.6";

    /// <summary>How often to look for the window manager's mark, and for how long.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(5);

    private const int ClientMessage = 33;
    private const int Success = 0;
    private const nint SubstructureNotifyMask = 1 << 19;
    private const nint SubstructureRedirectMask = 1 << 20;

    /// <summary><c>_NET_RESTACK_WINDOW</c>'s source for a pager, and its detail for "to the top".</summary>
    private const nint FromPager = 2;

    private const nint Above = 0;

    /// <summary>
    /// Raises <paramref name="window"/> once the window manager has mapped it. Nothing for a window
    /// that is not an X11 one, and nothing but a line in the log when the display cannot be asked.
    /// </summary>
    public static void RaiseOnce(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID", Handle: var xid } || xid == 0) return;

        nint display;
        try
        {
            display = XOpenDisplay(0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"Stacking: {LibX11} could not be used ({ex.Message}), so the window stays where the window manager put it.");
            return;
        }

        if (display == 0)
        {
            Log.Warn("Stacking: the X display could not be opened, so the window stays where the window manager put it.");
            return;
        }

        var managed = XInternAtom(display, "WM_STATE\0"u8.ToArray(), 0);
        var restack = XInternAtom(display, "_NET_RESTACK_WINDOW\0"u8.ToArray(), 0);
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = Poll };
        var done = false;

        void Finish()
        {
            if (done) return;
            done = true;

            timer.Stop();
            window.Closed -= OnClosed;
            XCloseDisplay(display);
        }

        void OnClosed(object? sender, EventArgs e) => Finish();

        timer.Tick += (_, _) =>
        {
            if (done) return;

            if (IsManaged(display, xid, managed))
            {
                Raise(display, xid, restack);
                Log.Info("Stacking: raised once over the windows in front, without the keyboard.");
                Finish();
                return;
            }

            if (DateTime.UtcNow - started < GiveUp) return;

            Log.Warn($"Stacking: the window manager had not mapped the window after {GiveUp.TotalSeconds:0} seconds, so it was not raised.");
            Finish();
        };

        window.Closed += OnClosed;
        timer.Start();
    }

    private static bool IsManaged(nint display, nint window, nint managed)
    {
        var status = XGetWindowProperty(
            display, window, managed, 0, 2, 0, 0, out var type, out _, out _, out _, out var data);
        if (data != 0) XFree(data);

        return status == Success && type != 0;
    }

    private static void Raise(nint display, nint window, nint restack)
    {
        var message = new RestackMessage
        {
            Type = ClientMessage,
            SendEvent = 1,
            Display = display,
            Window = window,
            MessageType = restack,
            Format = 32,
            Source = FromPager,
            Sibling = 0,
            Detail = Above,
            Unused3 = 0,
            Unused4 = 0,
        };

        XSendEvent(display, XDefaultRootWindow(display), 0, SubstructureRedirectMask | SubstructureNotifyMask, ref message);
        XFlush(display);
    }

    /// <summary>
    /// An <c>XClientMessageEvent</c>, laid out as Xlib lays it out, and as long as the
    /// <c>XEvent</c> union it is one arm of: <c>XSendEvent</c> copies the whole union, twenty-four
    /// longs, whichever arm was filled in.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RestackMessage
    {
        public int Type;
        public nint Serial;
        public int SendEvent;
        public nint Display;
        public nint Window;
        public nint MessageType;
        public int Format;
        public nint Source;
        public nint Sibling;
        public nint Detail;
        public nint Unused3;
        public nint Unused4;
        public UnionTail Tail;
    }

    /// <summary>The union's twelve longs past the end of a client message.</summary>
    [InlineArray(12)]
    private struct UnionTail
    {
        private nint _element;
    }

    [DllImport(LibX11)]
    private static extern nint XOpenDisplay(nint name);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(nint display);

    [DllImport(LibX11)]
    private static extern nint XDefaultRootWindow(nint display);

    [DllImport(LibX11)]
    private static extern nint XInternAtom(nint display, byte[] name, int onlyIfExists);

    [DllImport(LibX11)]
    private static extern int XGetWindowProperty(
        nint display, nint window, nint property, nint offset, nint length, int delete, nint requestedType,
        out nint actualType, out int actualFormat, out nint items, out nint bytesAfter, out nint data);

    [DllImport(LibX11)]
    private static extern int XFree(nint data);

    [DllImport(LibX11)]
    private static extern int XSendEvent(nint display, nint window, int propagate, nint eventMask, ref RestackMessage message);

    [DllImport(LibX11)]
    private static extern int XFlush(nint display);
}
