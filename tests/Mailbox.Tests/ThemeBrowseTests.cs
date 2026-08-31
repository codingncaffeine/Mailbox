using Mailbox.Theming.Browse;

namespace Mailbox.Tests;

/// <summary>
/// The theme browser's source layer, over committed fixtures — no network in a test, ever.
/// The AMO reading is proven on a hand-written listing in the API's shape; the directory
/// source is proven on the same fixtures the poses browse.
/// </summary>
public class ThemeBrowseTests
{
    private static readonly string Fixtures =
        Path.Combine(AppContext.BaseDirectory, FindRepoRelative("tests/fixtures/theme-browser"));

    private static string FindRepoRelative(string tail)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, tail)))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory is null ? tail : Path.Combine(directory, tail);
    }

    [Fact]
    public void TheAmoShapeReadsWholeAndTheGapsReadHonestly()
    {
        var (results, total) = AmoThemeSource.Parse(File.ReadAllText(Path.Combine(Fixtures, "amo-sample.json")));

        Assert.Equal(514655, total);

        // The fileless entry is dropped: nothing to install is nothing to list.
        Assert.Equal(2, results.Count);

        var full = results[0];
        Assert.Equal("sample-balanced", full.Slug);
        Assert.Equal("Sample Balanced", full.Name);              // localised map
        Assert.Equal("A Sample Author", full.Author);
        Assert.Equal(301399, full.Users);
        Assert.Equal(4.6323, full.Rating, 4);
        Assert.EndsWith("thumbs/1/2.png", full.ThumbnailUrl);    // the png thumb, never the svg
        Assert.EndsWith("sample.xpi", full.FileUrl);
        Assert.Equal(9543, full.FileSize);
        Assert.Equal("Creative Commons Attribution 3.0", full.LicenceName);

        var bare = results[1];
        Assert.Equal("Sample Bare", bare.Name);                  // plain-string name
        Assert.Equal("unknown", bare.Author);
        Assert.Null(bare.ThumbnailUrl);
        Assert.Null(bare.LicenceName);
    }

    [Fact]
    public void NotJsonIsSaidPlainly()
    {
        Assert.Throws<ThemeSourceException>(() => AmoThemeSource.Parse("<html>an error page</html>"));
    }

    [Fact]
    public async Task TheDirectorySourceServesTheFixturesAndSearchesThem()
    {
        var source = new DirectoryThemeSource(Fixtures);
        var (all, total) = await source.SearchAsync("", ThemeSort.Popular, null, 1, CancellationToken.None);
        Assert.Equal(2, total);
        Assert.Contains(all, l => l.Slug == "midnight");
        Assert.Contains(all, l => l.Slug == "harvest" && l.LicenceName == "All Rights Reserved");

        var (found, _) = await source.SearchAsync("harvest", ThemeSort.Popular, null, 1, CancellationToken.None);
        Assert.Single(found);

        var bytes = await source.FetchAsync("midnight.xpi", 1024 * 1024, CancellationToken.None);
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public async Task TheDirectorySourceRefusesEscapesAndOversize()
    {
        var source = new DirectoryThemeSource(Fixtures);
        await Assert.ThrowsAsync<ThemeSourceException>(
            () => source.FetchAsync("../theme-import/manifest.json", 1024 * 1024, CancellationToken.None));
        await Assert.ThrowsAsync<ThemeSourceException>(
            () => source.FetchAsync("harvest.xpi", 10, CancellationToken.None));
    }

    [Fact]
    public async Task TheAmoFetchOnlyDownloadsFromItsOwnHost()
    {
        using var source = new AmoThemeSource();
        await Assert.ThrowsAsync<ThemeSourceException>(
            () => source.FetchAsync("https://example.com/theme.xpi", 1024, CancellationToken.None));
        await Assert.ThrowsAsync<ThemeSourceException>(
            () => source.FetchAsync("http://addons.mozilla.org/insecure.xpi", 1024, CancellationToken.None));
    }

    [Fact]
    public void TheColourRowComesFromTheThemingProject()
    {
        // Ten swatches with usable chroma or deliberate neutrality — and defined here, so no
        // view ever names a colour value.
        Assert.Equal(10, AmoThemeSource.SearchColours.Count);
        Assert.All(AmoThemeSource.SearchColours, c => Assert.Matches("^#[0-9A-F]{6}$", c.Hex));
    }

    [Fact]
    public async Task AFixtureXpiRunsTheWholePreviewPipeline()
    {
        // The browser's preview is the mapper: fetch, open, map — the same journey install
        // takes, stopped before disk.
        var source = new DirectoryThemeSource(Fixtures);
        var bytes = await source.FetchAsync("midnight.xpi", 1024 * 1024, CancellationToken.None);

        var cached = Path.Combine(Path.GetTempPath(), $"browse-preview-{Guid.NewGuid():N}.xpi");
        File.WriteAllBytes(cached, bytes);
        using var package = Mailbox.Theming.Import.BrowserThemePackage.Open(cached);
        var theme = Mailbox.Theming.Import.BrowserThemeManifest.Parse(package.ManifestJson);
        var result = Mailbox.Theming.Import.SlimThemeImport.Map(theme, "preview", theme.Name, null);

        Assert.Equal("darkgray", result.BaseId);
        Assert.Equal("#141024", result.File.Tokens["titlebar.background"]);

        var resolved = new Mailbox.Theming.Files.ThemeLibrary([result.File]).Build("preview").Resolve();
        Assert.Equal("#D4D4D4", resolved.GetString("list.row.background")); // content stays the base's
    }
}
