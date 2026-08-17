using System.Globalization;
using MailKit;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Protocols;

/// <summary>
/// The conversation with a mail server, written down so a failure can be read rather than guessed
/// at.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> Nearly every mail problem a user actually hits is a disagreement
/// between this client and a server about something neither of them says out loud: a mailbox that
/// authenticates but returns nothing, a message the server accepted and never delivered, a folder
/// whose name is spelled differently than the list implied. The application log records what this
/// application decided; only the wire says what the two of them actually exchanged.
/// <para>
/// <b>Off unless asked for, and redacted when on.</b> A protocol log contains the whole session,
/// which includes the AUTH exchange and the mail itself. MailKit's own
/// <see cref="ProtocolLogger.RedactSecrets"/> takes the credentials out; the messages it cannot,
/// so a log is somebody's mail and is written where their data lives, never to a temporary
/// directory anything else on the machine can read.
/// </para>
/// </remarks>
public static class ProtocolDiagnostics
{
    /// <summary>Turns wire logging on for this run. <c>MAILBOX_PROTOCOL_LOG=1</c> does the same.</summary>
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("MAILBOX_PROTOCOL_LOG") is "1" or "true";

    /// <summary>Where the logs go. Set by the application to somewhere under its own state directory.</summary>
    public static string? Directory { get; set; }

    /// <summary>How many session logs to keep before the oldest is dropped.</summary>
    public const int Keep = 20;

    /// <summary>
    /// A logger for one session, or null when this is switched off.
    /// </summary>
    /// <param name="protocol">imap, pop3, smtp or sieve — what the file is named after.</param>
    /// <remarks>
    /// One file per session rather than one long file: a session is the unit somebody actually
    /// wants to read, and interleaving four accounts' conversations produces something nobody can
    /// follow. Returns null rather than a no-op logger so the caller does not pay for formatting
    /// nothing.
    /// </remarks>
    public static IProtocolLogger? For(string protocol)
    {
        if (!Enabled) return null;

        try
        {
            var directory = Directory ?? Path.Combine(Path.GetTempPath(), "mailbox-protocol");
            System.IO.Directory.CreateDirectory(directory);
            Prune(directory);

            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            // Named by protocol and moment. Which server it was is in the greeting on the first
            // line, so the name does not have to carry it — and a client is built before it is
            // told where to connect.
            var path = Path.Combine(directory, $"{stamp}-{protocol}.log");

            var logger = new ProtocolLogger(path, append: false)
            {
                // The AUTH exchange carries the password in base64. Every other line is the
                // reader's own mail, which cannot be redacted and is why this is off by default.
                RedactSecrets = true,
            };

            Log.Info($"Protocol log for {protocol}: {path}");
            return logger;
        }
        catch (Exception ex)
        {
            // Diagnostics failing must never stop mail being collected. That is the whole
            // hierarchy of importance here in one line.
            Log.Warn($"Could not open a protocol log for {protocol}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Drops the oldest logs, so switching this on does not fill somebody's disk.</summary>
    private static void Prune(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory)
                .GetFiles("*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(Keep)
                .ToList();

            foreach (var file in files) file.Delete();
        }
        catch (Exception ex)
        {
            Log.Info($"Old protocol logs could not be pruned: {ex.Message}");
        }
    }

}
