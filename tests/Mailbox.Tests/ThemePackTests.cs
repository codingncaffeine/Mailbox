using Mailbox.Theming.Files;
using Mailbox.Theming.Import;
using Mailbox.Theming.Tokens;

namespace Mailbox.Tests;

/// <summary>
/// A theme travels as a pack — json plus images, one zip — and arrives through the same door
/// as everything else, held to the same rules: limits, traversal checks, decoder-laundered
/// images, and never a built-in's name.
/// </summary>
public class ThemePackTests
{
    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailbox-pack-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SeedTheme(string directory, string id = "voyager")
    {
        var tokens = new TokenSet();
        tokens.Set(TokenKeys.TitleBar.Background, "#123456");
        tokens.Set(TokenKeys.TitleBar.Backdrop, $"images/{id}/frame.png");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ThemeFileFormat.Extension),
            ThemeFileFormat.Write(new ThemeFile(id, "Voyager", "darkgray", null, tokens)));
        var images = Path.Combine(directory, "images", id);
        Directory.CreateDirectory(images);
        File.WriteAllBytes(Path.Combine(images, "frame.png"), [0x89, 0x50, 0x4E, 0x47]);
        return id;
    }

    [Fact]
    public void APackRoundTripsThroughTheOneImportDoor()
    {
        var root = Scratch();
        var source = Path.Combine(root, "themes-a");
        var id = SeedTheme(source);

        var pack = ThemePack.Export(id, source, Path.Combine(root, "voyager" + ThemePack.Extension));
        Assert.True(ThemePack.IsPack(pack));

        // The one door: ImportedThemes.Import recognises the pack and routes it.
        var destination = Path.Combine(root, "themes-b");
        var outcome = ImportedThemes.Import(pack, destination, bytes => bytes);

        Assert.Equal("pack", outcome.Result.Origin);
        Assert.True(File.Exists(Path.Combine(destination, id + ThemeFileFormat.Extension)));
        Assert.True(File.Exists(Path.Combine(destination, "images", id, "frame.png")));

        var arrived = ThemeFileFormat.Parse(File.ReadAllText(outcome.Path));
        Assert.Equal("#123456", arrived.Tokens[TokenKeys.TitleBar.Background]);
        Assert.Equal($"images/{id}/frame.png", arrived.Tokens[TokenKeys.TitleBar.Backdrop]);

        // Again is an update, not a twin.
        Assert.True(ImportedThemes.Import(pack, destination, bytes => bytes).Updated);
    }

    [Fact]
    public void ABuiltInsNameInAPackIsRefused()
    {
        var root = Scratch();
        var source = Path.Combine(root, "themes");
        SeedTheme(source, "voyager");

        // Rewrite the packed json to carry a built-in's slug, the pack equivalent of shadowing.
        var tokens = new TokenSet();
        tokens.Set(TokenKeys.TitleBar.Background, "#000000");
        File.WriteAllText(Path.Combine(source, "dark-gray" + ThemeFileFormat.Extension),
            ThemeFileFormat.Write(new ThemeFile("dark-gray", "Dark Gray", "white", null, tokens)));
        var pack = ThemePack.Export("dark-gray", source, Path.Combine(root, "shadow.zip"));

        Assert.Throws<BrowserThemeException>(() => ImportedThemes.Import(pack, Path.Combine(root, "themes-b"), null));
    }

    [Fact]
    public void ExportRefusesABuiltInAndAnAbsentTheme()
    {
        var root = Scratch();
        Assert.Throws<ArgumentException>(() => ThemePack.Export("darkgray", root));
        Assert.Throws<FileNotFoundException>(() => ThemePack.Export("no-such", root));
    }

    [Fact]
    public void AnImageTheDecoderRefusesStaysOut()
    {
        var root = Scratch();
        var source = Path.Combine(root, "themes-a");
        var id = SeedTheme(source);
        var pack = ThemePack.Export(id, source, Path.Combine(root, "p.zip"));

        var destination = Path.Combine(root, "themes-b");
        var outcome = ImportedThemes.Import(pack, destination, _ => null);

        Assert.False(File.Exists(Path.Combine(destination, "images", id, "frame.png")));
        Assert.Contains(outcome.Notes, n => n.Contains("refused"));
        Assert.True(File.Exists(outcome.Path)); // the colours still arrive
    }
}
