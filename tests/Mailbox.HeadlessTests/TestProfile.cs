using System.Runtime.CompilerServices;

namespace Mailbox.HeadlessTests;

/// <summary>
/// A profile of this run's own, set before any code in the assembly runs.
/// </summary>
/// <remarks>
/// Bringing the platform up runs the application's startup — Avalonia calls
/// <c>OnFrameworkInitializationCompleted</c> from <c>SetupWithoutStarting</c> too — and startup
/// opens the stores, the settings and the log where the XDG directories say. Left as they were,
/// that is the profile of whoever ran the tests: on 2026-10-06 a run carrying a new schema step
/// migrated a reader's real account stores past what their installed build could open, and it
/// refused to start. Every directory the application reads is pointed at a scratch folder here,
/// first, so no test can reach a real one.
/// </remarks>
internal static class TestProfile
{
    /// <summary>Where this run's profile lives.</summary>
    public static string Root { get; } =
        Path.Combine(Path.GetTempPath(), $"mailbox-headless-{Environment.ProcessId}-{Guid.NewGuid():N}");

#pragma warning disable CA2255 // The point is to run before anything else in a test assembly.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Isolate()
    {
        foreach (var (variable, folder) in new[]
                 {
                     ("XDG_DATA_HOME", "data"),
                     ("XDG_CONFIG_HOME", "config"),
                     ("XDG_STATE_HOME", "state"),
                     ("XDG_CACHE_HOME", "cache"),
                 })
        {
            var path = Path.Combine(Root, folder);
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(variable, path);
        }

        Environment.SetEnvironmentVariable("MAILBOX_STORE", Path.Combine(Root, "data", "mailbox", "accounts"));

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
                // A file still held open by the platform on the way out; the folder is in the
                // temporary directory and carries this run's id.
            }
        };
    }
}
