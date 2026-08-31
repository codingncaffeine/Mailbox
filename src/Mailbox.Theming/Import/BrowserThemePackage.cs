using System.IO.Compression;

namespace Mailbox.Theming.Import;

/// <summary>Thrown for a package that cannot be read as a browser theme, with the reason in the reader's words.</summary>
public sealed class BrowserThemeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A Firefox static theme as it arrives: a zip with an <c>.xpi</c> name, an unpacked
/// directory, or a bare <c>manifest.json</c>. One reader, hardened here rather than in any
/// caller — the archive limits, the path checks and the size caps are the entire defence
/// against a hostile file, so they live where the file is opened.
/// </summary>
public sealed class BrowserThemePackage : IDisposable
{
    private const long ArchiveLimit = 64 * 1024 * 1024;
    private const long EntryLimit = 16 * 1024 * 1024;
    private const int EntryCount = 64;

    private readonly ZipArchive? _zip;
    private readonly string? _root;

    /// <summary>The manifest's text, read and bounded.</summary>
    public string ManifestJson { get; }

    private BrowserThemePackage(ZipArchive? zip, string? root, string manifest)
    {
        _zip = zip;
        _root = root;
        ManifestJson = manifest;
    }

    /// <summary>Opens a path as a theme package, whatever of the three shapes it is.</summary>
    public static BrowserThemePackage Open(string path)
    {
        if (Directory.Exists(path))
        {
            var manifestPath = Path.Combine(path, "manifest.json");
            if (!File.Exists(manifestPath)) throw new BrowserThemeException($"\"{path}\" has no manifest.json.");
            return new BrowserThemePackage(null, Path.GetFullPath(path), ReadBounded(manifestPath));
        }

        if (!File.Exists(path)) throw new BrowserThemeException($"\"{path}\" is not there.");

        if (string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserThemePackage(null, Path.GetDirectoryName(Path.GetFullPath(path)), ReadBounded(path));
        }

        if (new FileInfo(path).Length > ArchiveLimit)
        {
            throw new BrowserThemeException($"\"{path}\" is over the {ArchiveLimit / (1024 * 1024)} MB limit for a theme.");
        }

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new BrowserThemeException($"\"{path}\" is not a zip, and a Firefox theme is one.", ex);
        }

        try
        {
            if (zip.Entries.Count > EntryCount)
            {
                throw new BrowserThemeException($"\"{path}\" holds {zip.Entries.Count} entries; a theme needs a handful.");
            }

            var manifest = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase))
                ?? throw new BrowserThemeException($"\"{path}\" has no manifest.json at its root.");

            return new BrowserThemePackage(zip, null, ReadBounded(manifest));
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One image by its manifest-relative path, bounded and traversal-checked; null when the
    /// package does not hold it.
    /// </summary>
    public byte[]? ReadImage(string relativePath)
    {
        var clean = relativePath.Replace('\\', '/').TrimStart('/');
        if (clean.Split('/').Contains("..")) throw new BrowserThemeException($"\"{relativePath}\" climbs out of the theme.");

        if (_zip is not null)
        {
            var entry = _zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, clean, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            if (entry.Length > EntryLimit) throw new BrowserThemeException($"\"{relativePath}\" is over the image size limit.");

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer, 81920);
            return buffer.ToArray();
        }

        var full = Path.GetFullPath(Path.Combine(_root!, clean));
        if (!full.StartsWith(_root! + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new BrowserThemeException($"\"{relativePath}\" climbs out of the theme.");
        }

        if (!File.Exists(full)) return null;
        if (new FileInfo(full).Length > EntryLimit) throw new BrowserThemeException($"\"{relativePath}\" is over the image size limit.");
        return File.ReadAllBytes(full);
    }

    private static string ReadBounded(string path)
    {
        if (new FileInfo(path).Length > EntryLimit) throw new BrowserThemeException($"\"{path}\" is too large to be a manifest.");
        return File.ReadAllText(path);
    }

    private static string ReadBounded(ZipArchiveEntry entry)
    {
        if (entry.Length > EntryLimit) throw new BrowserThemeException("The manifest is too large to be one.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose() => _zip?.Dispose();
}
