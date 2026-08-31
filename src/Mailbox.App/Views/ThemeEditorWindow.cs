using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Theming;
using Mailbox.Theming.Files;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>
/// The live theme editor: the token list on the left, the selected token's own editor on
/// the right, and the running application as the preview — every committed value is applied to
/// the real theme service, so the shell behind this window is the split view's other half.
/// </summary>
/// <remarks>
/// Edits are session overrides on the current theme, visibly marked and individually
/// resettable; a theme file written by Save As is their durable form, landing in the themes
/// directory the library already watches. A value that breaks resolution — a reference to
/// nothing, a cycle — is named in the status line and backed out, because the one thing a
/// live editor must never do is take the application it is previewing down with it. Contrast
/// findings are the audit's own words, refreshed on every apply: a theme can be saved anyway,
/// it just cannot be saved silently.
/// </remarks>
public sealed class ThemeEditorWindow : Window
{
    private readonly ThemeService _themes;
    private readonly TokenSet _base;
    private readonly TokenSet _overrides = new();
    private readonly string _editedTheme;

    private readonly TextBox _search = new() { PlaceholderText = "Search tokens", Width = 300 };
    private readonly ListBox _list = new();
    private readonly TextBlock _keyLabel = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _layerLabel = new();
    private readonly TextBlock _baseLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _value = new() { Width = 320 };
    private readonly Border _swatch = new() { Width = 44, Height = 24, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
    private readonly Button _reset = new() { Content = "Reset", IsEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _modifiedCount = new();
    private readonly ItemsControl _contrast = new();

    private List<string> _keys = [];
    private IReadOnlyList<string>? _areaIds;
    private readonly TextBlock _areaLabel = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Button _allTokens = new() { Content = "All tokens" };

    public ThemeEditorWindow(ThemeService themes)
        : this(themes, areaIds: null)
    {
    }

    /// <summary>The editor scoped to the areas the picker chose; null is the whole theme.</summary>
    public ThemeEditorWindow(ThemeService themes, IReadOnlyList<string>? areaIds)
    {
        _themes = themes;
        _areaIds = areaIds;
        _editedTheme = themes.ThemeId;
        _base = themes.Library.Build(_editedTheme);
        foreach (var (key, value) in Overpairs(themes.UserOverrides)) _overrides.Set(key, value);

        Title = $"Customize Theme — {themes.DisplayName(_editedTheme)}";
        Width = 960;
        Height = 660;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        DialogChrome.Apply(this, BuildBody());
        RefreshList();
        RefreshContrast();
        if (_areaIds is not null)
        {
            Mailbox.Core.Diagnostics.Log.Info(
                $"Theme editor scoped to [{string.Join(", ", _areaIds)}] — {_keys.Count} token(s).");
        }

        Opened += (_, _) => _ = HarnessAsync();
    }

    private static IEnumerable<(string, string)> Overpairs(TokenSet? set)
        => set is null ? [] : set.Keys.Select(k => (k, set[k]!));

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    // ------------------------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------------------------

    private Control BuildBody()
    {
        var caption = new TextBlock
        {
            Text = "Every colour, size and family the theme is made of. Changes apply to the running "
                 + "application as you make them; Save As Theme writes them into a theme of your own.",
            TextWrapping = TextWrapping.Wrap,
        };
        Bind(caption, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        Bind(_modifiedCount, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        Bind(_status, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        Bind(_keyLabel, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        Bind(_layerLabel, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        Bind(_baseLabel, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        Bind(_swatch, Border.BorderBrushProperty, "dialog.foreground.subtle.brush");

        _search.TextChanged += (_, _) => RefreshList();
        _list.SelectionChanged += (_, _) => ShowSelected();
        _list.MinWidth = 360;

        _value.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) CommitValue();
        };
        _value.LostFocus += (_, _) => CommitValue();
        _reset.Click += (_, _) => ResetSelected();

        var resetAll = new Button { Content = "Reset All" };
        resetAll.Click += (_, _) => ResetAll();

        var saveAs = new Button { Content = "Save As Theme…" };
        saveAs.Click += async (_, _) => await SaveAsAsync();

        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var valueRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _value, _swatch, _reset },
        };

        var contrastHeading = new TextBlock { Text = "Contrast", FontWeight = FontWeight.SemiBold };
        Bind(contrastHeading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var detail = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(16, 0, 0, 0),
            Children =
            {
                _keyLabel, _layerLabel, _baseLabel, valueRow, _status,
                contrastHeading,
                new ScrollViewer { Content = _contrast, MaxHeight = 260 },
            },
        };

        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("380,*"),
            RowDefinitions = new RowDefinitions("*"),
        };
        var listScroll = new ScrollViewer { Content = _list };
        Grid.SetColumn(listScroll, 0);
        columns.Children.Add(listScroll);
        Grid.SetColumn(detail, 1);
        columns.Children.Add(detail);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { resetAll, saveAs, close },
        };

        // The area scope: what the picker chose, and the way back out of it. The picker
        // itself lives on the shell — the shell is the preview, so that is where pointing
        // at a region means something.
        Bind(_areaLabel, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        _allTokens.Click += (_, _) =>
        {
            _areaIds = null;
            RefreshScopeLabel();
            RefreshList();
        };

        var pickArea = new Button { Content = "Pick an area…" };
        ToolTip.SetTip(pickArea, "Point at a part of Mailbox and this editor scopes to what paints it.");
        pickArea.Click += (_, _) =>
        {
            // The shell is down the owner chain; the dialogs between close so the picker has
            // the shell to point at. The shell itself is never closed here.
            var owner = Owner as Window;
            Close();
            while (owner is not null && owner is not MainWindow)
            {
                var next = owner.Owner as Window;
                owner.Close();
                owner = next;
            }

            if (owner is MainWindow shell) shell.BeginAreaInspect();
        };

        RefreshScopeLabel();

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { _search, pickArea, _areaLabel, _allTokens, _modifiedCount },
        };

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
        buttons.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(buttons, 3);
        body.Children.Add(buttons);

        return body;
    }

    // ------------------------------------------------------------------------------------
    // The list and the selected token
    // ------------------------------------------------------------------------------------

    private string? SelectedKey =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _keys.Count ? _keys[_list.SelectedIndex] : null;

    /// <summary>The scoped tokens, or null for the whole theme.</summary>
    private HashSet<string>? AreaTokens()
    {
        if (_areaIds is null) return null;
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in Mailbox.Theming.Tokens.TokenMap.Areas.Where(a => _areaIds.Contains(a.Id)))
        {
            foreach (var token in area.Tokens) tokens.Add(token);
        }

        return tokens;
    }

    private void RefreshScopeLabel()
    {
        var names = _areaIds is null
            ? []
            : Mailbox.Theming.Tokens.TokenMap.Areas.Where(a => _areaIds.Contains(a.Id)).Select(a => a.Name).ToList();
        _areaLabel.Text = names.Count == 0 ? string.Empty : $"Area: {string.Join(" + ", names)}";
        _areaLabel.IsVisible = names.Count > 0;
        _allTokens.IsVisible = names.Count > 0;
    }

    private void RefreshList()
    {
        var selected = SelectedKey;
        var filter = _search.Text?.Trim() ?? string.Empty;
        var scope = AreaTokens();

        // Scoped, the list reads as the area does: grounds first, then inks, washes, lines —
        // the roles the map knows — instead of the alphabet.
        _keys = _base.Keys
            .Where(k => scope is null || scope.Contains(k))
            .Where(k => filter.Length == 0 || k.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => scope is null ? 0 : (int)Mailbox.Theming.Tokens.TokenMap.RoleOf(k))
            .ThenBy(k => k, StringComparer.Ordinal)
            .ToList();

        _list.ItemsSource = _keys.Select(Row).ToList();
        _modifiedCount.Text = _overrides.Count == 0
            ? "No changes"
            : $"{_overrides.Count} token{(_overrides.Count == 1 ? "" : "s")} changed";

        if (selected is not null && _keys.IndexOf(selected) is var index && index >= 0)
            _list.SelectedIndex = index;
        else if (_keys.Count > 0 && _list.SelectedIndex < 0)
            _list.SelectedIndex = 0;
    }

    private Control Row(string key)
    {
        var modified = _overrides.TryGetRaw(key, out _);
        var name = new TextBlock
        {
            Text = key,
            FontWeight = modified ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(name, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var row = new DockPanel { LastChildFill = true };

        if (ResolvedColour(key) is { } colour)
        {
            var swatch = new Border
            {
                Width = 18,
                Height = 14,
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(colour),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(swatch, Border.BorderBrushProperty, "dialog.foreground.subtle.brush");
            DockPanel.SetDock(swatch, Dock.Left);
            row.Children.Add(swatch);
        }
        else
        {
            var spacer = new Border { Width = 26 };
            DockPanel.SetDock(spacer, Dock.Left);
            row.Children.Add(spacer);
        }

        if (modified)
        {
            var dot = new TextBlock { Text = "●", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Bind(dot, TextBlock.ForegroundProperty, "accent.rest.brush");
            DockPanel.SetDock(dot, Dock.Right);
            row.Children.Add(dot);
        }

        row.Children.Add(name);
        return row;
    }

    private Color? ResolvedColour(string key)
        => _themes.Tokens.TryGetString(key, out var value) && Color.TryParse(value, out var colour) ? colour : null;

    private void ShowSelected()
    {
        if (SelectedKey is not { } key) return;

        _keyLabel.Text = key;
        var area = Mailbox.Theming.Tokens.TokenMap.AreaOf(key);
        _layerLabel.Text = $"{TokenLayerExtensions.InferLayer(key)} token · {Mailbox.Theming.Tokens.TokenMap.RoleOf(key)}"
                           + (area is null ? string.Empty : $" · {area.Name}");
        _baseLabel.Text = $"Theme value: {_base[key]}";
        _value.Text = _overrides.TryGetRaw(key, out var over) ? over : _base[key];
        _reset.IsEnabled = _overrides.TryGetRaw(key, out _);
        _status.Text = string.Empty;
        RefreshSwatch(key);
    }

    private void RefreshSwatch(string key)
    {
        _swatch.Background = ResolvedColour(key) is { } colour ? new SolidColorBrush(colour) : Brushes.Transparent;
    }

    // ------------------------------------------------------------------------------------
    // Editing
    // ------------------------------------------------------------------------------------

    private void CommitValue()
    {
        if (SelectedKey is not { } key) return;
        var text = _value.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || text == (_overrides.TryGetRaw(key, out var over) ? over : _base[key])) return;

        Set(key, text);
    }

    /// <summary>One edit, applied live — and backed out with its reason when it breaks resolution.</summary>
    internal bool Set(string key, string value)
    {
        var previous = _overrides.TryGetRaw(key, out var kept) ? kept : null;
        _overrides.Set(key, value);

        try
        {
            _themes.Apply(_editedTheme, overrides: _overrides);
        }
        catch (ThemeResolutionException ex)
        {
            _overrides[key] = previous;
            _themes.Apply(_editedTheme, overrides: _overrides);
            _status.Text = ex.Message;
            Log.Warn($"Theme editor: {ex.Message}");
            return false;
        }

        _status.Text = string.Empty;
        AfterApply(key);
        return true;
    }

    internal void Reset(string key)
    {
        if (!_overrides.TryGetRaw(key, out _)) return;
        _overrides[key] = null;
        _themes.Apply(_editedTheme, overrides: _overrides);
        AfterApply(key);
    }

    private void ResetSelected()
    {
        if (SelectedKey is { } key) Reset(key);
    }

    private void ResetAll()
    {
        foreach (var key in _overrides.Keys.ToList()) _overrides[key] = null;
        _themes.ClearOverrides();
        AfterApply(SelectedKey);
    }

    private void AfterApply(string? key)
    {
        RefreshList();
        RefreshContrast();
        if (key is not null && key == SelectedKey) ShowSelected();
    }

    private void RefreshContrast()
    {
        var findings = ContrastAudit.Check(_themes.Tokens);
        _contrast.ItemsSource = findings.Count == 0
            ? new List<Control> { Subtle("Every pair reads.") }
            : findings.Select(f => Subtle(f.ToString())).Cast<Control>().ToList();

        static TextBlock Subtle(string text)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
            return block;
        }
    }

    // ------------------------------------------------------------------------------------
    // Save As
    // ------------------------------------------------------------------------------------

    private async Task SaveAsAsync()
    {
        if (_overrides.Count == 0)
        {
            _status.Text = "Nothing is changed yet — a saved theme would be the current one under another name.";
            return;
        }

        var name = await Prompt.AskAsync(this, "Save As Theme", "Name the theme:", "My Theme");
        if (string.IsNullOrWhiteSpace(name)) return;

        SaveTheme(name.Trim());
    }

    /// <summary>
    /// Writes the overrides as a theme file based on the edited theme, loads it, and switches
    /// to it — the durable form of what the editor was previewing.
    /// </summary>
    internal string SaveTheme(string name)
    {
        var id = Slug(name);

        // The four built-ins are untouchable, and a save must not pretend otherwise. A file
        // carrying a built-in's id is ignored at load, so that save would report success, apply
        // the untouched built-in and lose the reader's edits behind a sentence saying the
        // opposite; a file named after a built-in without its id — "Dark Gray" slugs to
        // dark-gray, the built-in is darkgray — loads fine and puts two identical names in
        // every theme list. Both are the same mistake from the reader's chair, and one rule
        // guards every writer of theme files — the importer included.
        if (Mailbox.Theming.Import.ImportedThemes.ShadowsBuiltIn(id))
        {
            _status.Text = $"“{name}” is a built-in theme's name. Save under another.";
            Log.Info($"Theme editor: refused to save under the built-in name \"{name}\".");
            return string.Empty;
        }

        var directory = ThemeLibrary.DefaultDirectory();
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, id + ThemeFileFormat.Extension);

        var tokens = new TokenSet();
        foreach (var (key, value) in Overpairs(_overrides)) tokens.Set(key, value);
        File.WriteAllText(path, ThemeFileFormat.Write(new ThemeFile(id, name, _editedTheme, IsDark: null, tokens)));

        _themes.ReplaceLibrary(ThemeLibrary.Load(directory));
        _themes.ClearOverrides();
        _themes.Apply(_themes.Library.Canonical(id) ?? _editedTheme);
        App.Settings.Set(App.ThemeSetting, _themes.ThemeId);

        foreach (var key in _overrides.Keys.ToList()) _overrides[key] = null;
        _status.Text = $"Saved to {path} and applied.";
        RefreshList();
        Log.Info($"Theme editor: saved \"{id}\" to {path}.");
        return path;
    }

    private static string Slug(string name)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "my-theme" : slug;
    }

    // ------------------------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// <c>MAILBOX_THEME_EDIT</c> presses the editor's own machinery: comma-separated
    /// <c>set:key=value</c>, <c>reset:key</c>, <c>resetall</c>, <c>save:Name</c>, each logged
    /// with what the theme service then resolves — the claim is the running theme, not the box.
    /// </summary>
    private async Task HarnessAsync()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_THEME_EDIT") is not { Length: > 0 } script) return;

        // Each set re-applies the whole theme, which outlasts the capture's settle timer; the
        // hold keeps the shot and the exit until the last op has landed.
        using var hold = Mailbox.App.Theming.WindowCapture.Hold();
        foreach (var op in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            if (op.StartsWith("set:", StringComparison.OrdinalIgnoreCase)
                && op[4..].Split('=', 2) is [{ } key, { } value])
            {
                var applied = Set(key, value);
                _themes.Tokens.TryGetString(key, out var resolved);
                Log.Info($"Harness: theme editor — set {key} = {value}: {(applied ? $"resolves to {resolved}" : $"refused ({_status.Text})")}.");
            }
            else if (op.StartsWith("reset:", StringComparison.OrdinalIgnoreCase))
            {
                Reset(op[6..]);
                _themes.Tokens.TryGetString(op[6..], out var resolved);
                Log.Info($"Harness: theme editor — reset {op[6..]}: resolves to {resolved}.");
            }
            else if (string.Equals(op, "resetall", StringComparison.OrdinalIgnoreCase))
            {
                ResetAll();
                Log.Info($"Harness: theme editor — reset all; {_overrides.Count} override(s) remain.");
            }
            else if (op.StartsWith("save:", StringComparison.OrdinalIgnoreCase))
            {
                var path = SaveTheme(op[5..]);
                Log.Info(path.Length == 0
                    ? $"Harness: theme editor — save refused: {_status.Text}"
                    : $"Harness: theme editor — saved {path}; active theme is {_themes.ThemeId}; "
                      + $"library has it: {_themes.Library.Contains(_themes.ThemeId)}.");
            }
            else if (string.Equals(op, "audit", StringComparison.OrdinalIgnoreCase))
            {
                // The contrast audit as the reader sees it, refreshed on every apply.
                var findings = Mailbox.Theming.Tokens.ContrastAudit.Check(_themes.Tokens);
                Log.Info($"Harness: theme editor — contrast audit: "
                         + (findings.Count == 0
                             ? "every pair reads."
                             : $"{findings.Count} finding(s): {string.Join(" | ", findings)}"));
            }
        }
    }
}
