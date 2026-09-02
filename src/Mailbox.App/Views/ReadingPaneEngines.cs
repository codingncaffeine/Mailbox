namespace Mailbox.App.Views;

/// <summary>
/// Which of the installed web engines the reading pane can actually draw a message with.
/// </summary>
/// <remarks>
/// The pane draws the message into its own surface, so the only embedding it can use is the
/// offscreen one: the engine paints into memory and the pane composites that frame like any
/// other control. An engine that can only put the page in a native child window is no use here
/// — a native window over the pane sits above every flyout the shell opens, which is the reason
/// the offscreen embedding was chosen in the first place.
/// <para>
/// This has to be asked <em>before</em> a view is built, because the library will happily build
/// one either way. On a machine with no WPE it falls back to WebKitGTK, which reports the
/// document loaded, answers with the words it parsed, and paints nothing at all: measured, a
/// healthy-looking engine over a body that is blank. That is the worst shape a failure can take
/// — nothing crashes, nothing logs, and the reader simply sees an empty message. The pane's own
/// text rendering is the honest answer there, and it is what the packages have always claimed
/// happens.
/// </para>
/// <para>
/// Kept apart from the pane for the reason <see cref="ReadingPaneLoads"/> is: the rule can be
/// checked with no web engine on the machine at all, and the thing it guards against only
/// appears on a machine that is missing one.
/// </para>
/// </remarks>
internal static class ReadingPaneEngines
{
    /// <summary>One engine as the platform describes it, before anything is built.</summary>
    /// <param name="Name">What to call it in the log.</param>
    /// <param name="Installed">Whether the libraries it needs are on this machine.</param>
    /// <param name="Supported">Whether this build of the library can drive it at all.</param>
    /// <param name="DrawsOffscreen">Whether it can paint into memory rather than a native window.</param>
    /// <param name="Unavailable">The platform's own reason, when it gave one.</param>
    internal readonly record struct Candidate(
        string Name,
        bool Installed,
        bool Supported,
        bool DrawsOffscreen,
        string? Unavailable)
    {
        /// <summary>Whether this one can draw a message into the pane.</summary>
        internal bool Usable => Installed && Supported && DrawsOffscreen;

        /// <summary>Why it cannot, in the words the log should carry.</summary>
        internal string Refusal
        {
            get
            {
                if (!Supported) return $"{Name} is not supported by this build";
                if (!Installed)
                {
                    return Unavailable is { Length: > 0 } said
                        ? $"{Name} is not installed ({said})"
                        : $"{Name} is not installed";
                }

                return $"{Name} is installed but cannot draw off screen";
            }
        }
    }

    /// <summary>What the pane should build.</summary>
    /// <param name="UseWebView">Build the engine, rather than rendering the message as text.</param>
    /// <param name="PreferWebKitGtk">Ask the library for WebKitGTK instead of WPE.</param>
    /// <param name="Reason">One sentence for the log, whichever way it went.</param>
    internal readonly record struct Choice(bool UseWebView, bool PreferWebKitGtk, string Reason);

    /// <summary>
    /// Picks the engine, or decides there is none worth building.
    /// </summary>
    /// <param name="wpe">WPE WebKit, the pane's own engine.</param>
    /// <param name="gtk">WebKitGTK, which the library falls back to by itself.</param>
    /// <param name="gtkAsked">
    /// Whether the debugging escape asked for WebKitGTK. It only reorders the two: an engine that
    /// cannot draw into the pane is refused however it was chosen, because honouring the request
    /// literally would put a blank body in front of whoever asked, which is the thing being fixed.
    /// </param>
    internal static Choice Choose(Candidate wpe, Candidate gtk, bool gtkAsked)
    {
        var (first, second) = gtkAsked ? (gtk, wpe) : (wpe, gtk);

        if (first.Usable) return new Choice(true, PreferWebKitGtk(first, gtk), first.Name + " renders the message.");

        if (second.Usable)
        {
            return new Choice(
                true,
                PreferWebKitGtk(second, gtk),
                $"{first.Refusal}, so {second.Name} renders the message.");
        }

        return new Choice(
            false,
            false,
            $"No web engine here can draw into the reading pane — {first.Refusal}, "
            + $"{second.Refusal}. The message is rendered as text.");
    }

    private static bool PreferWebKitGtk(Candidate chosen, Candidate gtk) => chosen.Name == gtk.Name;
}
