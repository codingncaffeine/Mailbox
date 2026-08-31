using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Theming;
using Mailbox.Theming.Browse;
using Mailbox.Theming.Import;

namespace Mailbox.App.Views;

/// <summary>
/// The theme browser: search addons.mozilla.org's half-million community themes from inside
/// the application, see each one as Mailbox would actually wear it, and install through the
/// same door every import takes. The preview is the mapper — selecting a theme downloads its
/// tiny file and paints a miniature shell from the mapped tokens, so what the pane shows is
/// what Install produces.
/// </summary>
/// <remarks>
/// This is the one place Mailbox's theming touches a network: only when the reader opens it,
/// only to the source's own host, size-capped both ways. Install asks first, in plain words —
/// these are community files, with the theme's licence named while declining is still a
/// button. The harness browses a committed fixture directory instead
/// (<c>MAILBOX_THEME_SOURCE</c>), so every claim here is provable offline.
/// </remarks>
internal sealed class ThemeBrowserDialog : Window
{
    /// <summary>Points the dialog at a directory source instead of the live one.</summary>
    internal const string SourceVariable = "MAILBOX_THEME_SOURCE";

    /// <summary>Drives the dialog: <c>select:&lt;slug&gt;</c>, optionally <c>,install</c>.</summary>
    internal const string BrowseVariable = "MAILBOX_THEME_BROWSE";

    private const long ThemeFileCap = 16 * 1024 * 1024;
    private const long ThumbnailCap = 512 * 1024;

    private readonly ThemeService _themes;
    private readonly IThemeSource _source;
    private readonly bool _live;

    private readonly TextBox _search = new() { PlaceholderText = "Search themes", Width = 220 };
    private readonly ComboBox _sort = new()
    {
        ItemsSource = new List<string> { "Recommended", "Popular", "Top rated", "Trending" },
        SelectedIndex = 0,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly ComboBox _category = new()
    {
        ItemsSource = (List<string>)["All categories", .. AmoThemeSource.Categories.Select(c => c.Name)],
        SelectedIndex = 0,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly ListBox _list = new() { MinWidth = 300 };
    private readonly ThemePreview _preview = new() { Height = 240 };
    private readonly TextBlock _metaName = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _metaDetail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _metaLicence = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _install = new() { Content = "Install…", IsEnabled = false };
    private readonly Button _more = new() { Content = "More", IsEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private readonly List<ThemeListing> _listings = [];
    private readonly Dictionary<string, Bitmap> _thumbnails = new(StringComparer.Ordinal);
    private string? _colour;
    private int _page = 1;
    private long _total;
    private string? _cachedPath;
    private ThemeListing? _selected;
    private int _previewStamp;

    /// <summary>Raised after an install, so the Options theme row can rebuild.</summary>
    internal event Action? Installed;

    private readonly TaskCompletionSource _harnessDone = new();

    /// <summary>Completed once the opening search and any posed script have finished — the capture's cue.</summary>
    internal Task HarnessDone => _harnessDone.Task;

    public ThemeBrowserDialog(ThemeService themes)
    {
        _themes = themes;
        var fixture = Environment.GetEnvironmentVariable(SourceVariable);
        _live = string.IsNullOrEmpty(fixture);
        _source = _live ? new AmoThemeSource() : new DirectoryThemeSource(fixture!);

        Title = "Browse Themes";
        Width = 880;
        Height = 620;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        DialogChrome.Apply(this, BuildBody());
        Opened += async (_, _) =>
        {
            PruneCache();
            await SearchAsync(reset: true);
            await RunHarnessAsync();
            _harnessDone.TrySetResult();
        };
        Closed += (_, _) =>
        {
            foreach (var bitmap in _thumbnails.Values) bitmap.Dispose();
            (_source as IDisposable)?.Dispose();
        };
    }

    // ------------------------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------------------------

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    private Control BuildBody()
    {
        var caption = new TextBlock
        {
            Text = _live
                ? "Community themes from addons.mozilla.org. The preview shows each one as Mailbox "
                  + "would wear it; Install brings it in through the same checks as any imported file."
                : "Browsing a local fixture source.",
            TextWrapping = TextWrapping.Wrap,
        };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        foreach (var block in (TextBlock[])[_metaName, _metaDetail, _status])
        {
            Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        }

        Bind(_metaLicence, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _search.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) await SearchAsync(reset: true);
        };
        _sort.SelectionChanged += async (_, _) => await SearchAsync(reset: true);
        _category.SelectionChanged += async (_, _) => await SearchAsync(reset: true);
        _more.Click += async (_, _) => await SearchAsync(reset: false);
        _list.SelectionChanged += async (_, _) => await PreviewAsync();
        _install.Click += async (_, _) => await InstallAsync();

        var go = new Button { Content = "Search" };
        go.Click += async (_, _) => await SearchAsync(reset: true);

        // The colour row: AMO searches themes by a colour, and the swatches are the theming
        // project's own list — no view names a colour value.
        var colours = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var any = new Button { Content = "Any", Padding = new Thickness(8, 2) };
        any.Click += async (_, _) => { _colour = null; await SearchAsync(reset: true); };
        colours.Children.Add(any);
        foreach (var (name, hex) in AmoThemeSource.SearchColours)
        {
            var swatch = new Button
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse(hex)),
            };
            ToolTip.SetTip(swatch, name);
            swatch.Click += async (_, _) => { _colour = hex; await SearchAsync(reset: true); };
            colours.Children.Add(swatch);
        }

        // Two header rows: the words, then the colours — ten swatches will not share a line
        // with a search box at this width, and clipping them helped nobody.
        var header = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _search, go, _sort, _category } },
                colours,
            },
        };

        var meta = new StackPanel
        {
            Spacing = 6,
            Children = { _preview, _metaName, _metaDetail, _metaLicence, _install },
        };
        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("320,16,*"),
            RowDefinitions = new RowDefinitions("*"),
        };
        var listScroll = new ScrollViewer { Content = _list };
        Grid.SetColumn(listScroll, 0);
        columns.Children.Add(listScroll);
        Grid.SetColumn(meta, 2);
        columns.Children.Add(meta);

        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,8,Auto") };
        footer.Children.Add(_status);
        Grid.SetColumn(_more, 1);
        footer.Children.Add(_more);
        Grid.SetColumn(close, 3);
        footer.Children.Add(close);

        var body = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
        };
        Grid.SetRow(caption, 0);
        body.Children.Add(caption);
        header.Margin = new Thickness(0, 10, 0, 10);
        Grid.SetRow(header, 1);
        body.Children.Add(header);
        Grid.SetRow(columns, 2);
        body.Children.Add(columns);
        footer.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(footer, 3);
        body.Children.Add(footer);
        return body;
    }

    private Control Row(ThemeListing listing)
    {
        var thumb = new Border
        {
            Width = 72,
            Height = 30,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(thumb, Border.BackgroundProperty, "dialog.surface.brush");
        if (listing.ThumbnailUrl is { } url) _ = LoadThumbnailAsync(thumb, url);

        // The rows stand on the list box's own surface, whose ink is not the dialog ground's —
        // Dark Gray's light boxes under dark chrome are the case that decides it.
        var name = new TextBlock { Text = listing.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock
        {
            Text = listing.Rating > 0
                ? $"{listing.Author} · ★ {listing.Rating:0.0} · {Users(listing.Users)}"
                : listing.Author,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.75,
        };
        Bind(name, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
        Bind(detail, TextBlock.ForegroundProperty, "dialog.surface.text.brush");

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { name, detail } };
        return new DockPanel { LastChildFill = true, Children = { thumb, text }, Tag = listing };
    }

    private static string Users(long users)
        => users >= 1000 ? $"{users / 1000.0:0.#}k users" : $"{users} users";

    // ------------------------------------------------------------------------------------
    // Searching and the list
    // ------------------------------------------------------------------------------------

    private async Task SearchAsync(bool reset)
    {
        if (reset)
        {
            _page = 1;
            _listings.Clear();
        }
        else
        {
            _page++;
        }

        var sort = _sort.SelectedIndex switch
        {
            1 => ThemeSort.Popular,
            2 => ThemeSort.TopRated,
            3 => ThemeSort.Trending,
            _ => ThemeSort.Recommended,
        };
        var query = _search.Text?.Trim() ?? string.Empty;
        var category = _category.SelectedIndex > 0 ? AmoThemeSource.Categories[_category.SelectedIndex - 1].Slug : null;

        // The showcase shelf is small and curated; intersecting it with a search, a colour or
        // a category would answer with slivers. A narrowed browse means "the whole catalogue,
        // by reach" — quietly.
        if (sort == ThemeSort.Recommended && (query.Length > 0 || _colour is not null || category is not null))
        {
            sort = ThemeSort.Popular;
        }

        _status.Text = "Searching…";
        try
        {
            var (results, total) = await _source.SearchAsync(query, sort, _colour, category, _page, CancellationToken.None);
            _listings.AddRange(results);
            _total = total;
            _list.ItemsSource = _listings.Select(Row).ToList();
            _more.IsEnabled = _listings.Count < _total;
            _status.Text = _total == 0
                ? "Nothing matched."
                : $"{_listings.Count} of {_total:N0} theme{(_total == 1 ? "" : "s")}.";
        }
        catch (ThemeSourceException ex)
        {
            _status.Text = ex.Message;
            Log.Warn($"Theme browser: {ex.Message}");
        }
    }

    private async Task LoadThumbnailAsync(Border host, string url)
    {
        try
        {
            if (!_thumbnails.TryGetValue(url, out var bitmap))
            {
                var bytes = await _source.FetchAsync(url, ThumbnailCap, CancellationToken.None);
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
                if (_thumbnails.Count > 60)
                {
                    foreach (var kept in _thumbnails.Values) kept.Dispose();
                    _thumbnails.Clear();
                }

                _thumbnails[url] = bitmap;
            }

            host.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (ex is ThemeSourceException or ArgumentException or NotSupportedException)
        {
            Log.Debug($"Theme browser: thumbnail skipped — {ex.Message}");
        }
    }

    // ------------------------------------------------------------------------------------
    // The preview: fetch small, map for real, paint the miniature
    // ------------------------------------------------------------------------------------

    private ThemeListing? SelectedListing
        => (_list.SelectedItem as Control)?.Tag as ThemeListing;

    private async Task PreviewAsync()
    {
        if (SelectedListing is not { } listing) return;
        var stamp = ++_previewStamp;
        _selected = listing;
        _install.IsEnabled = false;
        _metaName.Text = listing.Name;
        _metaDetail.Text = listing.Rating > 0
            ? $"by {listing.Author} · ★ {listing.Rating:0.0} · {Users(listing.Users)}"
            : $"by {listing.Author}";
        _metaLicence.Text = listing.LicenceName is { Length: > 0 }
            ? $"Licence: {listing.LicenceName}" + (listing.LicenceUrl is { Length: > 0 } u ? $" — {u}" : "")
            : "Licence: not stated by its author.";
        _status.Text = "Fetching the theme…";

        try
        {
            var bytes = await _source.FetchAsync(listing.FileUrl, ThemeFileCap, CancellationToken.None);
            if (stamp != _previewStamp) return; // a later selection overtook this one

            // The cache first; somewhere that must exist second. A sandbox that closed the
            // cache is no reason the preview goes dark — the file only has to live as long
            // as this dialog.
            string cached;
            try
            {
                Directory.CreateDirectory(CacheDirectory());
                cached = Path.Combine(CacheDirectory(), Slug(listing.Slug) + ".xpi");
                await File.WriteAllBytesAsync(cached, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"Theme browser: the cache is closed here ({ex.Message}); using the run's own temp.");
                cached = Path.Combine(Path.GetTempPath(), "mailbox-theme-" + Slug(listing.Slug) + ".xpi");
                await File.WriteAllBytesAsync(cached, bytes);
            }

            _cachedPath = cached;

            using var package = BrowserThemePackage.Open(cached);
            var theme = BrowserThemeManifest.Parse(package.ManifestJson);
            var result = SlimThemeImport.Map(theme, "preview", theme.Name, backdropPath: null);
            var resolved = new Mailbox.Theming.Files.ThemeLibrary([result.File]).Build("preview").Resolve();

            Bitmap? backdrop = null;
            if ((theme.FrameImage ?? theme.AdditionalBackgrounds.FirstOrDefault()) is { } frame
                && package.ReadImage(frame) is { } image)
            {
                try
                {
                    backdrop = new Bitmap(new MemoryStream(image));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    Log.Debug($"Theme browser: the header image does not decode — {ex.Message}");
                }
            }

            _preview.Show(resolved, backdrop);
            _install.IsEnabled = true;
            _status.Text = $"Previewing as Mailbox would wear it — based on {result.BaseId}.";
            Log.Info($"Harness: theme browser — previewing \"{listing.Slug}\": base {result.BaseId}, "
                     + $"{result.TokensWritten.Count} token(s), licence {listing.LicenceName ?? "not stated"}.");
        }
        catch (Exception ex) when (ex is ThemeSourceException or BrowserThemeException or IOException or UnauthorizedAccessException)
        {
            if (stamp == _previewStamp) _status.Text = ex.Message;
            Log.Warn($"Theme browser: preview of \"{listing.Slug}\" failed — {ex.Message}");
        }
    }

    // ------------------------------------------------------------------------------------
    // Install: the warning, then the one door
    // ------------------------------------------------------------------------------------

    private async Task InstallAsync()
    {
        if (_selected is not { } listing || _cachedPath is not { } cached || !File.Exists(cached)) return;

        // The owner's ask, in honest words: community files, what Mailbox actually does with
        // them, and the terms — while declining is still a button.
        var licence = listing.LicenceName is { Length: > 0 } name ? name : "no licence stated";
        var confirmed = await Confirm.AskAsync(this, "Install Theme",
            $"“{listing.Name}” was made and uploaded by its author ({listing.Author}), not by Mailbox. "
            + "Mailbox reads only the theme's colours and re-encodes its images, but anything downloaded "
            + "from the internet deserves a moment's doubt — install only what you trust.\n\n"
            + $"Offered under: {licence}.",
            "Install", destructive: false);
        if (!confirmed) return;

        try
        {
            var directory = Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory();
            var outcome = ImportedThemes.Import(cached, directory, Theming.ThemeImportDoor.Reencode);
            _themes.ReplaceLibrary(Mailbox.Theming.Files.ThemeLibrary.Load(directory));
            if (_themes.Library.Canonical(outcome.Result.File.Id) is { } id)
            {
                _themes.ApplyFresh(id);
                App.Settings.Set(App.ThemeSetting, _themes.ThemeId);
            }

            _status.Text = $"Installed and applied — “{outcome.Result.File.Name}”. Remove it any time from the theme row.";
            Installed?.Invoke();
            Log.Info($"Harness: theme browser — installed \"{outcome.Result.File.Id}\"; active theme is {_themes.ThemeId}.");
        }
        catch (Exception ex) when (ex is BrowserThemeException or Mailbox.Theming.Files.ThemeFileException
                                       or IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
            Log.Warn($"Theme browser: install of \"{listing.Slug}\" refused — {ex.Message}");
        }
    }

    // ------------------------------------------------------------------------------------
    // The cache: a handful of tiny files, newest kept
    // ------------------------------------------------------------------------------------

    private static string CacheDirectory()
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrWhiteSpace(cache))
        {
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        return Path.Combine(cache, "mailbox", "theme-browser");
    }

    private static void PruneCache()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory())) return;
            foreach (var stale in new DirectoryInfo(CacheDirectory()).GetFiles()
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(20))
            {
                stale.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Theme browser: cache prune skipped — {ex.Message}");
        }
    }

    private static string Slug(string value)
        => new([.. value.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    // ------------------------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------------------------

    private async Task RunHarnessAsync()
    {
        if (Environment.GetEnvironmentVariable(BrowseVariable) is not { Length: > 0 } script) return;

        Log.Info($"Harness: theme browser — {_listings.Count} of {_total} listed from "
                 + $"{(_live ? "the live source" : "the fixture source")}.");

        foreach (var op in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (op.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
            {
                _search.Text = op[7..];
                await SearchAsync(reset: true);
                Log.Info($"Harness: theme browser — searched \"{op[7..]}\": {_listings.Count} of {_total} listed.");
            }
            else if (op.StartsWith("select:", StringComparison.OrdinalIgnoreCase))
            {
                var slug = op[7..];
                var index = _listings.FindIndex(l => string.Equals(l.Slug, slug, StringComparison.OrdinalIgnoreCase));
                Log.Info(index < 0
                    ? $"Harness: theme browser — no listing \"{slug}\"."
                    : $"Harness: theme browser — selecting \"{slug}\".");
                if (index >= 0)
                {
                    _list.SelectedIndex = index;
                    await PreviewAsync();
                }
            }
            else if (string.Equals(op, "install", StringComparison.OrdinalIgnoreCase))
            {
                await InstallAsync();
            }
        }
    }
}
