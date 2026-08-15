using Avalonia;
using Avalonia.Controls;

namespace Mailbox.App;

/// <summary>
/// Which windowing backend the application runs on, and how it is chosen.
/// </summary>
/// <remarks>
/// X11 is the default. Avalonia 12.1's native Wayland backend is a separate package that has
/// graduated from private preview but is still opt-in and experimental, so it sits behind
/// <c>MAILBOX_WAYLAND=1</c> until it settles. On a Wayland session the default therefore runs
/// through XWayland, which works everywhere and costs a translation layer the native backend
/// does not pay — frame pacing, live resize and fractional scaling are where the two are
/// expected to differ, and none of it shows in a capture, which is why the flag exists: the
/// comparison has to be made by hand.
/// <para>
/// The flag opts in strictly rather than "with fallback". A trial that could silently land back
/// on X11 measures nothing, so if the compositor cannot be reached the run fails at startup and
/// says so. Both backends stay supported; the log names which one a window actually opened on.
/// </para>
/// </remarks>
internal static class WindowingBackend
{
    /// <summary>Set to <c>1</c> to run on the native Wayland backend instead of X11.</summary>
    public const string Variable = "MAILBOX_WAYLAND";

    /// <summary>True when the native Wayland backend has been asked for.</summary>
    public static bool WaylandRequested =>
        Environment.GetEnvironmentVariable(Variable) is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the process was started inside a Wayland session — whichever backend it then
    /// uses. On such a session the X11 backend reaches the display through XWayland.
    /// </summary>
    public static bool InWaylandSession =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>
    /// Applies the choice. Platform detection has already selected X11; Wayland replaces it when
    /// asked for, and nothing else about the builder changes.
    /// </summary>
    public static AppBuilder Apply(AppBuilder builder) =>
        WaylandRequested ? builder.UseWayland() : builder;

    /// <summary>
    /// One line for the log naming the backend a window actually opened on, read from the
    /// window rather than from the request so the log cannot claim a backend that never came up.
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
            "X11" when InWaylandSession =>
                $"Windowing: X11 through XWayland ({Variable}=1 selects the native Wayland backend)",
            "X11" => "Windowing: X11",
            _ => $"Windowing: {backend}",
        };
    }
}
