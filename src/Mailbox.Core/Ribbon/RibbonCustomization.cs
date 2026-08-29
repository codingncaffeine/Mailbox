using System.Text.Json;
using System.Text.Json.Nodes;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;

namespace Mailbox.Core.Ribbon;

/// <summary>
/// What was read out of a customization file: the ribbon, and the Quick Access Toolbar if the
/// file carried one.
/// </summary>
/// <remarks>
/// An exported file holds both, because "Import/Export" in the reference exports a user's
/// customizations rather than one surface's. The stored document omits the toolbar, which lives
/// in the settings file with the rest of the small persistent choices.
/// </remarks>
/// <param name="Module">
/// Which module's ribbon the tree describes. Null for a document written before the field
/// existed, every one of which came out of the Mail editor.
/// </param>
public sealed record RibbonCustomizationFile(
    RibbonTree Tree, IReadOnlyList<CommandId>? QuickAccess, MailboxModule? Module = null);

/// <summary>
/// Where a user's ribbon edits are kept: one JSON document beside the settings file.
/// </summary>
/// <remarks>
/// A file rather than a key in the settings store, for the same reason a theme is a file — it is
/// a document a person can read, diff, copy to another machine, keep in a backup, or hand to
/// someone else. Import and Export are then the same code path as loading and saving, which is
/// why they are nearly free.
/// <para>
/// The file is allowed to be wrong. A command id that no longer exists, a malformed id, a group
/// with no name: each costs the thing it names and nothing else. A ribbon that will not load is
/// an application that will not start, and no customization is worth that.
/// </para>
/// </remarks>
public sealed class RibbonCustomization
{
    /// <summary>Bumped only if a later shape cannot be read by the reader below.</summary>
    private const int CurrentVersion = 1;

    private readonly string? _path;

    /// <summary>Opens the store at the given path, or the default location when null.</summary>
    public RibbonCustomization(string? path = null) => _path = path ?? DefaultPath();

    /// <summary>A store that never touches disk. For tests and previews.</summary>
    public static RibbonCustomization Transient() => new(path: string.Empty);

    public static string DefaultPath()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "mailbox", "ribbon.json");
    }

    /// <summary>True when the user has saved edits.</summary>
    public bool IsCustomized => _path is { Length: > 0 } && File.Exists(_path);

    /// <summary>
    /// The tree to edit: the saved document reconciled against the shipped layout, or the
    /// shipped layout itself when nothing has been saved.
    /// </summary>
    public RibbonTree Load(RibbonLayout shipped)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        if (!IsCustomized) return RibbonTree.From(shipped);

        try
        {
            var tree = Read(File.ReadAllText(_path!)).Tree;
            tree.Reconcile(shipped);
            return tree;
        }
        catch (Exception ex)
        {
            // Keep the bad file rather than overwriting it, so it can be looked at.
            Log.Warn($"Could not read {_path}; using the shipped ribbon.", ex);
            return RibbonTree.From(shipped);
        }
    }

    /// <summary>
    /// The layout to render: the shipped one with the saved edits applied, when the edits were
    /// made against this module's ribbon.
    /// </summary>
    /// <remarks>
    /// The module check is the whole of this method's difficulty. Every module asks this of the
    /// same document, and tab ids repeat across the layouts — Mail, Calendar, People, Tasks,
    /// Notes, Journal and Feeds all have a tab called <c>home</c>, and most of them a
    /// <c>sendreceive</c>, a <c>view</c> and a <c>help</c>. Applied blind, a document describing
    /// the Mail ribbon rewrote the Calendar's Home row with Mail's clusters, gave every module a
    /// Folder tab with nothing in it, and did the same to the other five. One press of Add on the
    /// Options page was enough. A document names the ribbon it describes and is applied to that
    /// one only.
    /// </remarks>
    public RibbonLayout Apply(RibbonLayout shipped)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        if (!IsCustomized) return shipped;
        if (Describes(shipped.Module) is false) return shipped;

        return Load(shipped).ApplyTo(shipped);
    }

    /// <summary>
    /// Whether the saved document describes this module's ribbon. Null when there is no readable
    /// document, in which case there is nothing to apply either way.
    /// </summary>
    private bool? Describes(MailboxModule module)
    {
        if (!IsCustomized) return null;

        try
        {
            // A document written before the module was recorded came out of the Mail editor,
            // which was the only editor there has ever been.
            return (Read(File.ReadAllText(_path!)).Module ?? MailboxModule.Mail) == module;
        }
        catch (Exception)
        {
            // Load says the same thing to the log a moment later; saying it twice per module
            // switch would fill the file with it.
            return null;
        }
    }

    /// <summary>
    /// Saves the tree, or deletes the document when the tree says nothing the shipped layout
    /// does not.
    /// </summary>
    /// <remarks>
    /// Deleting rather than storing an identical copy means a user who undoes their edits by
    /// hand ends up on the shipped ribbon, and keeps following it as later builds change it.
    /// </remarks>
    public void Save(RibbonTree tree, RibbonLayout shipped)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(shipped);

        if (!tree.DiffersFrom(shipped))
        {
            Reset();
            return;
        }

        Write(_path, tree, quickAccess: null, shipped.Module);
    }

    /// <summary>Discards every edit, so the shipped ribbon comes back.</summary>
    public void Reset()
    {
        if (_path is not { Length: > 0 } || !File.Exists(_path)) return;

        try
        {
            File.Delete(_path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {_path}.", ex);
        }
    }

    /// <summary>Writes the tree, and the toolbar with it, to a file the user chose.</summary>
    public static void Export(
        string path, RibbonTree tree, IReadOnlyList<CommandId>? quickAccess, MailboxModule module)
        => Write(path, tree, quickAccess, module);

    /// <summary>Reads a file the user chose. Throws only if it is not this document at all.</summary>
    public static RibbonCustomizationFile Import(string path)
        => Read(File.ReadAllText(path));

    // ---- The document ----------------------------------------------------------------------

    private static void Write(
        string? path, RibbonTree tree, IReadOnlyList<CommandId>? quickAccess, MailboxModule module)
    {
        if (path is not { Length: > 0 }) return;

        var tabs = new JsonArray();

        foreach (var tab in tree.Tabs)
        {
            var groups = new JsonArray();

            foreach (var group in tab.Groups)
            {
                groups.Add(new JsonObject
                {
                    ["id"] = group.Id,
                    ["label"] = group.Label,
                    ["custom"] = group.IsCustom,
                    ["commands"] = new JsonArray(
                        [.. group.Commands.Select(c => JsonValue.Create(c.Value))]),
                });
            }

            tabs.Add(new JsonObject
            {
                ["id"] = tab.Id,
                ["label"] = tab.Label,
                ["visible"] = tab.IsVisible,
                ["custom"] = tab.IsCustom,
                ["groups"] = groups,
            });
        }

        var document = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["module"] = module.ToString(),
            ["tabs"] = tabs,
        };

        if (quickAccess is not null)
        {
            document["quickAccess"] = new JsonArray(
                [.. quickAccess.Select(c => JsonValue.Create(c.Value))]);
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Written beside the target and moved into place, so an interrupted write cannot
            // leave half a ribbon where the ribbon used to be.
            var temporary = path + ".tmp";
            File.WriteAllText(
                temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write {path}.", ex);
        }
    }

    private static RibbonCustomizationFile Read(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject document)
        {
            throw new InvalidDataException("Not a ribbon customization document.");
        }

        var tree = new RibbonTree();

        foreach (var node in document["tabs"] as JsonArray ?? [])
        {
            if (node is not JsonObject tabNode) continue;
            if (Text(tabNode, "id") is not { Length: > 0 } id) continue;

            var tab = new RibbonTreeTab
            {
                Id = id,
                Label = Text(tabNode, "label") ?? id,
                IsVisible = Flag(tabNode, "visible", fallback: true),
                IsCustom = Flag(tabNode, "custom", fallback: false),
            };

            foreach (var groupNode in tabNode["groups"] as JsonArray ?? [])
            {
                if (groupNode is not JsonObject group) continue;
                if (Text(group, "id") is not { Length: > 0 } groupId) continue;

                tab.Groups.Add(new RibbonTreeGroup
                {
                    Id = groupId,
                    Label = Text(group, "label") ?? groupId,
                    IsCustom = Flag(group, "custom", fallback: false),
                    Commands = [.. Commands(group["commands"] as JsonArray)],
                });
            }

            tree.Tabs.Add(tab);
        }

        var quickAccess = document["quickAccess"] is JsonArray toolbar
            ? Commands(toolbar).ToList()
            : null;

        var module = Enum.TryParse<MailboxModule>(Text(document, "module"), out var named)
            ? named
            : (MailboxModule?)null;

        return new RibbonCustomizationFile(tree, quickAccess, module);
    }

    private static IEnumerable<CommandId> Commands(JsonArray? array)
    {
        foreach (var node in array ?? [])
        {
            if (node is not JsonValue entry
                || !entry.TryGetValue<string>(out var value)
                || value.Length == 0)
            {
                continue;
            }

            CommandId id;
            try
            {
                id = new CommandId(value);
            }
            catch (ArgumentException)
            {
                Log.Warn($"Ribbon customization lists '{value}', which is not a valid command id.");
                continue;
            }

            yield return id;
        }
    }

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Flag(JsonObject node, string key, bool fallback)
        => node[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
}
