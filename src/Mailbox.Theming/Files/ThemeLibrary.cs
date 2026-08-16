using Mailbox.Core.Diagnostics;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Theming.Files;

/// <summary>
/// Every theme that can be applied: the four built-ins, then the files a reader has put in
/// the themes directory. One place answers what exists, what to call it, whether it is dark,
/// and what its tokens are — with a file's <c>base</c> chain applied.
/// </summary>
/// <remarks>
/// A file theme is its base's tokens with its own laid over them, the base being another file
/// or a built-in, as deep as it goes; a chain that loops or names a base that does not exist is
/// refused when the theme is built, not when the library loads, so one bad file does not take
/// the others down. What a theme file may leave out is decided by the coverage gate at apply
/// time, exactly as for a built-in.
/// </remarks>
public sealed class ThemeLibrary
{
    private readonly Dictionary<string, ThemeFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public ThemeLibrary(IEnumerable<ThemeFile>? files = null)
    {
        foreach (var file in files ?? [])
        {
            if (OfficeThemes.All.Contains(file.Id, StringComparer.OrdinalIgnoreCase))
            {
                Log.Warn($"Theme file \"{file.Path}\" uses the built-in id \"{file.Id}\" and is ignored.");
                continue;
            }

            if (_files.ContainsKey(file.Id))
            {
                Log.Warn($"Theme file \"{file.Path}\" repeats the id \"{file.Id}\" and is ignored.");
                continue;
            }

            _files[file.Id] = file;
        }
    }

    /// <summary>The built-ins alone.</summary>
    public static ThemeLibrary BuiltIns { get; } = new();

    /// <summary>The built-ins, then the files in the order they were given.</summary>
    public IReadOnlyList<string> Ids => [.. OfficeThemes.All, .. _files.Keys];

    public IReadOnlyList<ThemeFile> Files => [.. _files.Values];

    public bool Contains(string id) => IsBuiltIn(id) || _files.ContainsKey(id);

    public static bool IsBuiltIn(string id) => OfficeThemes.All.Contains(id, StringComparer.OrdinalIgnoreCase);

    public string DisplayName(string id)
        => _files.TryGetValue(id, out var file) ? file.Name : OfficeThemes.DisplayName(id);

    /// <summary>The theme's own answer, else its base's, else the built-in rule.</summary>
    public bool IsDark(string id)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = id;
        while (_files.TryGetValue(current, out var file) && seen.Add(current))
        {
            if (file.IsDark is { } dark) return dark;
            if (file.Base is null) return false;
            current = file.Base;
        }

        return OfficeThemes.IsDark(current);
    }

    /// <summary>The id as the library knows it — the built-in's or the file's own casing — or null.</summary>
    public string? Canonical(string id)
    {
        if (OfficeThemes.All.FirstOrDefault(t => string.Equals(t, id, StringComparison.OrdinalIgnoreCase)) is { } builtIn) return builtIn;
        return _files.TryGetValue(id, out var file) ? file.Id : null;
    }

    /// <summary>
    /// The theme's tokens, base chain applied. Throws <see cref="ThemeResolutionException"/> for
    /// a theme that is not here, a base that is not here, or a chain that loops.
    /// </summary>
    public TokenSet Build(string id)
    {
        if (IsBuiltIn(id)) return OfficeThemes.Build(Canonical(id)!);

        var chain = new List<ThemeFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = id;
        TokenSet? root = null;

        while (true)
        {
            if (IsBuiltIn(current))
            {
                root = OfficeThemes.Build(Canonical(current)!);
                break;
            }

            if (!_files.TryGetValue(current, out var file))
            {
                throw new ThemeResolutionException(chain.Count == 0
                    ? $"There is no theme named \"{id}\"."
                    : $"Theme \"{chain[^1].Id}\" starts from \"{current}\", which is not a theme here.");
            }

            if (!seen.Add(current))
            {
                throw new ThemeResolutionException(
                    $"Theme \"{id}\" starts from itself: " + string.Join(" -> ", chain.Select(c => c.Id)) + $" -> {current}.");
            }

            chain.Add(file);
            if (file.Base is null) break;
            current = file.Base;
        }

        // The base's tokens first, then each file over them, the requested theme last.
        var tokens = root ?? new TokenSet();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            tokens = tokens.OverlaidWith(chain[i].Tokens);
        }

        return tokens;
    }

    /// <summary>
    /// A built-in as a complete theme file, with no base — the starting point for a theme of
    /// one's own, and the documentation of what a theme is made of.
    /// </summary>
    public static ThemeFile Export(string builtInId)
    {
        if (!IsBuiltIn(builtInId)) throw new ArgumentException($"'{builtInId}' is not a built-in theme.", nameof(builtInId));
        var id = OfficeThemes.All.First(t => string.Equals(t, builtInId, StringComparison.OrdinalIgnoreCase));
        return new ThemeFile(id, OfficeThemes.DisplayName(id), Base: null, OfficeThemes.IsDark(id), OfficeThemes.Build(id));
    }

    /// <summary>
    /// Every <c>*.mailbox-theme.json</c> in a directory, read and kept; a file that is not a
    /// theme is logged and left out. A directory that does not exist is an empty library.
    /// </summary>
    public static ThemeLibrary Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var files = new List<ThemeFile>();

        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*" + ThemeFileFormat.Extension).OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    files.Add(ThemeFileFormat.Parse(File.ReadAllText(path), path));
                }
                catch (Exception ex) when (ex is ThemeFileException or IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Theme file skipped: {ex.Message}");
                }
            }
        }

        return new ThemeLibrary(files);
    }

    /// <summary>Where a reader's theme files live: <c>$XDG_CONFIG_HOME/mailbox/themes</c>.</summary>
    public static string DefaultDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "mailbox", "themes");
    }
}
