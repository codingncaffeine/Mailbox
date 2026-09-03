using Avalonia;
using Avalonia.Controls;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App;

/// <summary>
/// Which windowing backend the application runs on, and how it is chosen.
/// </summary>
/// <remarks>
/// <b>On a Wayland session the native Wayland backend is the default.</b> X11 through XWayland
/// works everywhere, but it makes the compositor scale the window rather than the application draw
/// at the monitor's scale — which is what a HiDPI screen, and a pair of monitors at different
/// scales, actually need. The native backend speaks <c>wp_fractional_scale_v1</c>, so it is told
/// the scale it is being shown at and renders for it. That is the whole reason for the change, and
/// it is not something a screenshot shows.
/// <para>
/// Off a Wayland session there is nothing to ask for and X11 is chosen directly. Both backends stay
/// supported, and the log names the one a window actually opened on rather than the one that was
/// asked for.
/// </para>
/// <para>
/// <b>The variable is an override in both directions.</b> <c>MAILBOX_WAYLAND=0</c> pins X11 — the
/// way back for anybody the native backend does not suit, and the way to override the guard below.
/// <c>MAILBOX_WAYLAND=1</c> pins Wayland <em>strictly</em>: a measurement that could silently land
/// on X11 measures nothing, so it neither falls back nor consults the guard.
/// </para>
/// </remarks>
internal static class WindowingBackend
{
    /// <summary><c>1</c> pins the native Wayland backend, <c>0</c> pins X11. Unset chooses.</summary>
    public const string Variable = "MAILBOX_WAYLAND";

    /// <summary>
    /// What the variable asks for: true for Wayland, false for X11, null when it says nothing and
    /// the session decides.
    /// </summary>
    public static bool? Requested =>
        Environment.GetEnvironmentVariable(Variable)?.Trim() switch
        {
            "1" or "true" or "TRUE" or "True" => true,
            "0" or "false" or "FALSE" or "False" => false,
            _ => null,
        };

    /// <summary>
    /// True when the process was started inside a Wayland session — whichever backend it then
    /// uses. On such a session the X11 backend reaches the display through XWayland.
    /// </summary>
    public static bool InWaylandSession =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>Set when this run chose Wayland and no window has opened on it yet.</summary>
    private static bool _attempting;

    /// <summary>
    /// Applies the choice. Platform detection has already selected X11; Wayland replaces it when
    /// the session calls for it, and nothing else about the builder changes.
    /// </summary>
    public static AppBuilder Apply(AppBuilder builder)
    {
        switch (Requested)
        {
            // Pinned to Wayland: strict, because this is the setting a comparison is made under
            // and a comparison that could quietly become an X11 one is not a comparison.
            case true:
                return builder.UseWayland();

            // Pinned to X11: platform detection already chose it, so there is nothing to do.
            case false:
                return builder;
        }

        if (!InWaylandSession) return builder;

        if (LastAttemptFailed())
        {
            Log.Warn(
                "Windowing: the native Wayland backend did not get a window open last time, so "
                + $"this run uses X11 through XWayland. Delete {Breadcrumb} or set {Variable}=1 "
                + "to try it again.");
            return builder;
        }

        MarkAttempt();
        return builder.UseWaylandWithFallback();
    }

    // ---- The guard --------------------------------------------------------------------------
    //
    // UseWaylandWithFallback covers a backend that cannot start. It does not cover one that starts
    // and then throws from its own worker thread, which is what a Wayland session with no outputs
    // does — "Expected at least one wl_output at this point", raised after the platform has been
    // committed, on a thread nothing here can catch. A monitor asleep or unplugged at launch is
    // enough to produce it, and a mail client that will not open because the lid is shut is worse
    // than one that is soft on a HiDPI screen.
    //
    // So the attempt is written down before it is made and rubbed out once a window is actually
    // open. A run that dies in between leaves the note behind, and the next run reads it and takes
    // X11. It costs one failed launch on a session the backend cannot handle, and nothing at all
    // on a session it can.

    private static string Breadcrumb => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mailbox",
        "wayland-attempt");

    private static bool LastAttemptFailed()
    {
        try
        {
            return File.Exists(Breadcrumb);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as failed: a guard that cannot be consulted must not be
            // the thing that decides.
            return false;
        }
    }

    private static void MarkAttempt()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Breadcrumb)!);
            File.WriteAllText(
                Breadcrumb,
                $"{DateTimeOffset.Now:O} {Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")}\n");
            _attempting = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write the note. The attempt still happens; it simply is not guarded.
            Log.Warn($"Windowing: the Wayland attempt could not be noted ({ex.Message}).");
        }
    }

    /// <summary>
    /// Called once a window is actually on screen. That is the only proof the backend works, so it
    /// is the only thing that clears the note.
    /// </summary>
    public static void WindowOpened()
    {
        if (!_attempting) return;
        _attempting = false;

        try
        {
            File.Delete(Breadcrumb);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A note that cannot be removed makes the next run take X11 — the safe way to be wrong.
            Log.Warn($"Windowing: the Wayland attempt note could not be cleared ({ex.Message}).");
        }
    }

    /// <summary>
    /// One line for the log naming the backend a window actually opened on, read from the window
    /// rather than from the request so the log cannot claim a backend that never came up.
    /// </summary>
    public static string Describe(TopLevel window)
    {
        var implementation = window.PlatformImpl?.GetType().Namespace ?? string.Empty;
        var backend =
            implementation.StartsWith("Avalonia.Wayland", StringComparison.Ordinal) ? "Wayland"
            : implementation.StartsWith("Avalonia.X11", StringComparison.Ordinal) ? "X11"
            : implementation.Length > 0 ? implementation
            : "unknown";

        return backend switch
        {
            "Wayland" => "Windowing: native Wayland",

            // X11 on a Wayland session is now several different things, and which one it is
            // matters to somebody reading the log to find out why their scaling is soft.
            "X11" when InWaylandSession && Requested == false =>
                $"Windowing: X11 through XWayland, pinned by {Variable}=0",
            "X11" when InWaylandSession =>
                "Windowing: X11 through XWayland — the native Wayland backend was asked for and "
                + "did not come up, so the compositor is scaling this window rather than the "
                + "application drawing at the monitor's scale",
            "X11" => "Windowing: X11",
            _ => $"Windowing: {backend}",
        };
    }
}
