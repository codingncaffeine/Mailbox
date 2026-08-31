using System.IO.Compression;
using Mailbox.Theming.Files;
using Mailbox.Theming.Import;
using Mailbox.Theming.Themes;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// The slim importer, over hand-written fixtures — no third-party artwork anywhere. The
/// assertion that matters most is the light-content one: a pitch-dark browser theme lands on a
/// theme whose reading surfaces are still Dark Gray's light ones.
/// </summary>
public class ThemeImportTests
{
    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailbox-import-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string PackageWith(string root, string manifest, params (string Name, byte[] Bytes)[] files)
    {
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "manifest.json"), manifest);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(package, name), bytes);
        return package;
    }

    // ------------------------------------------------------------------------------------
    // The colour grammar
    // ------------------------------------------------------------------------------------

    [Fact]
    public void TheColourGrammarReadsWhatABrowserWrites()
    {
        Assert.Equal("#112233", CssColour.Parse("#112233"));
        Assert.Equal("#112233", CssColour.Parse("rgb(17, 34, 51)"));
        Assert.Equal("#80112233", CssColour.Parse("rgba(17, 34, 51, 0.5)"));
        Assert.Equal("#FF0000", CssColour.Parse("hsl(0, 100%, 50%)"));
        Assert.Equal("#008000", CssColour.Parse("green"));
        Assert.Equal("#00FFFFFF", CssColour.Parse("transparent"));
        Assert.Equal("#112233", CssColour.Parse([17, 34, 51]));
        Assert.Equal("#80112233", CssColour.Parse([17, 34, 51, 0.5]));
        Assert.Null(CssColour.Parse("conic-gradient(red, blue)"));
        Assert.Null(CssColour.Parse(""));
    }

    // ------------------------------------------------------------------------------------
    // The mapping
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ADarkFrameLandsOnDarkGrayWithLightContentIntact()
    {
        var root = Scratch();
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1.2", "name": "Midnight Fixture",
              "theme": { "colors": { "frame": "#101018", "tab_background_text": "#E8E8F0" } }
            }
            """);

        var outcome = ImportedThemes.Import(package, Path.Combine(root, "themes"), reencode: null);
        var result = outcome.Result;

        Assert.Equal(OfficeThemes.DarkGray, result.BaseId);
        Assert.True(result.ReadsDark);

        var resolved = new ThemeLibrary([result.File]).Build(result.File.Id).Resolve();
        Assert.Equal("#101018", resolved.GetString(TokenKeys.TitleBar.Background));
        Assert.Equal("#E8E8F0", resolved.GetString(TokenKeys.TitleBar.Foreground));
        Assert.Equal(Recolour.WashOverDark, resolved.GetString(TokenKeys.TitleBar.CaptionHover));

        // The light-content assertion — the most important lines in the whole verification.
        var darkGray = OfficeThemes.Build(OfficeThemes.DarkGray).Resolve();
        Assert.Equal(darkGray.GetString(TokenKeys.List.RowBackground), resolved.GetString(TokenKeys.List.RowBackground));
        Assert.Equal(darkGray.GetString(TokenKeys.Reading.Background), resolved.GetString(TokenKeys.Reading.Background));
        Assert.Equal(darkGray.GetString(TokenKeys.Text.Primary), resolved.GetString(TokenKeys.Text.Primary));
        Assert.Equal(darkGray.GetString(TokenKeys.Compose.BodyBackground), resolved.GetString(TokenKeys.Compose.BodyBackground));

        // And the file passes the same coverage gate any theme does.
        foreach (var key in TokenKeys.Required) Assert.True(resolved.Contains(key), $"missing {key}");
    }

    [Fact]
    public void NoImportEverWritesAContentFamilyKey()
    {
        var root = Scratch();
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1", "name": "Everything Fixture",
              "theme": { "colors": {
                "frame": "#222222", "tab_background_text": "#EEEEEE",
                "sidebar": "#333333", "sidebar_text": "#DDDDDD",
                "toolbar": "#444444", "popup": "#555555", "popup_text": "#CCCCCC",
                "ntp_background": "#000000", "ntp_text": "#FFFFFF"
              } }
            }
            """);

        var outcome = ImportedThemes.Import(package, Path.Combine(root, "themes"), reencode: null);

        foreach (var token in outcome.Result.TokensWritten)
        {
            var area = TokenMap.AreaOf(token);
            Assert.False(area?.IsContent ?? false, $"{token} is a content-family key and must never be written by an import.");
            Assert.False(area?.IsDesktop ?? false, $"{token} is the desktop's and must never be written by an import.");
        }

        // The browser's document surface is exactly what must not be mapped.
        Assert.Contains("ntp_background", outcome.Result.Unmapped);
        Assert.Contains("popup", outcome.Result.Unmapped);
    }

    [Fact]
    public void TheLegacyAliasesAreEnoughToBeATheme()
    {
        var root = Scratch();
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "3", "name": "Old LWT Fixture",
              "theme": { "colors": { "accentcolor": "#F5F0E8", "textcolor": "#332211" } }
            }
            """);

        var result = ImportedThemes.Import(package, Path.Combine(root, "themes"), null).Result;

        Assert.Equal(OfficeThemes.White, result.BaseId);
        Assert.False(result.ReadsDark);
        Assert.Equal("#F5F0E8", result.File.Tokens[TokenKeys.TitleBar.Background]);
        Assert.Equal("#332211", result.File.Tokens[TokenKeys.TitleBar.Foreground]);
        Assert.Equal(Recolour.WashOverLight, result.File.Tokens[TokenKeys.TitleBar.CaptionHover]);
    }

    [Fact]
    public void TheInkSignalBeatsTheMidGreyGuessAndColorSchemeBeatsBoth()
    {
        // A light-looking frame whose ink is lighter still: the ink signal says dark.
        var root = Scratch();
        var inkSignal = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1", "name": "Ink Signal Fixture",
              "theme": { "colors": { "frame": "#909090", "tab_background_text": "#FFFFFF" } }
            }
            """);
        Assert.True(ImportedThemes.Import(inkSignal, Path.Combine(root, "t1"), null).Result.ReadsDark);

        // color_scheme is authoritative over everything.
        var stated = PackageWith(Scratch(), """
            {
              "manifest_version": 2, "version": "1", "name": "Stated Scheme Fixture",
              "theme": {
                "colors": { "frame": "#101010", "tab_background_text": "#F0F0F0" },
                "properties": { "color_scheme": "light" }
              }
            }
            """);
        Assert.False(ImportedThemes.Import(stated, Path.Combine(root, "t2"), null).Result.ReadsDark);
    }

    [Fact]
    public void AlphaFlattensAndFullTransparencyMeansAbsent()
    {
        var root = Scratch();
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1", "name": "Alpha Fixture",
              "theme": { "colors": { "frame": "rgba(0, 0, 0, 0.5)", "sidebar": "transparent" } }
            }
            """);

        var result = ImportedThemes.Import(package, Path.Combine(root, "themes"), null).Result;

        // Half-black over the white it is drawn on is mid grey, stored opaque.
        Assert.Equal("#7F7F7F", result.File.Tokens[TokenKeys.TitleBar.Background]);

        // A fully transparent sidebar means "let the base show", never black.
        Assert.False(result.File.Tokens.TryGetRaw(TokenKeys.Nav.Background, out _));
    }

    [Fact]
    public void AnUnreadableStatedPairIsReportedNotRepaired()
    {
        var root = Scratch();
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1", "name": "Unreadable Fixture",
              "theme": { "colors": { "frame": "#777777", "tab_background_text": "#8A8A8A" } }
            }
            """);

        var result = ImportedThemes.Import(package, Path.Combine(root, "themes"), null).Result;

        // The author stated both halves; repairing would contradict them twice. The file is
        // still written — theirs to fix and still theirs to use — and the report says so.
        Assert.DoesNotContain(result.Repaired, r => r.Token == TokenKeys.TitleBar.Foreground);
        Assert.Contains(result.Residual, f => f.Ink == TokenKeys.TitleBar.Foreground);
        Assert.True(File.Exists(Path.Combine(root, "themes", result.File.Id + ThemeFileFormat.Extension)));
    }

    [Fact]
    public void DarkThemeOverlaysWhenTheThemeReadsDark()
    {
        var root = Scratch();
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1", "name": "Two Halves Fixture",
              "theme": { "colors": { "frame": "#151515", "tab_background_text": "#EEEEEE" } },
              "dark_theme": { "colors": { "frame": "#050510", "tab_background_text": "#F5F5FF" } }
            }
            """);

        var result = ImportedThemes.Import(package, Path.Combine(root, "themes"), null).Result;
        Assert.Equal("#050510", result.File.Tokens[TokenKeys.TitleBar.Background]);
    }

    // ------------------------------------------------------------------------------------
    // The reader and its hardening
    // ------------------------------------------------------------------------------------

    [Fact]
    public void AZipImportsLikeADirectoryAndAHostileOneIsRefusedWhole()
    {
        var root = Scratch();
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1", "name": "Zipped Fixture",
              "theme": { "colors": { "frame": "#123456" } }
            }
            """);
        var zip = Path.Combine(root, "theme.xpi");
        ZipFile.CreateFromDirectory(package, zip);

        var result = ImportedThemes.Import(zip, Path.Combine(root, "themes"), null).Result;
        Assert.Equal("#123456", result.File.Tokens[TokenKeys.TitleBar.Background]);

        // A package whose image path climbs out of the archive is refused with the reason,
        // and nothing is written.
        var hostile = Path.Combine(root, "hostile.xpi");
        using (var archive = ZipFile.Open(hostile, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write("""
                {
                  "manifest_version": 2, "version": "1", "name": "Hostile Fixture",
                  "theme": { "colors": { "frame": "#000000" }, "images": { "theme_frame": "../../escape.png" } }
                }
                """);
        }

        var hostileDirectory = Path.Combine(root, "themes-hostile");
        Assert.Throws<BrowserThemeException>(() => ImportedThemes.Import(hostile, hostileDirectory, _ => [1]));
        Assert.False(Directory.Exists(hostileDirectory) && Directory.EnumerateFiles(hostileDirectory).Any());
    }

    [Fact]
    public void NotAThemeIsSaidPlainly()
    {
        var root = Scratch();
        var extension = PackageWith(root, """{ "manifest_version": 2, "version": "1", "name": "An Extension" }""");
        var ex = Assert.Throws<BrowserThemeException>(() => ImportedThemes.Import(extension, Path.Combine(root, "themes"), null));
        Assert.Contains("not a theme", ex.Message);
    }

    // ------------------------------------------------------------------------------------
    // Ids, collisions, provenance, images
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ReimportUpdatesInPlaceAndACollisionGetsANumber()
    {
        var root = Scratch();
        var themes = Path.Combine(root, "themes");

        var first = PackageWith(Path.Combine(root, "a"), """
            {
              "manifest_version": 2, "version": "1", "name": "Same Name",
              "browser_specific_settings": { "gecko": { "id": "{11111111-aaaa}" } },
              "theme": { "colors": { "frame": "#111111" } }
            }
            """);
        var again = PackageWith(Path.Combine(root, "b"), """
            {
              "manifest_version": 2, "version": "2", "name": "Same Name",
              "browser_specific_settings": { "gecko": { "id": "{11111111-aaaa}" } },
              "theme": { "colors": { "frame": "#222222" } }
            }
            """);
        var stranger = PackageWith(Path.Combine(root, "c"), """
            {
              "manifest_version": 2, "version": "1", "name": "Same Name",
              "browser_specific_settings": { "gecko": { "id": "{22222222-bbbb}" } },
              "theme": { "colors": { "frame": "#333333" } }
            }
            """);

        var one = ImportedThemes.Import(first, themes, null);
        var two = ImportedThemes.Import(again, themes, null);
        var three = ImportedThemes.Import(stranger, themes, null);

        Assert.Equal("same-name", one.Result.File.Id);
        Assert.True(two.Updated);
        Assert.Equal("same-name", two.Result.File.Id);
        Assert.Equal("same-name-2", three.Result.File.Id);

        // The braces a gecko id wears would read as a token reference; they never survive.
        Assert.Equal("11111111-aaaa", two.Result.File.Tokens["import.source"]);
        Assert.Equal("2", two.Result.File.Tokens["import.version"]);

        // And everything still resolves — the provenance is inert.
        var resolved = new ThemeLibrary(ThemeLibrary.Load(themes).Files).Build("same-name").Resolve();
        Assert.Equal("#222222", resolved.GetString(TokenKeys.TitleBar.Background));
    }

    [Fact]
    public void ABuiltInsNameIsNeverShadowed()
    {
        var root = Scratch();
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1", "name": "Dark Gray",
              "theme": { "colors": { "frame": "#3D3D3D" } }
            }
            """);

        var result = ImportedThemes.Import(package, Path.Combine(root, "themes"), null).Result;
        Assert.Equal("dark-gray-imported", result.File.Id);
        Assert.True(ImportedThemes.ShadowsBuiltIn("darkgray"));
        Assert.True(ImportedThemes.ShadowsBuiltIn("dark-gray"));
        Assert.False(ImportedThemes.ShadowsBuiltIn("dark-gray-imported"));
    }

    [Fact]
    public void TheHeaderImageRidesTheBackdropAndARefusedOneLeavesItPlain()
    {
        var root = Scratch();
        byte[] fakeImage = [0x89, 0x50, 0x4E, 0x47];
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1", "name": "Header Image Fixture",
              "theme": {
                "colors": { "frame": "#101010" },
                "images": { "headerURL": "header.png" },
                "properties": { "additional_backgrounds_alignment": ["left top"], "additional_backgrounds_tiling": ["repeat-x"] }
              }
            }
            """, ("header.png", fakeImage));

        var themes = Path.Combine(root, "themes");
        var accepted = ImportedThemes.Import(package, themes, bytes => bytes);
        var id = accepted.Result.File.Id;
        Assert.Equal($"images/{id}/frame.png", accepted.Result.File.Tokens[TokenKeys.TitleBar.Backdrop]);
        Assert.Equal("left top", accepted.Result.File.Tokens[TokenKeys.TitleBar.BackdropAlignment]);
        Assert.Equal("repeat-x", accepted.Result.File.Tokens[TokenKeys.TitleBar.BackdropTiling]);
        Assert.True(File.Exists(Path.Combine(themes, "images", id, "frame.png")));

        // A decoder that rejects the bytes leaves the caption plain and the notes honest.
        var refused = ImportedThemes.Import(package, Path.Combine(root, "themes2"), _ => null);
        Assert.False(refused.Result.File.Tokens.TryGetRaw(TokenKeys.TitleBar.Backdrop, out _));
        Assert.Contains(refused.Notes, n => n.Contains("refused"));

        // Removal takes the file and its images together.
        ImportedThemes.Remove(id, themes);
        Assert.False(File.Exists(Path.Combine(themes, id + ThemeFileFormat.Extension)));
        Assert.False(Directory.Exists(Path.Combine(themes, "images", id)));
    }

    [Fact]
    public void TheWrittenFileRoundTrips()
    {
        var root = Scratch();
        var package = PackageWith(root, """
            {
              "manifest_version": 2, "version": "1", "name": "Round Trip Fixture",
              "theme": { "colors": { "frame": "#123456", "sidebar": "#234567" } }
            }
            """);

        var outcome = ImportedThemes.Import(package, Path.Combine(root, "themes"), null);
        var text = File.ReadAllText(outcome.Path);
        Assert.Equal(text, ThemeFileFormat.Write(ThemeFileFormat.Parse(text)));
    }
}
