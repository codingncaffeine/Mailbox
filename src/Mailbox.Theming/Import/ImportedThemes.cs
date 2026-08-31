using Mailbox.Theming.Files;
using Mailbox.Theming.Themes;

namespace Mailbox.Theming.Import;

/// <summary>
/// Turns image bytes from a stranger's package into PNG bytes through a real decoder, or null
/// for anything that does not decode. Supplied by the application, which owns a toolkit;
/// nothing from the package ever lands on disk with its original bytes.
/// </summary>
public delegate byte[]? ImageReencoder(byte[] source);

/// <summary>What an import did, beyond the mapping: where it wrote, whether it replaced an earlier self, and the notes.</summary>
public sealed record ImportOutcome(ImportResult Result, string Path, bool Updated, IReadOnlyList<string> Notes);

/// <summary>
/// The disk half of an import: id allocation and the collision rules, the image directory,
/// the file itself. The reading and mapping stay pure; everything that touches the themes
/// directory is here, and only here.
/// </summary>
public static class ImportedThemes
{
    /// <summary>
    /// Whether an id would shadow a built-in — its id, or its display name's slug. One rule
    /// for every writer of theme files: a file carrying a built-in's id is ignored at load,
    /// and a file named after one puts two identical names in every picker.
    /// </summary>
    public static bool ShadowsBuiltIn(string id)
        => ThemeLibrary.IsBuiltIn(id)
           || OfficeThemes.All.Any(b => string.Equals(Slug(OfficeThemes.DisplayName(b)), id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A display name as a file id: letters, digits, hyphens, never empty.</summary>
    public static string Slug(string name)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "imported-theme" : slug;
    }

    /// <summary>
    /// The whole journey: open, parse, map, extract the header image, write. The same theme
    /// imported again — matched on its recorded source — keeps its id and is replaced whole,
    /// images included; a different theme with the same name gets a numbered id and the notes
    /// say so.
    /// </summary>
    public static ImportOutcome Import(string packagePath, string directory, ImageReencoder? reencode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // One door for both shapes: a zip carrying a Mailbox theme at its root is a theme
        // pack; everything else is read as a browser theme.
        if (File.Exists(packagePath) && ThemePack.IsPack(packagePath))
        {
            return ThemePack.Import(packagePath, directory, reencode);
        }

        using var package = BrowserThemePackage.Open(packagePath);
        var theme = BrowserThemeManifest.Parse(package.ManifestJson);
        var notes = new List<string>();

        var (id, updated) = AllocateId(theme, directory, notes);

        // The header image, re-encoded through the application's decoder or not written at all.
        string? backdropPath = null;
        if (theme.FrameImage is { } frameImage)
        {
            if (reencode is null)
            {
                notes.Add($"the header image \"{frameImage}\" was skipped — no image decoder in this door");
            }
            else if (package.ReadImage(frameImage) is not { } bytes)
            {
                notes.Add($"the header image \"{frameImage}\" is named by the manifest and not in the package");
            }
            else if (reencode(bytes) is not { } png)
            {
                notes.Add($"the header image \"{frameImage}\" does not decode as an image and was refused");
            }
            else
            {
                var imageDirectory = Path.Combine(directory, "images", id);
                if (Directory.Exists(imageDirectory)) Directory.Delete(imageDirectory, recursive: true);
                Directory.CreateDirectory(imageDirectory);
                File.WriteAllBytes(Path.Combine(imageDirectory, "frame.png"), png);
                backdropPath = string.Join('/', "images", id, "frame.png");
            }
        }

        if (theme.AdditionalBackgrounds.Count > 0)
        {
            notes.Add($"{theme.AdditionalBackgrounds.Count} additional background(s) not used by the slim import");
        }

        var result = SlimThemeImport.Map(theme, id, theme.Name, backdropPath);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ThemeFileFormat.Extension);
        File.WriteAllText(path, ThemeFileFormat.Write(result.File));

        return new ImportOutcome(result, path, updated, notes);
    }

    /// <summary>Removes a user theme and its images. A built-in is refused by the caller's guard, not trusted here either.</summary>
    public static void Remove(string id, string directory)
    {
        if (ThemeLibrary.IsBuiltIn(id)) throw new ArgumentException($"\"{id}\" is a built-in theme.", nameof(id));

        var path = Path.Combine(directory, id + ThemeFileFormat.Extension);
        if (File.Exists(path)) File.Delete(path);

        var images = Path.Combine(directory, "images", id);
        if (Directory.Exists(images)) Directory.Delete(images, recursive: true);
    }

    private static (string Id, bool Updated) AllocateId(BrowserTheme theme, string directory, List<string> notes)
    {
        var existing = ThemeLibrary.Load(directory).Files;

        // The same theme again keeps its id: matched on the provenance the last import wrote.
        if (existing.FirstOrDefault(f =>
                f.Tokens.TryGetRaw("import.source", out var source)
                && string.Equals(source, theme.SourceId, StringComparison.OrdinalIgnoreCase)) is { } earlier)
        {
            notes.Add($"\"{theme.Name}\" was already imported as \"{earlier.Id}\" and is updated in place");
            return (earlier.Id, true);
        }

        var slug = Slug(theme.Name);
        if (ShadowsBuiltIn(slug)) slug += "-imported";

        var id = slug;
        for (var n = 2; existing.Any(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)); n++)
        {
            id = $"{slug}-{n}";
        }

        if (id != slug || ShadowsBuiltIn(Slug(theme.Name)))
        {
            notes.Add($"the name \"{theme.Name}\" was taken, so this one is \"{id}\"");
        }

        return (id, false);
    }
}
