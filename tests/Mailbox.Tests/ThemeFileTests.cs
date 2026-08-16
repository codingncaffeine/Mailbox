using Mailbox.Theming;
using Mailbox.Theming.Files;
using Mailbox.Theming.Fonts;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>A theme as a file: the format, the library that reads a directory of them, and the built-ins as files.</summary>
public class ThemeFileTests
{
    private static ThemeFile Parse(string json) => ThemeFileFormat.Parse(json, "test.mailbox-theme.json");

    [Fact]
    public void AFileWithAnIdANameABaseAndTokensParses()
    {
        var file = Parse("""
            {
              // A comment is fine; so is a trailing comma.
              "id": "midnight",
              "name": "Midnight",
              "base": "black",
              "dark": true,
              "tokens": {
                "palette.brand.primary": "#4FA3E0",
                "accent.rest": "{palette.brand.primary}",
                "type.ui.size": 13,
              },
            }
            """);

        Assert.Equal("midnight", file.Id);
        Assert.Equal("Midnight", file.Name);
        Assert.Equal("black", file.Base);
        Assert.True(file.IsDark);
        Assert.Equal("#4FA3E0", file.Tokens["palette.brand.primary"]);
        Assert.Equal("{palette.brand.primary}", file.Tokens["accent.rest"]);
        Assert.Equal("13", file.Tokens["type.ui.size"]);
    }

    [Theory]
    [InlineData("not json", "not JSON")]
    [InlineData("[]", "not a JSON object")]
    [InlineData("""{"name":"No id"}""", "no \"id\"")]
    [InlineData("""{"id":"has space"}""", "letters, digits and hyphens")]
    [InlineData("""{"id":"x","tokens":"nope"}""", "must be an object")]
    [InlineData("""{"id":"x","tokens":{"a":{"b":1}}}""", "must be a string")]
    public void AFileThatIsNotAThemeSaysWhy(string json, string reason)
    {
        var ex = Assert.Throws<ThemeFileException>(() => Parse(json));
        Assert.Contains(reason, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANameLessFileIsNamedByItsId()
        => Assert.Equal("plain", Parse("""{"id":"plain"}""").Name);

    [Fact]
    public void WriteThenParseIsTheSameTheme()
    {
        var tokens = new TokenSet();
        tokens.Set("ribbon.background", "#123456");
        tokens.Set("palette.brand.primary", "#0F6CBD");
        tokens.Set("accent.rest", "{palette.brand.primary}");
        var theme = new ThemeFile("mine", "Mine", "colorful", null, tokens);

        var text = ThemeFileFormat.Write(theme);
        var back = ThemeFileFormat.Parse(text);

        Assert.Equal(theme.Id, back.Id);
        Assert.Equal(theme.Name, back.Name);
        Assert.Equal(theme.Base, back.Base);
        Assert.Null(back.IsDark);
        Assert.Equal(3, back.Tokens.Count);
        Assert.Equal("{palette.brand.primary}", back.Tokens["accent.rest"]);
        // Grouped by layer, palette first, so a reader finds the knobs that matter at the top.
        Assert.True(text.IndexOf("palette.brand.primary", StringComparison.Ordinal) < text.IndexOf("accent.rest", StringComparison.Ordinal));
        Assert.True(text.IndexOf("accent.rest", StringComparison.Ordinal) < text.IndexOf("ribbon.background", StringComparison.Ordinal));
    }

    // ---- The library ---------------------------------------------------------------------

    private static ThemeFile FileTheme(string id, string? baseId, params (string Key, string Value)[] tokens)
    {
        var set = new TokenSet();
        foreach (var (key, value) in tokens) set.Set(key, value);
        return new ThemeFile(id, id.ToUpperInvariant(), baseId, null, set, id + ".mailbox-theme.json");
    }

    [Fact]
    public void APaletteOnlyFileIsACompleteThemeThroughItsBase()
    {
        var library = new ThemeLibrary([FileTheme("midnight", "black", ("palette.brand.primary", "#4FA3E0"))]);

        var tokens = library.Build("midnight").Resolve();
        var black = OfficeThemes.Build(OfficeThemes.Black).Resolve();

        // Everything the base derives from the brand colour follows the file's; the rest is the base's.
        Assert.Equal("#4FA3E0", tokens.GetString("accent.rest"));
        Assert.NotEqual(black.GetString("accent.rest"), tokens.GetString("accent.rest"));
        Assert.Equal(black.GetString("ribbon.background"), tokens.GetString("ribbon.background"));
        Assert.Equal(black.Count, tokens.Count);
        // And it passes the coverage gate the service applies.
        var service = new ThemeService(new FontResolver([]), library);
        service.Apply("midnight");
        Assert.Equal("midnight", service.ThemeId);
        Assert.True(service.IsDark);
        Assert.Equal("MIDNIGHT", service.DisplayName("midnight"));
    }

    [Fact]
    public void FilesChainAndTheLastWordIsTheRequestedThemes()
    {
        var library = new ThemeLibrary(
        [
            FileTheme("ocean", "colorful", ("palette.brand.primary", "#006D77"), ("ribbon.background", "#F0FAFA")),
            FileTheme("deep-ocean", "ocean", ("palette.brand.primary", "#003D44")),
        ]);

        var tokens = library.Build("deep-ocean").Resolve();
        Assert.Equal("#003D44", tokens.GetString("accent.rest"));
        Assert.Equal("#F0FAFA", tokens.GetString("ribbon.background"));
        Assert.False(library.IsDark("deep-ocean"));
        Assert.Equal([.. OfficeThemes.All, "ocean", "deep-ocean"], library.Ids);
    }

    [Fact]
    public void AMissingBaseAndALoopAreRefusedWhenBuiltNotWhenLoaded()
    {
        var library = new ThemeLibrary(
        [
            FileTheme("orphan", "nowhere"),
            FileTheme("a", "b"),
            FileTheme("b", "a"),
            FileTheme("fine", "white"),
        ]);

        Assert.True(library.Contains("orphan"));
        Assert.Contains("nowhere", Assert.Throws<ThemeResolutionException>(() => library.Build("orphan")).Message);
        Assert.Contains("itself", Assert.Throws<ThemeResolutionException>(() => library.Build("a")).Message);
        Assert.Contains("no theme", Assert.Throws<ThemeResolutionException>(() => library.Build("ghost")).Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(library.Build("fine"));
    }

    [Fact]
    public void AFileCannotTakeABuiltInsIdOrRepeatAnother()
    {
        var library = new ThemeLibrary(
        [
            FileTheme("black", null, ("ribbon.background", "#000000")),
            FileTheme("dup", "white"),
            FileTheme("dup", "black", ("ribbon.background", "#111111")),
        ]);

        Assert.Equal([.. OfficeThemes.All, "dup"], library.Ids);
        Assert.Equal(OfficeThemes.Build(OfficeThemes.Black).Resolve().GetString("ribbon.background"), library.Build("black").Resolve().GetString("ribbon.background"));
        Assert.Equal("white", library.Files.Single().Base);
        Assert.Equal("dup", library.Canonical("DUP"));
        Assert.Equal("darkgray", library.Canonical("DarkGray"));
        Assert.Null(library.Canonical("nothing"));
    }

    [Fact]
    public void ADirectoryOfFilesLoadsAndABadOneIsSkipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mailbox-themes-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "good" + ThemeFileFormat.Extension), """{"id":"good","base":"colorful","tokens":{"palette.brand.primary":"#AA0000"}}""");
            File.WriteAllText(Path.Combine(dir, "bad" + ThemeFileFormat.Extension), "{ this is not json");
            File.WriteAllText(Path.Combine(dir, "ignored.json"), """{"id":"ignored"}""");

            var library = ThemeLibrary.Load(dir);
            Assert.Equal([.. OfficeThemes.All, "good"], library.Ids);
            Assert.Equal("#AA0000", library.Build("good").Resolve().GetString("accent.rest"));

            Assert.Empty(ThemeLibrary.Load(Path.Combine(dir, "does-not-exist")).Files);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- The built-ins as files ------------------------------------------------------------

    [Theory]
    [InlineData(OfficeThemes.Colorful)]
    [InlineData(OfficeThemes.White)]
    [InlineData(OfficeThemes.DarkGray)]
    [InlineData(OfficeThemes.Black)]
    public void ABuiltInExportsAsACompleteFileThatResolvesToItself(string id)
    {
        var exported = ThemeLibrary.Export(id);
        Assert.Null(exported.Base);
        Assert.Equal(OfficeThemes.IsDark(id), exported.IsDark);

        var back = ThemeFileFormat.Parse(ThemeFileFormat.Write(exported));
        var fromFile = new ThemeLibrary([back with { Id = "copy-of-" + id }]).Build("copy-of-" + id).Resolve();
        var original = OfficeThemes.Build(id).Resolve();

        Assert.Equal(original.Count, fromFile.Count);
        foreach (var key in original.Keys) Assert.Equal(original.GetString(key), fromFile.GetString(key));

        // A file with no base has to say everything; the export does.
        var service = new ThemeService(new FontResolver([]), new ThemeLibrary([back with { Id = "copy-of-" + id }]));
        service.Apply("copy-of-" + id);
    }

    [Theory]
    [InlineData(OfficeThemes.Colorful)]
    [InlineData(OfficeThemes.White)]
    [InlineData(OfficeThemes.DarkGray)]
    [InlineData(OfficeThemes.Black)]
    public void TheCommittedThemeFilesStillMatchTheBuiltIns(string id)
    {
        // assets/themes/<id>.mailbox-theme.json is generated by `mailbox --export-theme`; if a
        // built-in changes, regenerate it (tools/export-themes.sh) rather than letting it drift.
        var root = RepoRoot();
        var path = Path.Combine(root, "assets", "themes", id + ThemeFileFormat.Extension);
        Assert.True(File.Exists(path), $"{path} is missing; run tools/export-themes.sh.");

        var committed = ThemeFileFormat.Parse(File.ReadAllText(path), path).Tokens.Resolve();
        var original = OfficeThemes.Build(id).Resolve();
        Assert.Equal(original.Count, committed.Count);
        foreach (var key in original.Keys)
        {
            Assert.True(committed.Contains(key), $"{path} lacks {key}; run tools/export-themes.sh.");
            Assert.True(original.GetString(key) == committed.GetString(key), $"{path}: {key} is {committed.GetString(key)}, the built-in says {original.GetString(key)}; run tools/export-themes.sh.");
        }
    }

    [Fact]
    public void TheServiceStartsOnAFileThemeTheEnvironmentNamesAndReloadsIt()
    {
        var previous = Environment.GetEnvironmentVariable(ThemeService.ThemeVariable);
        try
        {
            Environment.SetEnvironmentVariable(ThemeService.ThemeVariable, "Midnight");
            var library = new ThemeLibrary([FileTheme("midnight", "black", ("palette.brand.primary", "#4FA3E0"))]);
            var service = new ThemeService(new FontResolver([]), library);
            Assert.Equal("midnight", service.ThemeId);
            Assert.Equal("#4FA3E0", service.Tokens.GetString("accent.rest"));

            // The file changed: the same id, a new colour — and the service follows.
            var changes = 0;
            service.Changed += (_, _) => changes++;
            service.ReplaceLibrary(new ThemeLibrary([FileTheme("midnight", "black", ("palette.brand.primary", "#FF00FF"))]));
            Assert.Equal("#FF00FF", service.Tokens.GetString("accent.rest"));
            Assert.Equal(1, changes);

            // The file went away: back to Colorful rather than nowhere.
            service.ReplaceLibrary(new ThemeLibrary());
            Assert.Equal(OfficeThemes.Colorful, service.ThemeId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ThemeService.ThemeVariable, previous);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailbox.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
