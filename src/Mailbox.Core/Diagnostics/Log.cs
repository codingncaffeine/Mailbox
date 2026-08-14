using System.Diagnostics;
using System.Text;

namespace Mailbox.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Application log.
/// </summary>
/// <remarks>
/// Writes to a rolling file under <c>$XDG_STATE_HOME/mailbox/logs</c> and, when a terminal is
/// attached, to standard error. A mail client that dies with a truncated stack trace in a
/// terminal nobody was watching is a bug report nobody can action, so the goal here is that the
/// log alone is enough to diagnose a crash: environment on startup, a breadcrumb per
/// significant action, and the full exception with its inner chain on the way down.
/// <para>
/// Deliberately not a logging framework. One file, one format, no configuration, no
/// dependency — the point is that it always works, including before the UI exists.
/// </para>
/// </remarks>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    private static StreamWriter? _file;
    private static bool _echoToConsole = true;

    /// <summary>Entries kept in memory for the in-app log viewer.</summary>
    private static readonly Queue<string> Recent = new();
    private const int RecentLimit = 500;

    public static string? FilePath { get; private set; }

    public static LogLevel Minimum { get; set; } =
        Environment.GetEnvironmentVariable("MAILBOX_LOG_LEVEL")?.ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "warning" or "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Info,
        };

    /// <summary>
    /// Opens the log file and records the environment. Safe to call more than once; safe to
    /// skip entirely, in which case logging goes to the console only.
    /// </summary>
    public static void Initialize(string applicationVersion)
    {
        lock (Gate)
        {
            if (_file is not null) return;

            try
            {
                var directory = LogDirectory();
                Directory.CreateDirectory(directory);
                Roll(directory);

                FilePath = Path.Combine(directory, "mailbox.log");
                _file = new StreamWriter(
                    new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception ex)
            {
                // A log that cannot open its file must not take the application with it.
                Console.Error.WriteLine($"Could not open the log file: {ex.Message}");
            }
        }

        Info($"Mailbox {applicationVersion}");
        Info($"Runtime {Environment.Version} on {Environment.OSVersion}");
        Info($"Session {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown"}, " +
             $"desktop {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown"}");
        if (FilePath is not null) Info($"Log file {FilePath}");
    }

    /// <summary>Where the log lives. State, not data — it is disposable and machine-local.</summary>
    public static string LogDirectory()
    {
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(state))
        {
            state = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        }

        return Path.Combine(state, "mailbox", "logs");
    }

    /// <summary>Keeps the previous few runs, so a crash report can include the run before it.</summary>
    private static void Roll(string directory)
    {
        const int Keep = 5;
        var current = Path.Combine(directory, "mailbox.log");
        if (!File.Exists(current)) return;

        try
        {
            for (var i = Keep - 1; i >= 1; i--)
            {
                var older = Path.Combine(directory, $"mailbox.{i}.log");
                var newer = Path.Combine(directory, $"mailbox.{i + 1}.log");
                if (File.Exists(older)) File.Move(older, newer, overwrite: true);
            }

            File.Move(current, Path.Combine(directory, "mailbox.1.log"), overwrite: true);
        }
        catch
        {
            // Rolling is a convenience; failing to roll must not stop the app starting.
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);

    public static void Info(string message) => Write(LogLevel.Info, message, null);

    public static void Warn(string message, Exception? error = null)
        => Write(LogLevel.Warning, message, error);

    public static void Error(string message, Exception? error = null)
        => Write(LogLevel.Error, message, error);

    /// <summary>
    /// Records a crash with everything needed to act on it, and returns the text so the caller
    /// can show it to the user.
    /// </summary>
    public static string Crash(string source, Exception error)
    {
        var report = new StringBuilder();
        report.AppendLine($"Unhandled exception in {source}.");
        report.AppendLine();
        Describe(report, error, depth: 0);

        Write(LogLevel.Error, report.ToString().TrimEnd(), null);
        return report.ToString();
    }

    private static void Describe(StringBuilder report, Exception error, int depth)
    {
        var indent = new string(' ', depth * 2);
        report.AppendLine($"{indent}{error.GetType().FullName}: {error.Message}");

        foreach (var line in (error.StackTrace ?? string.Empty)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            report.AppendLine($"{indent}  {line.TrimEnd()}");
        }

        if (error.InnerException is { } inner)
        {
            report.AppendLine($"{indent}Caused by:");
            Describe(report, inner, depth + 1);
        }
    }

    /// <summary>The most recent entries, newest last, for the in-app viewer.</summary>
    public static IReadOnlyList<string> RecentEntries()
    {
        lock (Gate) return [.. Recent];
    }

    public static void SetConsoleEcho(bool enabled) => _echoToConsole = enabled;

    private static void Write(LogLevel level, string message, Exception? error)
    {
        if (level < Minimum) return;

        var line =
            $"{DateTime.Now:HH:mm:ss.fff} {Uptime.Elapsed.TotalSeconds,8:0.000}s " +
            $"{Label(level)} {message}";

        if (error is not null)
        {
            var report = new StringBuilder();
            Describe(report, error, depth: 1);
            line += Environment.NewLine + report.ToString().TrimEnd();
        }

        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > RecentLimit) Recent.Dequeue();

            try
            {
                _file?.WriteLine(line);
            }
            catch
            {
                // Never let logging throw into the caller.
            }

            if (!_echoToConsole) return;

            if (level >= LogLevel.Warning) Console.Error.WriteLine(line);
            else Console.WriteLine(line);
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "INF",
    };
}
