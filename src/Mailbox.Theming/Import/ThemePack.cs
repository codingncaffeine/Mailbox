using System.IO.Compression;
using Mailbox.Theming.Files;

namespace Mailbox.Theming.Import;

/// <summary>
/// A Mailbox theme as one file to hand somebody: the theme's own json plus the images it
/// brought, zipped. Sharing was already a file-drop for a colours-only theme; the pack is what
/// keeps a theme with images whole across the trip.
/// </summary>
/// <remarks>
/// Import is held to the same rules as a browser theme's package: the archive limits, the
/// traversal checks, and every image re-encoded through the application's decoder before it
/// lands on disk. A pack is a stranger's file the moment it has travelled, whoever made it.
/// </remarks>
public static class ThemePack
{
    /// <summary>What a pack's name ends in.</summary>
    public const string Extension = ".mailbox-theme-pack.zip";

    /// <summary>Whether a zip is a theme pack rather than a browser theme: it carries a theme json at its root.</summary>
    public static bool IsPack(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(e => e.FullName.EndsWith(ThemeFileFormat.Extension, StringComparison.OrdinalIgnoreCase)
                                        && !e.FullName.Contains('/'));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes one user theme — its json and its images — as a pack beside wherever the caller
    /// says. Built-ins are refused: they travel with the application.
    /// </summary>
    public static string Export(string id, string themesDirectory, string? destination = null)
    {
        if (ThemeLibrary.IsBuiltIn(id)) throw new ArgumentException($"\"{id}\" is a built-in theme; it travels with the application.", nameof(id));

        var jsonPath = Path.Combine(themesDirectory, id + ThemeFileFormat.Extension);
        if (!File.Exists(jsonPath)) throw new FileNotFoundException($"There is no theme \"{id}\" in {themesDirectory}.");

        var target = destination ?? id + Extension;
        if (File.Exists(target)) File.Delete(target);

        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(jsonPath, id + ThemeFileFormat.Extension);

        var images = Path.Combine(themesDirectory, "images", id);
        if (Directory.Exists(images))
        {
            foreach (var file in Directory.EnumerateFiles(images))
            {
                zip.CreateEntryFromFile(file, string.Join('/', "images", id, Path.GetFileName(file)));
            }
        }

        return target;
    }

    /// <summary>
    /// Reads a pack into the themes directory: the theme json under its own id, its images
    /// re-encoded through the decoder. The same id already present is replaced whole — a pack
    /// is how a theme updates as well as how it arrives.
    /// </summary>
    public static ImportOutcome Import(string zipPath, string themesDirectory, ImageReencoder? reencode)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 64) throw new BrowserThemeException($"\"{zipPath}\" holds {zip.Entries.Count} entries; a theme pack needs a handful.");

        var jsonEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(ThemeFileFormat.Extension, StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'))
            ?? throw new BrowserThemeException($"\"{zipPath}\" carries no theme file at its root.");
        if (jsonEntry.Length > 1024 * 1024) throw new BrowserThemeException("The pack's theme file is too large to be one.");

        string json;
        using (var reader = new StreamReader(jsonEntry.Open()))
        {
            json = reader.ReadToEnd();
        }

        var theme = ThemeFileFormat.Parse(json, zipPath);
        if (ImportedThemes.ShadowsBuiltIn(theme.Id))
        {
            throw new BrowserThemeException($"\"{theme.Id}\" is a built-in theme's name; the pack is refused.");
        }

        var notes = new List<string>();
        Directory.CreateDirectory(themesDirectory);

        // The images, under this theme's own directory and no other, decoder-laundered.
        var imagePrefix = $"images/{theme.Id}/";
        var imageDirectory = Path.Combine(themesDirectory, "images", theme.Id);
        if (Directory.Exists(imageDirectory)) Directory.Delete(imageDirectory, recursive: true);

        foreach (var entry in zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith(imagePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(entry.FullName);
            if (name.Length == 0 || entry.FullName.Contains("..")) continue;
            if (entry.Length > 16 * 1024 * 1024) { notes.Add($"\"{name}\" is over the image size limit and was left out"); continue; }

            if (reencode is null)
            {
                notes.Add($"\"{name}\" was skipped — no image decoder in this door");
                continue;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer, 81920);
            if (reencode(buffer.ToArray()) is not { } png)
            {
                notes.Add($"\"{name}\" does not decode as an image and was refused");
                continue;
            }

            Directory.CreateDirectory(imageDirectory);
            File.WriteAllBytes(Path.Combine(imageDirectory, Path.ChangeExtension(name, ".png")), png);
        }

        var stray = zip.Entries.Count(e =>
            !e.FullName.Replace('\\', '/').StartsWith(imagePrefix, StringComparison.OrdinalIgnoreCase)
            && !ReferenceEquals(e, jsonEntry) && e.Length > 0);
        if (stray > 0) notes.Add($"{stray} file(s) outside the theme's own images were ignored");

        var updated = File.Exists(Path.Combine(themesDirectory, theme.Id + ThemeFileFormat.Extension));
        var path = Path.Combine(themesDirectory, theme.Id + ThemeFileFormat.Extension);
        File.WriteAllText(path, ThemeFileFormat.Write(theme));

        var result = new ImportResult(theme, theme.Base ?? "(none)", ReadsDark: false,
            "a pack carries its base's answer", [.. theme.Tokens.Keys], [], [], [], [], Origin: "pack");
        return new ImportOutcome(result, path, updated, notes);
    }
}
