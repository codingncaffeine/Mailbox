using System.Diagnostics;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Core.Platform;

/// <summary>What came of asking the desktop to open something.</summary>
public enum DesktopOpenResult
{
    /// <summary>The desktop was asked.</summary>
    Opened,

    /// <summary>
    /// This run is posed, so nothing was asked and the address was logged instead. Not a failure:
    /// a surface should say whatever it would have said, or a capture stops showing what a person
    /// would see.
    /// </summary>
    Posed,

    /// <summary>Nothing on this desktop answered, or there was nothing to open.</summary>
    Failed,
}

/// <summary>
/// Hands an address or a path to the desktop — unless the run is a posed one, in which case it
/// says what it would have opened and opens nothing.
/// </summary>
/// <remarks>
/// <para>
/// Through <c>xdg-open</c> rather than naming a browser: which browser is the desktop's business,
/// and naming one is how a Linux application ends up opening the wrong thing. Also rather than
/// <see cref="ProcessStartInfo.UseShellExecute"/>, which on Linux goes through the same tool
/// anyway and swallows the failure when it is missing.
/// </para>
/// <para>
/// The reason this is one method rather than the six copies it replaces is the posed-run guard.
/// <c>MAILBOX_CAPTURE</c> gates the scratch settings copy, the tray, IDLE and the single instance
/// precisely so that a headless run cannot reach the machine it is running on — and every one of
/// those six copies ignored it. That is not theoretical: during the audit a pose pressed a contact
/// card's Map It and opened a map in the owner's own browser, on their own desktop, while they
/// were working. The reading pane's copy was the dangerous one, because the address there comes
/// from the message: a seeded link, pressed under a pose, would have handed an arbitrary URL to
/// whatever browser the person running the sweep had open.
/// </para>
/// <para>
/// Saying which address was asked for is also the better read-back. The claim a sweep needs to
/// check is *which* URI a button asks the desktop for, and that could previously only be
/// established by letting it actually launch.
/// </para>
/// <para>
/// The variable is read here rather than taken from the capture harness because that lives in
/// <c>Mailbox.App</c> and the sign-in flow that needs this lives in <c>Mailbox.Protocols</c>.
/// One string in one place, and no project reference invented to share a boolean.
/// </para>
/// </remarks>
public static class DesktopOpen
{
    /// <summary>The variable whose presence means this run is posed rather than a person's.</summary>
    private const string CaptureVariable = "MAILBOX_CAPTURE";

    /// <summary>Whether this run is a posed one, and must therefore not touch the desktop.</summary>
    public static bool IsPosedRun
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CaptureVariable));

    /// <summary>
    /// Asks the desktop to open <paramref name="target"/>, and says which of the three things
    /// happened — because a caller's wording needs all three. A posed run is not a failure and
    /// must not be reported as one: the surface should say what it would have said, so that what
    /// a capture shows is what a person would see.
    /// </summary>
    public static DesktopOpenResult Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return DesktopOpenResult.Failed;

        if (IsPosedRun)
        {
            Log.Info($"Harness: would have asked the desktop to open {target}.");
            return DesktopOpenResult.Posed;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { target },
                    UseShellExecute = false,
                },
            };

            process.Start();
            return DesktopOpenResult.Opened;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not ask the desktop to open {target}.", ex);
            return DesktopOpenResult.Failed;
        }
    }
}
