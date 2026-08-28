using System.Net.Http;
using System.Reflection;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App;

/// <summary>
/// The tarball install's answer to auto-update: ask the release page whether a newer version
/// exists, and say so — never download, never replace. A mail client that rewrites its own
/// binary is a different trust conversation; the packaged installs have their managers.
/// </summary>
/// <remarks>
/// §19's "nothing phones home" is why the automatic check ships <b>off</b>: a version check is
/// a network request with this machine's address on it. Pressing Check for Updates is consent
/// by the press; the Options switch (<see cref="AutomaticKey"/>) is consent standing. A capture
/// run never checks, whatever the switch says.
/// </remarks>
public static class UpdateCheck
{
    public const string AutomaticKey = "update.check";

    private const string LatestUrl = "https://api.github.com/repos/codingncaffeine/Mailbox/releases/latest";

    /// <summary>The running version, as the build stamped it.</summary>
    public static string Current =>
        typeof(UpdateCheck).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? typeof(UpdateCheck).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>
    /// Asks the release page. The answer is a sentence for the status line — an update check's
    /// whole job is one legible sentence.
    /// </summary>
    public static async Task<string> CheckAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mailbox/{Current}");

            using var answer = await http.GetAsync(LatestUrl, cancellation);

            // A project with nothing published yet answers 404, which is not a failure to
            // reach anything: the reader was told "Could not reach the release page: Response
            // status code does not indicate success: 404 (Not Found)" and learned nothing from
            // a sentence that is both wrong and unreadable.
            if (answer.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return $"This is {Current}. There is no published release to compare it with yet.";
            }

            answer.EnsureSuccessStatusCode();
            var json = await answer.Content.ReadAsStringAsync(cancellation);
            if (Mailbox.Core.Updates.Releases.LatestFrom(json) is not { } latest)
            {
                return "The release page did not answer with a release.";
            }

            Log.Info($"Update check: running {Current}, latest {latest.Version}.");
            return Mailbox.Core.Updates.Releases.IsNewer(Current, latest.Version)
                ? $"Version {latest.Version} is out — this is {Current}. Get it at {latest.Url}"
                : $"This is {Current}, and it is the latest.";
        }
        catch (Exception ex)
        {
            Log.Warn("The update check could not reach the release page.", ex);
            return $"Could not reach the release page: {ex.Message}";
        }
    }
}
