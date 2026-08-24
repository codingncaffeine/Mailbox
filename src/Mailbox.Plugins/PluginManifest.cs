using System.Text.Json.Nodes;

namespace Mailbox.Plugins;

/// <summary>
/// What a plugin's <c>plugin.json</c> declares. The manifest is read before any of the plugin's
/// code is, so everything the Add-ins page lists about a plugin that will not load — its name,
/// its author, what it asked for — comes from here.
/// </summary>
/// <remarks>
/// The id is the plugin's stable name: lowercase letters and digits in dot-separated segments,
/// like a command id, because it becomes one — every command the plugin registers lives under
/// <c>plugin.&lt;id&gt;.…</c>, and those ids are persisted in ribbon layouts and key bindings.
/// <para>
/// Unknown permission names are kept and shown rather than refused: a manifest written for a
/// newer host still reads here, and what the reader is owed is the full list of what was asked
/// for, not the subset this build understands.
/// </para>
/// </remarks>
public sealed record PluginManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required Version PluginVersion { get; init; }

    /// <summary>The API version the plugin was compiled against, from the manifest's "api".</summary>
    public required Version Api { get; init; }

    /// <summary>The assembly file beside the manifest. A bare file name, never a path.</summary>
    public required string Assembly { get; init; }

    public string Author { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// The entry type's full name, for an assembly holding more than one. Null means the one
    /// public <c>IPlugin</c> in the assembly — and exactly one, since guessing between two would
    /// run code the manifest never named.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>What the plugin asked for, verbatim, known names and unknown alike.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    public bool Declares(string permission)
        => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a manifest, or says what is wrong with it. A bad manifest is a listed plugin with
    /// an error, never an exception out of discovery — one broken file must not cost the rest.
    /// </summary>
    public static bool TryRead(string json, out PluginManifest? manifest, out string? error)
    {
        manifest = null;

        JsonObject document;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                error = "plugin.json is not a JSON object.";
                return false;
            }

            document = parsed;
        }
        catch (Exception ex)
        {
            error = $"plugin.json could not be parsed: {ex.Message}";
            return false;
        }

        var id = Text(document, "id");
        if (!IsWellFormedId(id))
        {
            error = "The manifest needs an \"id\" of lowercase letters and digits in " +
                    "dot-separated segments, e.g. \"sample.wordcount\".";
            return false;
        }

        var name = Text(document, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "The manifest needs a \"name\".";
            return false;
        }

        if (!Version.TryParse(Text(document, "version"), out var version))
        {
            error = "The manifest needs a \"version\", e.g. \"1.0.0\".";
            return false;
        }

        if (!Version.TryParse(Text(document, "api"), out var api))
        {
            error = "The manifest needs an \"api\" naming the API version it was written " +
                    "against, e.g. \"1.0\".";
            return false;
        }

        var assembly = Text(document, "assembly");
        if (string.IsNullOrWhiteSpace(assembly)
            || assembly != Path.GetFileName(assembly)
            || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            // A name with a path in it would load code from outside the directory the reader
            // installed, which is not the thing they agreed to trust.
            error = "The manifest needs an \"assembly\" naming a .dll beside it — a file " +
                    "name, not a path.";
            return false;
        }

        var permissions = new List<string>();
        foreach (var node in document["permissions"] as JsonArray ?? [])
        {
            if (node is JsonValue value
                && value.TryGetValue<string>(out var permission)
                && permission.Length > 0)
            {
                permissions.Add(permission);
            }
        }

        manifest = new PluginManifest
        {
            Id = id!,
            Name = name!,
            PluginVersion = version,
            Api = api,
            Assembly = assembly,
            Author = Text(document, "author") ?? string.Empty,
            Description = Text(document, "description") ?? string.Empty,
            Type = Text(document, "type"),
            Permissions = permissions,
        };

        error = null;
        return true;
    }

    /// <summary>Same segment rules as a command id, so <c>plugin.&lt;id&gt;.&lt;name&gt;</c> always forms one.</summary>
    internal static bool IsWellFormedId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] == '.' || value[^1] == '.') return false;

        var previousWasDot = false;
        foreach (var c in value)
        {
            if (c == '.')
            {
                if (previousWasDot) return false;
                previousWasDot = true;
                continue;
            }

            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)) return false;
            previousWasDot = false;
        }

        return true;
    }

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
