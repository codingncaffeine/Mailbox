using System.Text;

namespace Mailbox.Core.Platform;

/// <summary>
/// What the hardened launcher tells the application about the unit it runs in, and how a write
/// the unit refused is explained.
/// </summary>
/// <remarks>
/// <c>packaging/mailbox-launcher.sh</c> starts the application inside a transient systemd unit
/// with the home directory read-only and a few folders opened back up: the mail directories, and
/// the reader's own — Desktop, Documents, Downloads and the rest of what the desktop names. Inside
/// the unit the wall is invisible. A save anywhere else fails as "Read-only file system", exactly
/// as it would on a read-only disk, and for a while that was all that happened: the picker is the
/// desktop's and knows nothing of the unit, the application caught the error and logged it, and
/// an attachment saved to the Desktop simply never arrived there. So the launcher now says what it
/// did, in two variables, and this is where they are read: whether the run is confined, and which
/// folders were opened. A write that fails outside them is explained to the reader with the
/// folders that would have worked and the switch that lifts the confinement.
/// </remarks>
public static class Confinement
{
    /// <summary>Set to <c>1</c> by the launcher, inside the unit.</summary>
    public const string Variable = "MAILBOX_SANDBOX";

    /// <summary>The folders the launcher opened for the reader's own files, colon-separated.</summary>
    public const string WritableVariable = "MAILBOX_SANDBOX_WRITABLE";

    /// <summary>The launcher's own escape: with this set it runs the binary with no unit at all.</summary>
    public const string Escape = "MAILBOX_NO_SANDBOX=1";

    /// <summary>True inside the launcher's unit.</summary>
    public static bool IsConfined =>
        Environment.GetEnvironmentVariable(Variable)?.Trim() == "1";

    /// <summary>The folders the launcher opened, in the launcher's order. Empty outside the unit.</summary>
    public static IReadOnlyList<string> WritablePlaces =>
        Places(Environment.GetEnvironmentVariable(WritableVariable));

    /// <summary>The launcher's list as folders: colon-separated, blanks dropped.</summary>
    public static IReadOnlyList<string> Places(string? list) =>
        string.IsNullOrWhiteSpace(list)
            ? []
            : list.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Whether <paramref name="path"/> lies inside one of <paramref name="places"/> — by
    /// directory, so a sibling that merely shares the prefix does not count.
    /// </summary>
    public static bool IsWithin(string path, IReadOnlyList<string> places)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(places);

        var full = Path.GetFullPath(path);
        foreach (var place in places)
        {
            var root = Path.GetFullPath(place).TrimEnd(Path.DirectorySeparatorChar);
            if (full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The file and the system's own reason, worded as the reference words it. For a write that
    /// failed somewhere the unit never touches — the runtime directory an attachment is opened
    /// from — where blaming the confinement would be wrong.
    /// </summary>
    public static string DescribeWriteFailure(string path, Exception error)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(error);

        return "Cannot create file: " + Path.GetFileName(path) + "." + Environment.NewLine + error.Message;
    }

    /// <summary>
    /// The lines that follow a "Cannot save" heading: the file, the system's own reason, and —
    /// when the unit is what refused — where a save would have worked and how to lift it.
    /// </summary>
    public static string ExplainWriteFailure(string path, Exception error) =>
        ExplainWriteFailure(path, error, IsConfined, WritablePlaces);

    /// <inheritdoc cref="ExplainWriteFailure(string, Exception)"/>
    public static string ExplainWriteFailure(
        string path, Exception error, bool confined, IReadOnlyList<string> writable)
    {
        ArgumentNullException.ThrowIfNull(writable);

        var text = new StringBuilder(DescribeWriteFailure(path, error));

        // The unit is blamed only where it is the cause: outside every folder it opened. A failure
        // inside one — a full disk, a folder somebody made read-only — is the system's own, and is
        // reported as it came.
        if (!confined || IsWithin(path, writable)) return text.ToString();

        text.AppendLine().AppendLine();
        if (writable.Count == 0)
        {
            text.AppendLine("Mailbox is running confined and cannot save outside its own folders.");
            text.Append("Start Mailbox with ").Append(Escape).Append(" to save anywhere.");
            return text.ToString();
        }

        text.AppendLine("Mailbox is running confined and can save only into these folders:");
        foreach (var place in writable) text.AppendLine(place);
        text.Append("Choose one of them, or start Mailbox with ").Append(Escape).Append(" to save anywhere.");
        return text.ToString();
    }
}
