using System.Text.Json.Nodes;

namespace Mailbox.Core.Updates;

/// <summary>
/// The pure half of the update check: what a release page's answer says, and whether it is
/// newer. The half that touches the network lives with the application, where consent does.
/// </summary>
public static class Releases
{
    /// <summary>The version a GitHub release answer names, and where a person reads it.</summary>
    public static (string Version, string Url)? LatestFrom(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject release) return null;

            var tag = release["tag_name"]?.GetValue<string>();
            if (tag is not { Length: > 0 }) return null;

            return (tag.TrimStart('v', 'V'), release["html_url"]?.GetValue<string>() ?? string.Empty);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether the named version is newer than the running one, compared as versions.</summary>
    public static bool IsNewer(string current, string latest)
        => Version.TryParse(Pad(current), out var here)
           && Version.TryParse(Pad(latest), out var there)
           && there > here;

    private static string Pad(string version)
        => version.Split('.').Length >= 2 ? version : version + ".0";
}
