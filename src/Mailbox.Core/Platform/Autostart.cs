using System.Text;

namespace Mailbox.Core.Platform;

/// <summary>
/// Starting Mailbox when the desktop session starts: an XDG autostart entry, the Linux answer to
/// the reference's run-at-login, part of the desktop-integration contract.
/// </summary>
/// <remarks>
/// The whole mechanism is one desktop-entry file under <c>$XDG_CONFIG_HOME/autostart</c>, which
/// every desktop reads at login. Off by default, and opt-in from Options — a mail client that
/// installs itself into the session without asking is the kind of thing people uninstall.
/// <para>
/// The entry's <c>Exec</c> is worked out from how Mailbox is running now: the packaged binary
/// when it is on the path, otherwise the executable this process was started from, so a copy run
/// from a build tree autostarts that build. <c>--minimized</c> on the command line asks the
/// application to start into the tray rather than open a window, which is what most people want
/// from a client that starts at login.
/// </para>
/// </remarks>
public sealed class Autostart(string? directory = null)
{
    /// <summary>The switch that starts the application into the tray with no window.</summary>
    public const string MinimizedSwitch = "--minimized";

    private readonly string _directory = directory ?? DefaultDirectory();

    /// <summary><c>$XDG_CONFIG_HOME/autostart</c>, or <c>~/.config/autostart</c> when unset.</summary>
    public static string DefaultDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "autostart");
    }

    /// <summary>The entry itself.</summary>
    public string EntryPath => Path.Combine(_directory, "mailbox.desktop");

    /// <summary>
    /// True when an entry exists and is not switched off inside. A desktop's own settings can
    /// disable an entry in place by writing <c>Hidden=true</c> or the GNOME key, and that counts
    /// as off: the file being there is not the same as it running.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            var text = Read();
            if (text is null) return false;

            return !HasKey(text, "Hidden", "true")
                   && !HasKey(text, "X-GNOME-Autostart-enabled", "false");
        }
    }

    /// <summary>True when the entry starts Mailbox into the tray rather than a window.</summary>
    public bool StartsMinimized
    {
        get
        {
            var text = Read();
            if (text is null) return false;

            var exec = Lines(text).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal));
            return exec is not null && exec.Contains(MinimizedSwitch, StringComparison.Ordinal);
        }
    }

    /// <summary>Writes the entry, replacing whatever was there.</summary>
    public void Enable(bool minimized, string? command = null)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(EntryPath, Render(command ?? CommandForThisProcess(), minimized), new UTF8Encoding(false));
    }

    /// <summary>Removes the entry. Nothing to remove is not an error.</summary>
    public void Disable()
    {
        try
        {
            if (File.Exists(EntryPath)) File.Delete(EntryPath);
        }
        catch (IOException)
        {
            // A file we cannot delete is one we cannot own either; the caller re-reads IsEnabled.
        }
    }

    /// <summary>The entry's text for a command, with or without the tray switch.</summary>
    public static string Render(string command, bool minimized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var exec = minimized ? command + " " + MinimizedSwitch : command;

        return $"""
            [Desktop Entry]
            Type=Application
            Version=1.5
            Name=Mailbox
            Comment=Start Mailbox when you sign in
            Exec={exec}
            Icon=mailbox
            Terminal=false
            StartupNotify=false
            X-GNOME-Autostart-enabled=true

            """.ReplaceLineEndings("\n");
    }

    /// <summary>
    /// How to start this application again: the packaged binary if it is on the path, else the
    /// executable this process was started from, quoted for a desktop entry's Exec key.
    /// </summary>
    public static string CommandForThisProcess()
    {
        var packaged = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "mailbox"))
            .FirstOrDefault(File.Exists);

        if (packaged is not null) return "mailbox";

        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return "mailbox";

        // Run through the shared runtime host rather than an apphost: the host needs the
        // assembly named after it, which is the first command-line argument.
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Environment.GetCommandLineArgs().FirstOrDefault();
            return assembly is { Length: > 0 }
                ? QuoteExec(path) + " " + QuoteExec(Path.GetFullPath(assembly))
                : QuoteExec(path);
        }

        return QuoteExec(path);
    }

    /// <summary>
    /// Quotes a path for a desktop entry's Exec key, which has its own rules: a value with a
    /// space, a quote or a shell character goes in double quotes, with those characters escaped
    /// by a backslash inside them.
    /// </summary>
    public static string QuoteExec(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length > 0 && path.All(c => c is not (' ' or '\t' or '\n' or '"' or '\'' or '\\' or '>' or '<' or '~' or '|' or '&' or ';' or '$' or '*' or '?' or '#' or '(' or ')' or '`')))
        {
            return path;
        }

        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }

    private string? Read()
    {
        try
        {
            return File.Exists(EntryPath) ? File.ReadAllText(EntryPath) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Lines(string text)
        => text.Split('\n').Select(l => l.TrimEnd('\r').Trim());

    private static bool HasKey(string text, string key, string value)
        => Lines(text).Any(l =>
            l.StartsWith(key + "=", StringComparison.Ordinal)
            && string.Equals(l[(key.Length + 1)..].Trim(), value, StringComparison.OrdinalIgnoreCase));
}
